using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class QuiltService
{
    private static readonly Dictionary<string, (List<ForgeVersionInfo> List, DateTime Time)> LoaderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LoaderLock = new();
    private (string Url, string Sha1, string Version)? _installer;

    public async Task<List<ForgeVersionInfo>> GetVersionsAsync(string mcVersion, CancellationToken ct)
    {
        lock (LoaderLock)
        {
            if (LoaderCache.TryGetValue(mcVersion, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(20))
                return cached.List.ToList();
        }

        var result = new List<ForgeVersionInfo>();
        try
        {
            var json = await HttpService.GetStringAsync(
                "https://meta.quiltmc.org/v3/versions/loader/" + Uri.EscapeDataString(mcVersion), ct);
            if (JsonNode.Parse(json) is JsonArray array)
            {
                foreach (var node in array.OfType<JsonObject>())
                {
                    var version = node["loader"]?["version"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(version)) continue;
                    if (result.Any(x => x.ForgeVersion == version)) continue;
                    result.Add(new ForgeVersionInfo { ForgeVersion = version });
                }
            }
        }
        catch (Exception ex) { Log.Warn("Quilt 版本列表获取失败：" + ex.Message); }

        result = result
            .OrderByDescending(x => !x.ForgeVersion.Contains("beta", StringComparison.OrdinalIgnoreCase) &&
                                    !x.ForgeVersion.Contains("rc", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.ForgeVersion)
            .Take(24)
            .ToList();
        if (result.Count > 0) result[0] = new ForgeVersionInfo { ForgeVersion = result[0].ForgeVersion, Latest = true };
        lock (LoaderLock) LoaderCache[mcVersion] = (result, DateTime.UtcNow);
        return result;
    }

    private async Task<(string Url, string Sha1, string Version)> ResolveInstallerAsync(CancellationToken ct)
    {
        if (_installer.HasValue) return _installer.Value;
        var json = await HttpService.GetStringAsync("https://meta.quiltmc.org/v3/versions/installer", ct);
        if (JsonNode.Parse(json) is not JsonArray array || array.Count == 0)
            throw new InvalidOperationException("Quilt 安装器版本列表为空。");
        var first = array.OfType<JsonObject>().FirstOrDefault();
        var url = first?["url"]?.GetValue<string>() ?? "";
        var version = first?["version"]?.GetValue<string>() ?? "";
        var sha1 = first?["hashes"]?["sha1"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("无法获取 Quilt 安装器下载地址。");
        var resolved = (url, sha1, version);
        _installer = resolved;
        return resolved;
    }

    public async Task<string> InstallAsync(string mcVersion, string quiltVersion, string mcDir, string javaPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var (url, sha1, installerVersion) = await ResolveInstallerAsync(ct);
        var dir = Path.Combine(AppPaths.CacheDir, "installers");
        Directory.CreateDirectory(dir);
        var installer = Path.Combine(dir, $"quilt-installer-{installerVersion}.jar");
        if (!File.Exists(installer) || new FileInfo(installer).Length < 1024 * 100)
        {
            progress?.Report(new DownloadProgress { Stage = "下载 Quilt 部署器", Detail = "Installer " + installerVersion });
            await HttpService.DownloadFileAsync(url, installer, sha1, null, ct, 3);
        }

        var versionsDir = Path.Combine(mcDir, "versions");
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(versionsDir))
            foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
                before.Add(Path.GetFileName(versionDir));

        progress?.Report(new DownloadProgress { Stage = "运行 Quilt 部署器", Detail = "Quilt Loader " + quiltVersion });
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = mcDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("client");
        psi.ArgumentList.Add(mcVersion);
        psi.ArgumentList.Add(quiltVersion);
        psi.ArgumentList.Add("--install-dir=" + mcDir);
        psi.ArgumentList.Add("--no-profile");

        using var process = new Process { StartInfo = psi };
        var log = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.AppendLine(e.Data); progress?.Report(new DownloadProgress { Stage = "Quilt 部署", Detail = Trim(e.Data) }); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (log) log.AppendLine(e.Data); progress?.Report(new DownloadProgress { Stage = "Quilt 部署", Detail = Trim(e.Data) }); } };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Quilt 部署器。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var exited = await Task.Run(() => process.WaitForExit(15 * 60 * 1000), ct);
        if (!exited)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException("Quilt 部署器超过 15 分钟未结束，已终止。");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Quilt 部署器退出码 " + process.ExitCode + "：\n" + Tail(log.ToString()));

        var candidate = Directory.Exists(versionsDir)
            ? Directory.EnumerateDirectories(versionsDir)
                .Select(Path.GetFileName)
                .Where(name => name is not null && !before.Contains(name) &&
                               File.Exists(Path.Combine(versionsDir, name, name + ".json")))
                .OrderByDescending(name => Directory.GetLastWriteTime(Path.Combine(versionsDir, name!)))
                .FirstOrDefault()
            : null;
        candidate ??= Directory.Exists(versionsDir)
            ? Directory.EnumerateDirectories(versionsDir)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null &&
                                        name.Contains("quilt", StringComparison.OrdinalIgnoreCase) &&
                                        File.Exists(Path.Combine(versionsDir, name, name + ".json")))
            : null;
        if (candidate is null)
            throw new InvalidOperationException("Quilt 部署器已结束，但没有找到生成的版本 JSON。");
        progress?.Report(new DownloadProgress { Stage = "Quilt 部署完成", Current = 1, Total = 1, Detail = candidate });
        return candidate;
    }

    private static string Trim(string text) => text.Length > 120 ? text[..120] : text;
    private static string Tail(string text) => text.Length > 2000 ? text[^2000..] : text;
}
