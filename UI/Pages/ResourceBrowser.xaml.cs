using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class ResourceBrowser : UserControl
{
    private ResourceType _type = ResourceType.Modpack;
    private ResourceSource _source = ResourceSource.Modrinth;
    private readonly List<ResourceProject> _projects = new();
    private List<ResourceFileInfo> _files = new();
    private ResourceProject? _selected;
    private int _searchVersion;
    private CancellationTokenSource? _searchCts;
    private bool _installing;
    private bool _initialized;
    private bool _searched;
    private int _offset;
    private CancellationTokenSource? _cts;
    private int _versionToken;

    public ResourceBrowser()
    {
        InitializeComponent();
        Loaded += (_, _) => { EnsureInitialized(); RefreshTargets(); };
    }

    public void OnShown()
    {
        EnsureInitialized();
        RefreshTargets();
        if (!_searched) _ = SearchAsync(true);
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        var versions = LauncherApp.I.Versions.Versions;
        var mcList = new List<string> { "全部版本" };
        mcList.AddRange(versions.Select(v => v.MinecraftVersion).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderByDescending(x => x));
        var currentMc = LauncherApp.I.CurrentVersion?.MinecraftVersion;
        McCombo.ItemsSource = mcList;
        McCombo.SelectedItem = !string.IsNullOrWhiteSpace(currentMc) && mcList.Contains(currentMc) ? currentMc : mcList[0];
        TargetCombo.ItemsSource = versions;
        TargetCombo.SelectedItem = LauncherApp.I.CurrentVersion ?? versions.FirstOrDefault();
        UpdateTypeUi();
    }

    private void TargetCombo_DropDownOpened(object sender, EventArgs e) => RefreshTargets();

    private void RefreshTargets()
    {
        if (LauncherApp.I.Versions.Versions.Count == 0) LauncherApp.I.ReloadVersions();
        var selectedId = (TargetCombo.SelectedItem as VersionInfo)?.Id
                         ?? LauncherApp.I.CurrentVersion?.Id
                         ?? "";
        var versions = LauncherApp.I.Versions.Versions.ToList();
        TargetCombo.ItemsSource = versions;
        TargetCombo.SelectedItem = versions.FirstOrDefault(v =>
                                       string.Equals(v.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                                   ?? versions.FirstOrDefault();
        TargetHint.Text = versions.Count == 0
            ? "没有扫描到版本实例，请先到「版本」页安装或刷新。"
            : "";
        TargetHint.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Type_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton rb) return;
        _type = (rb.Tag as string) switch
        {
            "mod" => ResourceType.Mod,
            "resourcepack" => ResourceType.ResourcePack,
            "shader" => ResourceType.Shader,
            _ => ResourceType.Modpack
        };
        UpdateTypeUi();
        _ = SearchAsync(true);
    }

    private void UpdateTypeUi()
    {
        if (TargetPanel is null) return;
        TargetPanel.Visibility = _type == ResourceType.Modpack ? Visibility.Collapsed : Visibility.Visible;
        LoaderCombo.IsEnabled = _type is ResourceType.Mod or ResourceType.Modpack;
        if (LoaderCombo.SelectedIndex < 0) LoaderCombo.SelectedIndex = 0;
    }

    private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || SourceCombo is null) return;
        _source = SourceCombo.SelectedIndex == 1 ? ResourceSource.CurseForge : ResourceSource.Modrinth;
        var needKey = _source == ResourceSource.CurseForge && !LauncherApp.I.CurseForge.HasKey;
        KeyButton.Visibility = needKey ? Visibility.Visible : Visibility.Collapsed;
        _ = SearchAsync(true);
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchAsync(true);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(true);

    private void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        _offset += 20;
        _ = SearchAsync(false);
    }

    private async Task SearchAsync(bool reset)
    {
        // 快速切换资源类型时，取消上一次搜索，只让最后一次请求更新界面。
        var version = ++_searchVersion;
        try { _searchCts?.Cancel(); } catch { }
        _searchCts = new CancellationTokenSource();

        if (_source == ResourceSource.CurseForge && !LauncherApp.I.CurseForge.HasKey)
        {
            SearchStatus.Text = "";
            KeyButton.Visibility = Visibility.Visible;
            ProjectList.ItemsSource = null;
            EmptyText.Text = "";
            EmptyText.Visibility = Visibility.Collapsed;
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_searchCts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (reset)
            {
                _offset = 0;
                _projects.Clear();
                _selected = null;
                DetailTitle.Text = "选择一个项目";
                DetailSub.Text = "";
                DetailSummary.Text = "";
                VersionCombo.ItemsSource = null;
                InstallButton.IsEnabled = false;
            }
            SearchStatus.Text = "搜索中…";
            var mc = McCombo.SelectedItem as string;
            if (mc == "全部版本") mc = null;
            var loader = SelectedLoader();
            var list = _source == ResourceSource.Modrinth
                ? await LauncherApp.I.Modrinth.SearchAsync(_type, QueryBox.Text?.Trim() ?? "", mc, loader, _offset, timeout.Token)
                : await LauncherApp.I.CurseForge.SearchAsync(_type, QueryBox.Text?.Trim() ?? "", mc, loader, _offset, timeout.Token);
            if (version != _searchVersion) return;
            _projects.AddRange(list);
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _projects;
            SearchStatus.Text = $"{_projects.Count} 个结果";
            EmptyText.Visibility = _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = "没有找到匹配的项目。";
            LoadMoreButton.Visibility = list.Count >= 20 ? Visibility.Visible : Visibility.Collapsed;
            if (reset && _projects.Count > 0) ProjectList.SelectedIndex = 0;
            _searched = true;
        }
        catch (OperationCanceledException)
        {
            if (version != _searchVersion) return;
            SearchStatus.Text = "搜索超时";
            EmptyText.Text = "网络连接超时，请点击「搜索」重试。";
            EmptyText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (version != _searchVersion) return;
            Log.Error(ex, "资源搜索失败");
            SearchStatus.Text = "搜索失败";
            EmptyText.Text = ex.Message;
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private async void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ResourceProject project) return;
        _selected = project;
        DetailTitle.Text = project.Title;
        DetailSub.Text = $"{project.Author} · {project.DownloadsText} 次下载";
        DetailSummary.Text = project.Summary;
        _files = new List<ResourceFileInfo>();
        VersionCombo.ItemsSource = null;
        InstallButton.IsEnabled = false;
        var token = ++_versionToken;
        InstallStatus.Text = "正在加载版本…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var mc = McCombo.SelectedItem as string;
            if (mc == "全部版本") mc = null;
            var loader = SelectedLoader();
            var files = _source == ResourceSource.Modrinth
                ? await LauncherApp.I.Modrinth.GetFilesAsync(project, mc, loader, timeout.Token)
                : await LauncherApp.I.CurseForge.GetFilesAsync(project, mc, loader, timeout.Token);
            if (token != _versionToken) return;
            _files = files;
            VersionCombo.ItemsSource = files.Select(f => f.Display).ToList();
            VersionCombo.SelectedIndex = files.Count > 0 ? 0 : -1;
            InstallStatus.Text = files.Count == 0 ? "没有找到与当前筛选条件兼容的版本。" : $"{files.Count} 个可用版本";
            InstallButton.IsEnabled = files.Count > 0 && !_installing;
        }
        catch (Exception ex)
        {
            if (token == _versionToken)
            {
                InstallStatus.Text = "读取版本失败：" + ex.Message;
                Log.Warn("读取资源版本失败：" + ex.Message);
            }
        }
    }

    private void Version_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstallButton.IsEnabled = VersionCombo.SelectedIndex >= 0 && VersionCombo.SelectedIndex < _files.Count && !_installing;
    }

    private string? SelectedLoader() => LoaderCombo.SelectedIndex switch
    {
        1 => "fabric",
        2 => "forge",
        3 => "neoforge",
        4 => "quilt",
        _ => null
    };

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _installing) return;
        if (VersionCombo.SelectedIndex < 0 || VersionCombo.SelectedIndex >= _files.Count) return;
        var file = _files[VersionCombo.SelectedIndex];
        if (string.IsNullOrWhiteSpace(file.Url))
        {
            MessageDialog.Show(Window.GetWindow(this), "无法安装", "该文件没有可用的下载地址。", "知道了", danger: true);
            return;
        }
        if (_type == ResourceType.Modpack)
        {
            if (!MessageDialog.Show(Window.GetWindow(this), "安装整合包",
                    $"将安装「{_selected.Title}」的 {file.VersionNumber}。\n会先安装对应的加载器，再下载整合包文件，耗时可能较长。",
                    "开始安装", "取消")) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            SetInstalling(true, "准备安装整合包…");
            try
            {
                var progress = new Progress<DownloadProgress>(UpdateProgress);
                var result = _source == ResourceSource.Modrinth
                    ? await LauncherApp.I.Modrinth.InstallModpackAsync(_selected, file, progress, _cts.Token)
                    : await LauncherApp.I.CurseForge.InstallModpackAsync(_selected, file, progress, _cts.Token);
                LauncherApp.I.ReloadVersions();
                SetInstalling(false, $"整合包安装完成：{result.Name}（{result.Files} 个文件）");
                LauncherApp.I.Notify("整合包安装完成", result.Name);
                if (MessageDialog.Show(Window.GetWindow(this), "安装完成",
                        $"已创建版本「{result.Name}」。现在打开版本页吗？", "打开版本页", "留在这里"))
                    App.Window?.NavigateToVersions(result.Id);
            }
            catch (OperationCanceledException) { SetInstalling(false, "已取消安装。"); }
            catch (Exception ex)
            {
                Log.Error(ex, "整合包安装失败");
                SetInstalling(false, "安装失败：" + ex.Message);
                MessageDialog.Show(Window.GetWindow(this), "整合包安装失败", ex.Message, "关闭", danger: true);
            }
            return;
        }

        if (TargetCombo.SelectedItem is not VersionInfo target)
        {
            MessageDialog.Show(Window.GetWindow(this), "选择实例", "请选择要安装到哪个版本实例。", "知道了", danger: true);
            return;
        }
        if (!MessageDialog.Show(Window.GetWindow(this), "安装资源",
                $"将把「{_selected.Title}」安装到实例「{target.DisplayName}」。\n文件：{file.FileName}",
                "开始安装", "取消")) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetInstalling(true, "准备下载…");
        try
        {
            var progress = new Progress<DownloadProgress>(UpdateProgress);
            var result = _source == ResourceSource.Modrinth
                ? (_type == ResourceType.Mod
                    ? await LauncherApp.I.Modrinth.InstallModAsync(_selected, file, target, progress, _cts.Token)
                    : await LauncherApp.I.Modrinth.InstallResourceAsync(_selected, file, target, _type, progress, _cts.Token))
                : (_type == ResourceType.Mod
                    ? await LauncherApp.I.CurseForge.InstallModAsync(_selected, file, target, progress, _cts.Token)
                    : await LauncherApp.I.CurseForge.InstallResourceAsync(_selected, file, target, _type, progress, _cts.Token));
            SetInstalling(false, $"安装完成：{result.Name}");
            LauncherApp.I.Notify("安装完成", result.Name);
        }
        catch (OperationCanceledException) { SetInstalling(false, "已取消下载。"); }
        catch (Exception ex)
        {
            Log.Error(ex, "资源安装失败");
            SetInstalling(false, "安装失败：" + ex.Message);
            MessageDialog.Show(Window.GetWindow(this), "安装失败", ex.Message, "关闭", danger: true);
        }
    }

    private void UpdateProgress(DownloadProgress p)
    {
        InstallStatus.Text = p.Stage + (string.IsNullOrWhiteSpace(p.Detail) ? "" : " · " + p.Detail);
        if (p.Total > 0)
        {
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = Math.Clamp(p.Fraction * 100, 0, 100);
        }
        else
        {
            InstallProgress.IsIndeterminate = true;
        }
    }

    private void SetInstalling(bool installing, string status)
    {
        _installing = installing;
        InstallProgress.Visibility = installing || status.Contains("完成") || status.Contains("失败") ? Visibility.Visible : Visibility.Collapsed;
        if (!installing) InstallProgress.IsIndeterminate = false;
        InstallStatus.Text = status;
        InstallButton.IsEnabled = !installing && _files.Count > 0;
    }

    private void SetKey_Click(object sender, RoutedEventArgs e)
    {
        var key = PromptDialog.Show(Window.GetWindow(this), "CurseForge API Key",
            "粘贴 CurseForge API Key（在 CurseForge 开发者控制台创建）。\n只保存在本机 launcher.json 中。",
            LauncherApp.I.Settings.CurseForgeApiKey, "Key…", value => value.Length < 10 ? "Key 太短。" : null);
        if (key is null) return;
        LauncherApp.I.Settings.CurseForgeApiKey = key;
        LauncherApp.I.Save();
        KeyButton.Visibility = Visibility.Collapsed;
        _ = SearchAsync(true);
    }

    private void OpenWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null && !string.IsNullOrWhiteSpace(_selected.WebsiteUrl)) Shell.OpenUrl(_selected.WebsiteUrl);
        else if (_selected is not null && _source == ResourceSource.Modrinth)
            Shell.OpenUrl("https://modrinth.com/" + (_type == ResourceType.Modpack ? "modpack" : _type == ResourceType.ResourcePack ? "resourcepack" : _type == ResourceType.Shader ? "shader" : "mod") + "/" + _selected.Slug);
        else if (_selected is not null)
            Shell.OpenUrl("https://www.curseforge.com/minecraft/" + (_type == ResourceType.Modpack ? "modpacks" : _type == ResourceType.ResourcePack ? "texture-packs" : _type == ResourceType.Shader ? "shaders" : "mc-mods") + "/" + _selected.Slug);
    }
}
