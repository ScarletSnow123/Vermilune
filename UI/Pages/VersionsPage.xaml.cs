using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public sealed class ModRow : INotifyPropertyChanged
{
    public ModInfo Info { get; init; } = null!;
    public string FilePath => Info.FilePath;
    public string Title => string.IsNullOrWhiteSpace(Info.Name) ? Info.FileName : Info.Name;
    public bool Enabled => Info.Enabled;
    public string SubTitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Info.Id)) parts.Add(Info.Id);
            if (!string.IsNullOrWhiteSpace(Info.Version)) parts.Add(Info.Version);
            parts.Add(Info.FileName);
            return string.Join(" · ", parts);
        }
    }
    public string SizeText => Info.Size > 1024 * 1024
        ? $"{Info.Size / 1024.0 / 1024.0:0.0} MB"
        : $"{Math.Max(1, Info.Size / 1024)} KB";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public partial class VersionsPage : UserControl, IPage
{
    private List<VersionInfo> _all = new();
    private VersionInfo? _selected;
    private ResolvedVersion? _resolved;
    private InstanceSettings _instance = new();
    private bool _loading;
    private bool _loaded;
    private string _pendingSelect = "";
    private List<JavaInfo> _javaChoices = new();
    private List<ModRow> _modRows = new();

    public VersionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; ReloadVersions(); } };
    }

    public void OnShown() => ReloadVersions();

    public void RefreshState() => ReloadVersions();

    public void SelectVersion(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        _pendingSelect = id;
        var v = _all.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (v is not null)
        {
            VersionList.SelectedItem = v;
            VersionList.ScrollIntoView(v);
            _pendingSelect = "";
            TabModify.IsChecked = true;
        }
    }

    private void ReloadVersions()
    {
        if (!IsLoaded && !_loaded) return;
        var current = _selected?.Id ?? (string.IsNullOrEmpty(_pendingSelect) ? LauncherApp.I.Settings.LastVersion : _pendingSelect);
        _all = LauncherApp.I.Versions.Refresh();
        ApplyVersionFilter();
        NoVersionsText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var target = _all.FirstOrDefault(v => string.Equals(v.Id, current, StringComparison.OrdinalIgnoreCase))
                     ?? _all.FirstOrDefault();
        if (target is not null)
        {
            VersionList.SelectedItem = target;
            _pendingSelect = "";
        }
        else
        {
            _selected = null;
            _resolved = null;
            HeaderName.Text = "选择一个版本";
            HeaderLoader.Text = "—";
            HeaderMc.Text = "—";
            HeaderMods.Text = "—";
            HeaderInitial.Text = "V";
            HeaderIconImage.Source = null;
            PanelModify.IsEnabled = false;
        }
    }

    private void ApplyVersionFilter()
    {
        var q = VersionSearch.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              v.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              v.MinecraftVersion.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                              v.LoaderBadge.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        VersionList.ItemsSource = filtered;
        if (_selected is not null && filtered.Contains(_selected)) VersionList.SelectedItem = _selected;
    }

    private void VersionSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        ApplyVersionFilter();
    }

    private void RefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.ReloadVersions();
        ReloadVersions();
        LauncherApp.I.Notify("版本列表已刷新", "已重新扫描 versions 目录。");
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (VersionList.SelectedItem is not VersionInfo v) return;
        _selected = v;
        LoadVersion(v);
    }

    private void LoadVersion(VersionInfo v)
    {
        _loading = true;
        try
        {
            _resolved = LauncherApp.I.Versions.Resolve(v.Id);
            _instance = LauncherApp.I.Versions.GetInstance(v.Id);
            HeaderName.Text = v.DisplayName;
            HeaderInitial.Text = v.Initial;
            HeaderIconImage.Source = LoadIcon(v.IconPath);
            HeaderLoader.Text = v.LoaderBadge;
            HeaderMc.Text = string.IsNullOrEmpty(v.MinecraftVersion) ? "未知" : v.MinecraftVersion;
            HeaderMods.Text = v.ModCount + " Mods";

            OvMc.Text = string.IsNullOrEmpty(v.MinecraftVersion) ? "未知" : v.MinecraftVersion;
            OvLoader.Text = v.LoaderBadge;
            OvJava.Text = "Java " + (v.JavaMajor > 0 ? v.JavaMajor : 17);
            var gameDir = LauncherApp.I.Versions.GetGameDir(v.Id, _instance, LauncherApp.I.Settings.VersionIsolation);
            OvGameDir.Text = gameDir;
            OvMods.Text = v.ModCount.ToString();
            OvMemory.Text = LauncherApp.I.DefaultMemoryFor(v, _instance) + " MB";
            var recent = LauncherApp.I.Settings.Recent.FirstOrDefault(r => string.Equals(r.Id, v.Id, StringComparison.OrdinalIgnoreCase));
            OvLast.Text = recent is null ? "从未" : recent.LastPlayedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm");
            OvIsolate.Text = (_instance.Isolate ?? LauncherApp.I.Settings.VersionIsolation) ? "开启" : "关闭";
            OvJsonPath.Text = v.JsonPath;
            PanelModify.IsEnabled = true;
            LoadModify(v);
            if (TabMods.IsChecked == true) LoadMods(v);
            if (TabLog.IsChecked == true) LoadLog(v);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载版本详情失败");
            HeaderName.Text = "加载失败";
        }
        finally { _loading = false; }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelOverview is null) return;
        if (!_loading) { }
        var tag = (sender as RadioButton)?.Tag as string ?? "overview";
        PanelOverview.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PanelModify.Visibility = tag == "modify" ? Visibility.Visible : Visibility.Collapsed;
        PanelMods.Visibility = tag == "mods" ? Visibility.Visible : Visibility.Collapsed;
        PanelLog.Visibility = tag == "log" ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is null) return;
        if (tag == "mods") LoadMods(_selected);
        else if (tag == "log") LoadLog(_selected);
        else if (tag == "modify" && !_loading) LoadModify(_selected);
    }

    private void GoModify_Click(object sender, RoutedEventArgs e) => TabModify.IsChecked = true;

    private void LoadModify(VersionInfo v)
    {
        var s = LauncherApp.I.Settings;
        var memory = LauncherApp.I.DefaultMemoryFor(v, _instance);
        MemSlider.Value = Math.Clamp(memory, (int)MemSlider.Minimum, (int)MemSlider.Maximum);
        MemValue.Text = $"{memory} MB";
        var total = LauncherApp.I.TotalMemoryMb;
        var recommend = JavaService.RecommendMemoryMb(total);
        MemHint.Text = $"物理内存 {total / 1024.0:0} GB · 推荐 {recommend} MB";

        JavaAutoCheck.IsChecked = _instance.AutoJava;
        JavaCombo.IsEnabled = !_instance.AutoJava;
        FillJavaChoices();
        JavaHint.Text = $"该版本建议 Java {v.JavaMajor}" + (string.IsNullOrEmpty(_instance.JavaPath) ? "（当前自动）" : "（已指定）");

        WidthBox.Text = _instance.Width > 0 ? _instance.Width.ToString() : "";
        HeightBox.Text = _instance.Height > 0 ? _instance.Height.ToString() : "";
        FullscreenCheck.IsChecked = _instance.Fullscreen;
        IsolateCombo.SelectedIndex = _instance.Isolate is null ? 0 : (_instance.Isolate.Value ? 1 : 2);
        OptimizeCheck.IsChecked = _instance.OptimizeJvm ?? s.OptimizeJvm;
        JvmArgsBox.Text = _instance.JvmArgs;
        GameArgsBox.Text = _instance.GameArgs;
        SaveStatus.Text = "";
    }

    private void FillJavaChoices()
    {
        _javaChoices = LauncherApp.I.Java.All.ToList();
        var items = new List<string> { "自动选择（推荐）" };
        items.AddRange(_javaChoices.Select(j => $"Java {j.Major}  ·  {j.Version}  ·  {j.Path}"));
        JavaCombo.ItemsSource = items;
        var index = 0;
        if (!_instance.AutoJava && !string.IsNullOrWhiteSpace(_instance.JavaPath))
        {
            var hit = _javaChoices.FindIndex(j => string.Equals(Path.GetFullPath(j.Path), SafeFull(_instance.JavaPath), StringComparison.OrdinalIgnoreCase));
            if (hit >= 0) index = hit + 1;
        }
        JavaCombo.SelectedIndex = index;
    }

    private static string SafeFull(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    private void MemSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MemValue is null) return;
        MemValue.Text = $"{(int)e.NewValue} MB";
    }

    private void UseRecommendedMemory_Click(object sender, RoutedEventArgs e)
    {
        var recommend = JavaService.RecommendMemoryMb(LauncherApp.I.TotalMemoryMb);
        MemSlider.Value = Math.Clamp(recommend, (int)MemSlider.Minimum, (int)MemSlider.Maximum);
    }

    private void JavaAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (JavaCombo is null) return;
        JavaCombo.IsEnabled = JavaAutoCheck.IsChecked != true;
    }

    private async void RescanJava_Click(object sender, RoutedEventArgs e)
    {
        SaveStatus.Text = "正在扫描 Java…";
        SaveStatus.Foreground = (Brush)Application.Current.Resources["TextMutedBrush"];
        await LauncherApp.I.ScanJavaAsync();
        FillJavaChoices();
        SaveStatus.Text = "Java 扫描完成";
        SaveStatus.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
    }

    private void SaveModify_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var width = ParseDimension(WidthBox.Text, 0, 7680, "宽度");
            var height = ParseDimension(HeightBox.Text, 0, 4320, "高度");
            _instance.MemoryMb = (int)MemSlider.Value;
            _instance.AutoJava = JavaAutoCheck.IsChecked == true;
            _instance.JavaPath = _instance.AutoJava || JavaCombo.SelectedIndex <= 0
                ? ""
                : _javaChoices[JavaCombo.SelectedIndex - 1].Path;
            _instance.Width = width;
            _instance.Height = height;
            _instance.Fullscreen = FullscreenCheck.IsChecked == true;
            _instance.Isolate = IsolateCombo.SelectedIndex switch { 1 => true, 2 => false, _ => null };
            _instance.OptimizeJvm = OptimizeCheck.IsChecked == true;
            _instance.JvmArgs = JvmArgsBox.Text.Trim();
            _instance.GameArgs = GameArgsBox.Text.Trim();
            InstanceStore.Save(_selected.Id, _instance);
            SaveStatus.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
            SaveStatus.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
            LauncherApp.I.Save();
            LauncherApp.I.Notify("实例设置已保存", _selected.DisplayName);
            LoadVersion(_selected);
            SaveStatus.Text = "已保存";
            SaveStatus.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
        }
        catch (Exception ex)
        {
            SaveStatus.Text = ex.Message;
            SaveStatus.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        }
    }

    private static int ParseDimension(string text, int min, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!int.TryParse(text.Trim(), out var value) || value < min || value > max)
            throw new InvalidOperationException($"{name}必须是 {min}-{max} 之间的整数，或留空。");
        return value;
    }

    private void ResetModify_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (!MessageDialog.Show(Window.GetWindow(this), "恢复默认",
                "将清除此实例的内存、Java、窗口、隔离与高级参数设置，改用全局设置。确定继续吗？",
                "恢复默认", "取消", danger: true)) return;
        _instance = new InstanceSettings();
        InstanceStore.Save(_selected.Id, _instance);
        LoadModify(_selected);
        SaveStatus.Text = "已恢复默认";
        SaveStatus.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
    }

    private int _modLoadToken;
    private async void LoadMods(VersionInfo v)
    {
        var token = ++_modLoadToken;
        try
        {
            var modsDir = LauncherApp.I.Versions.GetModsDir(v.Id, _instance, LauncherApp.I.Settings.VersionIsolation);
            ModSummary.Text = "正在扫描 Mods…";
            var report = await Task.Run(() => ModScanner.Scan(modsDir, v.Loader, v.MinecraftVersion));
            if (token != _modLoadToken || _selected?.Id != v.Id) return;
            _modRows = report.Mods.Where(m => !m.Bundled).Select(m => new ModRow { Info = m }).ToList();
            ModSummary.Text = $"{report.EnabledCount} 个启用 · {report.DisabledCount} 个禁用 · {report.ModsDir}";
            ModHint.Text = report.Issues.Count > 0
                ? $"检测到 {report.Issues.Count} 个问题，可点击「分析冲突」查看。"
                : "开启/关闭会重命名 .jar 文件；不会删除内容。";
            ApplyModFilter();
        }
        catch (Exception ex)
        {
            if (token == _modLoadToken) ModSummary.Text = "读取 Mods 失败：" + ex.Message;
        }
    }

    private void ApplyModFilter()
    {
        var q = ModSearch.Text?.Trim() ?? "";
        var filtered = _modRows.Where(m => string.IsNullOrEmpty(q) ||
            m.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.Info.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.Info.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ModList.ItemsSource = filtered;
        ModEmpty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModFilterInfo.Text = $"{filtered.Count} / {_modRows.Count}";
    }

    private void ModSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        ApplyModFilter();
    }

    private void ModToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string path } check) return;
        var row = _modRows.FirstOrDefault(m => string.Equals(m.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        var enable = check.IsChecked == true;
        var (ok, error, _) = ModScanner.Toggle(row.Info.FilePath, enable);
        if (!ok)
        {
            check.IsChecked = row.Info.Enabled;
            MessageDialog.Show(Window.GetWindow(this), "无法切换 Mod", error, "知道了", danger: true);
            return;
        }
        if (_selected is not null) LoadMods(_selected);
    }

    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        var name = Path.GetFileName(path);
        if (!MessageDialog.Show(Window.GetWindow(this), "移除 Mod", $"将 {name} 移到回收站？", "删除", "取消", danger: true)) return;
        var (ok, error) = ModScanner.Delete(path);
        if (!ok) MessageDialog.Show(Window.GetWindow(this), "删除失败", error, "知道了", danger: true);
        if (_selected is not null) LoadMods(_selected);
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var dir = LauncherApp.I.Versions.GetModsDir(_selected.Id, _instance, LauncherApp.I.Settings.VersionIsolation);
            Directory.CreateDirectory(dir);
            Shell.OpenFolder(dir);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Window.GetWindow(this), "打开 Mods 文件夹失败", ex.Message, "关闭", danger: true);
        }
    }

    private async void AnalyzeMods_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var v = _selected;
        var inst = _instance;
        var analysis = await Task.Run(() => CrashAnalyzer.Analyze(v.Id,
            LauncherApp.I.Versions.GetGameDir(v.Id, inst, LauncherApp.I.Settings.VersionIsolation),
            v.Loader, v.MinecraftVersion, sinceUtc: DateTime.UtcNow.AddDays(-7)));
        LauncherApp.I.LastAnalysis = analysis;
        App.Window?.NavigateTo("error");
    }

    private void LoadLog(VersionInfo v)
    {
        try
        {
            var dir = LauncherApp.I.Versions.GetGameDir(v.Id, _instance, LauncherApp.I.Settings.VersionIsolation);
            var latest = Path.Combine(dir, "logs", "latest.log");
            LogPathText.Text = latest;
            if (!File.Exists(latest))
            {
                LogBox.Text = "没有找到 latest.log。先启动一次游戏，日志会出现在这里。";
                return;
            }
            using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            var take = (int)Math.Min(300 * 1024, length);
            fs.Seek(-take, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            LogBox.Text = reader.ReadToEnd();
            LogBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogBox.Text = "读取日志失败：" + ex.Message;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var dir = LauncherApp.I.Versions.GetGameDir(_selected.Id, _instance, LauncherApp.I.Settings.VersionIsolation);
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        Shell.OpenFolder(logs);
    }

    private async void AnalyzeCrash_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var v = _selected;
        var inst = _instance;
        var analysis = await Task.Run(() => CrashAnalyzer.Analyze(v.Id,
            LauncherApp.I.Versions.GetGameDir(v.Id, inst, LauncherApp.I.Settings.VersionIsolation),
            v.Loader, v.MinecraftVersion, sinceUtc: DateTime.UtcNow.AddDays(-7)));
        LauncherApp.I.LastAnalysis = analysis;
        App.Window?.NavigateTo("error");
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) App.Window?.LaunchVersion(_selected.Id);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        Shell.OpenFolder(Path.Combine(LauncherApp.I.MinecraftDir, "versions", _selected.Id));
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (LauncherApp.I.Launch.IsRunning && string.Equals(LauncherApp.I.Launch.CurrentVersionId, _selected.Id, StringComparison.OrdinalIgnoreCase))
        {
            MessageDialog.Show(Window.GetWindow(this), "游戏正在运行", "请先关闭游戏，再重命名这个版本。", "知道了", danger: true);
            return;
        }
        var old = _selected.Id;
        var newName = PromptDialog.Show(Window.GetWindow(this), "重命名版本", "输入新的版本文件夹名称：", old,
            "例如：My Pack 1.21.8", name =>
            {
                if (!VersionService.IsValidName(name)) return "名称包含非法字符，或为空。";
                if (!string.Equals(name, old, StringComparison.Ordinal) && LauncherApp.I.Versions.VersionExists(name))
                    return "已存在同名版本。";
                return null;
            });
        if (newName is null || newName == old) return;
        var (ok, error) = LauncherApp.I.Versions.Rename(old, newName);
        if (!ok)
        {
            MessageDialog.Show(Window.GetWindow(this), "重命名失败", error, "知道了", danger: true);
            return;
        }
        var s = LauncherApp.I.Settings;
        if (string.Equals(s.LastVersion, old, StringComparison.OrdinalIgnoreCase)) s.LastVersion = newName;
        foreach (var r in s.Recent.Where(r => string.Equals(r.Id, old, StringComparison.OrdinalIgnoreCase))) r.Id = newName;
        LauncherApp.I.Save();
        LauncherApp.I.ReloadVersions();
        _pendingSelect = newName;
        ReloadVersions();
        LauncherApp.I.Notify("已重命名", $"{old} → {newName}");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var v = _selected;
        if (LauncherApp.I.Launch.IsRunning && string.Equals(LauncherApp.I.Launch.CurrentVersionId, v.Id, StringComparison.OrdinalIgnoreCase))
        {
            MessageDialog.Show(Window.GetWindow(this), "游戏正在运行", "请先关闭游戏，再删除这个版本。", "知道了", danger: true);
            return;
        }
        if (!MessageDialog.Show(Window.GetWindow(this), "删除版本",
                $"将版本「{v.DisplayName}」整个文件夹移到回收站？\n存档与 Mods 都会一并删除。",
                "除到回收站", "取消", danger: true)) return;
        var (ok, error) = LauncherApp.I.Versions.Delete(v.Id);
        if (!ok)
        {
            MessageDialog.Show(Window.GetWindow(this), "删除失败", error, "知道了", danger: true);
            return;
        }
        var s = LauncherApp.I.Settings;
        s.Recent.RemoveAll(r => string.Equals(r.Id, v.Id, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(s.LastVersion, v.Id, StringComparison.OrdinalIgnoreCase)) s.LastVersion = "";
        LauncherApp.I.Save();
        _selected = null;
        LauncherApp.I.ReloadVersions();
        ReloadVersions();
        LauncherApp.I.Notify("版本已删除", v.DisplayName);
    }

    private static ImageSource? LoadIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
