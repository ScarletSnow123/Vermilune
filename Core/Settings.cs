using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vermilune.Core;

public sealed class AccountSettings
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "offline"; // offline | microsoft | yggdrasil
    public string Name { get; set; } = "Player";
    public string Uuid { get; set; } = "";
    public string SkinUrl { get; set; } = "";
    public string CapeUrl { get; set; } = "";
    public bool CapeEnabled { get; set; }
    public string SkinModel { get; set; } = "classic";
    public DateTime LastSkinUploadUtc { get; set; }
    public string Xuid { get; set; } = "";
    public string YggdrasilServer { get; set; } = "";
    public string YggdrasilLoginName { get; set; } = "";
    public string NideServer { get; set; } = "";
    public string NideLoginName { get; set; } = "";
    public List<YggdrasilCharacter> YggdrasilCharacters { get; set; } = new();
    public string RefreshTokenProtected { get; set; } = "";
    public string ClientTokenProtected { get; set; } = "";
    public string AccessTokenProtected { get; set; } = "";
    public DateTime AccessTokenExpiresUtc { get; set; }
    public DateTime LastLoginUtc { get; set; }

    [JsonIgnore] public bool IsMicrosoft => string.Equals(Type, "microsoft", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsYggdrasil => string.Equals(Type, "yggdrasil", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsNide => string.Equals(Type, "nide", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsOnline => IsMicrosoft || IsYggdrasil || IsNide;
    [JsonIgnore] public bool HasCachedSession => IsMicrosoft && !string.IsNullOrEmpty(RefreshTokenProtected);

    public AccountSettings Clone() => (AccountSettings)MemberwiseClone();
}

public sealed class RecentEntry
{
    public string Id { get; set; } = "";
    public DateTime LastPlayedUtc { get; set; }
}

public sealed class HomeCardOffset
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class HomeCardStyle
{
    public double Rotation { get; set; }
    public double Scale { get; set; } = 1;
    public double FontScale { get; set; } = 1;
    public double Opacity { get; set; } = 1;
}

public sealed class HomeWidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "clock";
    public double X { get; set; } = 420;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 280;
    public string Data { get; set; } = "";
    public bool Visible { get; set; } = true;
}

public sealed class AppSettings
{
    public string MinecraftDir { get; set; } = "";
    public string LastVersion { get; set; } = "";
    public string Accent { get; set; } = "#8FD6B4";
    /// <summary>头像渲染风格：minimal（正面）或 side（侧面）。</summary>
    public string AvatarStyle { get; set; } = "minimal";
    /// <summary>界面底色不透明度，0.25 - 1.0。</summary>
    public double UiOpacity { get; set; } = 0.96;
    public string MultiplayerRoom { get; set; } = "";
    public string MultiplayerSecret { get; set; } = "";
    public string MultiplayerIp { get; set; } = "10.126.126.1";
    public string MultiplayerPublicNode { get; set; } = "tcp://easytier-us.slarker.me:11010,tcp://225284.xyz:11010";
    public int ServerMemoryMb { get; set; } = 2048;
    public int ServerPort { get; set; } = 25565;
    public bool UseFrp { get; set; }
    public string FrpServer { get; set; } = "";
    public int FrpServerPort { get; set; } = 7000;
    public string FrpToken { get; set; } = "";
    public int FrpRemotePort { get; set; } = 25565;

    public string BackgroundMode { get; set; } = "default"; // default | custom | wallpaper
    public string BackgroundImage { get; set; } = "";
    public double BackgroundImageOpacity { get; set; } = 0.34;
    /// <summary>动画强度：off / smooth / fancy。</summary>
    public string AnimationMode { get; set; } = "smooth";

    /// <summary>页面切换时使用 BitmapCache / 模糊等高级效果；默认关闭以降低显存和内存占用。</summary>
    public bool EnableVisualEffects { get; set; }
    /// <summary>高级模糊过渡时长，0 - 1000ms，默认 420ms。</summary>
    public int BlurDelayMs { get; set; } = 420;
    public string FeaturedImage { get; set; } = "";
    public string FeaturedTitle { get; set; } = "Vermilune 1.3分支";
    public string FeaturedSubtitle { get; set; } = "这个卡片可以自定义哦！";
    public string FeaturedTitleColor { get; set; } = "";
    public string FeaturedSubtitleColor { get; set; } = "";
    public bool ShowHomeQuick { get; set; } = true;
    public bool ShowHomeRecent { get; set; } = true;
    public bool ShowHomeSkin { get; set; } = true;
    public bool ShowHomeLog { get; set; } = true;
    public bool ShowHomeFeatured { get; set; } = true;
    public bool ShowHomeStats { get; set; } = true;
    public bool ShowHome3D { get; set; } = true;
    public bool ShowCardBorders { get; set; } = true;
    public Dictionary<string, HomeCardOffset> HomeOffsets { get; set; } = new();
    public List<HomeWidgetConfig> HomeWidgets { get; set; } = new();
    public Dictionary<string, HomeCardStyle> HomeStyles { get; set; } = new();
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public bool VersionIsolation { get; set; } = true;
    public bool CloseAfterLaunch { get; set; }
    public string GameWindowTitle { get; set; } = "";
    public string CustomInfo { get; set; } = "";
    public string LauncherVisibility { get; set; } = "keep";
    public string ProcessPriority { get; set; } = "normal";
    public string WindowSize { get; set; } = "default";
    public string IpProtocol { get; set; } = "default";
    public bool ShowLogPanel { get; set; } = true;
    public bool OptimizeJvm { get; set; } = true;
    public string DownloadSource { get; set; } = "auto"; // auto | official | bmclapi
    public string CurseForgeApiKey { get; set; } = Environment.GetEnvironmentVariable("VERMILUNE_CURSEFORGE_API_KEY") ?? "";
    public string CurseForgeProxyUrl { get; set; } = "";
    public string CurseForgeProxyToken { get; set; } = "";
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
    /// <summary>仅个人内部构建显示提醒，不影响公开分发包。</summary>
    public bool ShowInternalBanner { get; set; }
    public List<RecentEntry> Recent { get; set; } = new();
    public List<AccountSettings> Accounts { get; set; } = new();
    public AccountSettings Account { get; set; } = new();

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(MinecraftDir)) MinecraftDir = AppPaths.DefaultMinecraftDir();
        if (string.IsNullOrWhiteSpace(Accent) || !Accent.StartsWith('#')) Accent = "#8FD6B4";
        if (FeaturedTitle == "Vermilune 1.2分支") FeaturedTitle = "Vermilune 1.3分支";
        if (AnimationMode is not ("off" or "smooth" or "fancy")) AnimationMode = "smooth";
        if (string.IsNullOrWhiteSpace(AvatarStyle)) AvatarStyle = "minimal";
        if (!string.Equals(AvatarStyle, "side", StringComparison.OrdinalIgnoreCase)) AvatarStyle = "minimal";
        HomeOffsets ??= new();
        HomeWidgets ??= new();
        HomeStyles ??= new();
        UiOpacity = Math.Clamp(UiOpacity, 0.25, 1.0);
        if (BackgroundMode is not ("default" or "custom" or "wallpaper"))
            BackgroundMode = string.IsNullOrWhiteSpace(BackgroundImage) || BackgroundImage == "none" ? "default" : "custom";
        if (BackgroundMode == "custom" && (string.IsNullOrWhiteSpace(BackgroundImage) || BackgroundImage == "none"))
            BackgroundMode = "default";
        ServerMemoryMb = Math.Clamp(ServerMemoryMb, 1024, 16384);
        ServerPort = Math.Clamp(ServerPort, 1024, 65535);
        FrpServerPort = Math.Clamp(FrpServerPort, 1, 65535);
        FrpRemotePort = Math.Clamp(FrpRemotePort, 1, 65535);
        BackgroundImageOpacity = Math.Clamp(BackgroundImageOpacity, 0.0, 1.0);
        CurseForgeApiKey = (CurseForgeApiKey ?? "").Trim();
        CurseForgeProxyUrl = (CurseForgeProxyUrl ?? "").Trim().TrimEnd('/');
        CurseForgeProxyToken = (CurseForgeProxyToken ?? "").Trim();
        MemoryMb = Math.Clamp(MemoryMb, 512, 65536);
        WindowWidth = Math.Clamp(WindowWidth, 920, 4096);
        WindowHeight = Math.Clamp(WindowHeight, 560, 2400);
        FeaturedTitle = (FeaturedTitle ?? "").Trim();
        if (FeaturedTitle.Length > 48) FeaturedTitle = FeaturedTitle[..48];
        FeaturedSubtitle = (FeaturedSubtitle ?? "").Trim();
        if (FeaturedSubtitle.Length > 96) FeaturedSubtitle = FeaturedSubtitle[..96];
        // 兼容旧的默认卡片，换成新的 Vermilune 默认文案。
        if (FeaturedTitle == "←----觉真可爱" && FeaturedImage == "")
        {
            FeaturedTitle = "Vermilune 1.3分支";
            FeaturedSubtitle = "这个卡片可以自定义哦！";
            FeaturedTitleColor = "";
            FeaturedSubtitleColor = "";
        }
        FeaturedTitleColor = NormalizeColor(FeaturedTitleColor, "");
        FeaturedSubtitleColor = NormalizeColor(FeaturedSubtitleColor, "");
        BlurDelayMs = Math.Clamp(BlurDelayMs, 360, 1000);
        GameWindowTitle = (GameWindowTitle ?? "").Trim();
        if (GameWindowTitle.Length > 64) GameWindowTitle = GameWindowTitle[..64];
        CustomInfo = (CustomInfo ?? "").Trim();
        if (CustomInfo.Length > 64) CustomInfo = CustomInfo[..64];
        LauncherVisibility = LauncherVisibility switch
        {
            "hide" => "hide",
            "close" => "close",
            _ => "keep"
        };
        ProcessPriority = ProcessPriority switch
        {
            "high" => "high",
            "below" => "below",
            _ => "normal"
        };
        WindowSize = WindowSize switch
        {
            "1280x720" or "1600x900" or "1920x1080" => WindowSize,
            _ => "default"
        };
        IpProtocol = IpProtocol switch
        {
            "ipv4" => "ipv4",
            "ipv6" => "ipv6",
            _ => "default"
        };
        if (CloseAfterLaunch && LauncherVisibility == "keep") LauncherVisibility = "close";
        if (Recent is null) Recent = new();
        if (Recent.Count > 24) Recent = Recent.OrderByDescending(r => r.LastPlayedUtc).Take(24).ToList();
        Account ??= new AccountSettings();
        if (string.IsNullOrEmpty(Account.Id)) Account.Id = Guid.NewGuid().ToString("N");
        if (Accounts is null) Accounts = new();
        foreach (var saved in Accounts)
            if (string.IsNullOrEmpty(saved.Id)) saved.Id = Guid.NewGuid().ToString("N");
        if (Accounts.Count == 0) Accounts.Add(Account.Clone());
        else if (Accounts.All(a => !string.Equals(a.Id, Account.Id, StringComparison.OrdinalIgnoreCase)))
            Accounts.Add(Account.Clone());
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length is not (7 or 9)) return fallback;
        return text.Skip(1).All(Uri.IsHexDigit) ? text.ToUpperInvariant() : fallback;
    }

    public void TouchRecent(string versionId)
    {        Recent.RemoveAll(r => string.Equals(r.Id, versionId, StringComparison.OrdinalIgnoreCase));
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
