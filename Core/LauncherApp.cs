using System.IO;
using System.Text.Json;

namespace Vermilune.Core;

public sealed class LauncherApp
{
    public static LauncherApp I { get; } = new();

    public AppSettings Settings { get; private set; } = new();
    public VersionService Versions { get; private set; } = null!;
    public JavaService Java { get; } = new();
    public AccountService Auth { get; } = new();
    public SkinService Skins { get; } = new();
    public LaunchService Launch { get; } = new();
    public DownloadService Downloads { get; } = new();
    public ForgeService Forge { get; } = new();
    public QuiltService Quilt { get; } = new();
    public MinecraftServerService Server { get; } = new();
    public UpnpService Upnp { get; } = new();
    public FrpService Frp { get; } = new();
    public ModrinthService Modrinth { get; } = new();
    public CurseForgeService CurseForge { get; } = new();
    public CrashAnalysis? LastAnalysis { get; set; }
    public bool Initialized { get; private set; }
    public bool JavaScanning { get; private set; }
    public long TotalMemoryMb { get; private set; }

    public event Action? SettingsChanged;
    public event Action? VersionsChanged;
    public event Action? JavaScanned;
    public event Action<string, string>? Toast;
    private readonly TaskCompletionSource<bool> _javaReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string MinecraftDir => Settings.MinecraftDir;

    public async Task InitializeAsync()
    {
        AppPaths.EnsureInitialized();
        Settings = SettingsStore.Load();
        Settings.Normalize();
        HttpService.Source = Settings.DownloadSource;
        TotalMemoryMb = JavaService.TotalMemoryMb();
        Versions = new VersionService(Settings.MinecraftDir);
        CrashAnalyzer.MinecraftDir = Settings.MinecraftDir;
        Versions.Refresh();
        CleanupRecent();

        Initialized = true;
        _ = ScanJavaAsync();
        _ = RefreshAccountAsync();
        Log.Info($"初始化完成：Minecraft 目录 {Settings.MinecraftDir}，版本 {Versions.Versions.Count} 个");
        await Task.CompletedTask;
    }

    public async Task ScanJavaAsync()
    {
        if (JavaScanning) return;
        JavaScanning = true;
        try
        {
            await Java.ScanAsync(Settings.MinecraftDir, Settings.JavaPath);
        }
        catch (Exception ex) { Log.Error(ex, "扫描 Java 失败"); }
        finally
        {
            JavaScanning = false;
            _javaReady.TrySetResult(true);
            JavaScanned?.Invoke();
        }
    }

    public async Task EnsureJavaAsync()
    {
        if (!Java.HasScanned)
        {
            if (!JavaScanning) _ = ScanJavaAsync();
            await _javaReady.Task;
        }
    }

