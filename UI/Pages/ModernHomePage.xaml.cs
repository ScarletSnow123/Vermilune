using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class ModernHomePage : UserControl, IPage, INotifyPropertyChanged
{
    private readonly LaunchPage _launcher = new();
    private VersionInfo? _selectedVersion;
    private LaunchState _state = LaunchState.Idle;
    private string _instanceName = "未选择实例";
    private string _versionMeta = "";
    private string _accountName = "Player";
    private string _accountSub = "离线账号";
    private string _avatarInitial = "P";
    private string _launchTitle = "启动游戏";
    private string _launchSub = "未选择版本";
    private DateTime _versionPopupClosedUtc;

    public ObservableCollection<VersionInfo> Versions { get; } = new();

    public VersionInfo? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (ReferenceEquals(_selectedVersion, value)) return;
            _selectedVersion = value;
            OnPropertyChanged();
            UpdateVersionInfo();
        }
    }

    public string InstanceName { get => _instanceName; private set => Set(ref _instanceName, value); }
    public string VersionMeta { get => _versionMeta; private set => Set(ref _versionMeta, value); }
    public string AccountName { get => _accountName; private set => Set(ref _accountName, value); }
    public string AccountSub { get => _accountSub; private set => Set(ref _accountSub, value); }
    public string AvatarInitial { get => _avatarInitial; private set => Set(ref _avatarInitial, value); }
    public string LaunchTitle { get => _launchTitle; private set => Set(ref _launchTitle, value); }
    public string LaunchSub { get => _launchSub; private set => Set(ref _launchSub, value); }

    public ModernHomePage()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window)
                window.Deactivated += (_, _) => CloseVersionPopup();
            Refresh();
        };
        LauncherApp.I.Launch.ProgressChanged += OnProgress;
        LauncherApp.I.Launch.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(Refresh));
    }

    public void OnShown() => Refresh();
    public void RefreshState() => Refresh();
    public void LaunchSelected() => _ = _launcher.LaunchSelectedAsync(SelectedVersion);

    private void Refresh()
    {
        try
        {
            var app = LauncherApp.I;
            EnsureHomeTextContrast(app);
            var previous = SelectedVersion?.Id ?? app.Settings.LastVersion;
            Versions.Clear();
            foreach (var version in app.Versions.Versions) Versions.Add(version);
            SelectedVersion = Versions.FirstOrDefault(v => v.Id == previous) ?? Versions.FirstOrDefault();

            var account = app.Settings.Account;
            AccountName = string.IsNullOrWhiteSpace(account.Name) ? "Player" : account.Name;
            AccountSub = account.IsMicrosoft ? "Microsoft 正版"
                : account.IsYggdrasil ? "外置登录"
                : account.IsNide ? "统一通行证" : "离线账号";
            AvatarInitial = AccountName[..1].ToUpperInvariant();
            DateText.Text = DateTime.Now.ToString("yyyy.MM.dd dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

            UpdateVersionInfo();
            _state = app.Launch.State;
            UpdateLaunchUi();
            _ = RefreshAvatarAsync();
            StatusText.Text = app.Launch.State switch
            {
                LaunchState.Running => "游戏运行中",
                LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting => "正在准备启动…",
                LaunchState.Failed => "启动失败",
                _ => ""
            };
        }
        catch (Exception ex) { Log.Warn("新版主页刷新失败：" + ex.Message); }
    }

    private static void EnsureHomeTextContrast(LauncherApp app)
    {
        var res = Application.Current.Resources;
        var settings = app.Settings;
        var hasImage = settings.BackgroundMode is "custom" or "wallpaper";
        if (hasImage)
        {
            res["HomeTextBrush"] = Brushes.White;
            res["HomeTextSubBrush"] = new SolidColorBrush(Color.FromRgb(0xE8, 0xE2, 0xEA));
            res["HomeTextMutedBrush"] = new SolidColorBrush(Color.FromRgb(0xC8, 0xC2, 0xCC));
        }
        else
        {
            res["HomeTextBrush"] = res["TextBrush"];
            res["HomeTextSubBrush"] = res["TextSubBrush"];
            res["HomeTextMutedBrush"] = res["TextMutedBrush"];
        }
    }

    private void UpdateVersionInfo()
    {
        var version = SelectedVersion;
        if (version is null)
        {
            InstanceName = "未选择实例";
            VersionMeta = "还没有可用版本";
            LaunchSub = "未选择版本";
            return;
        }
        try
        {
            var instance = InstanceStore.Load(version.Id);
            InstanceName = version.DisplayName;
            VersionMeta = $"{version.MinecraftVersion} · {version.LoaderBadge} · " +
                          (version.ModCount > 0 ? $"{version.ModCount} 个 Mod" : "无 Mod") +
                          $" · 启动 {LauncherApp.I.DefaultMemoryFor(version, instance)} MB · 建议 Java {version.JavaMajor}";
            if (_state is not (LaunchState.Running or LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting))
                LaunchSub = version.DisplayName;
        }
        catch (Exception ex) { Log.Warn("读取实例信息失败：" + ex.Message); }
    }

    private async Task RefreshAvatarAsync()
    {
        try
        {
            var skin = await LauncherApp.I.Skins.LoadSkinAsync();
            var cape = await LauncherApp.I.Skins.LoadCapeAsync();
            if (skin is not null)
            {
                SkinPreview.SetTextures(skin, cape, LauncherApp.I.Skins.IsSlim);
                AvatarImage.Source = AvatarRenderer.RenderFrontHeadDrawing(skin);
                AvatarImage.Visibility = Visibility.Visible;
                AvatarInitialText.Visibility = Visibility.Collapsed;
            }
            else
            {
                AvatarImage.Visibility = Visibility.Collapsed;
                AvatarInitialText.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_state == LaunchState.Running)
        {
            if (MessageDialog.Show(Window.GetWindow(this), "结束游戏", "要结束现在的 Minecraft 吗？没保存的进度会丢",
                    "结束游戏", "取消", danger: true))
                _launcher.CancelLaunch();
            return;
        }
        if (_state is LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting)
        {
            _launcher.CancelLaunch();
            return;
        }
        LaunchSelected();
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
        LaunchIcon.Data = (Geometry)Application.Current.Resources[_state == LaunchState.Running ? "IconPower" : "IconPlay"];
        LaunchIcon.Fill = _state == LaunchState.Running ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x08, 0x11, 0x0D));
        LaunchIcon.Stroke = _state == LaunchState.Running ? new SolidColorBrush(Color.FromRgb(0x08, 0x11, 0x0D)) : Brushes.Transparent;
        LaunchIcon.StrokeThickness = _state == LaunchState.Running ? 2 : 0;
        LaunchHint.Text = _state == LaunchState.Running ? "STOP" : "ENTER";
        LaunchSub = _state switch
        {
            LaunchState.Running => "游戏运行中",
            LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting => "点击取消",
            _ => SelectedVersion?.DisplayName ?? "未选择版本"
        };
        ProgressPanel.Visibility = _state is LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting
            ? Visibility.Visible : Visibility.Collapsed;
        if (_state == LaunchState.Running) LaunchProgress.Value = 100;
    }

    private void VersionCombo_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // ComboBox 在鼠标悬停到可见项边缘时会 RequestBringIntoView，
        // 导致列表自己滚动；这里只允许键盘/点击选择时滚动。
        if (e.TargetObject is ComboBoxItem)
            e.Handled = true;
    }

    private void VersionButton_Click(object sender, RoutedEventArgs e)
    {
        // StaysOpen=False 会在点到按钮的同一瞬间先关闭弹层，
        // 如果这里再取反就会立刻重新打开。
        if (VersionPopup.IsOpen)
        {
            VersionPopup.IsOpen = false;
            return;
        }
        // 只有「华丽」动画档才给版本列表加弹出动画。
        VersionPopup.PopupAnimation = string.Equals(LauncherApp.I.Settings.AnimationMode, "fancy", StringComparison.OrdinalIgnoreCase)
            ? PopupAnimation.Fade
            : PopupAnimation.None;
        VersionDismissLayer.Visibility = Visibility.Visible;
        VersionPopup.IsOpen = true;
    }

    private void CloseVersionPopup()
    {
        if (VersionPopup is null || !VersionPopup.IsOpen) return;
        VersionPopup.IsOpen = false;
        if (VersionDismissLayer is not null) VersionDismissLayer.Visibility = Visibility.Collapsed;
    }

    private void VersionPopup_Closed(object? sender, EventArgs e)
    {
        _versionPopupClosedUtc = DateTime.UtcNow;
        if (VersionDismissLayer is not null) VersionDismissLayer.Visibility = Visibility.Collapsed;
    }

    private void VersionDismissLayer_Click(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(VersionButton);
        var onButton = point.X >= 0 && point.Y >= 0 &&
                       point.X <= VersionButton.ActualWidth && point.Y <= VersionButton.ActualHeight;
        VersionPopup.IsOpen = false;
        if (onButton) _ = FlashVersionButtonAsync();
        e.Handled = true;
    }

    private async Task FlashVersionButtonAsync()
    {
        VersionButton.Opacity = 0.9;
        await Task.Delay(110);
        VersionButton.Opacity = 1;
    }

    private void VersionItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VersionInfo version }) return;
        SelectedVersion = version;
        LauncherApp.I.SetCurrentVersion(version.Id);
        VersionPopup.IsOpen = false;
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
            try { await LauncherApp.I.Auth.UploadSkinAsync(path, LauncherApp.I.Skins.IsSlim, CancellationToken.None); }
            catch (Exception ex) { MessageDialog.Show(Window.GetWindow(this), "上传皮肤失败", ex.Message, "关闭", danger: true); }
        }
        await RefreshAvatarAsync();
    }

    private async void SwitchCape_Click(object sender, RoutedEventArgs e)
    {
        if (!LauncherApp.I.Settings.Account.IsMicrosoft)
        {
            MessageDialog.Show(Window.GetWindow(this), "披风", "披风只支持 Microsoft 正版账号", "知道了");
            return;
        }
        if (!await LauncherApp.I.EnsureAccountReadyAsync())
        {
            MessageDialog.Show(Window.GetWindow(this), "登录已失效", "先重新登录 Microsoft 账号", "知道了", danger: true);
            return;
        }
        if (CapePickerDialog.Show(Window.GetWindow(this))) await RefreshAvatarAsync();
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (AccountDialog.Show(Window.GetWindow(this))) Refresh();
    }

    private void EditVersion_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is not null) App.Window?.NavigateToVersions(SelectedVersion.Id);
        else App.Window?.NavigateTo("versions");
    }

    private void ManageVersion_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("versions");
    private void OpenDownload_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("download");
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("settings");

    private void OpenInstanceResources_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is null) return;
        var dialog = new InstanceResourcesDialog(SelectedVersion) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion is null) return;
        try { Shell.OpenFolder(LauncherApp.I.Versions.GetGameDir(SelectedVersion.Id, InstanceStore.Load(SelectedVersion.Id), LauncherApp.I.Settings.VersionIsolation)); }
        catch { }
    }

    private void CancelLaunch_Click(object sender, RoutedEventArgs e) => _launcher.CancelLaunch();

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var path = LauncherApp.I.Launch.LastLogPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) Shell.OpenFile(path);
        else Shell.OpenLogs();
    }

    private void OnProgress(LaunchProgress progress)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _state = progress.State;
            StatusText.Text = progress.Stage;
            ProgressText.Text = progress.Stage;
            if (progress.Fraction is double fraction)
            {
                LaunchProgress.IsIndeterminate = false;
                LaunchProgress.Value = Math.Clamp(fraction * 100, 0, 100);
            }
            else LaunchProgress.IsIndeterminate = true;
            UpdateLaunchUi();
        }));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
