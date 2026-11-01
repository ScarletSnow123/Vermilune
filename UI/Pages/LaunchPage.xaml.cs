using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public sealed class RecentItem
{
    public string VersionId { get; init; } = "";
    public string Name { get; init; } = "";
    public string SubLine { get; init; } = "";
    public string TimeText { get; init; } = "";
}

public partial class LaunchPage : UserControl, IPage, INotifyPropertyChanged
{
    private readonly DispatcherTimer _logTimer;
    private readonly Queue<string> _pendingLog = new();
    private readonly StringBuilder _logBuffer = new();
    private CancellationTokenSource? _cts;
    private bool _suppressSelection;
    private bool _refreshing;
    private LaunchState _state = LaunchState.Idle;
    private string _logText = "";
    private bool _hasLog;
    private string _accountName = "Player";
    private string _accountSub = "离线账号";
    private string _avatarInitial = "P";
    private string _versionMeta = "";
    private string _launchTitle = "启动游戏";
    private string _launchSub = "选择版本";
    private string _welcomeText = "欢迎回来";
    private string _dateText = "";
    private VersionInfo? _selectedVersion;

    public LaunchPage()
    {
        InitializeComponent();
        DataContext = this;
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();

        LauncherApp.I.SettingsChanged += OnSettingsChanged;
        LauncherApp.I.VersionsChanged += OnVersionsChanged;
        LauncherApp.I.Launch.ProgressChanged += OnLaunchProgress;
        LauncherApp.I.Launch.OutputLine += OnGameLine;
        LauncherApp.I.Launch.Exited += OnGameExited;
        Loaded += (_, _) => RefreshState();
    }

    public ObservableCollection<VersionInfo> Versions { get; } = new();
    public ObservableCollection<RecentItem> RecentItems { get; } = new();

