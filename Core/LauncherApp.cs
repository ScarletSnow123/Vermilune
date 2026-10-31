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
        if (!account.IsMicrosoft) return;
        if (string.IsNullOrEmpty(account.RefreshTokenProtected)) return;
        // 只有快过期或已过期才在后台刷新，避免每次启动都请求微软
        if (account.AccessTokenExpiresUtc > DateTime.UtcNow.AddHours(2)) return;
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

    public async Task<bool> EnsureAccountReadyAsync()
    {
        if (Settings.Account.IsYggdrasil)
        {
            if (string.IsNullOrEmpty(Settings.Account.AccessTokenProtected)) return false;
            if (Settings.Account.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(5)) return true;
            try
            {
                var profile = await YggdrasilService.RefreshAsync(Settings.Account, CancellationToken.None);
                YggdrasilService.Apply(Settings.Account, profile);
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

    public void Save() => SettingsStore.Save(Settings);
    public void SaveDebounced() => SettingsStore.SaveDebounced(Settings);

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
        var baseline = Settings.MemoryMb;
        // 大型整合包直接用全局 4G 很容易 OOM；在用户没有单独设置时给出安全的下限。
        if (version.ModCount >= 80)
        {
            var recommended = JavaService.RecommendMemoryMb(TotalMemoryMb);
            return Math.Max(baseline, Math.Min(16384, recommended));
        }
        return baseline;
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
