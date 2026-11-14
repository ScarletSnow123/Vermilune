using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private string _logText = "等待游戏输出…";
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
    private bool _homeEditing;
    private bool _logPanelOpen;
    private bool _logBodyOpen;
    private FrameworkElement? _dragCard;
    private string? _dragKey;
    private Point _dragStart;
    private double _dragBaseX, _dragBaseY;
    private Point _lastDragPoint;
    private readonly HashSet<FrameworkElement> _dragIgnoredOverlaps = new();
    private readonly Dictionary<string, TranslateTransform> _homeTransforms = new();
    private readonly Dictionary<string, ScaleTransform> _homeScales = new();
    private readonly Dictionary<string, RotateTransform> _homeRotates = new();
    private readonly Dictionary<FrameworkElement, string> _homeCardKeys = new();
    private readonly Dictionary<TextBlock, double> _baseFontSizes = new();
    private readonly DispatcherTimer _clockTimer;

    public LaunchPage()
    {
        InitializeComponent();
        DataContext = this;
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClockWidgets();
        _clockTimer.Start();
        SetupHomeEditing();

        LauncherApp.I.SettingsChanged += OnSettingsChanged;
        SizeChanged += (_, _) => UpdateHomeResponsiveLayout();
        Loaded += (_, _) => UpdateHomeResponsiveLayout();
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
    public int ModCount { get => _modCount; private set { _modCount = value; OnPropertyChanged(); } }
    public int ResourcePackCount { get => _resourcePackCount; private set { _resourcePackCount = value; OnPropertyChanged(); } }
    public int SaveCount { get => _saveCount; private set { _saveCount = value; OnPropertyChanged(); } }
    public int ShaderCount { get => _shaderCount; private set { _shaderCount = value; OnPropertyChanged(); } }
    public int ScreenshotCount { get => _screenshotCount; private set { _screenshotCount = value; OnPropertyChanged(); } }
    private int _modCount, _resourcePackCount, _saveCount, _shaderCount, _screenshotCount;
    private string _statsCacheKey = "";
    private DateTime _statsCacheAt;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void OnShown()
    {
        RefreshState();
        ApplyHomeLayout();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AnimateHomeCards();
            HomeContentScroll?.ScrollToTop();
            HomeSideScroll?.ScrollToTop();
            LaunchButton?.Focus();
        }), DispatcherPriority.Loaded);
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
            BuildRecent();
            UpdateVersionLabels();
            UpdateLaunchUi();
            ApplyFeaturedCard();
            _ = RefreshSkinAsync();
            StatusLabel.Text = _state switch
            {
                LaunchState.Running => "游戏正在运行\n关闭游戏后，这里会记录退出状态",
                LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting => "正在准备启动…",
                LaunchState.Failed => "启动失败\n查看日志，或到报错页进行分析",
                _ => "就绪\n选择一个版本，然后按下启动"
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
        App.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshAccountHeader();
            ApplyFeaturedCard();
            ApplyHomeLayout();
        }));
        _ = RefreshSkinAsync();
    }

    private string _featuredImagePath = "";
    private const string DefaultFeaturedImage = "pack://application:,,,/Assets/VermiluneRing.png";

    private void ApplyFeaturedCard()
    {
        if (FeaturedImage is null) return;
        var s = LauncherApp.I.Settings;
        var title = (s.FeaturedTitle ?? "").Trim();
        var subtitle = (s.FeaturedSubtitle ?? "").Trim();
        var hasTitle = title.Length > 0;
        var hasSubtitle = subtitle.Length > 0;
        var light = string.Equals(s.UiThemeMode, "light", StringComparison.OrdinalIgnoreCase);
        var defaultTitleColor = light ? "#FFB3261E" : "#FFFF8A7A";
        var defaultSubtitleColor = light ? "#FF8A1F18" : "#FFFFB3A8";

        FeaturedTitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
        FeaturedTitleText.Text = title;
        FeaturedTitleText.FontSize = 20;
        FeaturedTitleText.Foreground = BrushFrom(s.FeaturedTitleColor, defaultTitleColor);

        FeaturedSubtitleText.Visibility = hasSubtitle ? Visibility.Visible : Visibility.Collapsed;
        FeaturedSubtitleText.Text = subtitle;
        FeaturedSubtitleText.FontSize = hasTitle ? 10.5 : 12.5;
        FeaturedSubtitleText.FontWeight = hasTitle ? FontWeights.Normal : FontWeights.Medium;
        FeaturedSubtitleText.Margin = hasTitle ? new Thickness(0, 6, 0, 0) : new Thickness(0);
        FeaturedSubtitleText.Foreground = BrushFrom(s.FeaturedSubtitleColor, defaultSubtitleColor);

        if (string.Equals(_featuredImagePath, s.FeaturedImage, StringComparison.OrdinalIgnoreCase)) return;
        _featuredImagePath = s.FeaturedImage ?? "";
        try
        {
            var source = !string.IsNullOrWhiteSpace(s.FeaturedImage) &&
                         (s.FeaturedImage.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) || File.Exists(s.FeaturedImage))
                ? s.FeaturedImage
                : DefaultFeaturedImage;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 256;
            bitmap.UriSource = new Uri(source, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            FeaturedImage.Source = bitmap;
        }
        catch
        {
            FeaturedImage.Source = new BitmapImage(new Uri(DefaultFeaturedImage, UriKind.Absolute));
        }
    }

    private static Brush BrushFrom(string? value, string fallback)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
        }
        catch { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
    }

    private void CustomizeFeatured_Click(object sender, RoutedEventArgs e)
    {
        var s = LauncherApp.I.Settings;
        var dialog = new FeaturedCardDialog(s.FeaturedImage, s.FeaturedTitle, s.FeaturedSubtitle,
            s.FeaturedTitleColor, s.FeaturedSubtitleColor)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;
        s.FeaturedImage = dialog.ImagePath;
        s.FeaturedTitle = dialog.CardTitle;
        s.FeaturedSubtitle = dialog.CardSubtitle;
        s.FeaturedTitleColor = dialog.CardTitleColor;
        s.FeaturedSubtitleColor = dialog.CardSubtitleColor;
        LauncherApp.I.Save();
        ApplyFeaturedCard();
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
            var hasSkin = skin is not null;
            SkinPreview.Visibility = hasSkin ? Visibility.Visible : Visibility.Collapsed;
            SkinFallback.Visibility = hasSkin ? Visibility.Collapsed : Visibility.Visible;
            if (hasSkin) SkinPreview.SetTextures(skin, cape, LauncherApp.I.Skins.IsSlim);

            CenterAvatarInitial.Text = _avatarInitial;
            if (hasSkin)
            {
                try
                {
                    CenterAvatarImage.Source = AvatarRenderer.RenderFrontHeadDrawing(skin!);
                    CenterAvatarImage.Visibility = Visibility.Visible;
                    CenterAvatarFallback.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    Log.Warn("生成 MC 头像失败：" + ex.Message);
                    CenterAvatarImage.Visibility = Visibility.Collapsed;
                    CenterAvatarFallback.Visibility = Visibility.Visible;
                }
            }
            else
            {
                CenterAvatarImage.Visibility = Visibility.Collapsed;
                CenterAvatarFallback.Visibility = Visibility.Visible;
            }
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
                MessageDialog.Show(Window.GetWindow(this), "登录已失效", "先重新登录 Microsoft 账号，再传皮肤", "知道了", danger: true);
                return;
            }
            try
            {
                await LauncherApp.I.Auth.UploadSkinAsync(path, LauncherApp.I.Skins.IsSlim, CancellationToken.None);
                LauncherApp.I.Notify("皮肤已上传", "已应用到 Microsoft 账号");
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
            MessageDialog.Show(Window.GetWindow(this), "披风", "披风只支持 Microsoft 正版账号；离线账号没有可切换的披风", "知道了");
            return;
        }
        if (!await LauncherApp.I.EnsureAccountReadyAsync())
        {
            MessageDialog.Show(Window.GetWindow(this), "登录已失效", "先重新登录 Microsoft 账号", "知道了", danger: true);
            return;
        }
        if (CapePickerDialog.Show(Window.GetWindow(this)))
            await RefreshSkinAsync();
    }

    private async void UseAccountSkin_Click(object sender, RoutedEventArgs e)
    {
        var local = LauncherApp.I.Settings.SkinPath;
        var hasLocal = !string.IsNullOrWhiteSpace(local) && File.Exists(local);
        if (!hasLocal)
        {
            if (LauncherApp.I.Settings.Account.IsMicrosoft)
            {
                await LauncherApp.I.RefreshMicrosoftSkinAsync();
            }
            else if (LauncherApp.I.Settings.Account.IsYggdrasil || LauncherApp.I.Settings.Account.IsNide)
            {
                await LauncherApp.I.RefreshAccountAsync();
            }
        }
        await RefreshSkinAsync();
        RefreshAccountHeader();
        LauncherApp.I.Notify("已刷新皮肤", hasLocal ? "已重新读取本地皮肤" : "已重新读取账号皮肤与披风");
    }

    private async void UseAccountSkinOriginal_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.Skins.UseAccountSkin();
        if (LauncherApp.I.Settings.Account.IsMicrosoft)
            await LauncherApp.I.RefreshMicrosoftSkinAsync();
        else if (LauncherApp.I.Settings.Account.IsYggdrasil || LauncherApp.I.Settings.Account.IsNide)
            await LauncherApp.I.RefreshAccountAsync();
        await RefreshSkinAsync();
        RefreshAccountHeader();
        LauncherApp.I.Notify("已使用账号皮肤", "已清除本地皮肤覆盖");
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
            VersionMeta = "还没有可用的版本\n先到「下载」安装一个，或把 .minecraft 放到启动器旁边";
            LaunchSub = "未选择版本";
            return;
        }
        var mods = v.ModCount > 0 ? $"{v.ModCount} 个 Mod" : "无 Mod";
        var instance = InstanceStore.Load(v.Id);
        UpdateInstanceStats(v, instance);
        var memory = LauncherApp.I.DefaultMemoryFor(v, instance);
        var memoryText = instance.MemoryMb is null ? $"自动 {memory} MB 内存" : $"{memory} MB 内存";
        VersionMeta = $"{v.MinecraftVersion} · {v.LoaderBadge} · {mods} · {memoryText} · 建议 Java {v.JavaMajor}";
        LaunchSub = v.DisplayName;
        if (LauncherApp.I.Launch.IsRunning && LauncherApp.I.Launch.CurrentVersionId == v.Id)
            LaunchTitle = "游戏运行中";
    }

    private void UpdateInstanceStats(VersionInfo version, InstanceSettings instance)
    {
        var dir = "";
        try { dir = LauncherApp.I.Versions.GetGameDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation); }
        catch { }
        var key = version.Id + "|" + version.LastWriteUtc.Ticks + "|" + dir;
        if (key == _statsCacheKey && DateTime.UtcNow - _statsCacheAt < TimeSpan.FromSeconds(8)) return;
        _statsCacheKey = key;
        _statsCacheAt = DateTime.UtcNow;

        ModCount = version.ModCount;
        ResourcePackCount = SaveCount = ShaderCount = ScreenshotCount = 0;
        try
        {
            if (Directory.Exists(dir))
            {
                var resourceDir = Path.Combine(dir, "resourcepacks");
                if (Directory.Exists(resourceDir)) ResourcePackCount = Directory.EnumerateFiles(resourceDir, "*.zip").Count();
                var savesDir = Path.Combine(dir, "saves");
                if (Directory.Exists(savesDir)) SaveCount = Directory.EnumerateDirectories(savesDir).Count();
                var shaderDir = Path.Combine(dir, "shaderpacks");
                if (Directory.Exists(shaderDir)) ShaderCount = Directory.EnumerateFiles(shaderDir, "*.zip").Count();
                var shotDir = Path.Combine(dir, "screenshots");
                if (Directory.Exists(shotDir))
                    ScreenshotCount = Directory.EnumerateFiles(shotDir)
                        .Count(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) { Log.Warn("统计实例资源失败：" + ex.Message); }
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
        if (account.IsNide) return "统一通行证 · " + account.NideServer;
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

    private static string ResolveWindowTitle(string template, VersionInfo version, AccountSettings account)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        var login = account.IsMicrosoft ? "微软正版"
            : account.IsYggdrasil || account.IsNide ? "外置登录"
            : "离线登录";
        var name = string.IsNullOrWhiteSpace(version.DisplayName) ? version.Id : version.DisplayName;
        var now = DateTime.Now;
        return template
            .Replace("{user}", account.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{login}", login, StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HH:mm:ss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", string.IsNullOrWhiteSpace(version.MinecraftVersion) ? version.Id : version.MinecraftVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Width, int Height) ParseWindowSize(string mode) => mode switch
    {
        "1280x720" => (1280, 720),
        "1600x900" => (1600, 900),
        "1920x1080" => (1920, 1080),
        _ => (0, 0)
    };

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
            if (MessageDialog.Show(Window.GetWindow(this), "结束游戏", "要结束现在的 Minecraft 吗？没保存的进度会丢",
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
                "没检测到 Minecraft 版本\n\n先去「下载」装个原版或 Fabric，或者检查 Minecraft 目录设置",
                "去下载", "关闭");
            App.Window?.NavigateTo("download");
            return;
        }
        var app = LauncherApp.I;
        var resolved = app.Versions.Resolve(version.Id);
        if (resolved is null)
        {
            MessageDialog.Show(Window.GetWindow(this), "版本损坏", "这个版本的 JSON 读不了，重装一下吧", "知道了", danger: true);
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
                    ? "外置登录令牌已失效，需要重新登录后才能启动\n\n也可以先切换到离线账号"
                    : "Microsoft 登录令牌已失效，需要重新登录后才能启动正版账号\n\n也可以先切换到离线账号";
                if (MessageDialog.Show(Window.GetWindow(this), "登录已失效", message,
                        "重新登录", "取消", danger: true))
                {
                    if (!AccountDialog.Show(Window.GetWindow(this))) return;
                    account = app.Settings.Account;
                }
                if (account.IsOnline && !await app.EnsureAccountReadyAsync())
                {
                    SetStatus("账号未就绪，启动已取消", LaunchState.Idle);
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
                $"这个版本需要 Java {resolved.JavaMajor}，但没有在系统里找到可用的 64 位 Java\n\n" +
                "装个对应的 Java，或者在「设置 → 启动」里手动指定 java.exe",
                "去设置", "关闭", danger: true);
            App.Window?.NavigateTo("settings");
            return;
        }
        if (java.Major < resolved.JavaMajor)
        {
            MessageDialog.Show(Window.GetWindow(this), "Java 主版本过低",
                $"这个版本需要 Java {resolved.JavaMajor} 或更高，当前只有 Java {java.Major}\n\n" +
                "装个新 Java，或者在这个版本的「修改」页里指定",
                "知道了", danger: true);
            return;
        }

        var memory = app.DefaultMemoryFor(version, instance);
        if (memory > app.TotalMemoryMb && app.TotalMemoryMb > 0)
        {
            if (!MessageDialog.Show(Window.GetWindow(this), "内存设置过大",
                    $"分配内存 {memory} MB 已超过物理内存 {app.TotalMemoryMb} MB\n\n继续可能导致系统卡死，建议降低内存",
                    "仍然启动", "返回修改", danger: true))
            {
                App.Window?.NavigateToVersions(version.Id);
                return;
            }
        }

        var gameDir = app.Versions.GetGameDir(version.Id, instance, app.Settings.VersionIsolation);
        Directory.CreateDirectory(gameDir);
        var (globalWidth, globalHeight) = ParseWindowSize(app.Settings.WindowSize);
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
            Width = instance.Width > 0 ? instance.Width : globalWidth,
            Height = instance.Height > 0 ? instance.Height : globalHeight,
            Fullscreen = instance.Fullscreen,
            OptimizeJvm = instance.OptimizeJvm ?? app.Settings.OptimizeJvm,
            ExtraJvmArgs = instance.JvmArgs,
            ExtraGameArgs = instance.GameArgs,
            GameWindowTitle = ResolveWindowTitle(app.Settings.GameWindowTitle, version, account),
            CustomInfo = app.Settings.CustomInfo,
            ProcessPriority = app.Settings.ProcessPriority,
            IpProtocol = app.Settings.IpProtocol,
            LauncherName = "Vermilune",
            LauncherVersion = "1.3.1"
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
            if (app.Settings.LauncherVisibility == "close")
            {
                await Task.Delay(1200);
                App.Window?.CloseForGame();
            }
            else if (app.Settings.LauncherVisibility == "hide")
            {
                await Task.Delay(1200);
                if (_state == LaunchState.Running && App.Window is not null)
                {
                    Action<int, string?>? onExit = null;
                    onExit = (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        app.Launch.Exited -= onExit;
                        App.Window?.Show();
                        App.Window?.Activate();
                    }));
                    app.Launch.Exited += onExit;
                    App.Window.Hide();
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("启动已取消", LaunchState.Idle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动失败");
            App.Window?.Show();
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
        LogText = string.Join(Environment.NewLine, lines.TakeLast(8));
        HasLog = true;
        LogScroll?.ScrollToEnd();
    }

    private async void OnGameExited(int code, string? crashFile)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            _state = code == 0 ? LaunchState.Exited : LaunchState.Failed;
            SetStatus(code == 0 ? "游戏已正常退出" : $"游戏异常退出（退出码 {code}）", _state);
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
                ? "游戏异常退出\n可以打开日志查看原因，或到「报错」页做一次完整分析"
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
        if (!_logPanelOpen && (state is LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting or LaunchState.Running or LaunchState.Failed))
        {
            _logPanelOpen = true;
            ApplyHomeLayout();
        }
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
    private HomeCardStyle GetHomeStyle(string key)
    {
        if (!LauncherApp.I.Settings.HomeStyles.TryGetValue(key, out var style))
        {
            style = new HomeCardStyle();
            LauncherApp.I.Settings.HomeStyles[key] = style;
        }
        return style;
    }

    private void ApplyCardStyle(FrameworkElement card, string key)
    {
        var style = GetHomeStyle(key);
        if (_homeScales.TryGetValue(key, out var scale))
        {
            var value = Math.Clamp(style.Scale, 0.4, 2.5);
            scale.ScaleX = value;
            scale.ScaleY = value;
        }
        if (_homeRotates.TryGetValue(key, out var rotate))
            rotate.Angle = Math.Clamp(style.Rotation, -180, 180);
        card.Opacity = Math.Clamp(style.Opacity, 0.2, 1.0);
        card.SnapsToDevicePixels = true;
        card.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(card, TextRenderingMode.ClearType);
        Dispatcher.BeginInvoke(new Action(() => ApplyFontScale(card, Math.Clamp(style.FontScale, 0.5, 2.5))),
            DispatcherPriority.Loaded);
    }

    private void ApplyFontScale(DependencyObject root, double factor)
    {
        if (root is TextBlock text)
        {
            if (!_baseFontSizes.TryGetValue(text, out var baseSize))
            {
                baseSize = text.FontSize;
                _baseFontSizes[text] = baseSize;
            }
            text.FontSize = Math.Max(6, Math.Round(baseSize * factor * 2) / 2);
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(text, TextRenderingMode.ClearType);
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) ApplyFontScale(VisualTreeHelper.GetChild(root, i), factor);
    }

    private void ShowCardStyleMenu(FrameworkElement card, string key)
    {
        var style = GetHomeStyle(key);
        var original = new HomeCardStyle
        {
            Rotation = style.Rotation,
            Scale = style.Scale,
            FontScale = style.FontScale,
            Opacity = style.Opacity
        };
        var canDelete = key.StartsWith("widget:", StringComparison.Ordinal) ||
                        key is "quick" or "recent" or "log" or "featured" or "skin3d";
        var dialog = new HomeCardStyleDialog(style, canDelete,
            preview =>
            {
                style.Rotation = preview.Rotation;
                style.Scale = preview.Scale;
                style.FontScale = preview.FontScale;
                style.Opacity = preview.Opacity;
                ApplyCardStyle(card, key);
            })
        {
            Owner = Window.GetWindow(this)
        };
        var result = dialog.ShowDialog();
        if (result != true)
        {
            style.Rotation = original.Rotation;
            style.Scale = original.Scale;
            style.FontScale = original.FontScale;
            style.Opacity = original.Opacity;
            ApplyCardStyle(card, key);
            return;
        }
        if (dialog.DeleteRequested)
        {
            var settings = LauncherApp.I.Settings;
            if (key.StartsWith("widget:", StringComparison.Ordinal))
            {
                var id = key["widget:".Length..];
                settings.HomeWidgets.RemoveAll(w => w.Id == id);
                settings.HomeStyles.Remove(key);
                BuildHomeWidgets();
            }
            else
            {
                switch (key)
                {
                    case "quick": settings.ShowHomeQuick = false; break;
                    case "recent": settings.ShowHomeRecent = false; break;
                    case "log": settings.ShowHomeLog = false; break;
                    case "featured": settings.ShowHomeFeatured = false; break;
                    case "skin3d": settings.ShowHome3D = false; break;
                }
            }
            LauncherApp.I.Save();
            ApplyHomeLayout();
            return;
        }

        style.Rotation = dialog.CardStyle.Rotation;
        style.Scale = dialog.CardStyle.Scale;
        style.FontScale = dialog.CardStyle.FontScale;
        style.Opacity = dialog.CardStyle.Opacity;
        ApplyCardStyle(card, key);
        LauncherApp.I.SaveDebounced();
    }

    private void AddChoice(ContextMenu menu, string title, string[] labels, double[] values,
        double current, Action<double> apply)
    {
        var item = new MenuItem { Header = title };
        for (var i = 0; i < labels.Length; i++)
        {
            var choice = new MenuItem
            {
                Header = labels[i],
                IsCheckable = true,
                IsChecked = Math.Abs(values[i] - current) < 0.001
            };
            var value = values[i];
            choice.Click += (_, _) =>
            {
                apply(value);
                LauncherApp.I.SaveDebounced();
                menu.IsOpen = false;
            };
            item.Items.Add(choice);
        }
        menu.Items.Add(item);
    }

    private void UpdateHomeResponsiveLayout()
    {
        if (CenterAvatarHost is null) return;
        var width = ActualWidth;
        var height = ActualHeight;
        var avatarSize = Math.Round(Math.Clamp(Math.Min(width * 0.095, height * 0.16), 88, 128));
        CenterAvatarHost.Width = avatarSize;
        CenterAvatarHost.Height = avatarSize;
        CenterAvatarImage.Width = avatarSize;
        CenterAvatarImage.Height = avatarSize;
        CenterAvatarInitial.FontSize = Math.Max(24, avatarSize * 0.32);

        var leftWidth = Math.Clamp(width * 0.34, 300, 420);
        var rightWidth = Math.Clamp(width * 0.27, 250, 340);
        var logWidth = Math.Clamp(width * 0.38, 340, 480);
        HomeSkinPanel.Width = leftWidth;
        HomeInstancePanel.Width = leftWidth;
        HomeRecentPanel.Width = rightWidth;
        HomeFeaturedPanel.Width = rightWidth;
        HomeLogPanel.Width = logWidth;

        var modelWidth = Math.Clamp(width * 0.14, 136, 168);
        var modelHeight = Math.Clamp(height * 0.25, 126, 192);
        HomeSkin3DPanel.Width = modelWidth + 22;
        Skin3DHost.Width = modelWidth;
        Skin3DHost.Height = modelHeight;
        SkinPreview.Width = modelWidth;
        SkinPreview.Height = modelHeight;
    }

    private void SetupHomeEditing()
    {
        SetupCardDrag(HomeQuickPanel, "quick");
        SetupCardDrag(HomeSkin3DPanel, "skin3d");
        SetupCardDrag(HomeRecentPanel, "recent");
        SetupCardDrag(HomeSkinPanel, "skin");
        SetupCardDrag(HomeInstancePanel, "instance");
        SetupCardDrag(HomeLogPanel, "log");
        SetupCardDrag(HomeFeaturedPanel, "featured");
        BuildHomeWidgets();
    }

    private readonly List<FrameworkElement> _homeCards = new();
    private readonly List<TextBlock> _clockTexts = new();

    private void SetupCardDrag(FrameworkElement card, string key)
    {
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var transform = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(transform);
        card.RenderTransform = group;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        _homeScales[key] = scale;
        _homeRotates[key] = rotate;
        _homeTransforms[key] = transform;
        _homeCardKeys[card] = key;
        _homeCards.Add(card);
        if (LauncherApp.I.Settings.HomeOffsets.TryGetValue(key, out var saved))
        {
            transform.X = saved.X;
            transform.Y = saved.Y;
        }
        ApplyCardStyle(card, key);
        card.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (!_homeEditing) return;
            ShowCardStyleMenu(card, key);
            e.Handled = true;
        };
        card.PreviewMouseLeftButtonDown += (_, e) => StartCardDrag(card, key, e);
        card.PreviewMouseMove += (_, e) => MoveCardDrag(card, e);
        card.PreviewMouseLeftButtonUp += (_, e) => EndCardDrag(card, e);
        card.LostMouseCapture += (_, _) => { if (ReferenceEquals(_dragCard, card)) _dragCard = null; };
    }

    private void StartCardDrag(FrameworkElement card, string? key, MouseButtonEventArgs e)
    {
        if (!_homeEditing || e.LeftButton != MouseButtonState.Pressed || IsInteractive(e.OriginalSource)) return;
        _dragCard = card;
        _dragKey = key;
        _dragStart = e.GetPosition(this);
        _lastDragPoint = _dragStart;
        _dragIgnoredOverlaps.Clear();
        var startBounds = GetCardRootBounds(card);
        if (!startBounds.IsEmpty)
        {
            foreach (var other in HomeCardElements())
            {
                if (ReferenceEquals(other, card) || other.Visibility != Visibility.Visible) continue;
                var otherBounds = GetCardRootBounds(other);
                if (!otherBounds.IsEmpty && startBounds.IntersectsWith(otherBounds))
                    _dragIgnoredOverlaps.Add(other);
            }
        }
        if (card.Tag is HomeWidgetConfig config)
        {
            _dragBaseX = Canvas.GetLeft(card);
            _dragBaseY = Canvas.GetTop(card);
            if (double.IsNaN(_dragBaseX)) _dragBaseX = config.X;
            if (double.IsNaN(_dragBaseY)) _dragBaseY = config.Y;
        }
        else if (key is not null && _homeTransforms.TryGetValue(key, out var tr))
        {
            _dragBaseX = tr.X;
            _dragBaseY = tr.Y;
        }
        else { _dragBaseX = 0; _dragBaseY = 0; }
        card.CaptureMouse();
        e.Handled = true;
    }

    private void MoveCardDrag(FrameworkElement card, MouseEventArgs e)
    {
        if (!ReferenceEquals(_dragCard, card) || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        var dx = point.X - _lastDragPoint.X;
        var dy = point.Y - _lastDragPoint.Y;
        _lastDragPoint = point;
        if (Math.Abs(dx) < 0.1 && Math.Abs(dy) < 0.1) return;

        if (CanMoveCard(card, dx, 0)) ApplyCardDelta(card, dx, 0);
        if (CanMoveCard(card, 0, dy)) ApplyCardDelta(card, 0, dy);
    }

    private bool CanMoveCard(FrameworkElement card, double dx, double dy)
    {
        var bounds = GetCardRootBounds(card);
        if (bounds.IsEmpty) return false;
        var proposed = new Rect(bounds.X + dx, bounds.Y + dy, bounds.Width, bounds.Height);
        const double edge = 8;
        if (proposed.Left < edge || proposed.Top < edge ||
            proposed.Right > ActualWidth - edge || proposed.Bottom > ActualHeight - edge)
            return false;

        foreach (var other in HomeCardElements())
        {
            if (ReferenceEquals(other, card) || _dragIgnoredOverlaps.Contains(other) ||
                other.Visibility != Visibility.Visible ||
                other.ActualWidth <= 0 || other.ActualHeight <= 0) continue;
            var otherBounds = GetCardRootBounds(other);
            if (otherBounds.IsEmpty) continue;
            if (proposed.IntersectsWith(otherBounds)) return false;
        }
        return true;
    }

    private Rect GetCardRootBounds(FrameworkElement card)
    {
        if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return Rect.Empty;
        try
        {
            return card.TransformToAncestor(this).TransformBounds(
                new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        }
        catch { return Rect.Empty; }
    }

    private IEnumerable<FrameworkElement> HomeCardElements()
    {
        foreach (var card in _homeCards) yield return card;
        if (HomeWidgetCanvas is null) yield break;
        foreach (var child in HomeWidgetCanvas.Children)
            if (child is FrameworkElement element) yield return element;
    }

    private void ApplyCardDelta(FrameworkElement card, double dx, double dy)
    {
        if (card.Tag is HomeWidgetConfig)
        {
            var left = Canvas.GetLeft(card);
            var top = Canvas.GetTop(card);
            Canvas.SetLeft(card, (double.IsNaN(left) ? 0 : left) + dx);
            Canvas.SetTop(card, (double.IsNaN(top) ? 0 : top) + dy);
        }
        else if (_dragKey is not null && _homeTransforms.TryGetValue(_dragKey, out var tr))
        {
            tr.X += dx;
            tr.Y += dy;
        }
    }

    private void EndCardDrag(FrameworkElement card, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(_dragCard, card)) return;
        card.ReleaseMouseCapture();
        if (card.Tag is HomeWidgetConfig config)
        {
            config.X = Canvas.GetLeft(card);
            config.Y = Canvas.GetTop(card);
        }
        else if (_dragKey is not null && _homeTransforms.TryGetValue(_dragKey, out var tr))
        {
            LauncherApp.I.Settings.HomeOffsets[_dragKey] = new HomeCardOffset { X = tr.X, Y = tr.Y };
        }
        _dragCard = null;
        _dragKey = null;
        _dragIgnoredOverlaps.Clear();
        LauncherApp.I.SaveDebounced();
    }

    private static bool IsInteractive(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button || current is ComboBox || current is TextBox ||
                current is CheckBox || current is Slider || current is ListBox) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void ResetHomeLayout_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Show(Window.GetWindow(this), "恢复初始界面",
                "会清除自定义卡片、卡片位置和全部样式设置，确定恢复吗？", "恢复", "取消", danger: true))
            return;
        var settings = LauncherApp.I.Settings;
        settings.ShowHomeQuick = true;
        settings.ShowHomeRecent = true;
        settings.ShowHomeSkin = true;
        settings.ShowHomeLog = true;
        settings.ShowHomeFeatured = true;
        settings.ShowHomeStats = true;
        settings.ShowHome3D = true;
        settings.ShowCardBorders = true;
        settings.HomeOffsets.Clear();
        settings.HomeStyles.Clear();
        settings.HomeWidgets.Clear();
        foreach (var transform in _homeTransforms.Values)
        {
            transform.X = 0;
            transform.Y = 0;
        }
        foreach (var scale in _homeScales.Values)
        {
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
        foreach (var rotate in _homeRotates.Values) rotate.Angle = 0;
        foreach (var card in _homeCards)
        {
            card.Opacity = 1;
            if (_homeCardKeys.TryGetValue(card, out var key)) ApplyCardStyle(card, key);
        }
        LauncherApp.I.Save();
        BuildHomeWidgets();
        ApplyHomeLayout();
        LauncherApp.I.Notify("已恢复初始界面", "卡片位置、样式和自定义卡片已重置");
    }

    private void EditHome_Click(object sender, RoutedEventArgs e)
    {
        _homeEditing = !_homeEditing;
        HomeEditBar.Visibility = _homeEditing ? Visibility.Visible : Visibility.Collapsed;
        EditHomeButton.Content = _homeEditing ? "退出编辑" : "自定义主页";
        foreach (var card in _homeCards) card.Cursor = _homeEditing ? Cursors.SizeAll : Cursors.Arrow;
        BuildHomeWidgets();
    }

    private void ToggleAddCardPopup(object sender, RoutedEventArgs e)
    {
        AddCardPopup.IsOpen = !AddCardPopup.IsOpen;
    }

    private void AddBuiltinCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        var s = LauncherApp.I.Settings;
        switch (key)
        {
            case "quick": s.ShowHomeQuick = true; break;
            case "recent": s.ShowHomeRecent = true; break;
            case "log": s.ShowHomeLog = true; break;
            case "featured": s.ShowHomeFeatured = true; break;
            case "skin3d": s.ShowHome3D = true; break;
        }
        LauncherApp.I.Save();
        ApplyHomeLayout();
        AddCardPopup.IsOpen = false;
    }

    private void AddWidgetCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string type }) return;
        AddHomeWidget(type);
        AddCardPopup.IsOpen = false;
    }

    private void AddClockWidget_Click(object sender, RoutedEventArgs e) => AddHomeWidget("clock");
    private void AddImageWidget_Click(object sender, RoutedEventArgs e) => AddHomeWidget("image");
    private void AddNewsWidget_Click(object sender, RoutedEventArgs e) => AddHomeWidget("news");

    private void AddHomeWidget(string type)
    {
        var settings = LauncherApp.I.Settings;
        var config = new HomeWidgetConfig
        {
            Type = type,
            X = 360 + (settings.HomeWidgets.Count % 3) * 24,
            Y = 120 + (settings.HomeWidgets.Count % 5) * 26
        };
        settings.HomeWidgets.Add(config);
        LauncherApp.I.Save();
        BuildHomeWidgets();
    }

    private void BuildHomeWidgets()
    {
        if (HomeWidgetCanvas is null) return;
        HomeWidgetCanvas.Children.Clear();
        _clockTexts.Clear();
        foreach (var config in LauncherApp.I.Settings.HomeWidgets)
        {
            if (!config.Visible) continue;
            var card = CreateHomeWidget(config);
            HomeWidgetCanvas.Children.Add(card);
        }
        ApplyCardBorders();
    }

    private FrameworkElement CreateHomeWidget(HomeWidgetConfig config)
    {
        var card = new Border
        {
            Width = Math.Clamp(config.Width, 180, 520),
            MinHeight = 104,
            Background = (Brush)FindResource("HomeCardBrush"),
            BorderBrush = (Brush)FindResource("HomeCardBorderBrush"),
            BorderThickness = new Thickness(LauncherApp.I.Settings.ShowCardBorders ? 1 : 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Tag = config,
            Cursor = _homeEditing ? Cursors.SizeAll : Cursors.Arrow
        };
        Canvas.SetLeft(card, config.X);
        Canvas.SetTop(card, config.Y);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        var label = new TextBlock
        {
            Text = config.Type switch { "clock" => "时钟", "image" => "图片", _ => "MC 新闻" },
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA8, 0xB0))
        };
        header.Children.Add(label);
        var handle = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x70, 0x7C, 0x84)),
            Cursor = Cursors.SizeAll,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = _homeEditing ? Visibility.Visible : Visibility.Collapsed,
            ToolTip = "拖动卡片"
        };
        handle.PreviewMouseLeftButtonDown += (_, e) => StartCardDrag(card, null, e);
        header.Children.Add(handle);
        var close = new Button
        {
            Content = "×",
            Style = (Style)FindResource("LinkButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = _homeEditing ? Visibility.Visible : Visibility.Collapsed,
            FontSize = 13
        };
        close.Click += (_, _) =>
        {
            LauncherApp.I.Settings.HomeWidgets.RemoveAll(w => w.Id == config.Id);
            LauncherApp.I.Save();
            BuildHomeWidgets();
        };
        header.Children.Add(close);
        root.Children.Add(header);

        FrameworkElement content;
        switch (config.Type)
        {
            case "clock":
                var clock = new TextBlock
                {
                    Text = DateTime.Now.ToString("HH:mm:ss"),
                    FontFamily = (FontFamily)FindResource("DisplayFont"),
                    FontSize = 26,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextBrush"),
                    Margin = new Thickness(0, 3, 0, 0)
                };
                clock.Tag = "clock";
                _clockTexts.Add(clock);
                var date = new TextBlock
                {
                    Text = DateTime.Now.ToString("yyyy-MM-dd  dddd"),
                    FontSize = 9.5,
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    Margin = new Thickness(0, 1, 0, 0)
                };
                content = new StackPanel { Children = { clock, date } };
                break;
            case "image":
                var stack = new StackPanel();
                if (!string.IsNullOrWhiteSpace(config.Data) && File.Exists(config.Data))
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(config.Data);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        const double maxWidth = 496.0;
                        const double maxHeight = 360.0;
                        var imageScale = Math.Min(1.0, Math.Min(maxWidth / bitmap.PixelWidth, maxHeight / bitmap.PixelHeight));
                        var displayWidth = Math.Max(100, bitmap.PixelWidth * imageScale);
                        var displayHeight = Math.Max(1, bitmap.PixelHeight * imageScale);
                        card.Width = Math.Max(180, displayWidth + 24);
                        stack.Children.Add(new Image
                        {
                            Source = bitmap,
                            Width = displayWidth,
                            Height = displayHeight,
                            Stretch = Stretch.Fill,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Margin = new Thickness(0, 4, 0, 0)
                        });
                    }
                    catch { }
                }
                if (stack.Children.Count == 0)
                    stack.Children.Add(new TextBlock
                    {
                        Text = "选择一张本地图片作为卡片内容",
                        FontSize = 10,
                        Foreground = (Brush)FindResource("TextMutedBrush"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 12, 0, 8)
                    });
                var pick = new Button
                {
                    Content = "选择图片",
                    Style = (Style)FindResource("SoftLink"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Visibility = _homeEditing ? Visibility.Visible : Visibility.Collapsed
                };
                pick.Click += (_, _) =>
                {
                    var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
                    if (dialog.ShowDialog() != true) return;
                    config.Data = dialog.FileName;
                    try
                    {
                        var probe = new BitmapImage();
                        probe.BeginInit();
                        probe.CacheOption = BitmapCacheOption.OnLoad;
                        probe.UriSource = new Uri(dialog.FileName);
                        probe.EndInit();
                        var maxWidth = 496.0;
                        var maxHeight = 360.0;
                        var width = (double)probe.PixelWidth;
                        var height = (double)probe.PixelHeight;
                        var scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
                        config.Width = Math.Round(width * scale) + 24;
                    }
                    catch { }
                    LauncherApp.I.Save();
                    BuildHomeWidgets();
                };
                stack.Children.Add(pick);
                content = stack;
                break;
            default:
                var news = new TextBlock
                {
                    Text = "正在加载 Minecraft 新闻…",
                    FontSize = 9.5,
                    Foreground = (Brush)FindResource("TextSubBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                    MaxHeight = 100
                };
                var refresh = new Button
                {
                    Content = "刷新新闻",
                    Style = (Style)FindResource("SoftLink"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                refresh.Click += (_, _) => _ = LoadNewsAsync(news);
                content = new StackPanel { Children = { news, refresh } };
                _ = LoadNewsAsync(news);
                break;
        }
        root.Children.Add(content);
        Grid.SetRow(content, 1);
        card.Child = root;
        var styleKey = "widget:" + config.Id;
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(translate);
        card.RenderTransform = group;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        _homeScales[styleKey] = scale;
        _homeRotates[styleKey] = rotate;
        _homeTransforms[styleKey] = translate;
        _homeCardKeys[card] = styleKey;
        ApplyCardStyle(card, styleKey);
        card.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (!_homeEditing) return;
            ShowCardStyleMenu(card, styleKey);
            e.Handled = true;
        };
        card.PreviewMouseLeftButtonDown += (_, e) => StartCardDrag(card, null, e);
        card.PreviewMouseMove += (_, e) => MoveCardDrag(card, e);
        card.PreviewMouseLeftButtonUp += (_, e) => EndCardDrag(card, e);
        card.LostMouseCapture += (_, _) => { if (ReferenceEquals(_dragCard, card)) _dragCard = null; };
        return card;
    }

    private async Task LoadNewsAsync(TextBlock target)
    {
        try
        {
            var json = await HttpService.GetStringAsync("https://launchercontent.mojang.com/news.json", CancellationToken.None);
            var root = JsonNode.Parse(json) as JsonObject;
            var entries = root?["entries"] as JsonArray;
            if (entries is null) { target.Text = "暂时无法获取新闻"; return; }
            var lines = new List<string>();
            foreach (var entry in entries.Take(4))
            {
                var title = entry?["title"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(title)) lines.Add("• " + title);
            }
            target.Text = lines.Count == 0 ? "暂时没有新闻" : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            target.Text = "新闻加载失败：" + ex.Message;
        }
    }

    private void UpdateClockWidgets()
    {
        var now = DateTime.Now;
        foreach (var clock in _clockTexts) clock.Text = now.ToString("HH:mm:ss");
    }

    private void AnimateLogPanel()
    {
        if (HomeLogPanel is null) return;
        if (!_homeTransforms.TryGetValue("log", out var translate))
            return;
        var baseY = translate.Y;
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(baseY + 10, baseY, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        HomeLogPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void ApplyLogBodyAnimation()
    {
        if (LogBody is null) return;
        if (_logPanelOpen)
        {
            if (_logBodyOpen) return;
            _logBodyOpen = true;
            LogBody.Visibility = Visibility.Visible;
            LogBody.BeginAnimation(MaxHeightProperty, new DoubleAnimation(0, 230, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            LogBody.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
        else
        {
            if (!_logBodyOpen) return;
            _logBodyOpen = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) =>
            {
                if (!_logPanelOpen) LogBody.Visibility = Visibility.Collapsed;
            };
            LogBody.BeginAnimation(MaxHeightProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
            LogBody.BeginAnimation(OpacityProperty, fade);
        }
    }

    private FrameworkElement? FindHomeCard(string key)
    {
        foreach (var pair in _homeCardKeys)
            if (string.Equals(pair.Value, key, StringComparison.OrdinalIgnoreCase))
                return pair.Key;
        if (key.StartsWith("widget:", StringComparison.Ordinal))
        {
            var id = key["widget:".Length..];
            return HomeWidgetCanvas.Children.OfType<Border>()
                .FirstOrDefault(b => b.Tag is HomeWidgetConfig c && c.Id == id);
        }
        return null;
    }

    private void ToggleCardBorders_Click(object sender, RoutedEventArgs e)
    {
        var settings = LauncherApp.I.Settings;
        settings.ShowCardBorders = !settings.ShowCardBorders;
        LauncherApp.I.Save();
        ApplyCardBorders();
    }

    private void ApplyCardBorders()
    {
        var settings = LauncherApp.I.Settings;
        var thickness = settings.ShowCardBorders ? new Thickness(1) : new Thickness(0);
        foreach (var card in new[]
                 {
                     HomeSkin3DPanel, HomeRecentPanel, HomeFeaturedPanel, HomeLogPanel,
                     HomeSkinPanel, HomeInstancePanel
                 })
            if (card is not null) card.BorderThickness = thickness;

        if (HomeWidgetCanvas is not null)
            foreach (var border in HomeWidgetCanvas.Children.OfType<Border>())
                border.BorderThickness = thickness;

        if (CardBorderButton is not null)
            CardBorderButton.Content = settings.ShowCardBorders ? "边缘线：开" : "边缘线：关";
    }

    private void AnimateHomeCards()
    {
        var mode = LauncherApp.I.Settings.AnimationMode;
        if (mode is not ("smooth" or "fancy")) return;
        var cards = _homeCards
            .Concat(HomeWidgetCanvas is null ? Enumerable.Empty<FrameworkElement>() :
                HomeWidgetCanvas.Children.OfType<FrameworkElement>())
            .Where(card => card.Visibility == Visibility.Visible)
            .ToList();
        var accent = ThemeManager.Accent;
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var targetOpacity = card.Opacity;
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = targetOpacity;
            var delay = TimeSpan.FromMilliseconds(Math.Min(i * 45, 420));
            var fade = new DoubleAnimation(0, targetOpacity, TimeSpan.FromMilliseconds(mode == "fancy" ? 420 : 260))
            {
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            card.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void ApplyHomeLayout()
    {
        var s = LauncherApp.I.Settings;
        HomeQuickPanel.Visibility = s.ShowHomeQuick ? Visibility.Visible : Visibility.Collapsed;
        HomeRecentPanel.Visibility = s.ShowHomeRecent ? Visibility.Visible : Visibility.Collapsed;
        HomeSkin3DPanel.Visibility = s.ShowHome3D ? Visibility.Visible : Visibility.Collapsed;
        HomeSkinPanel.Visibility = s.ShowHomeSkin ? Visibility.Visible : Visibility.Collapsed;
        HomeLogPanel.Visibility = s.ShowHomeLog ? Visibility.Visible : Visibility.Collapsed;
        if (LogBody is not null)
        {
            LogBody.Visibility = Visibility.Visible;
            LogBody.MaxHeight = double.PositiveInfinity;
            LogBody.Opacity = 1;
        }
        ApplyCardBorders();
        HomeFeaturedPanel.Visibility = s.ShowHomeFeatured ? Visibility.Visible : Visibility.Collapsed;
        HomeStatsPanel.Visibility = s.ShowHomeStats ? Visibility.Visible : Visibility.Collapsed;
        HomeFeaturedPanel.Margin = s.ShowHomeRecent
            ? new Thickness(0, 258, 22, 0)
            : new Thickness(0, 78, 22, 0);
    }

    private void CustomizeHome_Click(object sender, RoutedEventArgs e)
    {
        var s = LauncherApp.I.Settings;
        var dialog = new HomeCustomizeDialog(s, key =>
        {
            var card = FindHomeCard(key);
            if (card is not null) ShowCardStyleMenu(card, key);
        })
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        bool Value(string key, bool fallback) => dialog.CardVisibility.TryGetValue(key, out var v) ? v : fallback;
        s.ShowHome3D = Value("skin3d", s.ShowHome3D);
        s.ShowHomeQuick = Value("quick", s.ShowHomeQuick);
        s.ShowHomeRecent = Value("recent", s.ShowHomeRecent);
        s.ShowHomeLog = Value("log", s.ShowHomeLog);
        s.ShowHomeFeatured = Value("featured", s.ShowHomeFeatured);
        foreach (var widget in s.HomeWidgets)
        {
            if (dialog.CardVisibility.TryGetValue("widget:" + widget.Id, out var visible))
                widget.Visible = visible;
        }
        LauncherApp.I.Save();
        ApplyHomeLayout();
        BuildHomeWidgets();
        LauncherApp.I.Notify("主页已更新", "卡片显示设置已保存");
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("settings");
    private void OpenDownload_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("download");

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
            MessageDialog.Show(Window.GetWindow(this), "没有可用版本", "先去「版本」页装一个", "知道了");
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

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = _logBuffer.Length > 0 ? _logBuffer.ToString() : LogText;
            Clipboard.SetText(text);
            LauncherApp.I.Notify("日志已复制", $"{text.Length} 个字符");
        }
        catch (Exception ex) { Log.Warn("复制日志失败：" + ex.Message); }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        LogText = "等待游戏输出…";
        HasLog = false;
        StatusLabel.Text = "日志显示已清空";
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var path = LauncherApp.I.Launch.LastLogPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) Shell.OpenFile(path);
        else Shell.OpenLogs();
    }

    private void RefreshHome_Click(object sender, RoutedEventArgs e)
    {
        _statsCacheKey = "";
        LauncherApp.I.ReloadVersions();
        RefreshState();
        LauncherApp.I.Notify("主页已刷新", "版本列表与最近游玩已更新");
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        _logPanelOpen = !_logPanelOpen;
        if (_logPanelOpen) LauncherApp.I.Settings.ShowHomeLog = true;
        ApplyHomeLayout();
        if (_logPanelOpen) AnimateLogPanel();
    }

    private void ToggleSites_Click(object sender, RoutedEventArgs e)
    {
        SitesPanel.Visibility = SitesPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Site_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) Shell.OpenUrl(url);
        SitesPanel.Visibility = Visibility.Collapsed;
    }

    private void ToolTaskmgr_Click(object sender, RoutedEventArgs e) => Shell.Run("taskmgr.exe");
    private void ToolResmon_Click(object sender, RoutedEventArgs e) => Shell.Run("resmon.exe");
    private void ToolCmd_Click(object sender, RoutedEventArgs e) => Shell.Run("cmd.exe");
    private void ToolPaint_Click(object sender, RoutedEventArgs e) => Shell.Run("mspaint.exe");
}
