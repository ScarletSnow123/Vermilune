using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Vermilune.Core;

/// <summary>便携优先的路径约定。任何写入失败都会回退到 LocalAppData，避免 Program Files 下无法保存。</summary>
public static class AppPaths
{
    public static string ExeDir { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string ExePath { get; } = Path.Combine(ExeDir, "Vermilune.exe");
    public static string RefuseDir { get; } = Path.Combine(ExeDir, "Vermilune");
    public static string DataDir { get; private set; } = Path.Combine(ExeDir, "Vermilune");
    public static string SettingsFile => Path.Combine(DataDir, "launcher.json");
    public static string InstanceSettingsDir => Path.Combine(DataDir, "instances");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string CrashDir => Path.Combine(DataDir, "reports");
    public static string SkinCacheDir => Path.Combine(CacheDir, "skins");
    public static bool IsPortable { get; private set; } = true;

    public static void EnsureInitialized()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var probe = Path.Combine(DataDir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            IsPortable = true;
        }
        catch
        {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vermilune");
            IsPortable = false;
        }

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(InstanceSettingsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(CrashDir);
        Directory.CreateDirectory(SkinCacheDir);
    }

    public static string DefaultMinecraftDir()
    {
        var local = Path.Combine(ExeDir, ".minecraft");
        if (Directory.Exists(local)) return local;
        var parent = Path.GetFullPath(Path.Combine(ExeDir, ".."));
        var parentMc = Path.Combine(parent, ".minecraft");
        if (Directory.Exists(parentMc)) return parentMc;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    }

    public static string VersionsDir(string mcDir) => Path.Combine(mcDir, "versions");
    public static string LibrariesDir(string mcDir) => Path.Combine(mcDir, "libraries");
    public static string AssetsDir(string mcDir) => Path.Combine(mcDir, "assets");

    /// <summary>把任意版本名转换为可安全用作文件名的形式，并保证稳定。</summary>
    public static string SafeKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length == 0) s = "unnamed";
        if (s.Length > 90)
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(name)))[..8];
            s = s[..72] + "-" + hash;
        }
        return s;
    }

    public static string InstanceSettingsFile(string versionId) =>
        Path.Combine(InstanceSettingsDir, SafeKey(versionId) + ".json");

    public static string ToDisplayPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && full.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                return "~" + full[home.Length..];
            return full;
        }
        catch { return path; }
    }
}
