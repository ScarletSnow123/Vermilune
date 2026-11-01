using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vermilune.Core;

public sealed class ForgeVersionInfo
{
    public string ForgeVersion { get; init; } = "";
    public bool Recommended { get; init; }
    public bool Latest { get; init; }
    public string Display => ForgeVersion + (Recommended ? "  ·  推荐" : Latest ? "  ·  最新" : "");
}

public sealed class ForgeService
{
    private static readonly Dictionary<string, (List<ForgeVersionInfo> List, DateTime Time)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public async Task<List<ForgeVersionInfo>> GetVersionsAsync(string mcVersion, CancellationToken ct)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(mcVersion, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(20))
                return cached.List.ToList();
        }

        var result = new List<ForgeVersionInfo>();
        try
        {
            var json = await HttpService.GetStringAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root?["promos"] is JsonObject promos)
            {
                var latest = promos[mcVersion + "-latest"]?.GetValue<string>();
                var recommended = promos[mcVersion + "-recommended"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(recommended)) result.Add(new ForgeVersionInfo { ForgeVersion = recommended, Recommended = true });
                if (!string.IsNullOrEmpty(latest) && latest != recommended) result.Add(new ForgeVersionInfo { ForgeVersion = latest, Latest = true });
            }
        }
        catch (Exception ex) { Log.Warn("Forge promotions 获取失败：" + ex.Message); }

        if (result.Count == 0)
        {
            try
            {
                var xml = await HttpService.GetStringAsync("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct);
                foreach (Match m in Regex.Matches(xml, @"<version>([^<]+)</version>"))
                {
                    var v = m.Groups[1].Value;
                    if (!v.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase)) continue;
                    var fv = v[(mcVersion.Length + 1)..];
                    if (!result.Any(x => x.ForgeVersion == fv))
                        result.Add(new ForgeVersionInfo { ForgeVersion = fv });
                }
            }
            catch (Exception ex) { Log.Warn("Forge maven 版本获取失败：" + ex.Message); }
        }

        // Maven 返回顺序通常由旧到新，倒序以便展示最新
        result = result
            .OrderByDescending(x => x.Recommended)
            .ThenByDescending(x => ParseForgeVersion(x.ForgeVersion))
            .Take(24)
            .ToList();

        lock (CacheLock) Cache[mcVersion] = (result, DateTime.UtcNow);
        return result;
    }

    private static Version ParseForgeVersion(string v)
    {
        var m = Regex.Match(v, @"\d+(\.\d+)+");
        return m.Success && Version.TryParse(m.Value, out var parsed) ? parsed : new Version(0, 0);
    }

    public async Task<string> InstallAsync(string mcVersion, string forgeVersion, string mcDir, string javaPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var versionsDir = Path.Combine(mcDir, "versions");
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(versionsDir))
            foreach (var d in Directory.EnumerateDirectories(versionsDir))
                before.Add(Path.GetFileName(d));

        var installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar";
        var dir = Path.Combine(AppPaths.CacheDir, "installers");
        Directory.CreateDirectory(dir);
        var installer = Path.Combine(dir, $"forge-{mcVersion}-{forgeVersion}-installer.jar");
        if (!File.Exists(installer) || new FileInfo(installer).Length < 1024 * 100)
        {
            progress?.Report(new DownloadProgress { Stage = "下载 Forge 部署器", Detail = $"Forge {forgeVersion}" });
            await HttpService.DownloadFileAsync(installerUrl, installer, "", null, ct, 3);
        }

        var start = DateTime.UtcNow;
        var runInstaller = await PrepareOfflineMappingsAsync(mcVersion, forgeVersion, mcDir, installer, progress, ct);
        progress?.Report(new DownloadProgress { Stage = "运行 Forge 部署器", Detail = "正在写入 libraries 与版本 JSON，可能需要几分钟" });
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = mcDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(runInstaller);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(mcDir);

        using var process = new Process { StartInfo = psi };
        var log = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.AppendLine(e.Data); progress?.Report(new DownloadProgress { Stage = "Forge 部署", Detail = Trim(e.Data) }); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.AppendLine(e.Data); progress?.Report(new DownloadProgress { Stage = "Forge 部署", Detail = Trim(e.Data) }); } };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Forge 部署器。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exited = await Task.Run(() => process.WaitForExit(10 * 60 * 1000), ct);
        if (!exited)
        {
            try { process.Kill(true); } catch { }
            CleanupNewForgeVersions(mcDir, before, mcVersion, forgeVersion);
            throw new TimeoutException("Forge 部署器超过 10 分钟未结束，已终止。");
        }
        if (process.ExitCode != 0)
        {
            CleanupNewForgeVersions(mcDir, before, mcVersion, forgeVersion);
            var detail = Tail(log.ToString());
            if (detail.Contains("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("UnknownHostException", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Forge 安装失败：无法访问 piston-data.mojang.com（Mojang 映射表服务器）。\n" +
                    "请切换下载源、更换 DNS 或挂代理后重试；也可以先删除这个 Forge 版本再安装。\n\n" + detail);
            }
            throw new InvalidOperationException("Forge 部署器退出码 " + process.ExitCode + "：\n" + detail);
        }

        var targetId = FindInstalledVersion(mcDir, mcVersion, forgeVersion, start);
        if (string.IsNullOrEmpty(targetId))
        {
            // 某些 Forge 版本使用不同的文件夹名，退而查找最新修改的 Forge 版本
            targetId = FindAnyNewForgeVersion(mcDir, start);
        }
        if (string.IsNullOrEmpty(targetId))
        {
            CleanupNewForgeVersions(mcDir, before, mcVersion, forgeVersion);
            throw new InvalidOperationException("Forge 部署器已结束，但没有找到新生成的版本。请打开 versions 目录检查安装日志。");
        }

        // 部署器可能先写版本 JSON，再在下载映射表时失败；必须检查核心库是否完整。
        var universal = Path.Combine(mcDir, "libraries", "net", "minecraftforge", "forge",
            $"{mcVersion}-{forgeVersion}", $"forge-{mcVersion}-{forgeVersion}-universal.jar");
        var targetJson = Path.Combine(mcDir, "versions", targetId, targetId + ".json");
        if (!File.Exists(universal) || !File.Exists(targetJson))
        {
            CleanupNewForgeVersions(mcDir, before, mcVersion, forgeVersion);
            throw new InvalidOperationException(
                "Forge 安装不完整：缺少 forge-universal.jar 或版本 JSON。\n" +
                "通常是网络无法访问 piston-data.mojang.com，请切换下载源后重装。");
        }

        progress?.Report(new DownloadProgress { Stage = "Forge 部署完成", Current = 1, Total = 1, Detail = targetId });
        return targetId;
    }

    private static void CleanupNewForgeVersions(string mcDir, HashSet<string> before, string mcVersion, string forgeVersion)
    {
        try
        {
            var versions = Path.Combine(mcDir, "versions");
            if (!Directory.Exists(versions)) return;
            foreach (var dir in Directory.EnumerateDirectories(versions))
            {
                var name = Path.GetFileName(dir);
                if (before.Contains(name)) continue;
                if (!name.Contains("forge", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.Contains(mcVersion, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.Contains(forgeVersion, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); } catch (Exception ex) { Log.Warn("清理失败的 Forge 版本失败：" + ex.Message); }
            }
        }
        catch (Exception ex) { Log.Warn("清理失败的 Forge 版本失败：" + ex.Message); }
    }

    private async Task<string> PrepareOfflineMappingsAsync(string mcVersion, string forgeVersion, string mcDir,
        string installer, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var read = ZipFile.OpenRead(installer);
            var profileEntry = read.GetEntry("install_profile.json");
            if (profileEntry is null) return installer;
            string profileJson;
            using (var stream = profileEntry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                profileJson = await reader.ReadToEndAsync(ct);

            var profile = JsonNode.Parse(profileJson) as JsonObject;
            var raw = profile?["data"]?["MOJMAPS"]?["client"]?.GetValue<string>() ?? "";
            var match = Regex.Match(raw, @"\[([^:]+):([^:]+):([^:\]]+):mappings@txt\]");
            if (!match.Success) return installer;

            var group = match.Groups[1].Value.Replace('.', '/');
            var artifact = match.Groups[2].Value;
            var mappingVersion = match.Groups[3].Value;
            var mappingPath = Path.Combine(mcDir, "libraries", group, artifact, mappingVersion,
                $"{artifact}-{mappingVersion}-mappings.txt");

            if (!File.Exists(mappingPath) || new FileInfo(mappingPath).Length < 4096)
            {
                progress?.Report(new DownloadProgress { Stage = "下载 Mojang 映射表", Detail = mcVersion });
                try
                {
                    var metaJson = await HttpService.GetStringAsync(
                        $"https://bmclapi2.bangbang93.com/version/{Uri.EscapeDataString(mcVersion)}/json", ct);
                    var download = (JsonNode.Parse(metaJson) as JsonObject)?["downloads"]?["client_mappings"];
                    var officialUrl = download?["url"]?.GetValue<string>() ?? "";
                    var sha1 = download?["sha1"]?.GetValue<string>() ?? "";
                    var hash = officialUrl.Split('/').LastOrDefault() ?? "";
                    var mirrorUrl = string.IsNullOrWhiteSpace(hash)
                        ? ""
                        : $"https://bmclapi2.bangbang93.com/v1/objects/{hash}/client.txt";
                    Directory.CreateDirectory(Path.GetDirectoryName(mappingPath)!);
                    var failed = false;
                    if (!string.IsNullOrWhiteSpace(mirrorUrl))
                    {
                        try { await HttpService.DownloadFileAsync(mirrorUrl, mappingPath, sha1, null, ct, 3); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed = true;
                            Log.Warn("BMCLAPI 映射表下载失败：" + ex.Message);
                        }
                    }
                    if (failed || !File.Exists(mappingPath) || new FileInfo(mappingPath).Length < 4096)
                    {
                        if (!string.IsNullOrWhiteSpace(officialUrl))
                            await HttpService.DownloadFileAsync(officialUrl, mappingPath, sha1, null, ct, 3);
                    }
                    if (File.Exists(mappingPath))
                    {
                        var head = new byte[2];
                        using var fs = File.OpenRead(mappingPath);
                        _ = fs.Read(head, 0, 2);
                        if (head[0] == (byte)'P' && head[1] == (byte)'K')
                        {
                            File.Delete(mappingPath);
                            Log.Warn("映射表下载结果不是文本，已丢弃");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn("映射表准备失败：" + ex.Message);
                }
            }

            if (!File.Exists(mappingPath)) return installer;

            var patched = Path.Combine(Path.GetDirectoryName(installer)!, $"forge-{mcVersion}-{forgeVersion}-installer-nomojmaps.jar");
            File.Copy(installer, patched, true);
            using (var zip = ZipFile.Open(patched, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("install_profile.json");
                if (entry is null) return installer;
                string json;
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    json = await reader.ReadToEndAsync(ct);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root?["processors"] is JsonArray processors)
                {
                    for (var i = processors.Count - 1; i >= 0; i--)
                    {
                        if (processors[i]?.ToJsonString().Contains("DOWNLOAD_MOJMAPS", StringComparison.Ordinal) == true)
                            processors.RemoveAt(i);
                    }
                }
                entry.Delete();
                var replacement = zip.CreateEntry("install_profile.json", CompressionLevel.Optimal);
                using var output = replacement.Open();
                using var writer = new StreamWriter(output, new UTF8Encoding(false));
                writer.Write(root?.ToJsonString() ?? json);
            }
            Log.Info("已准备本地映射表，跳过 Forge 的 DownloadMojmaps");
            return patched;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("准备离线映射表失败：" + ex.Message);
            return installer;
        }
    }

    private static string Trim(string s) => s.Length > 120 ? s[..120] : s;
    private static string Tail(string s) => s.Length > 1200 ? s[^1200..] : s;

    private static string FindInstalledVersion(string mcDir, string mc, string forge, DateTime start)
    {
        var versions = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versions)) return "";
        var candidates = Directory.EnumerateDirectories(versions)
            .Select(d => new DirectoryInfo(d))
            .Where(d => d.LastWriteTimeUtc >= start.AddMinutes(-2))
            .Where(d => d.Name.Contains("forge", StringComparison.OrdinalIgnoreCase) && d.Name.Contains(mc, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .ToList();
        var exact = candidates.FirstOrDefault(d => d.Name.Contains(forge, StringComparison.OrdinalIgnoreCase));
        return (exact ?? candidates.FirstOrDefault())?.Name ?? "";
    }

    private static string FindAnyNewForgeVersion(string mcDir, DateTime start)
    {
        var versions = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versions)) return "";
        return Directory.EnumerateDirectories(versions)
            .Select(d => new DirectoryInfo(d))
            .Where(d => d.Name.Contains("forge", StringComparison.OrdinalIgnoreCase) && d.LastWriteTimeUtc >= start.AddMinutes(-2))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => d.Name)
            .FirstOrDefault() ?? "";
    }
}
