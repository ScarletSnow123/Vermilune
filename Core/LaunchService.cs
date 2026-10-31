using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vermilune.Core;

public enum LaunchState { Idle, Preparing, Downloading, Starting, Running, Exited, Failed }

public sealed class LaunchProgress
{
    public LaunchState State { get; init; }
    public string Stage { get; init; } = "";
    public double? Fraction { get; init; }
    public string? Detail { get; init; }
    public int? ExitCode { get; init; }
}

public sealed class LaunchService
{
    public event Action<LaunchProgress>? ProgressChanged;
    public event Action<string>? OutputLine;
    public event Action<int, string?>? Exited;

    public LaunchState State { get; private set; } = LaunchState.Idle;
    public Process? CurrentProcess { get; private set; }
    public string CurrentVersionId { get; private set; } = "";
    public string LastLogPath { get; private set; } = "";
    public string LastGameDir { get; private set; } = "";
    public string? LastCrashReport { get; private set; }
    public DateTime LaunchStartedUtc { get; private set; }
    public int LastExitCode { get; private set; }
    public bool IsRunning => CurrentProcess is { HasExited: false };
    public string LastCommandDisplay { get; private set; } = "";

    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly LegacyLogParser _logParser = new();
    private System.Threading.Timer? _flushTimer;
    private bool _stopRequested;

    public void Cancel()
    {
        _stopRequested = true;
        try
        {
            if (CurrentProcess is { HasExited: false })
            {
                CurrentProcess.Kill(true);
            }
        }
        catch (Exception ex) { Log.Warn("结束游戏进程失败：" + ex.Message); }
    }

