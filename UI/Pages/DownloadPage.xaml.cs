using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class DownloadPage : UserControl, IPage
{
    private List<RemoteVersion> _manifest = new();
    private List<RemoteVersion> _filtered = new();
    private RemoteVersion? _selected;
    private CancellationTokenSource? _cts;
    private bool _loaded;
    private bool _installing;
    private bool _loadingFabric;
    private bool _loadingForge;
    private List<ForgeVersionInfo> _forgeVersions = new();
    private string _detailFabricMc = "";
    private string _detailForgeMc = "";
    private string _detailNeoForgeMc = "";
    private string _detailQuiltMc = "";

    public DownloadPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; _ = LoadManifestAsync(false); } };
    }

    public void OnShown()
    {
        SourceText.Text = "下载源：" + SourceName(LauncherApp.I.Settings.DownloadSource);
        if (!_loaded) { _loaded = true; _ = LoadManifestAsync(false); }
    }

    public void RefreshState() { }

    private static string SourceName(string source) => source switch
    {
        "bmclapi" => "BMCLAPI",
        "official" => "Mojang 官方源",
        _ => "自动（官方优先）"
    };

    private async Task LoadManifestAsync(bool force)
    {
        try
        {
            ManifestLoading.Visibility = Visibility.Visible;
            FabricStatus.Text = "正在加载版本清单…";
            var manifest = await LauncherApp.I.Downloads.GetVersionManifestAsync(force, CancellationToken.None);
            var installed = LauncherApp.I.Versions.Versions.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var v in manifest) v.Installed = installed.Contains(v.Id);
            _manifest = manifest;
            ApplyFilter();
            PopulateFabricMc();
            if (PanelForge.Visibility == Visibility.Visible) PopulateForgeMc();
            FabricStatus.Text = "选择 Minecraft 版本后会自动加载 Loader。";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载版本清单失败");
            VersionEmpty.Text = "版本清单加载失败：" + ex.Message;
            VersionEmpty.Visibility = Visibility.Visible;
            FabricStatus.Text = "版本清单加载失败：" + ex.Message;
        }
        finally
        {
            ManifestLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateFabricMc()
    {
        var current = FabricMcCombo.SelectedItem as string;
        var releases = _manifest.Where(v => v.Type == "release").OrderByDescending(v => v.ReleaseTime).Select(v => v.Id).ToList();
        FabricMcCombo.ItemsSource = releases;
        var currentMc = LauncherApp.I.CurrentVersion?.MinecraftVersion;
        if (!string.IsNullOrEmpty(current) && releases.Contains(current)) FabricMcCombo.SelectedItem = current;
        else if (!string.IsNullOrEmpty(currentMc) && releases.Contains(currentMc)) FabricMcCombo.SelectedItem = currentMc;
        else if (releases.Count > 0) FabricMcCombo.SelectedIndex = 0;
    }

    private void ApplyFilter()
    {
        var q = VersionSearch.Text?.Trim() ?? "";
        var typeIndex = TypeFilter.SelectedIndex;
        var list = _manifest.AsEnumerable();
        list = typeIndex switch
        {
            1 => list.Where(v => v.Type == "release"),
            2 => list.Where(v => v.Type == "snapshot"),
            3 => list.Where(v => v.Type is "old_beta" or "old_alpha"),
            _ => list
        };
        if (!string.IsNullOrEmpty(q))
            list = list.Where(v => v.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
        _filtered = list.Take(600).ToList();
        VersionList.ItemsSource = _filtered;
        VersionEmpty.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is not null && _filtered.Contains(_selected)) VersionList.SelectedItem = _selected;
        else if (_filtered.Count > 0) VersionList.SelectedIndex = 0;
    }

    private void VersionSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded) ApplyFilter();
    }

    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) ApplyFilter();
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not RemoteVersion v) return;
        _selected = v;
        ShowDetail(v);
    }

    private void ShowDetail(RemoteVersion v)
    {
        DetailTitle.Text = v.Id;
        DetailSub.Text = $"{v.DisplayType} · {v.ReleaseDateText}" + (v.Installed ? " · 已安装" : "");
        DetailNote.Text = v.Installed
            ? "这个版本已经在了。重装会补齐缺失文件，并用官方 JSON 替换版本 JSON（存档和 Mods 不动）。"
            : "安装会下载版本 JSON、客户端 jar、运行库与资源文件。已存在的文件会跳过。";
        InstallButton.IsEnabled = !_installing;
        if (NewInstanceButton is not null) NewInstanceButton.IsEnabled = !_installing && _selected is not null;
        UpdateInstallButtonLabel();
        ShowNewInstanceButton(v.Installed);
        UpdateFabricOptions();
    }

    private void ShowNewInstanceButton(bool show)
    {
        if (NewInstanceButton is null) return;
        if (!show)
        {
            NewInstanceButton.BeginAnimation(OpacityProperty, null);
            NewInstanceButton.Visibility = Visibility.Collapsed;
            return;
        }

        NewInstanceButton.Visibility = Visibility.Visible;
        NewInstanceButton.Opacity = 0;
        NewInstanceScale.ScaleX = 0.94;
        NewInstanceScale.ScaleY = 0.94;
        NewInstanceTranslate.Y = 6;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        NewInstanceButton.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        NewInstanceScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        NewInstanceScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        NewInstanceTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void UpdateInstallButtonLabel()
    {
        if (InstallButton is null || _selected is null) return;
        if (!string.IsNullOrWhiteSpace(InstallNameBox?.Text))
        {
            InstallButton.Content = "安装为新实例";
            return;
        }
        InstallButton.Content = _selected.Installed ? "补齐 / 重新安装" : "装这个版本";
        if (VanillaLoaderCombo?.SelectedItem is ComboBoxItem item && (item.Tag as string) is { Length: > 0 } loader)
            InstallButton.Content = loader switch
            {
                "fabric" => "下载本体并部署 Fabric",
                "forge" => "下载本体并部署 Forge",
                "neoforge" => "下载本体并部署 NeoForge",
                "quilt" => "下载本体并部署 Quilt",
                _ => "下载本体并部署"
            };
    }

    private void InstallName_Changed(object sender, TextChangedEventArgs e) => UpdateInstallButtonLabel();

    private void NewInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _installing) return;
        InstallNameBox.Text = UniqueInstanceName(_selected.Id);
        InstallVanilla_Click(sender, e);
    }

    private static string UniqueInstanceName(string baseId)
    {
        var candidate = baseId + " 副本";
        if (!LauncherApp.I.Versions.VersionExists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            candidate = $"{baseId} 副本 {i}";
            if (!LauncherApp.I.Versions.VersionExists(candidate)) return candidate;
        }
        return baseId + " " + DateTime.Now.ToString("HHmmss");
    }

    private void Tabs_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelGame is null) return;
        var tag = (sender as RadioButton)?.Tag as string ?? "game";
        if (tag == "forge") PopulateForgeMc();
        if (tag == "resource") ResourcePanel.OnShown();
    }

    private void PopulateForgeMc()
    {
        var releases = _manifest.Where(v => v.Type == "release").OrderByDescending(v => v.ReleaseTime).Select(v => v.Id).ToList();
        if (releases.Count == 0)
            releases = LauncherApp.I.Versions.Versions.Select(v => v.MinecraftVersion)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderByDescending(x => x).ToList();
        var current = ForgeMcCombo.SelectedItem as string;
        ForgeMcCombo.ItemsSource = releases;
        var currentMc = LauncherApp.I.CurrentVersion?.MinecraftVersion;
        if (!string.IsNullOrEmpty(current) && releases.Contains(current)) ForgeMcCombo.SelectedItem = current;
        else if (!string.IsNullOrEmpty(currentMc) && releases.Contains(currentMc)) ForgeMcCombo.SelectedItem = currentMc;
        else if (releases.Count > 0) ForgeMcCombo.SelectedIndex = 0;
    }

    private async void ForgeMc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || ForgeMcCombo.SelectedItem is not string mc) return;
        await LoadForgeVersionsAsync(mc);
    }

    private async Task LoadForgeVersionsAsync(string mc)
    {
        if (_loadingForge) return;
        _loadingForge = true;
        try
        {
            ForgeVersionCombo.ItemsSource = null;
            ForgeVersionCombo.IsEnabled = false;
            InstallForgeButton.IsEnabled = false;
            ForgeStatus.Text = $"正在获取 Forge {mc} 版本…";
            _forgeVersions = await LauncherApp.I.Forge.GetVersionsAsync(mc, CancellationToken.None);
            ForgeVersionCombo.ItemsSource = _forgeVersions.Select(v => v.Display).ToList();
            ForgeVersionCombo.SelectedIndex = _forgeVersions.Count > 0 ? 0 : -1;
            ForgeVersionCombo.IsEnabled = _forgeVersions.Count > 0;
            InstallForgeButton.IsEnabled = _forgeVersions.Count > 0 && !_installing;
            ForgeStatus.Text = _forgeVersions.Count > 0
                ? $"共 {_forgeVersions.Count} 个版本，默认选择推荐版。"
                : $"没找到 Forge {mc} 的版本；换个 Minecraft 版本，或者用本地 installer。";
        }
        catch (Exception ex)
        {
            ForgeStatus.Text = "获取 Forge 版本失败：" + ex.Message;
            Log.Warn("获取 Forge 版本失败：" + ex.Message);
        }
        finally { _loadingForge = false; }
    }

    private async void InstallForge_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (ForgeMcCombo.SelectedItem is not string mc) return;
        if (ForgeVersionCombo.SelectedIndex < 0 || ForgeVersionCombo.SelectedIndex >= _forgeVersions.Count) return;
        var forge = _forgeVersions[ForgeVersionCombo.SelectedIndex].ForgeVersion;
        if (!MessageDialog.Show(Window.GetWindow(this), "一键部署 Forge",
                $"将为 Minecraft {mc} 部署 Forge {forge}。\n安装器会写入 libraries 与 versions，可能需要几分钟。",
                "开始安装", "取消")) return;

        await LauncherApp.I.EnsureJavaAsync();
        var major = VersionResolver.InferJavaMajor(mc, "");
        var java = LauncherApp.I.Java.Pick(major, LauncherApp.I.Settings.JavaPath);
        if (java is null)
        {
            MessageDialog.Show(Window.GetWindow(this), "找不到 Java",
                $"部署 Forge {mc} 要 Java {major} 或更高，先去「设置 → 启动」里指定 Java。",
                "知道了", danger: true);
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetInstalling(true, "准备部署 Forge " + mc);
        var progress = new Progress<DownloadProgress>(p => UpdateProgress(p, "Forge " + mc));
        try
        {
            await LauncherApp.I.Downloads.EnsureVanillaAsync(mc, LauncherApp.I.MinecraftDir, progress, _cts.Token);
            var id = await LauncherApp.I.Forge.InstallAsync(mc, forge, LauncherApp.I.MinecraftDir, java.Path, progress, _cts.Token);
            LauncherApp.I.ReloadVersions();
            SetInstalling(false, "Forge 安装完成：" + id);
            ForgeStatus.Text = "已安装：" + id + "。可以到「版本」页选择它。";
            LauncherApp.I.Notify("Forge 安装完成", id);
        }
        catch (OperationCanceledException)
        {
            SetInstalling(false, "已取消 Forge 安装。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Forge 部署失败");
            SetInstalling(false, "Forge 部署失败：" + ex.Message);
            await ShowInstallFailureAsync("Forge 部署失败", ex.Message);
        }
    }

    private async void RefreshManifest_Click(object sender, RoutedEventArgs e)
    {
        _manifest.Clear();
        _selected = null;
        DetailTitle.Text = "选择一个版本";
        DetailSub.Text = "从左侧列表选择要安装的版本。";
        InstallButton.IsEnabled = false;
        await LoadManifestAsync(true);
        LauncherApp.I.ReloadVersions();
        LauncherApp.I.Notify("清单已刷新", "版本列表已更新。");
    }

    private void OpenDownloadSettings_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("settings");

    private async void InstallVanilla_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _installing) return;
        var version = _selected;
        var loader = (VanillaLoaderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var loaderName = loader switch
        {
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "quilt" => "Quilt",
            _ => ""
        };
        var customName = InstallNameBox?.Text.Trim() ?? "";
        if (customName.Length > 0)
        {
            if (!VersionService.IsValidName(customName))
            {
                MessageDialog.Show(Window.GetWindow(this), "实例名称无效", "名称包含非法字符，或为空。", "知道了", danger: true);
                return;
            }
            if (LauncherApp.I.Versions.VersionExists(customName))
            {
                MessageDialog.Show(Window.GetWindow(this), "实例已存在", $"已经有叫「{customName}」的版本了，换个名字。", "知道了", danger: true);
                return;
            }
        }
        var nameTail = customName.Length > 0 ? $"\n实例名称：{customName}" : "";
        var tail = string.IsNullOrEmpty(loaderName) ? nameTail : $"\n同时会继续安装 {loaderName}。{nameTail}";
        if (!MessageDialog.Show(Window.GetWindow(this), version.Installed ? "重新安装" : "安装版本",
                (version.Installed
                    ? $"{version.Id} 已存在。重新安装会补齐缺失文件并替换 JSON，确定继续吗？"
                    : $"将安装 Minecraft {version.Id}。需要下载客户端、运行库与资源，可能需要几分钟。") + tail,
                "开始安装", "取消"))
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetInstalling(true, "准备安装 " + version.Id);
        var progress = new Progress<DownloadProgress>(p => UpdateProgress(p, version.Id));
        IProgress<DownloadProgress> reporter = progress;
        try
        {
            await LauncherApp.I.Downloads.InstallVanillaAsync(version, LauncherApp.I.MinecraftDir, progress, _cts.Token);
            version.Installed = true;
            VersionList.Items.Refresh();

            var installedLoader = "";
            if (loader == "fabric")
            {
                var fabricLoader = (FabricDetailLoaderCombo.SelectedItem as FabricLoaderInfo)?.Version;
                reporter.Report(new DownloadProgress { Stage = "部署 Fabric", Detail = string.IsNullOrEmpty(fabricLoader) ? "自动选择稳定 Loader" : "Loader " + fabricLoader });
                installedLoader = await LauncherApp.I.Downloads.InstallFabricAsync(version.Id, fabricLoader, LauncherApp.I.MinecraftDir, progress, _cts.Token);
                if (FabricDetailApiCheck.IsChecked == true)
                {
                    reporter.Report(new DownloadProgress { Stage = "匹配 Fabric API", Detail = "按 Minecraft " + version.Id + " 选择兼容版本" });
                    var gameDir = LauncherApp.I.Versions.GetGameDir(installedLoader, InstanceStore.Load(installedLoader),
                        LauncherApp.I.Settings.VersionIsolation);
                    var api = await LauncherApp.I.Modrinth.InstallFabricApiAsync(version.Id, gameDir, progress, _cts.Token);
                    FabricDetailStatus.Text = api is null
                        ? "未找到匹配的 Fabric API，可稍后在资源页手动安装。"
                        : "已自动安装匹配的 Fabric API：" + api;
                }
            }
            else if (loader == "forge")
            {
                var chosen = (ForgeDetailCombo.SelectedItem as ForgeVersionInfo)?.ForgeVersion ?? "";
                reporter.Report(new DownloadProgress { Stage = "部署 Forge", Detail = string.IsNullOrEmpty(chosen) ? "正在获取推荐版本" : "Forge " + chosen });
                var versions = await LauncherApp.I.Forge.GetVersionsAsync(version.Id, _cts.Token);
                var forge = versions.FirstOrDefault(x => x.ForgeVersion == chosen)
                            ?? versions.FirstOrDefault()
                            ?? throw new InvalidOperationException("没找到这个 Minecraft 版本的 Forge。");
                await LauncherApp.I.EnsureJavaAsync();
                var java = LauncherApp.I.Java.Pick(VersionResolver.InferJavaMajor(version.Id, ""), LauncherApp.I.Settings.JavaPath)
                           ?? throw new InvalidOperationException("部署 Forge 要对应版本的 Java，先去设置里指定。");
                installedLoader = await LauncherApp.I.Forge.InstallAsync(version.Id, forge.ForgeVersion, LauncherApp.I.MinecraftDir, java.Path, progress, _cts.Token);
            }
            else if (loader == "neoforge")
            {
                var chosen = (NeoForgeDetailCombo.SelectedItem as ForgeVersionInfo)?.ForgeVersion ?? "";
                reporter.Report(new DownloadProgress { Stage = "部署 NeoForge", Detail = string.IsNullOrEmpty(chosen) ? "正在获取最新版本" : "NeoForge " + chosen });
                var versions = await LauncherApp.I.Forge.GetNeoForgeVersionsAsync(version.Id, _cts.Token);
                var neo = versions.FirstOrDefault(x => x.ForgeVersion == chosen)
                          ?? versions.FirstOrDefault()
                          ?? throw new InvalidOperationException("没找到这个 Minecraft 版本的 NeoForge。");
                await LauncherApp.I.EnsureJavaAsync();
                var java = LauncherApp.I.Java.Pick(VersionResolver.InferJavaMajor(version.Id, ""), LauncherApp.I.Settings.JavaPath)
                           ?? throw new InvalidOperationException("部署 NeoForge 要对应版本的 Java，先去设置里指定。");
                installedLoader = await LauncherApp.I.Forge.InstallNeoForgeAsync(version.Id, neo.ForgeVersion, LauncherApp.I.MinecraftDir, java.Path, progress, _cts.Token);
            }
            else if (loader == "quilt")
            {
                var chosen = (QuiltDetailCombo.SelectedItem as ForgeVersionInfo)?.ForgeVersion ?? "";
                reporter.Report(new DownloadProgress { Stage = "部署 Quilt", Detail = string.IsNullOrEmpty(chosen) ? "正在获取最新版本" : "Quilt " + chosen });
                var versions = await LauncherApp.I.Quilt.GetVersionsAsync(version.Id, _cts.Token);
                var quilt = versions.FirstOrDefault(x => x.ForgeVersion == chosen)
                            ?? versions.FirstOrDefault()
                            ?? throw new InvalidOperationException("没找到这个 Minecraft 版本的 Quilt。");
                await LauncherApp.I.EnsureJavaAsync();
                var java = LauncherApp.I.Java.Pick(VersionResolver.InferJavaMajor(version.Id, ""), LauncherApp.I.Settings.JavaPath)
                           ?? throw new InvalidOperationException("部署 Quilt 要对应版本的 Java，先去设置里指定。");
                installedLoader = await LauncherApp.I.Quilt.InstallAsync(version.Id, quilt.ForgeVersion, LauncherApp.I.MinecraftDir, java.Path, progress, _cts.Token);
            }

            var finalId = string.IsNullOrEmpty(installedLoader) ? version.Id : installedLoader;
            if (customName.Length > 0)
            {
                if (string.IsNullOrEmpty(installedLoader))
                {
                    var (copied, copyError) = LauncherApp.I.Versions.Copy(version.Id, customName);
                    if (!copied) throw new InvalidOperationException("复制实例失败：" + copyError);
                }
                else
                {
                    var (renamed, renameError) = LauncherApp.I.Versions.Rename(installedLoader, customName);
                    if (!renamed) throw new InvalidOperationException("重命名实例失败：" + renameError);
                }
                finalId = customName;
                if (InstallNameBox is not null) InstallNameBox.Text = "";
            }

            LauncherApp.I.SetCurrentVersion(finalId);
            LauncherApp.I.ReloadVersions();
            ShowDetail(version);
            SetInstalling(false, "安装完成：" + finalId);
            LauncherApp.I.Notify("安装完成", finalId);
            DetailNote.Text = "安装完成：" + finalId + "。可以到「启动」页选择并启动。";
        }
        catch (OperationCanceledException)
        {
            SetInstalling(false, "已取消安装。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "安装版本失败");
            SetInstalling(false, "安装失败：" + ex.Message);
            await ShowInstallFailureAsync("安装失败", ex.Message);
        }
    }

    private void VanillaLoader_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstallButton is null || VanillaLoaderCombo?.SelectedItem is not ComboBoxItem) return;
        UpdateInstallButtonLabel();
        UpdateFabricOptions();
    }

    private void UpdateFabricOptions()
    {
        if (FabricOptions is null) return;
        var loader = (VanillaLoaderCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        FabricOptions.Visibility = loader == "fabric" ? Visibility.Visible : Visibility.Collapsed;
        ForgeOptions.Visibility = loader == "forge" ? Visibility.Visible : Visibility.Collapsed;
        NeoForgeOptions.Visibility = loader == "neoforge" ? Visibility.Visible : Visibility.Collapsed;
        QuiltOptions.Visibility = loader == "quilt" ? Visibility.Visible : Visibility.Collapsed;
        if (loader == "fabric" && _selected is not null) _ = LoadDetailFabricAsync(_selected.Id);
        else if (loader == "forge" && _selected is not null) _ = LoadDetailForgeAsync(_selected.Id);
        else if (loader == "neoforge" && _selected is not null) _ = LoadDetailNeoForgeAsync(_selected.Id);
        else if (loader == "quilt" && _selected is not null) _ = LoadDetailQuiltAsync(_selected.Id);
    }

    private async Task LoadDetailForgeAsync(string mc)
    {
        if (_detailForgeMc == mc && ForgeDetailCombo.Items.Count > 0) return;
        _detailForgeMc = mc;
        ForgeDetailCombo.ItemsSource = null;
        ForgeDetailStatus.Text = "正在加载 Forge 版本列表…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var list = await LauncherApp.I.Forge.GetVersionsAsync(mc, timeout.Token);
            ForgeDetailCombo.ItemsSource = list;
            ForgeDetailCombo.SelectedItem = list.FirstOrDefault(x => x.Recommended) ?? list.FirstOrDefault();
            ForgeDetailStatus.Text = list.Count == 0
                ? "没找到这个 Minecraft 版本的 Forge。"
                : $"{list.Count} 个版本；推荐 / 最新已标注，可自行选择。";
        }
        catch (OperationCanceledException)
        {
            _detailForgeMc = "";
            ForgeDetailStatus.Text = "Forge 版本列表加载超时。";
        }
        catch (Exception ex)
        {
            _detailForgeMc = "";
            ForgeDetailStatus.Text = "Forge 版本加载失败：" + ex.Message;
            Log.Warn("Forge 版本列表加载失败：" + ex.Message);
        }
    }

    private async Task LoadDetailNeoForgeAsync(string mc)
    {
        if (_detailNeoForgeMc == mc && NeoForgeDetailCombo.Items.Count > 0) return;
        _detailNeoForgeMc = mc;
        NeoForgeDetailCombo.ItemsSource = null;
        NeoForgeDetailStatus.Text = "正在加载 NeoForge 版本列表…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var list = await LauncherApp.I.Forge.GetNeoForgeVersionsAsync(mc, timeout.Token);
            NeoForgeDetailCombo.ItemsSource = list;
            NeoForgeDetailCombo.SelectedItem = list.FirstOrDefault();
            NeoForgeDetailStatus.Text = list.Count == 0
                ? "没找到这个 Minecraft 版本的 NeoForge。"
                : $"{list.Count} 个版本，默认选择最新版。";
        }
        catch (OperationCanceledException)
        {
            _detailNeoForgeMc = "";
            NeoForgeDetailStatus.Text = "NeoForge 版本列表加载超时。";
        }
        catch (Exception ex)
        {
            _detailNeoForgeMc = "";
            NeoForgeDetailStatus.Text = "NeoForge 版本加载失败：" + ex.Message;
            Log.Warn("NeoForge 版本列表加载失败：" + ex.Message);
        }
    }

    private async Task LoadDetailQuiltAsync(string mc)
    {
        if (_detailQuiltMc == mc && QuiltDetailCombo.Items.Count > 0) return;
        _detailQuiltMc = mc;
        QuiltDetailCombo.ItemsSource = null;
        QuiltDetailStatus.Text = "正在加载 Quilt 版本列表…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var list = await LauncherApp.I.Quilt.GetVersionsAsync(mc, timeout.Token);
            QuiltDetailCombo.ItemsSource = list;
            QuiltDetailCombo.SelectedItem = list.FirstOrDefault();
            QuiltDetailStatus.Text = list.Count == 0
                ? "没找到这个 Minecraft 版本的 Quilt。"
                : $"{list.Count} 个版本，默认选择最新稳定版。";
        }
        catch (OperationCanceledException)
        {
            _detailQuiltMc = "";
            QuiltDetailStatus.Text = "Quilt 版本列表加载超时。";
        }
        catch (Exception ex)
        {
            _detailQuiltMc = "";
            QuiltDetailStatus.Text = "Quilt 版本加载失败：" + ex.Message;
            Log.Warn("Quilt 版本列表加载失败：" + ex.Message);
        }
    }

    private async Task LoadDetailFabricAsync(string mc)
    {
        if (_detailFabricMc == mc && FabricDetailLoaderCombo.Items.Count > 0) return;
        _detailFabricMc = mc;
        FabricDetailLoaderCombo.ItemsSource = null;
        FabricDetailStatus.Text = "正在加载 Fabric Loader 列表…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var list = await LauncherApp.I.Downloads.GetFabricLoadersAsync(mc, timeout.Token);
            FabricDetailLoaderCombo.ItemsSource = list;
            FabricDetailLoaderCombo.SelectedItem = list.FirstOrDefault(x => x.Stable) ?? list.FirstOrDefault();
            FabricDetailStatus.Text = list.Count == 0
                ? "没有找到 Loader；仍会尝试自动选择稳定版。"
                : $"{list.Count} 个 Loader；默认推荐稳定版。";
        }
        catch (OperationCanceledException)
        {
            _detailFabricMc = "";
            FabricDetailStatus.Text = "Loader 列表加载超时，可重试或直接安装（自动选择稳定版）。";
        }
        catch (Exception ex)
        {
            _detailFabricMc = "";
            FabricDetailStatus.Text = "Loader 列表加载失败：" + ex.Message;
            Log.Warn("Fabric Loader 列表加载失败：" + ex.Message);
        }
    }

    private void UpdateProgress(DownloadProgress p, string id)
    {
        ProgressTitle.Text = p.Stage + " · " + id;
        ProgressDetail.Text = p.Detail;
        if (p.Total > 0)
        {
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = Math.Clamp(p.Fraction * 100, 0, 100);
            ProgressPercent.Text = $"{p.Fraction * 100:0}%";
        }
        else
        {
            InstallProgress.IsIndeterminate = true;
            ProgressPercent.Text = "";
        }
    }

    private void SetInstalling(bool installing, string title)
    {
        _installing = installing;
        ProgressCard.Visibility = installing || title.Contains("完成") || title.Contains("失败") || title.Contains("取消")
            ? Visibility.Visible : Visibility.Collapsed;
        ProgressTitle.Text = title;
        InstallButton.IsEnabled = !installing && _selected is not null;
        if (NewInstanceButton is not null) NewInstanceButton.IsEnabled = !installing && _selected is not null;
        InstallFabricButton.IsEnabled = !installing && FabricLoaderCombo.SelectedItem is not null && FabricMcCombo.SelectedItem is not null;
        InstallForgeButton.IsEnabled = !installing && _forgeVersions.Count > 0 && ForgeVersionCombo.SelectedIndex >= 0;
        if (!installing)
        {
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Value = 0;
            ProgressPercent.Text = "";
        }
    }

    private async Task ShowInstallFailureAsync(string title, string message)
    {
        // 先让进度遮罩完成隐藏，否则用户会点到后面的“取消”按钮。
        await Dispatcher.Yield(DispatcherPriority.Render);
        MessageDialog.Show(Window.GetWindow(this), title, message, "关闭", danger: true);
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
    }

    private async void FabricMc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || PanelFabric is null) return;
        var mc = FabricMcCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(mc)) return;
        await LoadFabricLoadersAsync(mc);
    }

    private async Task LoadFabricLoadersAsync(string mc)
    {
        if (_loadingFabric) return;
        _loadingFabric = true;
        try
        {
            FabricLoaderCombo.ItemsSource = null;
            FabricLoaderCombo.IsEnabled = false;
            InstallFabricButton.IsEnabled = false;
            FabricStatus.Text = $"正在获取 Fabric Loader（{mc}）…";
            var loaders = await LauncherApp.I.Downloads.GetFabricLoadersAsync(mc, CancellationToken.None);
            var items = loaders.Select(l => new FabricLoaderInfo { Version = l.Version, Stable = l.Stable }).ToList();
            FabricLoaderCombo.ItemsSource = items.Select(l => (l.Stable ? "稳定版 · " : "") + l.Version).ToList();
            var stableIndex = items.FindIndex(l => l.Stable);
            FabricLoaderCombo.SelectedIndex = stableIndex >= 0 ? stableIndex : 0;
            FabricLoaderCombo.IsEnabled = items.Count > 0;
            InstallFabricButton.IsEnabled = items.Count > 0 && !_installing;
            FabricStatus.Text = items.Count > 0
                ? $"共 {items.Count} 个 Loader；默认选择稳定版。"
                : "没有可用的 Fabric Loader，检查一下网络。";
        }
        catch (Exception ex)
        {
            FabricStatus.Text = "获取 Fabric Loader 失败：" + ex.Message;
            Log.Warn("获取 Fabric Loader 失败：" + ex.Message);
        }
        finally { _loadingFabric = false; }
    }

    private async void InstallFabric_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        var mc = FabricMcCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(mc)) return;
        var loaderText = FabricLoaderCombo.SelectedItem as string ?? "";
        var loader = loaderText.Replace("稳定版 · ", "").Trim();
        if (!MessageDialog.Show(Window.GetWindow(this), "部署 Fabric",
                $"将为 Minecraft {mc} 部署 Fabric Loader {loader}。\n如果基础版本不存在，会先自动安装原版。",
                "开始安装", "取消")) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetInstalling(true, "准备部署 Fabric");
        var progress = new Progress<DownloadProgress>(p => UpdateProgress(p, "Fabric " + mc));
        try
        {
            var id = await LauncherApp.I.Downloads.InstallFabricAsync(mc, loader, LauncherApp.I.MinecraftDir, progress, _cts.Token);
            var apiInfo = "";
            if (FabricApiCheck.IsChecked == true)
            {
                var gameDir = LauncherApp.I.Versions.GetGameDir(id, InstanceStore.Load(id), LauncherApp.I.Settings.VersionIsolation);
                var api = await LauncherApp.I.Modrinth.InstallFabricApiAsync(mc, gameDir, progress, _cts.Token);
                apiInfo = api is null ? "（未找到匹配的 Fabric API）" : $" + Fabric API {api}";
            }
            LauncherApp.I.ReloadVersions();
            SetInstalling(false, "安装完成：" + id);
            FabricStatus.Text = "已安装：" + id + apiInfo + "。可以到「启动」页选择。";
            LauncherApp.I.Notify("Fabric 安装完成", id);
        }
        catch (OperationCanceledException)
        {
            SetInstalling(false, "已取消 Fabric 安装。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "部署 Fabric 失败");
            SetInstalling(false, "安装失败：" + ex.Message);
            await ShowInstallFailureAsync("Fabric 安装失败", ex.Message);
        }
    }

    private async void RunForgeInstaller_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Forge / NeoForge 安装器",
            Filter = "安装器 (*.jar)|*.jar|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var java = LauncherApp.I.Java.All.OrderByDescending(j => j.Major).FirstOrDefault();
        if (java is null)
        {
            MessageDialog.Show(Window.GetWindow(this), "找不到 Java", "先去「设置」里扫描或指定 Java。", "知道了", danger: true);
            return;
        }
        try
        {
            ForgeStatus.Text = "安装器起来了。按提示装完，再点「刷新版本」。";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = java.Path,
                Arguments = "-jar \"" + dialog.FileName + "\"",
                UseShellExecute = true,
                WorkingDirectory = LauncherApp.I.MinecraftDir
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            ForgeStatus.Text = "启动安装器失败：" + ex.Message;
            await ShowInstallFailureAsync("启动安装器失败", ex.Message);
        }
    }

    private void OpenForgeSite_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl("https://files.minecraftforge.net/net/minecraftforge/forge/");

    private void RefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.ReloadVersions();
        var installed = LauncherApp.I.Versions.Versions.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _manifest) v.Installed = installed.Contains(v.Id);
        VersionList.Items.Refresh();
        LauncherApp.I.Notify("版本已刷新", "检测到 " + LauncherApp.I.Versions.Versions.Count + " 个版本。");
    }
}
