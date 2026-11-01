using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vermilune.Core;
using Vermilune.UI;
using Vermilune.UI.Dialogs;
using Vermilune.UI.Pages;

namespace Vermilune;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _toastTimer;
    private string _currentPage = "launch";
    private bool _isMaximized;
    private bool _ready;
    private bool _updatingSliders;
    private bool _closeForGame;
    private Rect _restoreBounds;
    private string _loadedBackgroundPath = "";
    private BitmapImage? _loadedBackground;

    public MainWindow()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) => HideToast();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        StateChanged += (_, _) => { };
    }

    public void OnAppReady()
    {
        _ready = true;
        var s = LauncherApp.I.Settings;
        try
        {
            if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
            {
                Width = s.WindowWidth;
                Height = s.WindowHeight;
            }
            var work = SystemParameters.WorkArea;
            Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
            Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        }
        catch { }

        if (s.Maximized) ToggleMaximize();
        ApplyTheme();
        _updatingSliders = true;
        OpacitySlider.Value = s.UiOpacity * 100;
        BgOpacitySlider.Value = s.BackgroundImageOpacity * 100;
        _updatingSliders = false;
        OpacityValue.Text = $"{s.UiOpacity * 100:0}%";
        RailVersion.Text = "v1.2.13-beta";

        LauncherApp.I.SettingsChanged += OnSettingsChanged;
        LauncherApp.I.VersionsChanged += OnVersionsChanged;
        LauncherApp.I.JavaScanned += OnVersionsChanged;
        LauncherApp.I.Toast += ShowToast;
        LauncherApp.I.Launch.ProgressChanged += OnLaunchProgress;
        LauncherApp.I.Launch.Exited += OnLaunchExited;

        NavigateTo("launch");
        UpdateTitleStatus();
    }

    private void OnSettingsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyTheme();
            UpdateTitleStatus();
        }));
    }

    private void OnVersionsChanged()
    {
        Dispatcher.BeginInvoke(new Action(UpdateTitleStatus));
    }

    private void OnLaunchProgress(LaunchProgress p)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TitleStatus.Text = p.State switch
            {
                LaunchState.Preparing => p.Stage,
                LaunchState.Downloading => p.Stage,
                LaunchState.Starting => "正在启动游戏…",
                LaunchState.Running => "游戏运行中",
                LaunchState.Failed => "启动失败",
                LaunchState.Exited => "游戏已退出",
                _ => "就绪"
            };
        }));
    }

    private void OnLaunchExited(int code, string? crash)
    {
        Dispatcher.BeginInvoke(new Action(UpdateTitleStatus));
    }

    private void ApplyTheme()
    {
        if (!_ready) return;
        var s = LauncherApp.I.Settings;
        if (string.IsNullOrWhiteSpace(s.BackgroundImage))
        {
            var picked = BackgroundService.PickLatest();
            if (!string.IsNullOrWhiteSpace(picked))
            {
                s.BackgroundImage = picked;
                s.BackgroundImageOpacity = Math.Max(0.2, s.BackgroundImageOpacity);
                LauncherApp.I.SaveDebounced();
            }
        }
        var hasImage = s.BackgroundImage != "none" && !string.IsNullOrWhiteSpace(s.BackgroundImage) && File.Exists(s.BackgroundImage);
        ThemeManager.Apply(s.Accent, s.UiOpacity, hasImage,
            string.Equals(s.UiThemeMode, "light", StringComparison.OrdinalIgnoreCase));
        if (hasImage)
        {
            try
            {
                if (!string.Equals(_loadedBackgroundPath, s.BackgroundImage, StringComparison.OrdinalIgnoreCase) || _loadedBackground is null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(s.BackgroundImage, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    _loadedBackground = bmp;
                    _loadedBackgroundPath = s.BackgroundImage;
                }
                BgImageBrush.ImageSource = _loadedBackground;
                BgImageBorder.Visibility = Visibility.Visible;
                BgImageBorder.Opacity = s.BackgroundImageOpacity;
            }
            catch (Exception ex)
            {
                Log.Warn("加载背景图失败：" + ex.Message);
                BgImageBorder.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            BgImageBrush.ImageSource = null;
            _loadedBackground = null;
            _loadedBackgroundPath = "";
            BgImageBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateTitleStatus()
    {
        if (!_ready) return;
        var app = LauncherApp.I;
        if (app.Launch.IsRunning)
        {
            TitleStatus.Text = "游戏运行中";
            return;
        }
        var java = app.Java.All.Count;
        var javaText = java > 0 ? $"Java {java} 个" : (app.JavaScanning ? "扫描 Java…" : "未发现 Java");
        TitleStatus.Text = $"就绪 · {app.Versions.Versions.Count} 个版本 · {javaText}";
    }

    public void NavigateTo(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) key = "launch";
        key = key.ToLowerInvariant();
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
        }
        if (ReferenceEquals(PageHost.Content, page))
        {
            _currentPage = key;
            SetNav(key);
            if (page is IPage same) same.OnShown();
            return;
        }
        _currentPage = key;
        PageHost.Content = page;
        SetNav(key);
        if (page is IPage p) p.OnShown();

        var move = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        page.Opacity = 1;
        if (page.RenderTransform is not TranslateTransform)
            page.RenderTransform = new TranslateTransform(0, 0);
        ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.YProperty, move);
    }

    public void NavigateToVersions(string versionId)
    {
        NavigateTo("versions");
        if (_pages.TryGetValue("versions", out var page) && page is VersionsPage vp)
            vp.SelectVersion(versionId);
    }

    public void LaunchVersion(string versionId)
    {
        var version = LauncherApp.I.Versions.Find(versionId);
        if (version is null) return;
        NavigateTo("launch");
        if (_pages.TryGetValue("launch", out var page) && page is LaunchPage lp)
        {
            lp.SelectedVersion = version;
            LauncherApp.I.SetCurrentVersion(version.Id);
            _ = lp.LaunchSelectedAsync(version);
        }
    }

    private UserControl CreatePage(string key)
    {
        return key switch
        {
            "launch" => new LaunchPage(),
            "download" => new DownloadPage(),
            "versions" => new VersionsPage(),
            "settings" => new SettingsPage(),
            "error" => new ErrorPage(),
            "more" => new MorePage(),
            _ => new LaunchPage()
        };
    }

    private void SetNav(string key)
    {
        NavLaunch.IsChecked = key == "launch";
        NavDownload.IsChecked = key == "download";
        NavVersions.IsChecked = key == "versions";
        NavSettings.IsChecked = key == "settings";
        NavError.IsChecked = key == "error";
        NavMore.IsChecked = key == "more";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton rb) return;
        if (rb.Tag is string key && key != _currentPage) NavigateTo(key);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (_isMaximized) return;
        try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (!_isMaximized)
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);
            var wa = SystemParameters.WorkArea;
            Left = wa.Left;
            Top = wa.Top;
            Width = wa.Width;
            Height = wa.Height;
            _isMaximized = true;
        }
        else
        {
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = Math.Max(MinWidth, _restoreBounds.Width);
            Height = Math.Max(MinHeight, _restoreBounds.Height);
            _isMaximized = false;
        }
        UpdateMaximizeVisuals();
    }

    private void UpdateMaximizeVisuals()
    {
        var radius = _isMaximized ? new CornerRadius(0) : new CornerRadius(16);
        Frame.CornerRadius = radius;
        BgTint.CornerRadius = radius;
        BgImageBorder.CornerRadius = radius;
        MaximizeIcon.Data = (Geometry)Application.Current.Resources[_isMaximized ? "IconRestore" : "IconMaximize"];
        var vis = _isMaximized ? Visibility.Collapsed : Visibility.Visible;
        ThumbLeft.Visibility = vis;
        ThumbRight.Visibility = vis;
        ThumbTop.Visibility = vis;
        ThumbBottom.Visibility = vis;
        ThumbTL.Visibility = vis;
        ThumbTR.Visibility = vis;
        ThumbBL.Visibility = vis;
        ThumbBR.Visibility = vis;
    }

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isMaximized || sender is not Thumb { Tag: string dir }) return;
        var minW = MinWidth;
        var minH = MinHeight;
        var dx = e.HorizontalChange;
        var dy = e.VerticalChange;
        if (dir.Contains('L'))
        {
            var newWidth = Width - dx;
            if (newWidth >= minW) { Width = newWidth; Left += dx; }
        }
        if (dir.Contains('R'))
        {
            var newWidth = Width + dx;
            if (newWidth >= minW) Width = newWidth;
        }
        if (dir.Contains('T'))
        {
            var newHeight = Height - dy;
            if (newHeight >= minH) { Height = newHeight; Top += dy; }
        }
        if (dir.Contains('B'))
        {
            var newHeight = Height + dy;
            if (newHeight >= minH) Height = newHeight;
        }
    }

    private void OpacityButton_Click(object sender, RoutedEventArgs e)
    {
        var s = LauncherApp.I.Settings;
        _updatingSliders = true;
        OpacitySlider.Value = Math.Clamp(s.UiOpacity * 100, 30, 100);
        BgOpacitySlider.Value = Math.Clamp(s.BackgroundImageOpacity * 100, 0, 100);
        _updatingSliders = false;
        OpacityValue.Text = $"{s.UiOpacity * 100:0}%";
        OpacityPopup.IsOpen = !OpacityPopup.IsOpen;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updatingSliders) return;
        var s = LauncherApp.I.Settings;
        s.UiOpacity = Math.Clamp(e.NewValue / 100.0, 0.3, 1.0);
        OpacityValue.Text = $"{e.NewValue:0}%";
        ApplyTheme();
        LauncherApp.I.SaveDebounced();
    }

    private void BgOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updatingSliders) return;
        var s = LauncherApp.I.Settings;
        s.BackgroundImageOpacity = Math.Clamp(e.NewValue / 100.0, 0, 1);
        BgImageBorder.Opacity = s.BackgroundImageOpacity;
        LauncherApp.I.SaveDebounced();
    }

    public void ShowToast(string title, string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ToastText.Text = string.IsNullOrWhiteSpace(title) ? message : $"{title} · {message}";
            ToastBar.Visibility = Visibility.Visible;
            ToastBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }));
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => ToastBar.Visibility = Visibility.Collapsed;
        ToastBar.BeginAnimation(OpacityProperty, fade);
    }

    public void CloseForGame()
    {
        _closeForGame = true;
        SaveBounds();
        Hide();
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeForGame) return;
        var app = LauncherApp.I;
        if (app.Launch.State is LaunchState.Preparing or LaunchState.Downloading or LaunchState.Starting)
        {
            if (!MessageDialog.Show(this, "正在启动", "游戏仍在准备启动，现在退出会中断启动。确定要退出吗？",
                    "退出", "继续等待", danger: true))
            {
                e.Cancel = true;
                return;
            }
            app.Launch.Cancel();
        }
        else if (app.Launch.IsRunning)
        {
            if (!MessageDialog.Show(this, "游戏正在运行", "关闭启动器不会结束 Minecraft 进程，游戏会继续运行。确定关闭启动器吗？",
                    "关闭启动器", "取消"))
            {
                e.Cancel = true;
                return;
            }
        }
        SaveBounds();
    }

    private void SaveBounds()
    {
        try
        {
            var s = LauncherApp.I.Settings;
            if (_isMaximized)
            {
                s.WindowWidth = Math.Max(1100, _restoreBounds.Width);
                s.WindowHeight = Math.Max(700, _restoreBounds.Height);
            }
            else
            {
                s.WindowWidth = Math.Max(1100, Width);
                s.WindowHeight = Math.Max(700, Height);
            }
            s.Maximized = _isMaximized;
            LauncherApp.I.Save();
        }
        catch { }
    }

    private CancellationTokenSource? _dropCts;
    private bool _dropInstalling;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dropInstalling || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) { e.Effects = DragDropEffects.None; return; }
        var supported = files.Any(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                       f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dropInstalling || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) return;
        var archive = files.FirstOrDefault(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var mods = files.Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && File.Exists(f)).ToList();
        if (archive is null)
        {
            if (mods.Count > 0) InstallDroppedMods(mods);
            return;
        }
        if (!File.Exists(archive)) return;

        if (!MessageDialog.Show(this, "安装整合包",
                $"检测到整合包：\n{Path.GetFileName(archive)}\n\n" +
                "启动器会自动识别 CurseForge / Modrinth 格式，安装对应加载器并下载文件。\n" +
                "CurseForge 整合包需要配置 API Key。\n\n继续吗？",
                "开始安装", "取消"))
            return;

        _dropInstalling = true;
        _dropCts = new CancellationTokenSource();
        DropOverlay.Visibility = Visibility.Visible;
        DropProgress.IsIndeterminate = true;
        DropProgress.Value = 0;
        DropStatusText.Text = "正在识别整合包格式…";
        DropCancelButton.IsEnabled = true;

        var progress = new Progress<DownloadProgress>(p =>
        {
            DropStatusText.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
            if (p.Fraction is double fraction)
            {
                DropProgress.IsIndeterminate = false;
                DropProgress.Value = fraction * 100;
            }
            else
            {
                DropProgress.IsIndeterminate = true;
            }
        });

        try
        {
            var result = await LocalModpackService.InstallAsync(archive, Path.GetFileNameWithoutExtension(archive),
                progress, _dropCts.Token);
            LauncherApp.I.ReloadVersions();
            NavigateToVersions(result.Id);
            MessageDialog.Show(this, "整合包安装完成",
                $"「{result.Name}」已安装。\n共处理 {result.Files} 个文件，版本已自动创建。", "知道了");
        }
        catch (OperationCanceledException)
        {
            MessageDialog.Show(this, "已取消", "整合包安装已取消，已下载的文件会保留。", "知道了");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "拖入整合包安装失败");
            MessageDialog.Show(this, "整合包安装失败", ex.Message, "关闭", danger: true);
        }
        finally
        {
            _dropInstalling = false;
            _dropCts?.Dispose();
            _dropCts = null;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void InstallDroppedMods(List<string> mods)
    {
        VersionInfo? target = null;
        if (_pages.TryGetValue("launch", out var page) && page is LaunchPage launch)
            target = launch.SelectedVersion;
        target ??= LauncherApp.I.CurrentVersion;
        if (target is null)
        {
            MessageDialog.Show(this, "安装 Mod", "当前没有可用版本，请先安装一个 Minecraft 版本。", "知道了");
            return;
        }

        var dir = LauncherApp.I.Versions.GetModsDir(target.Id, InstanceStore.Load(target.Id),
            LauncherApp.I.Settings.VersionIsolation);
        Directory.CreateDirectory(dir);
        var conflicts = mods.Where(f => File.Exists(Path.Combine(dir, Path.GetFileName(f)))).ToList();
        var message = $"将把 {mods.Count} 个 Mod 安装到实例「{target.DisplayName}」。";
        if (conflicts.Count > 0) message += $"\n\n其中 {conflicts.Count} 个文件已存在，将继续并覆盖。";
        if (!MessageDialog.Show(this, "安装 Mod", message + "\n\n继续吗？", "安装", "取消")) return;

        try
        {
            foreach (var file in mods)
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), true);
            LauncherApp.I.Notify("Mod 已安装", $"{mods.Count} 个文件 → {target.DisplayName}");
            if (_pages.TryGetValue("versions", out var vp) && vp is VersionsPage versions)
                versions.RefreshState();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "拖入 Mod 安装失败");
            MessageDialog.Show(this, "安装 Mod 失败", ex.Message, "关闭", danger: true);
        }
    }

    private void DropCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _dropCts?.Cancel(); } catch { }
        DropStatusText.Text = "正在取消…";
        DropCancelButton.IsEnabled = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && _currentPage == "versions")
        {
            LauncherApp.I.ReloadVersions();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && _currentPage == "launch")
        {
            if (Keyboard.FocusedElement is TextBox or ComboBox) return;
            if (_pages.TryGetValue("launch", out var page) && page is LaunchPage lp)
            {
                _ = lp.LaunchSelectedAsync(lp.SelectedVersion);
                e.Handled = true;
            }
        }
        if (e.Key == Key.Escape) OpacityPopup.IsOpen = false;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T matched) return matched;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
