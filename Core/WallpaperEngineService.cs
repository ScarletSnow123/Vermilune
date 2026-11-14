using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Vermilune.Core;

/// <summary>
/// 尽力读取 Wallpaper Engine 当前选中的壁纸。
/// 场景壁纸无法直接渲染时回退到项目目录里的 preview 图片。
/// </summary>
public static class WallpaperEngineService
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    private static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mkv", ".mov", ".m4v" };
    private static readonly string[] WebExtensions = { ".html", ".htm" };
    private static readonly HashSet<string> Extracting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>场景壁纸的高清静帧提取完成后触发，参数是图片路径。</summary>
    public static event Action<string>? HighQualityPreviewReady;

    public static string? GetCurrentWallpaper()
    {
        var config = FindConfig();
        if (config is null) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(config)) as JsonObject;
            var selected = FindSelected(root);
            if (selected is null) return null;
            foreach (var monitor in selected)
            {
                if (monitor.Value is not JsonObject info) continue;
                var file = info["file"]?.GetValue<string>() ?? "";
                var resolved = Resolve(file);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取 Wallpaper Engine 配置失败：" + ex.Message);
        }
        return null;
    }

    public static string? FindConfig()
    {
        var env = Environment.GetEnvironmentVariable("WE_CONFIG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        // Wallpaper Engine 可以装在任意 Steam 库或自定义目录，优先从正在运行的进程定位。
        foreach (var processName in new[] { "wallpaper32", "wallpaper64" })
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        var exe = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(exe)) continue;
                        var config = Path.Combine(Path.GetDirectoryName(exe)!, "config.json");
                        if (File.Exists(config)) return config;
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }
        }

        var steamRoots = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path)) steamRoots.Add(path.Replace('/', '\\'));
        }
        catch { }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(path)) steamRoots.Add(path);
        }
        catch { }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
            steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam"));
        }

        // Steam 库可能不在默认目录，libraryfolders.vdf 里记录了实际路径。
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            try
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (var line in File.ReadLines(vdf))
                {
                    var match = Regex.Match(line, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    var library = match.Groups[1].Value.Replace("\\\\", "\\");
                    var candidate = Path.Combine(library, "steamapps", "common", "wallpaper_engine", "config.json");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }

        // Steam 卸载项通常记录了 Wallpaper Engine 的实际安装目录。
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var uninstallPath in new[]
                             {
                                 @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 431960",
                                 @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Wallpaper Engine"
                             })
                    {
                        using var key = baseKey.OpenSubKey(uninstallPath);
                        var location = key?.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(location)) continue;
                        var candidate = Path.Combine(location, "config.json");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch { }
            }
        }

        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(root, "steamapps", "common", "wallpaper_engine", "config.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static JsonObject? FindSelected(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        if (obj["selectedwallpapers"] is JsonObject selected) return selected;
        foreach (var pair in obj)
        {
            var found = FindSelected(pair.Value);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return null;
            var ext = Path.GetExtension(full);
            if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ||
                VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ||
                WebExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return full;

            if (string.Equals(ext, ".pkg", StringComparison.OrdinalIgnoreCase))
            {
                if (LauncherApp.I.Settings.WallpaperSceneAnimated)
                {
                    var dir0 = Path.GetDirectoryName(full)!;
                    foreach (var preview in new[] { "preview.gif", "preview.webp", "preview.jpg", "preview.png" })
                    {
                        var found = Path.Combine(dir0, preview);
                        if (File.Exists(found)) return found;
                    }
                }
                var cached = FindCachedPreview(full);
                if (cached is not null) return cached;
                QueuePackageExtraction(full);
                var dir = Path.GetDirectoryName(full)!;
                foreach (var preview in new[] { "preview.jpg", "preview.png", "preview.gif", "preview.webp" })
                {
                    var found = Path.Combine(dir, preview);
                    if (File.Exists(found)) return found;
                }
            }

            if (string.Equals(Path.GetFileName(full), "project.json", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(full)!;
                try
                {
                    var project = JsonNode.Parse(File.ReadAllText(full)) as JsonObject;
                    var file = project?["file"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(file))
                    {
                        var media = Path.Combine(dir, file);
                        var mediaExt = Path.GetExtension(media);
                        if (File.Exists(media) &&
                            (ImageExtensions.Contains(mediaExt, StringComparer.OrdinalIgnoreCase) ||
                             VideoExtensions.Contains(mediaExt, StringComparer.OrdinalIgnoreCase) ||
                             WebExtensions.Contains(mediaExt, StringComparer.OrdinalIgnoreCase)))
                            return media;
                    }
                }
                catch { }
                foreach (var preview in new[] { "preview.jpg", "preview.png", "preview.gif", "preview.webp" })
                {
                    var found = Path.Combine(dir, preview);
                    if (File.Exists(found)) return found;
                }
            }
        }
        catch { }
        return null;
    }

    private static string CacheDirectory(string pkg)
    {
        try
        {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(pkg + "|" + File.GetLastWriteTimeUtc(pkg).Ticks)));
            return Path.Combine(AppPaths.CacheDir, "WallpaperPreviews", key[..16]);
        }
        catch { return Path.Combine(AppPaths.CacheDir, "WallpaperPreviews", "fallback"); }
    }

    private static string? FindCachedPreview(string pkg)
    {
        try
        {
            var dir = CacheDirectory(pkg);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "wallpaper.*").FirstOrDefault();
        }
        catch { return null; }
    }

    private static void QueuePackageExtraction(string pkg)
    {
        lock (Extracting)
        {
            if (!Extracting.Add(pkg)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var image = await ExtractHighQualityPreviewAsync(pkg);
                if (!string.IsNullOrWhiteSpace(image)) HighQualityPreviewReady?.Invoke(image);
            }
            catch (Exception ex)
            {
                Log.Warn("提取场景壁纸预览失败：" + ex.Message);
            }
            finally
            {
                lock (Extracting) Extracting.Remove(pkg);
            }
        });
    }

    private static async Task<string?> ExtractHighQualityPreviewAsync(string pkg)
    {
        var repkg = await EnsureRepkgAsync();
        if (string.IsNullOrWhiteSpace(repkg)) return null;
        var temp = Path.Combine(AppPaths.CacheDir, "WallpaperPreviews", "tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var psi = new ProcessStartInfo
            {
                FileName = repkg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(repkg)!
            };
            psi.ArgumentList.Add("extract");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(temp);
            psi.ArgumentList.Add(pkg);
            using var process = Process.Start(psi);
            if (process is null) return null;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) return null;

            string? best = null;
            long bestArea = 0;
            foreach (var file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    using var stream = File.OpenRead(file);
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                        System.Windows.Media.Imaging.BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    var area = (long)frame.PixelWidth * frame.PixelHeight;
                    if (area <= bestArea) continue;
                    bestArea = area;
                    best = file;
                }
                catch { }
            }
            if (best is null || bestArea < 1280L * 720L) return null;

            var cacheDir = CacheDirectory(pkg);
            Directory.CreateDirectory(cacheDir);
            foreach (var old in Directory.EnumerateFiles(cacheDir, "wallpaper.*")) File.Delete(old);
            var target = Path.Combine(cacheDir, "wallpaper" + Path.GetExtension(best).ToLowerInvariant());
            File.Copy(best, target, true);
            return target;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static async Task<string?> EnsureRepkgAsync()
    {
        var dir = Path.Combine(AppPaths.DataDir, "Tools", "RePKG");
        var exe = Path.Combine(dir, "RePKG.exe");
        if (File.Exists(exe)) return exe;
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(AppPaths.CacheDir, "RePKG.zip");
        var urls = new[]
        {
            "https://github.com/notscuffed/repkg/releases/download/v0.4.0-alpha/RePKG.zip",
            "https://ghproxy.net/https://github.com/notscuffed/repkg/releases/download/v0.4.0-alpha/RePKG.zip"
        };
        foreach (var url in urls)
        {
            try
            {
                if (!File.Exists(zip) || new FileInfo(zip).Length < 100_000)
                    await HttpService.DownloadFileAsync(url, zip, "", null, CancellationToken.None, 2);
                var temp = Path.Combine(AppPaths.CacheDir, "repkg-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    ZipFile.ExtractToDirectory(zip, temp, true);
                    var found = Directory.EnumerateFiles(temp, "RePKG.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found is null) throw new InvalidOperationException("RePKG 压缩包里没有 RePKG.exe");
                    File.Copy(found, exe, true);
                    return exe;
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
            catch (Exception ex)
            {
                Log.Warn("下载 RePKG 失败：" + ex.Message);
                try { File.Delete(zip); } catch { }
            }
        }
        return null;
    }

}
