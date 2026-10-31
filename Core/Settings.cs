using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vermilune.Core;

public sealed class AccountSettings
{
    public string Type { get; set; } = "offline"; // offline | microsoft | yggdrasil
    public string Name { get; set; } = "Player";
    public string Uuid { get; set; } = "";
    public string SkinUrl { get; set; } = "";
    public string CapeUrl { get; set; } = "";
    public string SkinModel { get; set; } = "classic";
    public string Xuid { get; set; } = "";
    public string YggdrasilServer { get; set; } = "";
    public string RefreshTokenProtected { get; set; } = "";
    public string ClientTokenProtected { get; set; } = "";
    public string AccessTokenProtected { get; set; } = "";
    public DateTime AccessTokenExpiresUtc { get; set; }
    public DateTime LastLoginUtc { get; set; }

    [JsonIgnore] public bool IsMicrosoft => string.Equals(Type, "microsoft", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsYggdrasil => string.Equals(Type, "yggdrasil", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsOnline => IsMicrosoft || IsYggdrasil;
    [JsonIgnore] public bool HasCachedSession => IsMicrosoft && !string.IsNullOrEmpty(RefreshTokenProtected);
}

public sealed class RecentEntry
{
    public string Id { get; set; } = "";
    public DateTime LastPlayedUtc { get; set; }
}

public sealed class AppSettings
{
    public string MinecraftDir { get; set; } = "";
    public string LastVersion { get; set; } = "";
    public string Accent { get; set; } = "#E5B567";
    /// <summary>界面底色不透明度，0.25 - 1.0。</summary>
    public double UiOpacity { get; set; } = 0.96;
    public string BackgroundImage { get; set; } = "";
    public double BackgroundImageOpacity { get; set; } = 0.34;
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public bool VersionIsolation { get; set; } = true;
    public bool CloseAfterLaunch { get; set; }
    public bool ShowLogPanel { get; set; } = true;
    public bool OptimizeJvm { get; set; } = true;
    public string DownloadSource { get; set; } = "auto"; // auto | official | bmclapi
    public string CurseForgeApiKey { get; set; } = Environment.GetEnvironmentVariable("VERMILUNE_CURSEFORGE_API_KEY") ?? "";
    public const string DefaultMicrosoftClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
    public string MicrosoftClientId { get; set; } = Environment.GetEnvironmentVariable("VERMILUNE_MS_CLIENT_ID") ?? DefaultMicrosoftClientId;
    public string SkinPath { get; set; } = "";
    public string CapePath { get; set; } = "";
    public bool SkinSlim { get; set; }
    public bool? SkinModelOverride { get; set; }
    public string UiThemeMode { get; set; } = "dark";
    public bool AutoAnalyzeCrash { get; set; } = true;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool Maximized { get; set; }
    public bool FirstRun { get; set; } = true;
    public List<RecentEntry> Recent { get; set; } = new();
    public AccountSettings Account { get; set; } = new();

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(MinecraftDir)) MinecraftDir = AppPaths.DefaultMinecraftDir();
        if (string.IsNullOrWhiteSpace(Accent) || !Accent.StartsWith('#')) Accent = "#E5B567";
        UiOpacity = Math.Clamp(UiOpacity, 0.25, 1.0);
        BackgroundImageOpacity = Math.Clamp(BackgroundImageOpacity, 0.0, 1.0);
        MemoryMb = Math.Clamp(MemoryMb, 512, 65536);
        WindowWidth = Math.Clamp(WindowWidth, 1080, 4096);
        WindowHeight = Math.Clamp(WindowHeight, 680, 2400);
        if (Recent is null) Recent = new();
        if (Recent.Count > 24) Recent = Recent.OrderByDescending(r => r.LastPlayedUtc).Take(24).ToList();
        Account ??= new AccountSettings();
    }

    public void TouchRecent(string versionId)
    {
        Recent.RemoveAll(r => string.Equals(r.Id, versionId, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, new RecentEntry { Id = versionId, LastPlayedUtc = DateTime.UtcNow });
        if (Recent.Count > 24) Recent.RemoveRange(24, Recent.Count - 24);
    }
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly object SaveLock = new();
    private static System.Threading.Timer? _debounce;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取配置失败，已使用默认配置");
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", true); } catch { }
        }

        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        lock (SaveLock)
        {
            try
            {
                settings.Normalize();
                Directory.CreateDirectory(AppPaths.DataDir);
                var tmp = AppPaths.SettingsFile + ".tmp";
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(tmp, json);
                File.Move(tmp, AppPaths.SettingsFile, true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存配置失败");
            }
        }
    }

    public static void SaveDebounced(AppSettings settings, int delayMs = 350)
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => Save(settings), null, delayMs, Timeout.Infinite);
    }
}
