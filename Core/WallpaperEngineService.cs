using System.IO;
using System.Text.Json.Nodes;
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
                VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return full;

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
                             VideoExtensions.Contains(mediaExt, StringComparer.OrdinalIgnoreCase)))
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
}