    public async Task LaunchAsync(LaunchOptions opt, CancellationToken ct)
    {
        await _launchLock.WaitAsync(ct);
        try
        {
            if (IsRunning) throw new InvalidOperationException("游戏已经在运行中。");
            _stopRequested = false;
            CurrentVersionId = opt.Version.Id;
            LastGameDir = opt.GameDir;
            LastCrashReport = null;
            LaunchStartedUtc = DateTime.UtcNow;
            State = LaunchState.Preparing;
            Report(LaunchState.Preparing, "检查运行环境…");

            if (!File.Exists(opt.JavaPath))
                throw new FileNotFoundException("找不到 Java 可执行文件，请到设置里重新选择 Java。", opt.JavaPath);
            if (!File.Exists(opt.Resolved.ClientJarPath) && string.IsNullOrEmpty(opt.Resolved.ClientJarUrl))
                throw new FileNotFoundException("客户端 jar 缺失，且版本 JSON 没有提供下载地址。请重装该版本。", opt.Resolved.ClientJarPath);

            Directory.CreateDirectory(opt.GameDir);
            Directory.CreateDirectory(opt.NativesDir);
            if (!opt.Account.IsMicrosoft && !opt.Account.IsYggdrasil)
                LauncherApp.I.Skins.ApplyOfflinePack(opt.GameDir, opt.Resolved.MinecraftVersion);
            ApplyDefaultLanguage(opt);
            var mcDir = opt.MinecraftDir;
            if (string.IsNullOrWhiteSpace(mcDir))
                throw new InvalidOperationException("未指定 Minecraft 目录。");

            var features = new LaunchFeatures
            {
                HasCustomResolution = opt.Width > 0 && opt.Height > 0,
                HasQuickPlaysSupport = false
            };

            // 1. 资源索引与客户端 jar
            Report(LaunchState.Downloading, "检查游戏资源…", 0.02);
            await AssetService.EnsureIndexAsync(opt.Resolved, mcDir, new Progress<string>(s => Report(LaunchState.Downloading, s, 0.05)), ct);

            if (!File.Exists(opt.Resolved.ClientJarPath))
            {
                if (string.IsNullOrEmpty(opt.Resolved.ClientJarUrl))
                    throw new FileNotFoundException("客户端 jar 缺失，且没有下载地址。", opt.Resolved.ClientJarPath);
                Report(LaunchState.Downloading, "下载客户端 jar…", 0.08);
                await HttpService.DownloadFileAsync(opt.Resolved.ClientJarUrl, opt.Resolved.ClientJarPath,
                    opt.Resolved.ClientJarSha1, null, ct);
            }

            // 2. 库文件
            var enabledLibs = opt.Resolved.Libraries
                .Where(l => RuleEvaluator.IsAllowed(l.Rules, features))
                .Where(l => !l.IsNative)
                .ToList();
            var nativeLibs = opt.Resolved.Libraries
                .Where(l => l.IsNative && RuleEvaluator.IsAllowed(l.Rules, features))
                .ToList();

            var libDir = Path.Combine(mcDir, "libraries");
            var missing = enabledLibs
                .Select(l => (Lib: l, Path: Path.Combine(libDir, l.Path)))
                .Where(x => !File.Exists(x.Path))
                .ToList();
            var missingNatives = nativeLibs
                .Select(l => (Lib: l, Path: Path.Combine(libDir, l.Path)))
                .Where(x => !File.Exists(x.Path))
                .ToList();

            var allMissing = missing.Concat(missingNatives).ToList();
            if (allMissing.Count > 0)
            {
                Report(LaunchState.Downloading, $"补全运行库（{allMissing.Count} 个）…", 0.12);
                var done = 0;
                var errors = new ConcurrentBag<string>();
                var semaphore = new SemaphoreSlim(8);
                var tasks = allMissing.Select(async item =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var urls = item.Lib.AllUrls().ToList();
                        if (urls.Count == 0)
                        {
                            errors.Add($"{item.Lib.Name} 缺少下载地址");
                            return;
                        }
                        await HttpService.DownloadFileAsync(urls, item.Path, item.Lib.Sha1, null, ct, 3);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { errors.Add($"{item.Lib.Name}: {ex.Message}"); }
                    finally
                    {
                        var d = Interlocked.Increment(ref done);
                        semaphore.Release();
                        if (d % 3 == 0 || d == allMissing.Count)
                            Report(LaunchState.Downloading, $"补全运行库 {d}/{allMissing.Count}", 0.12 + 0.35 * d / allMissing.Count);
                    }
                });
                await Task.WhenAll(tasks);
                if (!errors.IsEmpty)
                {
                    var msg = string.Join(Environment.NewLine, errors.Take(6));
                    throw new InvalidOperationException("运行库下载失败：" + Environment.NewLine + msg);
                }
            }

            // 3. 资源对象（只补缺失）
            if (File.Exists(opt.Resolved.AssetIndexPath))
            {
                var assetProgress = new Progress<(long Done, long Total, string Detail)>(p =>
                {
                    var frac = p.Total > 0 ? 0.5 + 0.3 * p.Done / p.Total : 0.55;
                    Report(LaunchState.Downloading, p.Detail, frac);
                });
                var (downloaded, missingAssets) = await AssetService.EnsureObjectsAsync(opt.Resolved, mcDir, assetProgress, ct);
                if (downloaded > 0) Log.Info($"补全资源文件 {downloaded} 个，失败 {missingAssets} 个");
            }

            // 4. 日志配置
            string? logConfigPath = null;
            if (opt.Resolved.Logging is { FileUrl.Length: > 0 } logging)
            {
                var target = Path.Combine(mcDir, "assets", "log_configs", logging.FileId);
                if (!File.Exists(target))
                {
                    try
                    {
                        await HttpService.DownloadFileAsync(logging.FileUrl, target, logging.FileSha1, null, ct);
                    }
                    catch (Exception ex) { Log.Warn("日志配置下载失败，已忽略：" + ex.Message); }
                }
                if (File.Exists(target)) logConfigPath = target;
            }

            // 5. 解压 natives
            Report(LaunchState.Preparing, "整理本地运行库…", 0.82);
            ExtractNatives(nativeLibs, enabledLibs, libDir, opt.NativesDir);

            // 6. 构造命令行
            var classpath = string.Join(';', enabledLibs
                .Select(l => Path.Combine(libDir, l.Path))
                .Append(opt.Resolved.ClientJarPath)
                .Distinct(StringComparer.OrdinalIgnoreCase));

            string? authlibJar = null;
            if (opt.Account.IsYggdrasil)
            {
                Report(LaunchState.Preparing, "准备外置登录组件…", 0.84);
                authlibJar = await AuthlibInjector.EnsureAsync(ct);
            }

            var args = BuildArguments(opt, features, classpath, libDir, mcDir, logConfigPath, authlibJar);
            var psi = new ProcessStartInfo
            {
                FileName = opt.JavaPath,
                WorkingDirectory = opt.GameDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            LastCommandDisplay = BuildDisplayCommand(opt.JavaPath, args, opt.Account);
            StartLogFile(opt, args);

            State = LaunchState.Starting;
            Report(LaunchState.Starting, "启动 Java 进程…", 0.9);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnGameOutput(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnGameOutput(e.Data); };
            process.Exited += (_, _) => OnProcessExited(process);

            if (!process.Start()) throw new InvalidOperationException("无法启动 Java 进程。");
            CurrentProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            State = LaunchState.Running;
            Report(LaunchState.Running, "游戏已启动", 1.0);
            Log.Info($"游戏进程已启动：{opt.JavaPath}，版本 {opt.Version.Id}，PID {process.Id}");
        }
        catch (OperationCanceledException)
        {
            State = LaunchState.Idle;
            Report(LaunchState.Idle, "已取消");
            throw;
        }
        catch (Exception ex)
        {
            State = LaunchState.Failed;
            Log.Error(ex, "启动失败");
            Report(LaunchState.Failed, ex.Message);
            throw;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    private void OnProcessExited(Process process)
    {
        try
        {
            foreach (var line in _logParser.Flush())
            {
                OutputLine?.Invoke(line);
                _logQueue.Enqueue(line);
            }
        }
        catch { }
        int code;
        try { code = process.ExitCode; } catch { code = -1; }
        if (_stopRequested)
        {
            _stopRequested = false;
            code = 0;
        }
        LastExitCode = code;
        State = code == 0 ? LaunchState.Exited : LaunchState.Failed;
        Thread.Sleep(120);
        CurrentProcess = null;
        try { process.Dispose(); } catch { }

        string? crash = null;
        if (code != 0)
        {
            try { crash = CrashAnalyzer.FindLatestCrashReport(LastGameDir, LaunchStartedUtc); } catch { }
        }
        LastCrashReport = crash;
        Log.Info($"游戏进程退出，退出码 {code}" + (crash is null ? "" : $"，崩溃报告 {crash}"));
        Report(State, code == 0 ? "游戏已退出" : $"游戏异常退出（{code}）", 1.0, code);
        Exited?.Invoke(code, crash);
    }

    private void OnGameOutput(string line)
    {
        try
        {
            foreach (var formatted in _logParser.Feed(line))
            {
                OutputLine?.Invoke(formatted);
                _logQueue.Enqueue(formatted);
            }
            _flushTimer ??= new System.Threading.Timer(_ => FlushGameLog(), null, 300, 300);
        }
        catch { }
    }

    private StreamWriter? _gameLogWriter;
    private readonly object _gameLogLock = new();

    private void StartLogFile(LaunchOptions opt, List<string> args)
    {
        try
        {
            var name = $"game-{AppPaths.SafeKey(opt.Version.Id)}-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            LastLogPath = Path.Combine(AppPaths.LogDir, name);
            _gameLogWriter = new StreamWriter(LastLogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            _gameLogWriter.WriteLine("===== Vermilune 启动日志 =====");
            _gameLogWriter.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _gameLogWriter.WriteLine($"版本：{opt.Version.Id} ({opt.Version.LoaderBadge} {opt.Version.MinecraftVersion})");
            _gameLogWriter.WriteLine($"Java：{opt.JavaPath}");
            _gameLogWriter.WriteLine($"游戏目录：{opt.GameDir}");
            _gameLogWriter.WriteLine($"命令行：{LastCommandDisplay}");
            _gameLogWriter.WriteLine("==============================");
        }
        catch (Exception ex) { Log.Warn("创建游戏日志失败：" + ex.Message); }
    }

    private void FlushGameLog()
    {
        try
        {
            if (_gameLogWriter is null) return;
            lock (_gameLogLock)
            {
                while (_logQueue.TryDequeue(out var line)) _gameLogWriter.WriteLine(line);
            }
        }
        catch { }
    }

    private void ExtractNatives(List<LibraryEntry> natives, List<LibraryEntry> allLibs, string libDir, string nativesDir)
    {
        try
        {
            if (Directory.Exists(nativesDir)) Directory.Delete(nativesDir, true);
            Directory.CreateDirectory(nativesDir);
            var toExtract = new List<LibraryEntry>(natives);
            // 现代版本把 natives 作为普通 classpath 库，仍解压到 natives 目录以兼容 Forge / LWJGL
            toExtract.AddRange(allLibs.Where(l =>
                l.Path.Contains("natives-windows", StringComparison.OrdinalIgnoreCase) &&
                !l.Path.Contains("natives-windows-x86", StringComparison.OrdinalIgnoreCase) &&
                !l.Path.Contains("natives-windows-arm64", StringComparison.OrdinalIgnoreCase)));

            foreach (var lib in toExtract.DistinctBy(l => l.Path))
            {
                var jar = Path.Combine(libDir, lib.Path);
                if (!File.Exists(jar)) continue;
                try
                {
                    using var zip = ZipFile.OpenRead(jar);
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith('/')) continue;
                        if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (lib.ExtractExclude.Length > 0 &&
                            lib.ExtractExclude.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                .Any(ex => entry.FullName.Contains(ex, StringComparison.OrdinalIgnoreCase))) continue;
                        var fileName = Path.GetFileName(entry.FullName);
                        if (string.IsNullOrEmpty(fileName)) continue;
                        var dest = Path.Combine(nativesDir, fileName);
                        entry.ExtractToFile(dest, true);
                    }
                }
                catch (Exception ex) { Log.Warn($"解压运行库失败 {lib.Name}：{ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("准备 natives 目录失败：" + ex.Message);
            throw new IOException("无法准备 natives 目录（可能被游戏占用）：" + ex.Message, ex);
        }
    }

    private static void ApplyDefaultLanguage(LaunchOptions opt)
    {
        try
        {
            var path = Path.Combine(opt.GameDir, "options.txt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "lang:zh_cn\n");
                return;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Any(l => l.StartsWith("lang:", StringComparison.OrdinalIgnoreCase))) return;
            File.AppendAllText(path, (lines.Length > 0 ? "\n" : "") + "lang:zh_cn\n");
            Log.Info("已为游戏设置默认中文语言：" + path);
        }
        catch (Exception ex)
        {
            Log.Warn("设置游戏默认语言失败：" + ex.Message);
        }
    }

    private List<string> BuildArguments(LaunchOptions opt, LaunchFeatures features, string classpath,
        string libDir, string mcDir, string? logConfigPath, string? authlibJar = null)
    {
        var resolved = opt.Resolved;
        var account = opt.Account;
        var natives = opt.NativesDir;
        var assetsRoot = Path.Combine(mcDir, "assets");
        var gameAssets = Path.Combine(assetsRoot, "virtual", resolved.AssetsId);
        var uuid = AccountService.EnsureUuid(account);
        var token = string.IsNullOrEmpty(account.AccessTokenProtected) ? "0" : Dpapi.Unprotect(account.AccessTokenProtected);
        if (string.IsNullOrEmpty(token)) token = "0";
        var userType = account.IsMicrosoft ? "msa" : account.IsYggdrasil ? "mojang" : "legacy";

        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = opt.Version.Id,
            ["game_directory"] = opt.GameDir,
            ["assets_root"] = assetsRoot,
            ["game_assets"] = gameAssets,
            ["assets_index_name"] = resolved.AssetsId,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = token,
            ["auth_session"] = token,
            ["user_type"] = userType,
            ["version_type"] = resolved.Type,
            ["user_properties"] = "{}",
            ["clientid"] = "",
            ["auth_xuid"] = account.Xuid,
            ["natives_directory"] = natives,
            ["launcher_name"] = opt.LauncherName,
            ["launcher_version"] = opt.LauncherVersion,
            ["classpath"] = classpath,
            ["library_directory"] = libDir,
            ["classpath_separator"] = ";",
            ["resolution_width"] = opt.Width > 0 ? opt.Width.ToString() : "854",
            ["resolution_height"] = opt.Height > 0 ? opt.Height.ToString() : "480",
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = "",
            ["quickPlayRealms"] = "",
            ["version"] = opt.Version.Id
        };

        string Replace(string s)
        {
            foreach (var kv in placeholders) s = s.Replace("${" + kv.Key + "}", kv.Value, StringComparison.OrdinalIgnoreCase);
            // 未识别的占位符替换为空，避免把 ${...} 原样传给游戏
            return Regex.Replace(s, @"\$\{[a-zA-Z0-9_]+\}", "");
        }

        var jvm = new List<string>();
        if (!string.IsNullOrEmpty(authlibJar) && !string.IsNullOrWhiteSpace(account.YggdrasilServer))
            jvm.Add($"-javaagent:{authlibJar}={account.YggdrasilServer}");
        var memory = Math.Clamp(opt.MemoryMb, 512, 65536);
        jvm.Add($"-Xmx{memory}m");
        jvm.Add($"-Xms{Math.Min(1024, Math.Max(512, memory / 4))}m");
        if (opt.OptimizeJvm)
        {
            jvm.AddRange(new[]
            {
                "-XX:+UseG1GC", "-XX:+ParallelRefProcEnabled", "-XX:MaxGCPauseMillis=200",
                "-XX:+UnlockExperimentalVMOptions", "-XX:+DisableExplicitGC",
                "-XX:G1NewSizePercent=20", "-XX:G1MaxNewSizePercent=40", "-XX:G1HeapRegionSize=8M",
                "-XX:G1ReservePercent=20", "-XX:G1HeapWastePercent=5", "-XX:G1MixedGCCountTarget=4",
                "-XX:InitiatingHeapOccupancyPercent=15", "-XX:G1MixedGCLiveThresholdPercent=90",
                "-XX:G1RSetUpdatingPauseTimePercent=5", "-XX:SurvivorRatio=32",
                "-XX:+PerfDisableSharedMem", "-XX:MaxTenuringThreshold=1"
            });
        }
        jvm.Add("-Djava.library.path=" + natives);
        jvm.Add("-Djna.tmpdir=" + natives);
        jvm.Add("-Dorg.lwjgl.system.SharedLibraryExtractPath=" + natives);
        jvm.Add("-Dio.netty.native.workdir=" + natives);
        jvm.Add("-Dminecraft.launcher.brand=" + opt.LauncherName);
        jvm.Add("-Dminecraft.launcher.version=" + opt.LauncherVersion);
        jvm.Add("-Dlog4j2.formatMsgNoLookups=true");
        if (opt.Version.Loader is LoaderKind.Forge or LoaderKind.NeoForge)
        {
            jvm.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
            jvm.Add("-Dfml.ignorePatchDiscrepancies=true");
        }
        if (logConfigPath is not null && !string.IsNullOrEmpty(resolved.Logging?.Argument))
            jvm.Add(Replace(resolved.Logging.Argument.Replace("${path}", logConfigPath)));

        // 版本 JSON 的 JVM 参数
        var versionJvm = ExpandArguments(resolved.JvmArguments, features, Replace);
        // 去掉重复的 -cp 对（保留最后一个）
        var cpIndexes = new List<int>();
        for (var i = 0; i < versionJvm.Count; i++)
            if (versionJvm[i] is "-cp" or "-classpath") cpIndexes.Add(i);
        var skip = new HashSet<int>();
        for (var k = 0; k < cpIndexes.Count - 1; k++)
        {
            skip.Add(cpIndexes[k]);
            if (cpIndexes[k] + 1 < versionJvm.Count) skip.Add(cpIndexes[k] + 1);
        }
        for (var i = 0; i < versionJvm.Count; i++)
            if (!skip.Contains(i)) jvm.Add(versionJvm[i]);

        var hasClassPathArg = cpIndexes.Count > 0;
        if (!hasClassPathArg)
        {
            jvm.Add("-cp");
            jvm.Add(classpath);
        }

        // 用户追加 JVM 参数
        foreach (var arg in CommandLineSplitter.Split(opt.ExtraJvmArgs))
            jvm.Add(Replace(arg));

        var game = ExpandArguments(resolved.GameArguments, features, Replace);
        if (game.Count == 0 && !string.IsNullOrWhiteSpace(resolved.LegacyArguments))
            game.AddRange(CommandLineSplitter.Split(resolved.LegacyArguments).Select(Replace));

        if (opt.Width > 0 && opt.Height > 0 && !game.Contains("--width"))
        {
            game.Add("--width"); game.Add(opt.Width.ToString());
            game.Add("--height"); game.Add(opt.Height.ToString());
        }
        if (opt.Fullscreen && !game.Contains("--fullscreen")) game.Add("--fullscreen");
        foreach (var arg in CommandLineSplitter.Split(opt.ExtraGameArgs))
            game.Add(Replace(arg));

        var result = new List<string>(jvm.Count + game.Count + 2);
        result.AddRange(jvm);
        result.Add(resolved.MainClass);
        result.AddRange(game);
        return result;
    }

    private static List<string> ExpandArguments(List<JsonNode?> nodes, LaunchFeatures features, Func<string, string> replace)
    {
        var result = new List<string>();
        foreach (var node in nodes)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var s))
            {
                result.Add(replace(s));
            }
            else if (node is JsonObject obj)
            {
                if (obj["rules"] is JsonArray rules && !RuleEvaluator.IsAllowed(rules, features)) continue;
                var value = obj["value"];
                if (value is JsonValue vv && vv.TryGetValue<string>(out var one)) result.Add(replace(one));
                else if (value is JsonArray arr)
                {
                    foreach (var item in arr)
                        if (item is JsonValue iv && iv.TryGetValue<string>(out var str)) result.Add(replace(str));
                }
            }
        }
        return result;
    }

    private static string BuildDisplayCommand(string java, List<string> args, AccountSettings account)
    {
        var masked = args.Select(a =>
            a.Length > 20 && account.IsOnline && !a.StartsWith("-") && !a.Contains('\\') && !a.Contains('/')
                ? "******" : a);
        return "\"" + java + "\" " + string.Join(' ', masked.Select(Quote));
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;

    private void Report(LaunchState state, string stage, double? fraction = null, int? exitCode = null)
    {
        State = state;
        try { ProgressChanged?.Invoke(new LaunchProgress { State = state, Stage = stage, Fraction = fraction, ExitCode = exitCode }); }
        catch { }
    }
}

/// <summary>按 Windows 命令行规则拆分参数，支持双引号与反斜杠转义。</summary>
public static class CommandLineSplitter
{
    public static List<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;
        var sb = new StringBuilder();
        var inQuotes = false;
        var backslashes = 0;
        foreach (var c in commandLine)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) sb.Append('"');
                }
                else inQuotes = !inQuotes;
                backslashes = 0;
                continue;
            }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (backslashes > 0) sb.Append('\\', backslashes);
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}
