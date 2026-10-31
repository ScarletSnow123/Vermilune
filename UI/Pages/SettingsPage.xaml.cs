using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            BgPathText.Text = s.BackgroundImage == "none"
                ? "已关闭背景图片"
                : (!string.IsNullOrWhiteSpace(s.BackgroundImage) && File.Exists(s.BackgroundImage)
                    ? "当前：" + s.BackgroundImage
                    : "把图片放进背景文件夹后点刷新");
            LightModeCheck.IsChecked = string.Equals(s.UiThemeMode, "light", StringComparison.OrdinalIgnoreCase);
            BgOpacitySlider.Value = s.BackgroundImageOpacity * 100;
            MemorySlider.Value = Math.Clamp(s.MemoryMb, (int)MemorySlider.Minimum, (int)MemorySlider.Maximum);
            MemoryValueText.Text = $"{s.MemoryMb} MB";
            var total = LauncherApp.I.TotalMemoryMb;
            MemHintText.Text = $"物理内存 {total / 1024.0:0} GB · 推荐 {JavaService.RecommendMemoryMb(total)} MB";
            IsolationCheck.IsChecked = s.VersionIsolation;
            CloseAfterCheck.IsChecked = s.CloseAfterLaunch;
            OptimizeCheck.IsChecked = s.OptimizeJvm;
            FillJava();
            SourceCombo.ItemsSource = new[] { "自动（官方优先）", "Mojang 官方", "BMCLAPI 镜像" };
            SourceCombo.SelectedIndex = s.DownloadSource switch { "official" => 1, "bmclapi" => 2, _ => 0 };
            CurseForgeKeyBox.Text = s.CurseForgeApiKey;
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
            MessageDialog.Show(Window.GetWindow(this), "颜色格式不正确", "请输入 #RRGGBB，例如 #E5B567。", "知道了", danger: true);
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
        LauncherApp.I.Notify("背景文件夹已打开", "把图片放进去后点「刷新」即可使用。");
    }

    private void RefreshBackground_Click(object sender, RoutedEventArgs e)
    {
        var picked = BackgroundService.PickLatest();
        if (string.IsNullOrWhiteSpace(picked))
        {
            MessageDialog.Show(Window.GetWindow(this), "没有找到背景图片",
                "请把 png / jpg / jpeg / bmp 图片放进刚打开的背景文件夹，然后再点刷新。",
                "知道了", danger: true);
            return;
        }
        var s = LauncherApp.I.Settings;
        s.BackgroundImage = picked;
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
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        LoadSettings();
    }

    private void MemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.MemoryMb = (int)e.NewValue;
        MemoryValueText.Text = $"{s.MemoryMb} MB";
        LauncherApp.I.SaveDebounced();
        LauncherApp.I.RaiseSettingsChanged();
    }

    private void UseRecommendedMemory_Click(object sender, RoutedEventArgs e)
    {
        var value = JavaService.RecommendMemoryMb(LauncherApp.I.TotalMemoryMb);
        MemorySlider.Value = Math.Clamp(value, (int)MemorySlider.Minimum, (int)MemorySlider.Maximum);
    }

    private void IsolationCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.VersionIsolation = IsolationCheck.IsChecked == true;
        LauncherApp.I.Save();
        LauncherApp.I.Notify("版本隔离", IsolationCheck.IsChecked == true ? "已开启" : "已关闭");
    }

    private void CloseAfterCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LauncherApp.I.Settings.CloseAfterLaunch = CloseAfterCheck.IsChecked == true;
        LauncherApp.I.Save();
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
        LauncherApp.I.Notify("Java 扫描完成", $"发现 {_java.Count} 个 Java。");
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

    private void OpenCurseForgeKey_Click(object sender, RoutedEventArgs e)
        => Shell.OpenUrl("https://console.curseforge.com/?#/api-keys");

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Show(Window.GetWindow(this), "清空缓存", "将删除头像、接口与下载缓存。不会删除游戏或 Mods。确定吗？",
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
                    "这个目录里没有 versions 文件夹。仍然要把它作为 Minecraft 目录吗？",
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
        LauncherApp.I.Notify("版本已重新扫描", $"当前 {LauncherApp.I.Versions.Versions.Count} 个版本。");
    }
}
