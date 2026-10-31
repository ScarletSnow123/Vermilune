using System.Diagnostics;
using System.IO;
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
        psi.ArgumentList.Add(installer);
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
            throw new TimeoutException("Forge 部署器超过 10 分钟未结束，已终止。");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Forge 部署器退出码 " + process.ExitCode + "：\n" + Tail(log.ToString()));

        var targetId = FindInstalledVersion(mcDir, mcVersion, forgeVersion, start);
        if (string.IsNullOrEmpty(targetId))
        {
            // 某些 Forge 版本使用不同的文件夹名，退而查找最新修改的 Forge 版本
            targetId = FindAnyNewForgeVersion(mcDir, start);
        }
        if (string.IsNullOrEmpty(targetId))
            throw new InvalidOperationException("Forge 部署器已结束，但没有找到新生成的版本。请打开 versions 目录检查安装日志。");
        progress?.Report(new DownloadProgress { Stage = "Forge 部署完成", Current = 1, Total = 1, Detail = targetId });
        return targetId;
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
