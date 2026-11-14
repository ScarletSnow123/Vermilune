using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class SettingsPage : UserControl, IPage
{
    private bool _loading = true;
    private bool _rgbGuard;
    private List<JavaInfo> _java = new();

    public SettingsPage()
    {
        InitializeComponent();
        BuildAccentSwatches();
        Loaded += (_, _) => LoadSettings();
    }

    public void OnShown() => LoadSettings();
    public void RefreshState() => LoadSettings();

    private void BuildAccentSwatches()
    {
        AccentPanel.Children.Clear();
        foreach (var hex in ThemeManager.Presets)
        {
            var button = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(ThemeManager.ParseColor(hex, Colors.Gold)),
                ToolTip = hex,
                Tag = hex,
                Style = (Style)Application.Current.Resources["IconButton"]
            };
            button.Click += AccentSwatch_Click;
            AccentPanel.Children.Add(button);
        }
    }

    private void LoadSettings()
    {
        if (!IsLoaded && !_loading) { }
        _loading = true;
        try
        {
            var s = LauncherApp.I.Settings;
            AccentHexBox.Text = s.Accent;
            SyncRgbFromHex(s.Accent);
            OpacitySlider.Value = Math.Clamp(s.UiOpacity * 100, 30, 100);
            OpacityValueText.Text = $"{s.UiOpacity * 100:0}%";
            var bgMode = s.BackgroundMode is "custom" or "wallpaper" ? s.BackgroundMode : "default";
            BgModeDefault.IsChecked = bgMode == "default";
            BgModeCustom.IsChecked = bgMode == "custom";
            BgModeWallpaper.IsChecked = bgMode == "wallpaper";
            CustomBackgroundPanel.Visibility = bgMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
            SceneWallpaperRow.Visibility = bgMode == "wallpaper" ? Visibility.Visible : Visibility.Collapsed;
            SceneAnimatedCheck.IsChecked = s.WallpaperSceneAnimated;
            BgPathText.Text = bgMode switch
            {
                "wallpaper" => "跟随 Wallpaper Engine 当前壁纸；未检测到时自动隐藏背景",
                "custom" when !string.IsNullOrWhiteSpace(s.BackgroundImage) && s.BackgroundImage != "none" &&
                               File.Exists(s.BackgroundImage) => "当前：" + s.BackgroundImage,
                "custom" => "选择图片或视频文件，支持 png / jpg / gif / mp4 / webm",
                _ => "使用纯色背景"
            };
            LightModeCheck.IsChecked = string.Equals(s.UiThemeMode, "light", StringComparison.OrdinalIgnoreCase);
            var motion = s.AnimationMode is "off" or "fancy" ? s.AnimationMode : "smooth";
            MotionOff.IsChecked = motion == "off";
            MotionSmooth.IsChecked = motion == "smooth";
            MotionFancy.IsChecked = motion == "fancy";
            VisualEffectsCheck.IsChecked = s.EnableVisualEffects;
            BlurDelaySlider.Value = Math.Clamp(s.BlurDelayMs, 360, 1000);
            BlurDelayValueText.Text = $"{(int)BlurDelaySlider.Value} ms";
            BlurDelayPanel.Visibility = s.EnableVisualEffects ? Visibility.Visible : Visibility.Collapsed;
            BlurDelayPanel.Height = s.EnableVisualEffects ? 96 : 0;
            BlurDelayPanel.Opacity = s.EnableVisualEffects ? 1 : 0;
            BgOpacitySlider.Value = s.BackgroundImageOpacity * 100;
            IsolationCombo.ItemsSource = new[] { "隔离所有实例", "不隔离" };
            IsolationCombo.SelectedIndex = s.VersionIsolation ? 0 : 1;
            WindowTitleBox.Text = s.GameWindowTitle;
            CustomInfoBox.Text = s.CustomInfo;
            VisibilityCombo.ItemsSource = new[] { "游戏启动后仍保持不变", "游戏启动后隐藏启动器", "游戏启动后退出启动器" };
            VisibilityCombo.SelectedIndex = s.LauncherVisibility switch { "hide" => 1, "close" => 2, _ => 0 };
            PriorityCombo.ItemsSource = new[] { "普通", "高（优先保证游戏运行）", "低（让其他程序优先）" };
            PriorityCombo.SelectedIndex = s.ProcessPriority switch { "high" => 1, "below" => 2, _ => 0 };
            WindowSizeCombo.ItemsSource = new[] { "默认", "1280 × 720", "1600 × 900", "1920 × 1080" };
            WindowSizeCombo.SelectedIndex = s.WindowSize switch { "1280x720" => 1, "1600x900" => 2, "1920x1080" => 3, _ => 0 };
            AuthModeCombo.ItemsSource = new[] { "设备代码流" };
            AuthModeCombo.SelectedIndex = 0;
            IpProtocolCombo.ItemsSource = new[] { "Java 默认", "优先 IPv4", "优先 IPv6" };
            IpProtocolCombo.SelectedIndex = s.IpProtocol switch { "ipv4" => 1, "ipv6" => 2, _ => 0 };
            OptimizeCheck.IsChecked = s.OptimizeJvm;
            FillJava();
            SourceCombo.ItemsSource = new[] { "自动（官方优先）", "Mojang 官方", "BMCLAPI 镜像" };
            SourceCombo.SelectedIndex = s.DownloadSource switch { "official" => 1, "bmclapi" => 2, _ => 0 };
            CurseForgeKeyBox.Text = s.CurseForgeApiKey;
            CurseForgeProxyBox.Text = s.CurseForgeProxyUrl;
            CurseForgeProxyTokenBox.Text = s.CurseForgeProxyToken;
            McPathText.Text = LauncherApp.I.MinecraftDir;
            CacheHint.Text = "头像、接口与下载缓存 · " + AppPaths.CacheDir;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取设置页失败");
        }
        finally { _loading = false; }
    }

    private void FillJava()
    {
        _java = LauncherApp.I.Java.All.ToList();
        var items = new List<string> { "自动选择（推荐）" };
        items.AddRange(_java.Select(j => $"Java {j.Major}  ·  {j.Version}  ·  {j.Path}"));
        JavaCombo.ItemsSource = items;
        var s = LauncherApp.I.Settings;
        var index = 0;
        if (!string.IsNullOrWhiteSpace(s.JavaPath))
        {
            var hit = _java.FindIndex(j => string.Equals(SafeFull(j.Path), SafeFull(s.JavaPath), StringComparison.OrdinalIgnoreCase));
            if (hit >= 0) index = hit + 1;
        }
        JavaCombo.SelectedIndex = index;
    }

    private static string SafeFull(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            AccentHexBox.Text = hex;
            ApplyAccent(hex);
        }
    }

    private void ApplyAccent_Click(object sender, RoutedEventArgs e)
    {
        var hex = AccentHexBox.Text.Trim();
        if (!IsHex(hex))
        {
            MessageDialog.Show(Window.GetWindow(this), "颜色格式不正确", "填 #RRGGBB，比如 #E5B567", "知道了", danger: true);
            return;
        }
        ApplyAccent(hex.StartsWith('#') ? hex : "#" + hex);
    }

    private static bool IsHex(string s)
    {
        s = s.TrimStart('#');
        return s.Length == 6 && s.All(c => Uri.IsHexDigit(c));
    }

    private void ApplyAccent(string hex, bool syncRgb = true)
    {
        var s = LauncherApp.I.Settings;
        s.Accent = hex;
        LauncherApp.I.SaveDebounced();
        LauncherApp.I.RaiseSettingsChanged();
        AccentHexBox.Text = hex;
        if (syncRgb) SyncRgbFromHex(hex);
    }

    private void SyncRgbFromHex(string hex)
    {
        if (RgbRSlider is null || RgbPreview is null) return;
        var color = ThemeManager.ParseColor(hex, Colors.Gold);
        _rgbGuard = true;
        try
        {
            RgbRSlider.Value = color.R;
            RgbGSlider.Value = color.G;
            RgbBSlider.Value = color.B;
            RgbPreview.Background = new SolidColorBrush(color);
            RgbHexText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        finally { _rgbGuard = false; }
    }

    private void Rgb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _rgbGuard || RgbRSlider is null) return;
        var hex = $"#{(byte)RgbRSlider.Value:X2}{(byte)RgbGSlider.Value:X2}{(byte)RgbBSlider.Value:X2}";
        ApplyAccent(hex, syncRgb: false);
        RgbPreview.Background = new SolidColorBrush(ThemeManager.ParseColor(hex, Colors.Gold));
        RgbHexText.Text = hex;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.UiOpacity = Math.Clamp(e.NewValue / 100.0, 0.3, 1.0);
        OpacityValueText.Text = $"{e.NewValue:0}%";
        LauncherApp.I.SaveDebounced();
        LauncherApp.I.RaiseSettingsChanged();
    }

    private void SceneAnimated_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.WallpaperSceneAnimated = SceneAnimatedCheck.IsChecked == true;
        LauncherApp.I.Save();
        LauncherApp.I.Notify("背景已更新", LauncherApp.I.Settings.WallpaperSceneAnimated
            ? "场景壁纸使用动态预览（低清）"
            : "场景壁纸使用高清静帧");
    }

    private void BackgroundMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string mode }) return;
        var s = LauncherApp.I.Settings;
        s.BackgroundMode = mode is "custom" or "wallpaper" ? mode : "default";
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        LoadSettings();
    }

    private void ChooseBackgroundFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "背景文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.webm;*.mkv;*.mov;*.m4v|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|视频|*.mp4;*.webm;*.mkv;*.mov;*.m4v|所有文件|*.*",
            Title = "选择背景图片或视频"
        };
        if (dialog.ShowDialog() != true) return;
        var s = LauncherApp.I.Settings;
        s.BackgroundImage = dialog.FileName;
        s.BackgroundMode = "custom";
        s.BackgroundImageOpacity = Math.Max(0.2, s.BackgroundImageOpacity);
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        LoadSettings();
        LauncherApp.I.Notify("背景已更新", Path.GetFileName(dialog.FileName));
    }

    private void BgOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.BackgroundImageOpacity = Math.Clamp(e.NewValue / 100.0, 0, 1);
        LauncherApp.I.SaveDebounced();
        LauncherApp.I.RaiseSettingsChanged();
    }

    private void OpenBackgroundFolder_Click(object sender, RoutedEventArgs e)
    {
        BackgroundService.EnsureFolder();
        Shell.OpenFolder(BackgroundService.Folder);
        LauncherApp.I.Notify("背景文件夹已打开", "把图片或视频放进去后点「刷新」即可使用");
    }

    private void RefreshBackground_Click(object sender, RoutedEventArgs e)
    {
        var picked = BackgroundService.PickLatest();
        if (string.IsNullOrWhiteSpace(picked))
        {
            MessageDialog.Show(Window.GetWindow(this), "没有找到背景图片",
                "把 png / jpg / gif / mp4 / webm 放进背景文件夹，再点刷新",
                "知道了", danger: true);
            return;
        }
        var s = LauncherApp.I.Settings;
        s.BackgroundImage = picked;
        s.BackgroundMode = "custom";
        s.BackgroundImageOpacity = Math.Max(0.2, s.BackgroundImageOpacity);
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        LoadSettings();
        LauncherApp.I.Notify("背景已更新", Path.GetFileName(picked));
    }

    private void LightModeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.UiThemeMode = LightModeCheck.IsChecked == true ? "light" : "dark";
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        var s = LauncherApp.I.Settings;
        s.BackgroundImage = "none";
        s.BackgroundMode = "default";
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        LoadSettings();
    }

    private void IsolationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.VersionIsolation = IsolationCombo.SelectedIndex == 0;
        LauncherApp.I.Save();
        LauncherApp.I.Notify("版本隔离", s.VersionIsolation ? "已开启" : "已关闭");
    }

    private void WindowTitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.GameWindowTitle = WindowTitleBox.Text.Trim();
        LauncherApp.I.SaveDebounced();
    }

    private void CustomInfoBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.CustomInfo = CustomInfoBox.Text.Trim();
        LauncherApp.I.SaveDebounced();
    }

    private void VisibilityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.LauncherVisibility = VisibilityCombo.SelectedIndex switch { 1 => "hide", 2 => "close", _ => "keep" };
        s.CloseAfterLaunch = s.LauncherVisibility == "close";
        LauncherApp.I.Save();
    }

    private void PriorityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.ProcessPriority = PriorityCombo.SelectedIndex switch { 1 => "high", 2 => "below", _ => "normal" };
        LauncherApp.I.Save();
    }

    private void WindowSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.WindowSize = WindowSizeCombo.SelectedIndex switch
        {
            1 => "1280x720",
            2 => "1600x900",
            3 => "1920x1080",
            _ => "default"
        };
        LauncherApp.I.Save();
    }

    private void IpProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.IpProtocol = IpProtocolCombo.SelectedIndex switch { 1 => "ipv4", 2 => "ipv6", _ => "default" };
        LauncherApp.I.Save();
    }

    private void AnimationMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string mode }) return;
        var s = LauncherApp.I.Settings;
        s.AnimationMode = mode is "off" or "fancy" ? mode : "smooth";
        LauncherApp.I.Save();
        LauncherApp.I.Notify("动画效果", s.AnimationMode switch
        {
            "off" => "已关闭",
            "fancy" => "华丽模式：柔和错峰过渡，可叠加模糊",
            _ => "流畅模式"
        });
    }

    private void VisualEffectsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.EnableVisualEffects = VisualEffectsCheck.IsChecked == true;
        s.BlurDelayMs = Math.Clamp((int)BlurDelaySlider.Value, 0, 1000);
        LauncherApp.I.Save();
        AnimateBlurDelayPanel(s.EnableVisualEffects);
        LauncherApp.I.Notify("高级视觉效果", s.EnableVisualEffects ? "已开启" : "已关闭");
    }

    private void BlurDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurDelayValueText is null) return;
        var value = Math.Clamp((int)e.NewValue, 0, 1000);
        BlurDelayValueText.Text = $"{value} ms";
        if (_loading) return;
        LauncherApp.I.Settings.BlurDelayMs = value;
        LauncherApp.I.SaveDebounced();
    }

    private void ResetBlurDelay_Click(object sender, RoutedEventArgs e)
    {
        BlurDelaySlider.Value = 420;
        BlurDelayValueText.Text = "420 ms";
        if (_loading) return;
        LauncherApp.I.Settings.BlurDelayMs = 420;
        LauncherApp.I.SaveDebounced();
    }

    private void AnimateBlurDelayPanel(bool show)
    {
        if (BlurDelayPanel is null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (show)
        {
            BlurDelayPanel.Visibility = Visibility.Visible;
            BlurDelayPanel.BeginAnimation(HeightProperty,
                new DoubleAnimation(BlurDelayPanel.ActualHeight, 96, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            BlurDelayPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(BlurDelayPanel.Opacity, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        }
        else
        {
            var fade = new DoubleAnimation(BlurDelayPanel.Opacity, 0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = ease
            };
            fade.Completed += (_, _) =>
            {
                if (VisualEffectsCheck.IsChecked == true) return;
                BlurDelayPanel.Visibility = Visibility.Collapsed;
                BlurDelayPanel.BeginAnimation(HeightProperty, null);
                BlurDelayPanel.BeginAnimation(OpacityProperty, null);
                BlurDelayPanel.Height = 0;
                BlurDelayPanel.Opacity = 0;
            };
            BlurDelayPanel.BeginAnimation(OpacityProperty, fade);
            BlurDelayPanel.BeginAnimation(HeightProperty,
                new DoubleAnimation(BlurDelayPanel.ActualHeight, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        }
    }

    private void OptimizeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.OptimizeJvm = OptimizeCheck.IsChecked == true;
        LauncherApp.I.Save();
    }

    private async void RescanJava_Click(object sender, RoutedEventArgs e)
    {
        await LauncherApp.I.ScanJavaAsync();
        FillJava();
        LauncherApp.I.Notify("Java 扫描完成", $"发现 {_java.Count} 个 Java");
    }

    private void JavaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.JavaPath = JavaCombo.SelectedIndex <= 0 || JavaCombo.SelectedIndex - 1 >= _java.Count
            ? ""
            : _java[JavaCombo.SelectedIndex - 1].Path;
        LauncherApp.I.Save();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.DownloadSource = SourceCombo.SelectedIndex switch { 1 => "official", 2 => "bmclapi", _ => "auto" };
        HttpService.Source = s.DownloadSource;
        LauncherApp.I.Save();
        LauncherApp.I.Notify("下载源已切换", SourceCombo.SelectedItem?.ToString() ?? "");
    }

    private void OpenCacheFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(AppPaths.CacheDir);
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(AppPaths.DataDir);
    private void OpenMcFolder_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(LauncherApp.I.MinecraftDir);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Shell.OpenLogs();
    private void GoMore_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("more");

    private void SaveCurseForgeKey_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.Settings.CurseForgeApiKey = CurseForgeKeyBox.Text.Trim();
        LauncherApp.I.Save();
        LauncherApp.I.Notify("CurseForge Key", string.IsNullOrEmpty(LauncherApp.I.Settings.CurseForgeApiKey) ? "已清空" : "已保存");
    }

    private void SaveCurseForgeProxy_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.Settings.CurseForgeProxyUrl = CurseForgeProxyBox.Text.Trim().TrimEnd('/');
        LauncherApp.I.Save();
        LauncherApp.I.Notify("CurseForge 中转", string.IsNullOrEmpty(LauncherApp.I.Settings.CurseForgeProxyUrl) ? "已清空，恢复直连" : "已保存");
    }

    private void SaveCurseForgeProxyToken_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.Settings.CurseForgeProxyToken = CurseForgeProxyTokenBox.Text.Trim();
        LauncherApp.I.Save();
        LauncherApp.I.Notify("中转令牌", string.IsNullOrEmpty(LauncherApp.I.Settings.CurseForgeProxyToken) ? "已清空" : "已保存");
    }

    private void OpenCurseForgeKey_Click(object sender, RoutedEventArgs e)
        => Shell.OpenUrl("https://console.curseforge.com/?#/api-keys");

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Show(Window.GetWindow(this), "清空缓存", "将删除头像、接口与下载缓存\n不会删除游戏或 Mods\n确定吗？",
                "清空", "取消", danger: true)) return;
        try
        {
            if (Directory.Exists(AppPaths.CacheDir))
            {
                foreach (var dir in Directory.GetDirectories(AppPaths.CacheDir)) Directory.Delete(dir, true);
                foreach (var file in Directory.GetFiles(AppPaths.CacheDir)) File.Delete(file);
            }
            Directory.CreateDirectory(AppPaths.SkinCacheDir);
            LauncherApp.I.Notify("缓存已清空", AppPaths.CacheDir);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Window.GetWindow(this), "清空失败", ex.Message, "知道了", danger: true);
        }
    }

    private void ChangeMcDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Minecraft 目录（包含 versions 文件夹）",
            InitialDirectory = Directory.Exists(LauncherApp.I.MinecraftDir) ? LauncherApp.I.MinecraftDir : AppPaths.ExeDir,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var dir = dialog.FolderName;
        if (!Directory.Exists(Path.Combine(dir, "versions")) && !Directory.Exists(Path.Combine(dir, ".minecraft")))
        {
            if (!MessageDialog.Show(Window.GetWindow(this), "目录可能不正确",
                    "这个目录里没有 versions 文件夹\n仍然要把它作为 Minecraft 目录吗？",
                    "仍然使用", "取消", danger: true)) return;
        }
        LauncherApp.I.SetMinecraftDir(dir);
        LauncherApp.I.ReloadVersions();
        LoadSettings();
        LauncherApp.I.Notify("游戏目录已更改", dir);
    }

    private void RescanVersions_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.ReloadVersions();
        LauncherApp.I.Notify("版本已重新扫描", $"当前 {LauncherApp.I.Versions.Versions.Count} 个版本");
    }
}