    public VersionInfo? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (ReferenceEquals(_selectedVersion, value)) return;
            _selectedVersion = value;
            OnPropertyChanged();
            UpdateVersionLabels();
        }
    }

    public string AccountName { get => _accountName; private set { _accountName = value; OnPropertyChanged(); } }
    public string AccountSub { get => _accountSub; private set { _accountSub = value; OnPropertyChanged(); } }
    public string AvatarInitial { get => _avatarInitial; private set { _avatarInitial = value; OnPropertyChanged(); } }
    public string VersionMeta { get => _versionMeta; private set { _versionMeta = value; OnPropertyChanged(); } }
    public string LaunchTitle { get => _launchTitle; private set { _launchTitle = value; OnPropertyChanged(); } }
    public string LaunchSub { get => _launchSub; private set { _launchSub = value; OnPropertyChanged(); } }
    public string WelcomeText { get => _welcomeText; private set { _welcomeText = value; OnPropertyChanged(); } }
    public string DateText { get => _dateText; private set { _dateText = value; OnPropertyChanged(); } }
    public string LogText { get => _logText; private set { _logText = value; OnPropertyChanged(); } }
    public bool HasLog { get => _hasLog; private set { _hasLog = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void OnShown()
    {
        RefreshState();
        if (LauncherApp.I.Settings.FirstRun)
        {
            LauncherApp.I.Settings.FirstRun = false;
            LauncherApp.I.Save();
        }
    }

    public void RefreshState()
    {
        if (_refreshing) return;
        _refreshing = true;
        var app = LauncherApp.I;
        try
        {
            var currentId = SelectedVersion?.Id ?? app.Settings.LastVersion;
            _suppressSelection = true;
            Versions.Clear();
            foreach (var v in app.Versions.Versions) Versions.Add(v);
            _suppressSelection = false;
            var selected = Versions.FirstOrDefault(v => string.Equals(v.Id, currentId, StringComparison.OrdinalIgnoreCase))
                           ?? Versions.FirstOrDefault();
            SelectedVersion = selected;
            if (selected is not null && !string.Equals(app.Settings.LastVersion, selected.Id, StringComparison.OrdinalIgnoreCase))
                app.Settings.LastVersion = selected.Id;

            var account = app.Settings.Account;
            AccountName = string.IsNullOrWhiteSpace(account.Name) ? "Player" : account.Name;
            AccountSub = AccountSubText(account);
            AvatarInitial = (AccountName.Length > 0 ? AccountName[..1] : "P").ToUpperInvariant();
            WelcomeText = $"欢迎回来，{AccountName}";
            DateText = DateTime.Now.ToString("yyyy.MM.dd  dddd", CultureInfo.GetCultureInfo("zh-CN"));
            _ = LoadAvatarAsync(account);
            BuildRecent();
            UpdateVersionLabels();
            UpdateLaunchUi();
            _ = RefreshSkinAsync();
            StatusLabel.Text = _state switch
            {
                LaunchState.Running => "游戏正在运行。关闭游戏后，这里会记录退出状态。",
                LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting => "正在准备启动，请稍候…",
                LaunchState.Failed => "启动失败。查看日志，或到报错页进行分析。",
                _ => "就绪。选择一个版本，然后按下启动。"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新启动页失败");
            StatusLabel.Text = "刷新界面时出现错误：" + ex.Message;
        }
        finally { _refreshing = false; }
    }

    private void OnSettingsChanged()
    {
        App.Current.Dispatcher.BeginInvoke(new Action(RefreshAccountHeader));
        _ = RefreshSkinAsync();
    }

    private int _skinLoadToken;

    private async Task RefreshSkinAsync()
    {
        var token = ++_skinLoadToken;
        try
        {
            var skin = await LauncherApp.I.Skins.LoadSkinAsync();
            var cape = await LauncherApp.I.Skins.LoadCapeAsync();
            if (token != _skinLoadToken) return;
            SkinPreview.SetTextures(skin, cape, LauncherApp.I.Skins.IsSlim);
            SkinNameLabel.Text = "皮肤：" + LauncherApp.I.Skins.ActiveSkinName;
            CapeNameLabel.Text = "披风：" + LauncherApp.I.Skins.ActiveCapeName;
            SkinModelLabel.Content = LauncherApp.I.Skins.IsSlim ? "Alex" : "Steve";
        }
        catch (Exception ex)
        {
            Log.Warn("刷新皮肤预览失败：" + ex.Message);
        }
    }

    private async void SwitchSkin_Click(object sender, RoutedEventArgs e)
    {
        var path = SkinPickerDialog.Show(Window.GetWindow(this));
        if (path is null) return;
        if (LauncherApp.I.Settings.Account.IsMicrosoft)
        {
            if (!await LauncherApp.I.EnsureAccountReadyAsync())
            {
                MessageDialog.Show(Window.GetWindow(this), "登录已失效", "请先重新登录 Microsoft 账号，再上传皮肤。", "知道了", danger: true);
                return;
            }
            try
            {
                await LauncherApp.I.Auth.UploadSkinAsync(path, LauncherApp.I.Skins.IsSlim, CancellationToken.None);
                LauncherApp.I.Notify("皮肤已上传", "已应用到 Microsoft 账号。");
            }
            catch (Exception ex)
            {
                MessageDialog.Show(Window.GetWindow(this), "上传皮肤失败", ex.Message, "关闭", danger: true);
            }
        }
        RefreshState();
    }

    private async void ToggleSkinModel_Click(object sender, RoutedEventArgs e)
    {
        var slim = !LauncherApp.I.Skins.IsSlim;
        LauncherApp.I.Skins.SetSlimOverride(slim);
        var local = LauncherApp.I.Settings.SkinPath;
        if (LauncherApp.I.Settings.Account.IsMicrosoft && !string.IsNullOrEmpty(local) && File.Exists(local))
        {
            try { await LauncherApp.I.Auth.UploadSkinAsync(local, slim, CancellationToken.None); }
            catch (Exception ex) { MessageDialog.Show(Window.GetWindow(this), "上传皮肤失败", ex.Message, "关闭", danger: true); }
        }
        await RefreshSkinAsync();
        LauncherApp.I.Notify("皮肤模型", slim ? "已切换为 Alex（细手臂）" : "已切换为 Steve（粗手臂）");
        RefreshState();
    }

    private async void SwitchCape_Click(object sender, RoutedEventArgs e)
    {
        if (!LauncherApp.I.Settings.Account.IsMicrosoft)
        {
            MessageDialog.Show(Window.GetWindow(this), "披风", "披风只支持 Microsoft 正版账号；离线账号没有可切换的披风。", "知道了");
            return;
        }
        if (!await LauncherApp.I.EnsureAccountReadyAsync())
        {
            MessageDialog.Show(Window.GetWindow(this), "登录已失效", "请先重新登录 Microsoft 账号。", "知道了", danger: true);
            return;
        }
        if (CapePickerDialog.Show(Window.GetWindow(this)))
            await RefreshSkinAsync();
    }

    private void UseAccountSkin_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.Skins.UseAccountSkin();
        RefreshState();
        LauncherApp.I.Notify("已恢复账号皮肤", "启动器会重新读取账号皮肤与披风。");
    }

    private void RefreshAccountHeader()
    {
        try
        {
            var account = LauncherApp.I.Settings.Account;
            AccountName = string.IsNullOrWhiteSpace(account.Name) ? "Player" : account.Name;
            AccountSub = AccountSubText(account);
            AvatarInitial = (AccountName.Length > 0 ? AccountName[..1] : "P").ToUpperInvariant();
            WelcomeText = $"欢迎回来，{AccountName}";
        }
        catch { }
    }

    private void OnVersionsChanged()
    {
        App.Current.Dispatcher.BeginInvoke(new Action(RefreshState));
    }

    private void BuildRecent()
    {
        var app = LauncherApp.I;
        RecentItems.Clear();
        var map = app.Versions.Versions.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in app.Settings.Recent.OrderByDescending(r => r.LastPlayedUtc).Take(3))
        {
            if (!map.TryGetValue(entry.Id, out var version)) continue;
            RecentItems.Add(new RecentItem
            {
                VersionId = version.Id,
                Name = string.IsNullOrWhiteSpace(version.DisplayName) ? version.Id : version.DisplayName,
                SubLine = version.SubLine,
                TimeText = RelativeTime(entry.LastPlayedUtc)
            });
        }
        RecentEmpty.Visibility = RecentItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string RelativeTime(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 2) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 2) return "昨天";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return utc.ToLocalTime().ToString("yyyy.MM.dd");
    }

    private void UpdateVersionLabels()
    {
        var v = SelectedVersion;
        if (v is null)
        {
            VersionMeta = "还没有可用的版本。先到「下载」安装一个，或把 .minecraft 放到启动器旁边。";
            LaunchSub = "未选择版本";
            return;
        }
        var mods = v.ModCount > 0 ? $"{v.ModCount} 个 Mod" : "无 Mod";
        VersionMeta = $"{v.MinecraftVersion} · {v.LoaderBadge} · {mods} · 建议 Java {v.JavaMajor}";
        LaunchSub = v.DisplayName;
        if (LauncherApp.I.Launch.IsRunning && LauncherApp.I.Launch.CurrentVersionId == v.Id)
            LaunchTitle = "游戏运行中";
    }

    private static string AccountSubText(AccountSettings account)
    {
        if (account.IsMicrosoft) return "Microsoft 正版 · 已登录";
        if (account.IsYggdrasil)
        {
            var host = account.YggdrasilServer;
            try { host = new Uri(account.YggdrasilServer).Host; } catch { }
            return "外置登录 · " + host;
        }
        return "离线账号 · 本地档案";
    }

    private void UpdateLaunchUi()
    {
        LaunchTitle = _state switch
        {
            LaunchState.Preparing => "准备启动…",
            LaunchState.Downloading => "补全文件中…",
            LaunchState.Starting => "正在启动…",
            LaunchState.Running => "结束游戏",
            _ => "启动游戏"
        };
        LaunchIcon.Data = (Geometry)Application.Current.Resources[(_state == LaunchState.Running ? "IconPower" : "IconPlay")];
        LaunchIcon.Fill = _state == LaunchState.Running ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x17, 0x12, 0x08));
        LaunchIcon.Stroke = _state == LaunchState.Running ? new SolidColorBrush(Color.FromRgb(0x17, 0x12, 0x08)) : Brushes.Transparent;
        LaunchIcon.StrokeThickness = _state == LaunchState.Running ? 2.0 : 0;
        LaunchHint.Text = _state == LaunchState.Running ? "STOP" : "ENTER";
        LaunchButton.IsEnabled = _state is not (LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting);
        StatusDot.Fill = (Brush)Application.Current.Resources[_state switch
        {
            LaunchState.Running => "SuccessBrush",
            LaunchState.Failed => "DangerBrush",
            LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting => "WarnBrush",
            _ => "TextMutedBrush"
        }];
        ProgressPanel.Visibility = _state is LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting
            ? Visibility.Visible : Visibility.Collapsed;
        if (_state == LaunchState.Running) LaunchProgress.Value = 100;
    }

    private async Task LoadAvatarAsync(AccountSettings account)
    {
        try
        {
            // 直接从当前皮肤裁头部，不再依赖 mc-heads 的远程头像缓存。
            // 这样切换皮肤后头像会立即更新，后台挂着代理/VPN 也不会卡住。
            var skin = await LauncherApp.I.Skins.LoadSkinAsync();
            if (skin is not null)
            {
                AvatarImage.Source = CreateHead(skin);
                AvatarInitialText.Visibility = Visibility.Collapsed;
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Info("头像加载失败（不影响启动）：" + ex.Message);
        }
        AvatarImage.Source = null;
        AvatarInitialText.Visibility = Visibility.Visible;
    }

    private static BitmapSource CreateHead(BitmapSource skin)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var head = new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8));
            dc.DrawImage(head, new Rect(0, 0, 8, 8));
            if (skin.PixelWidth >= 48 && skin.PixelHeight >= 16)
            {
                var hat = new CroppedBitmap(skin, new Int32Rect(40, 8, 8, 8));
                dc.DrawImage(hat, new Rect(0, 0, 8, 8));
            }
        }
        var bitmap = new RenderTargetBitmap(8, 8, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (VersionCombo.SelectedItem is not VersionInfo v) return;
        SelectedVersion = v;
        LauncherApp.I.SetCurrentVersion(v.Id);
        UpdateVersionLabels();
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_state == LaunchState.Running)
        {
            if (MessageDialog.Show(Window.GetWindow(this), "结束游戏", "要结束当前 Minecraft 进程吗？未保存的世界进度可能丢失。",
                    "结束游戏", "取消", danger: true))
                LauncherApp.I.Launch.Cancel();
            return;
        }
        if (_state is LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting) return;
        await LaunchSelectedAsync(SelectedVersion);
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var version = LauncherApp.I.Versions.Find(id);
        if (version is null) return;
        SelectedVersion = version;
        LauncherApp.I.SetCurrentVersion(version.Id);
        _ = LaunchSelectedAsync(version);
    }

    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public async Task LaunchSelectedAsync(VersionInfo? version)
    {
        // 防止按钮双击 / 回车和点击同时触发，导致重复启动并弹出“游戏已经在运行中”。
        if (!_launchGate.Wait(0)) return;
        try
        {
            await LaunchSelectedCoreAsync(version);
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private async Task LaunchSelectedCoreAsync(VersionInfo? version)
    {
        if (version is null)
        {
            MessageDialog.Show(Window.GetWindow(this), "没有可启动的版本",
                "当前没有检测到任何 Minecraft 版本。\n请先到「下载」安装原版或 Fabric，或检查 Minecraft 目录设置。",
                "去下载", "关闭");
            App.Window?.NavigateTo("download");
            return;
        }
        var app = LauncherApp.I;
        var resolved = app.Versions.Resolve(version.Id);
        if (resolved is null)
        {
            MessageDialog.Show(Window.GetWindow(this), "版本损坏", "无法解析该版本的 JSON，请重装该版本。", "知道了", danger: true);
            return;
        }

        var account = app.Settings.Account;
        if (account.IsOnline)
        {
            SetStatus(account.IsYggdrasil ? "正在验证外置登录…" : "正在刷新账号令牌…", LaunchState.Preparing);
            var ready = await app.EnsureAccountReadyAsync();
            if (!ready)
            {
                var message = account.IsYggdrasil
                    ? "外置登录令牌已失效，需要重新登录后才能启动。\n也可以先切换到离线账号。"
                    : "Microsoft 登录令牌已失效，需要重新登录后才能启动正版账号。\n也可以先切换到离线账号。";
                if (MessageDialog.Show(Window.GetWindow(this), "登录已失效", message,
                        "重新登录", "取消", danger: true))
                {
                    if (!AccountDialog.Show(Window.GetWindow(this))) return;
                    account = app.Settings.Account;
                }
                if (account.IsOnline && !await app.EnsureAccountReadyAsync())
                {
                    SetStatus("账号未就绪，启动已取消。", LaunchState.Idle);
                    return;
                }
            }
        }

        await app.EnsureJavaAsync();
        var instance = app.Versions.GetInstance(version.Id);
        var preferredJava = !string.IsNullOrWhiteSpace(instance.JavaPath) ? instance.JavaPath : app.Settings.JavaPath;
        var java = app.Java.Pick(resolved.JavaMajor, preferredJava);
        if (java is null)
        {
            MessageDialog.Show(Window.GetWindow(this), "找不到 Java",
                $"这个版本需要 Java {resolved.JavaMajor}，但没有在系统里找到可用的 64 位 Java。\n" +
                "请安装对应 Java，或在「设置 → 启动」里手动指定 java.exe。",
                "去设置", "关闭", danger: true);
            App.Window?.NavigateTo("settings");
            return;
        }
        if (java.Major < resolved.JavaMajor)
        {
            MessageDialog.Show(Window.GetWindow(this), "Java 主版本过低",
                $"该版本需要 Java {resolved.JavaMajor} 或更高，当前只有 Java {java.Major}。\n" +
                "请安装新版本 Java，或在该版本的「修改」页里指定。",
                "知道了", danger: true);
            return;
        }

        var memory = app.DefaultMemoryFor(version, instance);
        if (memory > app.TotalMemoryMb && app.TotalMemoryMb > 0)
        {
            if (!MessageDialog.Show(Window.GetWindow(this), "内存设置过大",
                    $"分配内存 {memory} MB 已超过物理内存 {app.TotalMemoryMb} MB。\n继续可能导致系统卡死，建议降低内存。",
                    "仍然启动", "返回修改", danger: true))
            {
                App.Window?.NavigateToVersions(version.Id);
                return;
            }
        }

        var gameDir = app.Versions.GetGameDir(version.Id, instance, app.Settings.VersionIsolation);
        Directory.CreateDirectory(gameDir);
        var options = new LaunchOptions
        {
            Version = version,
            Resolved = resolved,
            Instance = instance,
            Account = account,
            MinecraftDir = app.MinecraftDir,
            GameDir = gameDir,
            NativesDir = app.Versions.GetNativesDir(version.Id),
            JavaPath = java.Path,
            MemoryMb = memory,
            Width = instance.Width,
            Height = instance.Height,
            Fullscreen = instance.Fullscreen,
            OptimizeJvm = instance.OptimizeJvm ?? app.Settings.OptimizeJvm,
            ExtraJvmArgs = instance.JvmArgs,
            ExtraGameArgs = instance.GameArgs,
            LauncherName = "Vermilune",
            LauncherVersion = "1.2.9-beta"
        };

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _pendingLog.Clear();
        _logBuffer.Clear();
        LogText = "";
        HasLog = false;
        SetStatus("正在检查运行环境…", LaunchState.Preparing);
        try
        {
            await app.Launch.LaunchAsync(options, _cts.Token);
            if (_state == LaunchState.Running)
            {
                app.Settings.TouchRecent(version.Id);
                app.Settings.LastVersion = version.Id;
                app.Save();
                BuildRecent();
            }
            if (app.Settings.CloseAfterLaunch)
            {
                await Task.Delay(1200);
                App.Window?.CloseForGame();
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("启动已取消。", LaunchState.Idle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动失败");
            SetStatus("启动失败：" + ex.Message, LaunchState.Failed);
            MessageDialog.Show(Window.GetWindow(this), "启动失败", ex.Message + "\n\n详细日志：" + app.Launch.LastLogPath,
                "查看日志", "关闭", danger: true);
            if (app.Launch.LastLogPath.Length > 0) Shell.OpenFile(app.Launch.LastLogPath);
        }
    }

    private void OnLaunchProgress(LaunchProgress p)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _state = p.State;
            SetStatus(p.Stage, p.State);
            ProgressText.Text = p.Stage;
            if (p.Fraction is double f)
            {
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = Math.Clamp(f * 100, 0, 100);
            }
            else
            {
                LaunchProgress.IsIndeterminate = true;
            }
            if (p.State == LaunchState.Running)
            {
                var id = LauncherApp.I.Launch.CurrentVersionId;
                if (!string.IsNullOrEmpty(id))
                {
                    LauncherApp.I.Settings.TouchRecent(id);
                    LauncherApp.I.Save();
                    BuildRecent();
                }
            }
        }));
    }

    private void OnGameLine(string line)
    {
        lock (_pendingLog)
        {
            _pendingLog.Enqueue(line);
            while (_pendingLog.Count > 300) _pendingLog.Dequeue();
        }
    }

    private void FlushLog()
    {
        string? last = null;
        var changed = false;
        lock (_pendingLog)
        {
            while (_pendingLog.Count > 0)
            {
                last = _pendingLog.Dequeue();
                _logBuffer.AppendLine(last);
                changed = true;
            }
        }
        if (!changed) return;
        if (_logBuffer.Length > 5000) _logBuffer.Remove(0, _logBuffer.Length - 5000);
        var lines = _logBuffer.ToString().TrimEnd().Split('\n');
        LogText = string.Join(Environment.NewLine, lines.TakeLast(4));
        HasLog = true;
    }

    private async void OnGameExited(int code, string? crashFile)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            _state = code == 0 ? LaunchState.Exited : LaunchState.Failed;
            SetStatus(code == 0 ? "游戏已正常退出。" : $"游戏异常退出（退出码 {code}）。", _state);
        });

        if (code == 0) return;
        var analysis = await Task.Run(() =>
        {
            try
            {
                var id = LauncherApp.I.Launch.CurrentVersionId;
                var v = LauncherApp.I.Versions.Find(id);
                var inst = LauncherApp.I.Versions.GetInstance(id);
                var dir = LauncherApp.I.Launch.LastGameDir;
                if (string.IsNullOrEmpty(dir))
                    dir = LauncherApp.I.Versions.GetGameDir(id, inst, LauncherApp.I.Settings.VersionIsolation);
                return CrashAnalyzer.Analyze(id, dir, v?.Loader ?? LoaderKind.Unknown, v?.MinecraftVersion ?? "",
                    sinceUtc: LauncherApp.I.Launch.LaunchStartedUtc);
            }
            catch (Exception ex)
            {
                Log.Warn("分析崩溃失败：" + ex.Message);
                return null;
            }
        });
        LauncherApp.I.LastAnalysis = analysis;
        await Dispatcher.InvokeAsync(() =>
        {
            var first = analysis?.Findings.FirstOrDefault();
            var message = first is null
                ? "游戏异常退出。可以打开日志查看原因，或到「报错」页做一次完整分析。"
                : $"{first.Title}\n{first.Suggestion}";
            if (MessageDialog.Show(Window.GetWindow(this), "游戏异常退出", message,
                    "打开报错分析", "关闭", danger: true))
                App.Window?.NavigateTo("error");
        });
    }

    private void SetStatus(string text, LaunchState state)
    {
        _state = state;
        StatusLabel.Text = text;
        UpdateLaunchUi();
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (AccountDialog.Show(Window.GetWindow(this))) RefreshState();
    }

    private void CancelLaunch_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        if (_state == LaunchState.Running) LauncherApp.I.Launch.Cancel();
        SetStatus("正在取消启动…", _state);
    }

    private void ManageVersion_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("versions");
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("settings");

    private void EditVersion_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is not null) App.Window?.NavigateToVersions(SelectedVersion.Id);
        else App.Window?.NavigateTo("versions");
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is null) return;
        Shell.OpenFolder(Path.Combine(LauncherApp.I.MinecraftDir, "versions", SelectedVersion.Id));
    }

    private void OpenMcFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(LauncherApp.I.MinecraftDir);
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(AppPaths.DataDir);

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedVersion?.Id ?? LauncherApp.I.CurrentVersion?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            MessageDialog.Show(Window.GetWindow(this), "没有可用版本", "请先在「版本」页安装一个版本。", "知道了");
            return;
        }
        try
        {
            var dir = LauncherApp.I.Versions.GetModsDir(id, null, LauncherApp.I.Settings.VersionIsolation);
            Directory.CreateDirectory(dir);
            Shell.OpenFolder(dir);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Window.GetWindow(this), "打开 Mods 文件夹失败", ex.Message, "关闭", danger: true);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var path = LauncherApp.I.Launch.LastLogPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) Shell.OpenFile(path);
        else Shell.OpenLogs();
    }

    private void RefreshHome_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.ReloadVersions();
        RefreshState();
        LauncherApp.I.Notify("主页已刷新", "版本列表与最近游玩已更新。");
    }

    private void OpenSatori_Click(object sender, RoutedEventArgs e)
        => Shell.OpenUrl("https://s1.imagehub.cc/images/2026/07/08/26dd444015c43ab6834afad1864c0aea.th.jpg");

    private void Site_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) Shell.OpenUrl(url);
    }

    private void ToolTaskmgr_Click(object sender, RoutedEventArgs e) => Shell.Run("taskmgr.exe");
    private void ToolResmon_Click(object sender, RoutedEventArgs e) => Shell.Run("resmon.exe");
    private void ToolCmd_Click(object sender, RoutedEventArgs e) => Shell.Run("cmd.exe");
    private void ToolPaint_Click(object sender, RoutedEventArgs e) => Shell.Run("mspaint.exe");
}
