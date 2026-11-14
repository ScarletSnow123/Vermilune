using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    private DispatcherTimer? _wallpaperTimer;
    private DispatcherTimer? _wallpaperDebounce;
    private FileSystemWatcher? _wallpaperWatcher;
    private string _lastWallpaperPath = "";
    private DispatcherTimer? _gifTimer;
    private List<BitmapSource>? _gifFrames;
    private int _gifIndex;
    private WebWallpaperRenderer? _webRenderer;
    private string _webPath = "";

    public MainWindow()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _toastTimer.Tick += (_, _) => HideToast();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
                Closed += (_, _) =>
        {
            LauncherApp.I.Frp.Stop();
            LauncherApp.I.Server.Dispose();
            _webRenderer?.Dispose();
            _gifTimer?.Stop();
            _wallpaperWatcher?.Dispose();
            _wallpaperDebounce?.Stop();
        };
        StateChanged += (_, _) => { };
    }

    public void OnAppReady()
    {
        _ready = true;
        var s = LauncherApp.I.Settings;
        try
        {
            var work = SystemParameters.WorkArea;
            var targetWidth = s.WindowWidth >= MinWidth ? s.WindowWidth : 1280;
            var targetHeight = s.WindowHeight >= MinHeight ? s.WindowHeight : 800;
            Width = Math.Max(MinWidth, Math.Min(targetWidth, work.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(targetHeight, work.Height - 24));
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
        }
        catch { }

        if (s.Maximized) ToggleMaximize();
        ApplyTheme();
        WallpaperEngineService.HighQualityPreviewReady += _ =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LauncherApp.I.Settings.BackgroundMode == "wallpaper") ApplyTheme();
            }));
        _wallpaperTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _wallpaperTimer.Tick += (_, _) => RefreshWallpaperIfChanged();
        _wallpaperTimer.Start();
        StartWallpaperWatcher();
        _updatingSliders = true;
        OpacitySlider.Value = s.UiOpacity * 100;
        BgOpacitySlider.Value = s.BackgroundImageOpacity * 100;
        _updatingSliders = false;
        OpacityValue.Text = $"{s.UiOpacity * 100:0}%";
        RailVersion.Text = "v1.3.2-beta";

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
        string? backgroundPath = null;
        if (s.BackgroundMode == "custom" && !string.IsNullOrWhiteSpace(s.BackgroundImage) &&
            s.BackgroundImage != "none" && File.Exists(s.BackgroundImage))
            backgroundPath = s.BackgroundImage;
        else if (s.BackgroundMode == "wallpaper")
            backgroundPath = WallpaperEngineService.GetCurrentWallpaper();

        var hasBackground = !string.IsNullOrWhiteSpace(backgroundPath) && File.Exists(backgroundPath);
        ThemeManager.Apply(s.Accent, s.UiOpacity, hasBackground,
            string.Equals(s.UiThemeMode, "light", StringComparison.OrdinalIgnoreCase));
        if (string.Equals(s.BackgroundMode, "wallpaper", StringComparison.OrdinalIgnoreCase))
            _lastWallpaperPath = backgroundPath ?? "";
        if (hasBackground && BackgroundService.IsVideo(backgroundPath!))
            ApplyVideoBackground(backgroundPath!, s.BackgroundImageOpacity);
        else if (hasBackground && string.Equals(Path.GetExtension(backgroundPath), ".html", StringComparison.OrdinalIgnoreCase))
            ApplyWebBackground(backgroundPath!, s.BackgroundImageOpacity);
        else if (hasBackground)
            ApplyImageBackground(backgroundPath!, s.BackgroundImageOpacity);
        else
            ClearBackground();
    }

    private void StartWallpaperWatcher()
    {
        try
        {
            var config = WallpaperEngineService.FindConfig();
            if (string.IsNullOrWhiteSpace(config) || !File.Exists(config)) return;
            _wallpaperWatcher = new FileSystemWatcher(Path.GetDirectoryName(config)!, Path.GetFileName(config))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _wallpaperWatcher.Changed += (_, _) => ScheduleWallpaperRefresh();
            _wallpaperWatcher.Renamed += (_, _) => ScheduleWallpaperRefresh();
            _wallpaperWatcher.Created += (_, _) => ScheduleWallpaperRefresh();
        }
        catch (Exception ex)
        {
            Log.Warn("监听 Wallpaper Engine 配置失败：" + ex.Message);
        }
    }

    private void ScheduleWallpaperRefresh()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _wallpaperDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _wallpaperDebounce.Stop();
            _wallpaperDebounce.Tick -= WallpaperDebounce_Tick;
            _wallpaperDebounce.Tick += WallpaperDebounce_Tick;
            _wallpaperDebounce.Start();
        }));
    }

    private void WallpaperDebounce_Tick(object? sender, EventArgs e)
    {
        _wallpaperDebounce?.Stop();
        RefreshWallpaperIfChanged();
    }

    private void RefreshWallpaperIfChanged()
    {
        if (!_ready || !string.Equals(LauncherApp.I.Settings.BackgroundMode, "wallpaper", StringComparison.OrdinalIgnoreCase)) return;
        var path = WallpaperEngineService.GetCurrentWallpaper() ?? "";
        if (!string.Equals(path, _lastWallpaperPath, StringComparison.OrdinalIgnoreCase))
            ApplyTheme();
    }

    private void ApplyImageBackground(string path, double opacity)
    {
        BgVideo.Stop();
        BgVideo.Visibility = Visibility.Collapsed;
        BgVideo.Source = null;
        StopAnimatedBackgrounds();
        var s = LauncherApp.I.Settings;
        try
        {
            if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                StartGifBackground(path, opacity);
                return;
            }
            if (!string.Equals(_loadedBackgroundPath, path, StringComparison.OrdinalIgnoreCase) || _loadedBackground is null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                var targetWidth = Math.Max(1280, Math.Min(1920, (int)SystemParameters.WorkArea.Width));
                bmp.DecodePixelWidth = targetWidth;
                bmp.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                bmp.EndInit();
                bmp.Freeze();
                BgImageBrush.ImageSource = null;
                _loadedBackground = bmp;
                _loadedBackgroundPath = path;
            }
            BgImageBrush.ImageSource = _loadedBackground;
            BgImageBorder.Visibility = Visibility.Visible;
            BgImageBorder.Opacity = opacity;
        }
        catch (Exception ex)
        {
            Log.Warn("加载背景图失败：" + ex.Message);
            BgImageBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyVideoBackground(string path, double opacity)
    {
        StopAnimatedBackgrounds();
        BgImageBrush.ImageSource = null;
        BgImageBorder.Visibility = Visibility.Collapsed;
        try
        {
            if (!string.Equals(_loadedBackgroundPath, path, StringComparison.OrdinalIgnoreCase) ||
                BgVideo.Source is null)
            {
                BgVideo.Stop();
                BgVideo.Source = new Uri(path, UriKind.Absolute);
                _loadedBackgroundPath = path;
            }
            BgVideo.Visibility = Visibility.Visible;
            BgVideo.Opacity = opacity;
            BgVideo.Play();
        }
        catch (Exception ex)
        {
            Log.Warn("加载背景视频失败：" + ex.Message);
            BgVideo.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearBackground()
    {
        StopAnimatedBackgrounds();
        BgImageBrush.ImageSource = null;
        _loadedBackground = null;
        _loadedBackgroundPath = "";
        BgImageBorder.Visibility = Visibility.Collapsed;
        BgVideo.Stop();
        BgVideo.Source = null;
        BgVideo.Visibility = Visibility.Collapsed;
    }

    private void StopAnimatedBackgrounds()
    {
        _gifTimer?.Stop();
        _gifTimer = null;
        _gifFrames = null;
        _gifIndex = 0;
        _webPath = "";
        try { _webRenderer?.Dispose(); } catch { }
        _webRenderer = null;
    }

    private void StartGifBackground(string path, double opacity)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            _gifFrames = decoder.Frames.Cast<BitmapSource>().ToList();
            if (_gifFrames.Count == 0) return;
            foreach (var frame in _gifFrames) if (frame.CanFreeze) frame.Freeze();
            _gifIndex = 0;
            BgImageBorder.Visibility = Visibility.Visible;
            BgImageBorder.Opacity = opacity;
            BgImageBrush.ImageSource = _gifFrames[0];
            _gifTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _gifTimer.Tick += (_, _) =>
            {
                if (_gifFrames is null || _gifFrames.Count == 0) return;
                _gifIndex = (_gifIndex + 1) % _gifFrames.Count;
                BgImageBrush.ImageSource = _gifFrames[_gifIndex];
            };
            _gifTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("加载动态背景失败：" + ex.Message);
            BgImageBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyWebBackground(string path, double opacity)
    {
        BgVideo.Stop();
        BgVideo.Visibility = Visibility.Collapsed;
        BgVideo.Source = null;
        if (_webRenderer is not null && string.Equals(_webPath, path, StringComparison.OrdinalIgnoreCase))
        {
            BgImageBorder.Visibility = Visibility.Visible;
            BgImageBorder.Opacity = opacity;
            return;
        }
        StopAnimatedBackgrounds();
        _webPath = path;
        BgImageBorder.Visibility = Visibility.Visible;
        BgImageBorder.Opacity = opacity;
        var renderer = new WebWallpaperRenderer();
        _webRenderer = renderer;
        renderer.FrameReady += frame => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_webRenderer, renderer)) return;
            BgImageBrush.ImageSource = frame;
            BgImageBorder.Visibility = Visibility.Visible;
            BgImageBorder.Opacity = opacity;
        }));
        renderer.Failed += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            Log.Warn("Web 壁纸不可用：" + message);
            BgImageBorder.Visibility = Visibility.Collapsed;
        }));
        _ = renderer.StartAsync(path);
    }

    private void BgVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        try
        {
            BgVideo.Position = TimeSpan.Zero;
            BgVideo.Play();
        }
        catch { }
    }

    private void BgVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Log.Warn("背景视频播放失败：" + e.ErrorException.Message);
        BgVideo.Visibility = Visibility.Collapsed;
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

        AnimatePageIn(page);
    }

    private static (TranslateTransform Translate, ScaleTransform Scale, RotateTransform Rotate) EnsurePageTransform(FrameworkElement page)
    {
        if (page.RenderTransform is TransformGroup group &&
            group.Children.OfType<TranslateTransform>().FirstOrDefault() is { } existingTranslate &&
            group.Children.OfType<ScaleTransform>().FirstOrDefault() is { } existingScale &&
            group.Children.OfType<RotateTransform>().FirstOrDefault() is { } existingRotate)
            return (existingTranslate, existingScale, existingRotate);

        var translate = new TranslateTransform();
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform();
        var created = new TransformGroup();
        created.Children.Add(translate);
        created.Children.Add(scale);
        created.Children.Add(rotate);
        page.RenderTransform = created;
        page.RenderTransformOrigin = new Point(0.5, 0.5);
        return (translate, scale, rotate);
    }

    private void AnimatePageIn(FrameworkElement page)
    {
        var mode = LauncherApp.I.Settings.AnimationMode;
        if (mode is not ("off" or "fancy")) mode = "smooth";
        var (translate, scale, rotate) = EnsurePageTransform(page);
        page.BeginAnimation(OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        translate.Y = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        rotate.Angle = 0;
        page.Opacity = 1;
        page.CacheMode = null;
        page.Effect = null;
        PageHost.Effect = null;

        if (mode == "off") return;

        if (mode == "smooth")
        {
            page.Opacity = 1;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            if (LauncherApp.I.Settings.EnableVisualEffects)
            {
                var blurMs = Math.Clamp(LauncherApp.I.Settings.BlurDelayMs, 360, 1000);
                var blur = new BlurEffect { Radius = 0 };
                PageHost.Effect = blur;
                var blurIn = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(blurMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                blurIn.Completed += (_, _) =>
                {
                    if (ReferenceEquals(PageHost.Content, page))
                    {
                        PageHost.Effect = null;
                        page.CacheMode = null;
                    }
                };
                blur.BeginAnimation(BlurEffect.RadiusProperty, blurIn);
            }
            return;
        }

        // 华丽模式：柔和的错峰缩放与上移，不使用旋转、粒子和辉光。
        page.Opacity = 1;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        translate.Y = 0;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        var scaleIn = new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn.Clone());
        rotate.Angle = 0;
        PlayAccentSweep();

        // 华丽模式也可以叠加高级模糊；模糊放在 PageHost 上，缩放仍作用于页面本身。
        if (LauncherApp.I.Settings.EnableVisualEffects)
        {
            var blurMs = Math.Clamp(LauncherApp.I.Settings.BlurDelayMs, 360, 1000);
            var blur = new BlurEffect { Radius = 0 };
            PageHost.Effect = blur;
            var blurIn = new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(Math.Min(360, blurMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            blurIn.Completed += (_, _) =>
            {
                if (ReferenceEquals(PageHost.Content, page)) PageHost.Effect = null;
            };
            blur.BeginAnimation(BlurEffect.RadiusProperty, blurIn);
        }
    }

    private void PlayAccentSweep()
    {
        if (FxSweep is null) return;
        FxSweep.BeginAnimation(OpacityProperty, null);
        FxSweep.Visibility = Visibility.Visible;
        FxSweep.Width = Math.Max(1, ActualWidth);
        if (FxSweep.RenderTransform is not TranslateTransform move)
        {
            move = new TranslateTransform(-FxSweep.Width, 0);
            FxSweep.RenderTransform = move;
        }
        move.BeginAnimation(TranslateTransform.XProperty, null);
        move.X = -FxSweep.Width;
        FxSweep.Opacity = 0.9;
        var slide = new DoubleAnimation(-FxSweep.Width, FxSweep.Width, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(620));
        fade.Completed += (_, _) => FxSweep.Visibility = Visibility.Collapsed;
        move.BeginAnimation(TranslateTransform.XProperty, slide);
        FxSweep.BeginAnimation(OpacityProperty, fade);
    }

    public void NavigateToVersions(string versionId)
    {
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            LauncherApp.I.SetCurrentVersion(versionId);
            if (_pages.TryGetValue("launch", out var launchPage) && launchPage is LaunchPage lp)
            {
                var version = LauncherApp.I.Versions.Find(versionId);
                if (version is not null) lp.SelectedVersion = version;
            }
        }
        NavigateTo("versions");
        if (_pages.TryGetValue("versions", out var page) && page is VersionsPage vp)
            vp.SelectVersion(versionId);
        BringToFront();
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        // 从模态对话框返回时，Windows 有时不会自动把 Owner 提到前台。
        Topmost = true;
        Topmost = false;
        Focus();
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
            "multiplayer" => new MultiplayerPage(),
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
        NavMultiplayer.IsChecked = key == "multiplayer";
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
            var wa = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, Math.Min(_restoreBounds.Width, wa.Width));
            Height = Math.Max(MinHeight, Math.Min(_restoreBounds.Height, wa.Height));
            Left = Math.Clamp(_restoreBounds.Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
            Top = Math.Clamp(_restoreBounds.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
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
            var mode = LauncherApp.I.Settings.AnimationMode;
            if (mode == "off")
            {
                ToastBar.BeginAnimation(OpacityProperty, null);
                ToastBar.Opacity = 1;
            }
            else
            {
                var fancy = mode == "fancy";
                ToastBar.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fancy ? 240 : 140))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                if (ToastBar.RenderTransform is not TranslateTransform toastMove)
                {
                    toastMove = new TranslateTransform(0, 0);
                    ToastBar.RenderTransform = toastMove;
                }
                toastMove.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(fancy ? 14 : 6, 0, TimeSpan.FromMilliseconds(fancy ? 300 : 160))
                    {
                        EasingFunction = new BackEase { Amplitude = fancy ? 0.25 : 0.1, EasingMode = EasingMode.EaseOut }
                    });
            }
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
            if (!MessageDialog.Show(this, "正在启动", "游戏仍在准备启动，现在退出会中断启动\n确定要退出吗？",
                    "退出", "继续等待", danger: true))
            {
                e.Cancel = true;
                return;
            }
            app.Launch.Cancel();
        }
        else if (app.Launch.IsRunning)
        {
            if (!MessageDialog.Show(this, "游戏正在运行", "关闭启动器不会结束 Minecraft 进程，游戏会继续运行\n确定关闭启动器吗？",
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
            var work = SystemParameters.WorkArea;
            var saveWidth = _isMaximized ? _restoreBounds.Width : Width;
            var saveHeight = _isMaximized ? _restoreBounds.Height : Height;
            s.WindowWidth = (int)Math.Max(MinWidth, Math.Min(saveWidth, work.Width));
            s.WindowHeight = (int)Math.Max(MinHeight, Math.Min(saveHeight, work.Height));
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
                "启动器会自动识别 CurseForge / Modrinth 格式，安装对应加载器并下载文件\n\n" +
                "CurseForge 整合包需要配置 API Key\n\n\n继续吗？",
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
                $"「{result.Name}」已安装\n\n共处理 {result.Files} 个文件，版本已自动创建", "知道了");
        }
        catch (OperationCanceledException)
        {
            MessageDialog.Show(this, "已取消", "整合包安装已取消，已下载的文件会保留", "知道了");
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
            MessageDialog.Show(this, "安装 Mod", "现在没有可用版本，先装个 Minecraft", "知道了");
            return;
        }

        var dir = LauncherApp.I.Versions.GetModsDir(target.Id, InstanceStore.Load(target.Id),
            LauncherApp.I.Settings.VersionIsolation);
        Directory.CreateDirectory(dir);
        var conflicts = mods.Where(f => File.Exists(Path.Combine(dir, Path.GetFileName(f)))).ToList();
        var message = $"将把 {mods.Count} 个 Mod 安装到实例「{target.DisplayName}」";
        if (conflicts.Count > 0) message += $"\n\n其中 {conflicts.Count} 个文件已存在，将继续并覆盖";
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
