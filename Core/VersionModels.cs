using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vermilune.Core;

public enum LoaderKind { Vanilla, Forge, NeoForge, Fabric, Quilt, Unknown }

public sealed class VersionInfo
{
    public string Id { get; init; } = "";
    public string Folder { get; init; } = "";
    public string JsonPath { get; init; } = "";
    public string Type { get; set; } = "release";
    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;
    public string LoaderName { get; set; } = "原版";
    public string MinecraftVersion { get; set; } = "";
    public string MainClass { get; set; } = "";
    public int JavaMajor { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public int ModCount { get; set; }
    public string DisplayName { get; set; } = "";
    public bool HasJson => !string.IsNullOrEmpty(JsonPath) && File.Exists(JsonPath);

    public string IconPath
    {
        get
        {
            if (string.IsNullOrEmpty(Folder) || !Directory.Exists(Folder)) return "";
            foreach (var name in new[] { "icon.png", "icon.jpg", "icon.jpeg", "logo.png", "logo.jpg", Id + ".png", Id + ".jpg" })
            {
                var path = Path.Combine(Folder, name);
                if (File.Exists(path)) return path;
            }
            try
            {
                var gameRoot = Path.GetFullPath(Path.Combine(Folder, "..", ".."));
                foreach (var name in new[] { "icon.png", "icon.jpg", "icon.jpeg" })
                {
                    var path = Path.Combine(gameRoot, name);
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
            return "";
        }
    }

    public string LoaderBadge => Loader switch
    {
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        _ => "Vanilla"
    };

    public string Initial => Loader switch
    {
        LoaderKind.Forge => "F",
        LoaderKind.NeoForge => "N",
        LoaderKind.Fabric => "Fa",
        LoaderKind.Quilt => "Q",
        _ => "V"
    };

    public string SubLine
    {
        get
        {
            var mc = string.IsNullOrWhiteSpace(MinecraftVersion) ? "?" : MinecraftVersion;
            var mods = ModCount > 0 ? $" · {ModCount} Mods" : "";
            return $"{mc} · {LoaderBadge}{mods}";
        }
    }
}

public sealed class LibraryEntry
{
    public string Name { get; init; } = "";
    public string ArtifactKey { get; init; } = "";
    public string Path { get; init; } = "";
    public string Url { get; init; } = "";
    public List<string> FallbackUrls { get; init; } = new();
    public string Sha1 { get; init; } = "";
    public long Size { get; init; }
    public bool IsNative { get; init; }
    public string NativeClassifier { get; init; } = "";
    public string ExtractExclude { get; init; } = "";
    public JsonArray? Rules { get; init; }
    public string? NativeOsKey { get; init; }

    public IEnumerable<string> AllUrls()
    {
        if (!string.IsNullOrEmpty(Url)) yield return Url;
        foreach (var u in FallbackUrls) yield return u;
    }
}

public sealed class LoggingConfig
{
    public string Argument { get; init; } = "";
    public string FileId { get; init; } = "";
    public string FileUrl { get; init; } = "";
    public string FileSha1 { get; init; } = "";
    public long FileSize { get; init; }
}

public sealed class ResolvedVersion
{
    public string Id { get; init; } = "";
    public string BaseId { get; init; } = "";
    public string MainClass { get; init; } = "";
    public string Type { get; init; } = "release";
    public string AssetsId { get; init; } = "";
    public string AssetIndexUrl { get; init; } = "";
    public string AssetIndexSha1 { get; init; } = "";
    public string AssetIndexPath { get; init; } = "";
    public string ClientJarPath { get; init; } = "";
    public string ClientJarUrl { get; init; } = "";
    public string ClientJarSha1 { get; init; } = "";
    public int JavaMajor { get; init; } = 8;
    public string MinecraftVersion { get; init; } = "";
    public LoaderKind Loader { get; init; }
    public List<LibraryEntry> Libraries { get; init; } = new();
    public List<JsonNode?> GameArguments { get; init; } = new();
    public List<JsonNode?> JvmArguments { get; init; } = new();
    public string LegacyArguments { get; init; } = "";
    public LoggingConfig? Logging { get; init; }
    public List<string> Chain { get; init; } = new();
    public bool HasCustomResolutionArg { get; set; }

    public string MinecraftVersionShort
    {
        get
        {
            if (!string.IsNullOrEmpty(MinecraftVersion)) return MinecraftVersion;
            var m = Regex.Match(Id, @"\d+\.\d+(\.\d+)?");
            return m.Success ? m.Value : Id;
        }
    }
}

public sealed class LaunchFeatures
{
    public bool IsDemoUser { get; set; }
    public bool HasCustomResolution { get; set; }
    public bool HasQuickPlaysSupport { get; set; }
    public bool IsQuickPlaySingleplayer { get; set; }
    public bool IsQuickPlayMultiplayer { get; set; }
    public bool IsQuickPlayRealms { get; set; }
}

public sealed class LaunchOptions
{
    public VersionInfo Version { get; init; } = new();
    public ResolvedVersion Resolved { get; init; } = null!;
    public InstanceSettings Instance { get; init; } = new();
    public AccountSettings Account { get; init; } = new();
    public string MinecraftDir { get; init; } = "";
    public string GameDir { get; init; } = "";
    public string NativesDir { get; init; } = "";
    public string JavaPath { get; init; } = "";
    public int MemoryMb { get; init; } = 4096;
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Fullscreen { get; init; }
    public bool OptimizeJvm { get; init; } = true;
    public string ExtraJvmArgs { get; init; } = "";
    public string ExtraGameArgs { get; init; } = "";
    public string LauncherName { get; init; } = "Vermilune";
    public string LauncherVersion { get; init; } = "1.2.9-beta";
}

public static class RuleEvaluator
{
    public static bool IsAllowed(JsonArray? rules, LaunchFeatures features)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) continue;
            var action = rule["action"]?.GetValue<string>() ?? "allow";
            if (!Matches(rule, features)) continue;
            allowed = action == "allow";
        }
        return allowed;
    }

    private static bool Matches(JsonObject rule, LaunchFeatures features)
    {
        if (rule["features"] is JsonObject feat)
        {
            foreach (var kv in feat)
            {
                var expected = kv.Value?.GetValue<bool>() ?? false;
                var actual = kv.Key switch
                {
                    "is_demo_user" => features.IsDemoUser,
                    "has_custom_resolution" => features.HasCustomResolution,
                    "has_quick_plays_support" => features.HasQuickPlaysSupport,
                    "is_quick_play_singleplayer" => features.IsQuickPlaySingleplayer,
                    "is_quick_play_multiplayer" => features.IsQuickPlayMultiplayer,
                    "is_quick_play_realms" => features.IsQuickPlayRealms,
                    _ => false
                };
                if (actual != expected) return false;
            }
        }

        if (rule["os"] is JsonObject os)
        {
            var name = os["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name))
            {
                var current = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
                if (!string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) return false;
            }
            var arch = os["arch"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(arch))
            {
                var currentArch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
                if (!string.Equals(arch, currentArch, StringComparison.OrdinalIgnoreCase) &&
                    !(arch == "x86" && !Environment.Is64BitOperatingSystem))
                    return false;
            }
            var version = os["version"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(version))
            {
                try
                {
                    if (!Regex.IsMatch(Environment.OSVersion.Version.ToString(), version)) return false;
                }
                catch { }
            }
        }
        return true;
    }
}