    public async Task RefreshAccountAsync()
    {
        var account = Settings.Account;
        if (account.IsYggdrasil || account.IsNide)
        {
            if (string.IsNullOrEmpty(account.AccessTokenProtected)) return;
            try
            {
                if (await YggdrasilService.UpdateTexturesAsync(account, CancellationToken.None))
                {
                    Save();
                    Log.Info("外置账号皮肤已刷新：" + account.Name);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("外置账号皮肤刷新失败：" + ex.Message);
            }
            SettingsChanged?.Invoke();
            return;
        }
        if (!account.IsMicrosoft) return;
        if (string.IsNullOrEmpty(account.RefreshTokenProtected)) return;

        // 每次启动刷新一次资料，外部网站换皮肤后这里才能拿到新 SkinUrl。
        try
        {
            if (await Auth.RefreshProfileAsync(CancellationToken.None))
            {
                Save();
                Log.Info("微软账号皮肤资料已刷新：" + account.Name);
            }
        }
        catch (Exception ex) { Log.Warn("微软账号皮肤资料刷新失败：" + ex.Message); }

        // 只有快过期或已过期才刷新令牌，避免每次启动都请求微软。
        if (account.AccessTokenExpiresUtc > DateTime.UtcNow.AddHours(2))
        {
            SettingsChanged?.Invoke();
            return;
        }
        try
        {
            var refresh = Dpapi.Unprotect(account.RefreshTokenProtected);
            if (string.IsNullOrWhiteSpace(refresh)) return;
            var profile = await Auth.RefreshAsync(refresh, CancellationToken.None);
            AccountService.ApplyProfile(account, profile);
            Save();
            Log.Info("微软账号令牌已在后台刷新：" + account.Name);
        }
        catch (Exception ex)
        {
            Log.Warn("微软账号后台刷新失败：" + ex.Message);
        }
        SettingsChanged?.Invoke();
    }

    public async Task<bool> RefreshMicrosoftSkinAsync()
    {
        var account = Settings.Account;
        if (!account.IsMicrosoft) return false;
        try
        {
            if (await Auth.RefreshProfileAsync(CancellationToken.None))
            {
                Save();
                return true;
            }
        }
        catch (Exception ex) { Log.Warn("刷新微软皮肤资料失败：" + ex.Message); }

        // 资料接口可能因为访问令牌过期失败，这时强制刷新一次令牌再取资料。
        try
        {
            var refresh = Dpapi.Unprotect(account.RefreshTokenProtected);
            if (string.IsNullOrWhiteSpace(refresh)) return false;
            var profile = await Auth.RefreshAsync(refresh, CancellationToken.None);
            AccountService.ApplyProfile(account, profile);
            Save();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("强制刷新微软皮肤失败：" + ex.Message);
            return false;
        }
    }

    public async Task<bool> EnsureAccountReadyAsync()
    {
        if (Settings.Account.IsYggdrasil || Settings.Account.IsNide)
        {
            if (string.IsNullOrEmpty(Settings.Account.AccessTokenProtected)) return false;
            if (Settings.Account.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return true;
            try
            {
                var wasNide = Settings.Account.IsNide;
                var nideServer = Settings.Account.NideServer;
                var profile = await YggdrasilService.RefreshAsync(Settings.Account, CancellationToken.None);
                YggdrasilService.Apply(Settings.Account, profile);
                if (wasNide)
                {
                    Settings.Account.Type = "nide";
                    Settings.Account.NideServer = nideServer;
                }
                Save();
                SettingsChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("刷新外置登录令牌失败：" + ex.Message);
                return false;
            }
        }
        if (!Settings.Account.IsMicrosoft) return true;
        if (Settings.Account.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(5) &&
            !string.IsNullOrEmpty(Settings.Account.AccessTokenProtected))
            return true;
        try
        {
            var refresh = Dpapi.Unprotect(Settings.Account.RefreshTokenProtected);
            if (string.IsNullOrWhiteSpace(refresh)) return false;
            var profile = await Auth.RefreshAsync(refresh, CancellationToken.None);
            AccountService.ApplyProfile(Settings.Account, profile);
            Save();
            SettingsChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("刷新微软令牌失败：" + ex.Message);
            return false;
        }
    }

    public void Save()
    {
        SyncActiveAccount();
        SettingsStore.Save(Settings);
    }

    public void SaveDebounced()
    {
        SyncActiveAccount();
        SettingsStore.SaveDebounced(Settings);
    }

    /// <summary>把当前账号写入已保存账号列表，供之后直接切换。</summary>
    public void RememberAccount(AccountSettings account)
    {
        if (string.IsNullOrEmpty(account.Id)) account.Id = Guid.NewGuid().ToString("N");
        Settings.Accounts ??= new List<AccountSettings>();
        var existing = Settings.Accounts.FirstOrDefault(a =>
            string.Equals(a.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = Settings.Accounts.FirstOrDefault(a => SameAccountIdentity(a, account));
        }
        if (existing is null)
        {
            Settings.Accounts.Add(account.Clone());
        }
        else
        {
            account.Id = existing.Id;
            var index = Settings.Accounts.IndexOf(existing);
            Settings.Accounts[index] = account.Clone();
        }
        Settings.Account = account.Clone();
    }

    public bool LogoutCurrentAccount()
    {
        var current = Settings.Account;
        if (!current.IsOnline) return false;
        Settings.Accounts ??= new List<AccountSettings>();
        Settings.Accounts.RemoveAll(a =>
            string.Equals(a.Id, current.Id, StringComparison.OrdinalIgnoreCase) || SameAccountIdentity(a, current));

        var next = Settings.Accounts.FirstOrDefault()
                   ?? new AccountSettings
                   {
                       Type = "offline",
                       Name = "Player",
                       Uuid = AccountService.OfflineUuid("Player")
                   };
        Settings.Account = next.Clone();
        Save();
        SettingsChanged?.Invoke();
        if (Settings.Account.IsYggdrasil || Settings.Account.IsNide) _ = RefreshAccountAsync();
        return true;
    }

    public bool SwitchAccount(string id)
    {
        var saved = Settings.Accounts?.FirstOrDefault(a =>
            string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (saved is null) return false;
        Settings.Account = saved.Clone();
        Save();
        SettingsChanged?.Invoke();
        if (Settings.Account.IsYggdrasil || Settings.Account.IsNide || Settings.Account.IsMicrosoft)
            _ = RefreshAccountAsync();
        return true;
    }

    private void SyncActiveAccount()
    {
        if (Settings.Account is null) return;
        if (string.IsNullOrEmpty(Settings.Account.Id)) Settings.Account.Id = Guid.NewGuid().ToString("N");
        Settings.Accounts ??= new List<AccountSettings>();
        var index = Settings.Accounts.FindIndex(a =>
            string.Equals(a.Id, Settings.Account.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Settings.Accounts[index] = Settings.Account.Clone();
        else Settings.Accounts.Add(Settings.Account.Clone());
    }

    private static bool SameAccountIdentity(AccountSettings a, AccountSettings b)
    {
        if (!string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.IsMicrosoft)
            return !string.IsNullOrEmpty(a.Uuid) && string.Equals(a.Uuid, b.Uuid, StringComparison.OrdinalIgnoreCase);
        if (a.IsYggdrasil)
            return !string.IsNullOrEmpty(a.Uuid) &&
                   string.Equals(a.Uuid, b.Uuid, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.YggdrasilServer, b.YggdrasilServer, StringComparison.OrdinalIgnoreCase);
        if (a.IsNide)
            return !string.IsNullOrEmpty(a.Uuid) &&
                   string.Equals(a.Uuid, b.Uuid, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.NideServer, b.NideServer, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    public void ReloadVersions()
    {
        Versions.Refresh();
        CleanupRecent();
        VersionsChanged?.Invoke();
    }

    public VersionInfo? CurrentVersion
    {
        get
        {
            var v = Versions.Find(Settings.LastVersion);
            if (v is not null) return v;
            v = Versions.Versions.FirstOrDefault();
            if (v is not null) Settings.LastVersion = v.Id;
            return v;
        }
    }

    public void SetCurrentVersion(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (string.Equals(Settings.LastVersion, id, StringComparison.OrdinalIgnoreCase)) return;
        Settings.LastVersion = id;
        Save();
        SettingsChanged?.Invoke();
    }

    public void TouchRecent(string id)
    {
        Settings.TouchRecent(id);
        Save();
    }

    private void CleanupRecent()
    {
        var ids = Versions.Versions.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Settings.Recent.RemoveAll(r => !ids.Contains(r.Id));
        if (!string.IsNullOrEmpty(Settings.LastVersion) && !ids.Contains(Settings.LastVersion))
            Settings.LastVersion = Versions.Versions.FirstOrDefault()?.Id ?? "";
        Save();
    }

    public int DefaultMemoryFor(VersionInfo version, InstanceSettings instance)
    {
        if (instance.MemoryMb is int explicitValue) return explicitValue;

        // 按版本自动分配：原版给基础值，整合包按 Mod 数量每 16 个加 512 MB，
        // 再受物理内存 60% 上限约束，避免默认 4G 直接 OOM。
        var mods = Math.Max(0, version.ModCount);
        var auto = version.Loader == LoaderKind.Vanilla ? 2048 : 4096;
        if (mods > 0) auto += (int)Math.Ceiling(mods / 16.0) * 512;
        if (mods >= 150) auto = Math.Max(auto, 8192);
        if (mods >= 300) auto = Math.Max(auto, 12288);

        if (TotalMemoryMb > 0)
            auto = Math.Min(auto, (int)(TotalMemoryMb * 0.6));
        auto = Math.Clamp(auto, 2048, 65536);
        return auto / 512 * 512;
    }

    public void SetMinecraftDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        Settings.MinecraftDir = Path.GetFullPath(dir);
        Versions.SetMinecraftDir(Settings.MinecraftDir);
        CrashAnalyzer.MinecraftDir = Settings.MinecraftDir;
        ReloadVersions();
        Save();
        SettingsChanged?.Invoke();
    }

    public void ApplyDownloadSource()
    {
        HttpService.Source = Settings.DownloadSource;
    }

    public void Notify(string title, string message) => Toast?.Invoke(title, message);

    public void RaiseSettingsChanged() => SettingsChanged?.Invoke();
    public void RaiseVersionsChanged() => VersionsChanged?.Invoke();
}
