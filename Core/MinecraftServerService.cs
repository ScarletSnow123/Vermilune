using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Management;

namespace Vermilune.Core;

/// <summary>
/// 在主机上启动一个供虚拟局域网连接的小型 Minecraft 服务端。
/// 当前一键开服支持原版和 Fabric；其他加载器会明确提示暂不支持。
/// </summary>
public sealed class MinecraftServerService : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _log = new();

    public bool IsRunning => _process is { HasExited: false };
    public string? ServerDirectory { get; private set; }
    public int Port { get; private set; }
    public string? PublicAddress { get; private set; }
    public string? Ipv6Address { get; private set; }
    public string? LocalAddress { get; private set; }
    public string? MappingStatus { get; private set; }
    public string? FirewallStatus { get; private set; }
    public string? LastError { get; private set; }
    public event Action<string>? LogReceived;
    public event Action? Ready;

    public async Task StartAsync(VersionInfo version, int memoryMb, int port, bool eulaAccepted,
        bool whitelist, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!eulaAccepted) throw new InvalidOperationException("需要先勾选同意 Minecraft EULA。");
        if (version.Loader is not (LoaderKind.Vanilla or LoaderKind.Fabric))
            throw new InvalidOperationException("一键开服目前支持原版和 Fabric 实例；Forge / NeoForge / Quilt 请先使用对应服务端安装器。");
        await StopAsync();

        var safeName = string.Concat(version.Id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var serverDir = Path.Combine(AppPaths.DataDir, "Servers", safeName);
        Directory.CreateDirectory(serverDir);
        ServerDirectory = serverDir;
        Port = port;

        File.WriteAllText(Path.Combine(serverDir, "eula.txt"), "eula=true\n");
        var properties = Path.Combine(serverDir, "server.properties");
        var lines = File.Exists(properties) ? File.ReadAllLines(properties).ToList() : new List<string>
        {
            "online-mode=true",
            "motd=Vermilune 联机服务器",
            "enable-command-block=true",
            "spawn-protection=16"
        };
        SetProperty(lines, "server-port", port.ToString());
        SetProperty(lines, "white-list", whitelist ? "true" : "false");
        SetProperty(lines, "enforce-whitelist", whitelist ? "true" : "false");
        File.WriteAllLines(properties, lines);

        var jar = await EnsureServerJarAsync(version, serverDir, progress, ct);
        CopyInstanceMods(version, serverDir);
        StopStaleServerProcesses(serverDir);
        var java = LauncherApp.I.Java.Pick(version.JavaMajor, LauncherApp.I.Settings.JavaPath)
                   ?? throw new InvalidOperationException($"启动服务端需要 Java {version.JavaMajor} 或更高。");

        var psi = new ProcessStartInfo
        {
            FileName = java.Path,
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-Xms1G");
        psi.ArgumentList.Add($"-Xmx{Math.Clamp(memoryMb, 1024, 16384)}M");
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(jar);
        psi.ArgumentList.Add("nogui");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process))
            {
                LastError = $"服务端已退出（代码 {process.ExitCode}）。";
                LogReceived?.Invoke(LastError);
            }
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Minecraft 服务端。");
        _process = process;
        try { File.WriteAllText(Path.Combine(serverDir, ".server.pid"), process.Id.ToString()); } catch { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Ipv6Address = UpnpService.FindGlobalIpv6();
        FirewallStatus = await FirewallService.EnsureTcpRuleAsync(port, ct);
        var mapping = await LauncherApp.I.Upnp.MapAsync(port, ct);
        PublicAddress = mapping.PublicAddress;
        LocalAddress = mapping.LocalAddress;
        MappingStatus = mapping.Status;
        progress?.Report(new DownloadProgress { Stage = "服务端启动中", Detail = "Java " + java.Major + " · 端口 " + port });
    }

    private static void SetProperty(List<string> lines, string key, string value)
    {
        var index = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = key + "=" + value;
        else lines.Add(key + "=" + value);
    }

    private void Append(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_log) _log.AppendLine(line);
        LogReceived?.Invoke(line);
        if (line.Contains("Done (", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("For help, type", StringComparison.OrdinalIgnoreCase))
            Ready?.Invoke();
    }

    private async Task<string> EnsureServerJarAsync(VersionInfo version, string serverDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (version.Loader == LoaderKind.Fabric)
        {
            var resolved = LauncherApp.I.Versions.Resolve(version.Id);
            var loader = resolved?.Libraries
                .Select(l => l.Name)
                .FirstOrDefault(n => n.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':').LastOrDefault();
            if (string.IsNullOrWhiteSpace(loader))
            {
                var loaders = await LauncherApp.I.Downloads.GetFabricLoadersAsync(version.MinecraftVersion, ct);
                loader = loaders.FirstOrDefault(l => l.Stable)?.Version ?? loaders.FirstOrDefault()?.Version;
            }
            if (string.IsNullOrWhiteSpace(loader)) throw new InvalidOperationException("无法确定 Fabric Loader 版本。");
            var installer = await ResolveFabricInstallerAsync(ct);
            var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(version.MinecraftVersion)}/{Uri.EscapeDataString(loader)}/{Uri.EscapeDataString(installer)}/server/jar";
            var target = Path.Combine(serverDir, "fabric-server.jar");
            if (!File.Exists(target) || new FileInfo(target).Length < 1024 * 100)
            {
                progress?.Report(new DownloadProgress { Stage = "下载 Fabric 服务端", Detail = loader });
                await HttpService.DownloadFileAsync(url, target, "", null, ct, 3);
            }
            return target;
        }

        var manifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        var manifest = JsonNode.Parse(await HttpService.GetStringAsync(manifestUrl, ct)) as JsonObject;
        var entry = (manifest?["versions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(v => string.Equals(v["id"]?.GetValue<string>(), version.MinecraftVersion, StringComparison.OrdinalIgnoreCase));
        var versionUrl = entry?["url"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(versionUrl)) throw new InvalidOperationException("无法获取原版服务端信息。");
        var versionJson = JsonNode.Parse(await HttpService.GetStringAsync(versionUrl, ct)) as JsonObject;
        var server = versionJson?["downloads"]?["server"];
        var serverUrl = server?["url"]?.GetValue<string>() ?? "";
        var sha1 = server?["sha1"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(serverUrl)) throw new InvalidOperationException("该 Minecraft 版本没有可用的服务端下载。");
        var vanillaJar = Path.Combine(serverDir, "server.jar");
        if (!File.Exists(vanillaJar) || new FileInfo(vanillaJar).Length < 1024 * 100)
        {
            progress?.Report(new DownloadProgress { Stage = "下载原版服务端", Detail = version.MinecraftVersion });
            await HttpService.DownloadFileAsync(serverUrl, vanillaJar, sha1, null, ct, 3);
        }
        return vanillaJar;
    }

    private static async Task<string> ResolveFabricInstallerAsync(CancellationToken ct)
    {
        var json = await HttpService.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer", ct);
        var array = JsonNode.Parse(json) as JsonArray;
        var stable = array?.OfType<JsonObject>().FirstOrDefault(x => x["stable"]?.GetValue<bool>() == true)
                     ?? array?.OfType<JsonObject>().FirstOrDefault();
        var version = stable?["version"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("无法获取 Fabric 安装器版本。");
        return version;
    }

    private static void CopyInstanceMods(VersionInfo version, string serverDir)
    {
        try
        {
            var instance = InstanceStore.Load(version.Id);
            var gameDir = LauncherApp.I.Versions.GetGameDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation);
            var source = Path.Combine(gameDir, "mods");
            if (!Directory.Exists(source)) return;
            var target = Path.Combine(serverDir, "mods");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "*.jar"))
            {
                var dest = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(dest)) File.Copy(file, dest);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("复制服务端 Mod 失败：" + ex.Message);
        }
    }

    public async Task<bool> TestLocalPortAsync(CancellationToken ct = default)
    {
        if (!IsRunning) return false;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(System.Net.IPAddress.Loopback, Port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    private static void StopStaleServerProcesses(string serverDir)
    {
        try
        {
            var pidFile = Path.Combine(serverDir, ".server.pid");
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var savedPid))
            {
                try { Process.GetProcessById(savedPid).Kill(true); } catch { }
                try { File.Delete(pidFile); } catch { }
                Thread.Sleep(500);
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='java.exe'");
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                var command = item["CommandLine"]?.ToString() ?? "";
                if (!command.Contains(serverDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(item["ProcessId"]?.ToString(), out var pid))
                {
                    try { Process.GetProcessById(pid).Kill(true); } catch { }
                }
            }
            Thread.Sleep(500);
        }
        catch (Exception ex)
        {
            Log.Warn("清理残留服务端失败：" + ex.Message);
        }
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("stop"); } catch { }
                await Task.Run(() => process.WaitForExit(8000));
            }
            if (!process.HasExited) process.Kill(true);
        }
        catch { }
        try { process.Dispose(); } catch { }
        if (ServerDirectory is not null)
            try { File.Delete(Path.Combine(ServerDirectory, ".server.pid")); } catch { }
        await LauncherApp.I.Upnp.UnmapAsync();
        PublicAddress = null;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
