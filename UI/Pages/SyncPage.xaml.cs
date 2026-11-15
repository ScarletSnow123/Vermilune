using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class SyncPage : UserControl, IPage
{
    private readonly ObservableCollection<string> _paths = new();
    private bool _loading;

    public SyncPage()
    {
        InitializeComponent();
        SyncPathsList.ItemsSource = _paths;
        Loaded += (_, _) => LoadState();
    }

    public void OnShown() => LoadState();
    public void RefreshState() => LoadState();

    private void LoadState()
    {
        _loading = true;
        try
        {
            var s = LauncherApp.I.Settings;
            var versions = LauncherApp.I.Versions.Versions.ToList();
            var sourceId = s.SyncSourceVersionId;
            SyncSourceCombo.ItemsSource = versions;
            SyncTargetCombo.ItemsSource = versions;
            SyncSourceCombo.SelectedItem = versions.FirstOrDefault(v => v.Id == sourceId)
                                          ?? versions.FirstOrDefault(v => v.Id == LauncherApp.I.CurrentVersion?.Id)
                                          ?? versions.FirstOrDefault();
            SyncTargetCombo.SelectedItem = versions.FirstOrDefault(v => v.Id != (SyncSourceCombo.SelectedItem as VersionInfo)?.Id)
                                          ?? versions.FirstOrDefault();
            AutoSyncCheck.IsChecked = s.AutoSyncData;
            SyncKeysCheck.IsChecked = ((GameSettingsScope)s.SyncOptionsScope).HasFlag(GameSettingsScope.Keybinds);
            SyncGameCheck.IsChecked = ((GameSettingsScope)s.SyncOptionsScope).HasFlag(GameSettingsScope.Game);
            SyncLangCheck.IsChecked = ((GameSettingsScope)s.SyncOptionsScope).HasFlag(GameSettingsScope.Language);
            SyncPacksCheck.IsChecked = ((GameSettingsScope)s.SyncOptionsScope).HasFlag(GameSettingsScope.ResourcePacks);
            SyncXaeroCheck.IsChecked = s.SyncXaero;
            SyncJourneyCheck.IsChecked = s.SyncJourneyMap;
            _paths.Clear();
            foreach (var path in s.SyncCustomPaths) _paths.Add(path);
            SyncStatusText.Text = versions.Count < 2 ? "至少需要两个实例" : "";
        }
        finally { _loading = false; }
    }

    private GameSettingsScope BuildScope()
    {
        var scope = GameSettingsScope.None;
        if (SyncKeysCheck.IsChecked == true) scope |= GameSettingsScope.Keybinds;
        if (SyncGameCheck.IsChecked == true) scope |= GameSettingsScope.Game;
        if (SyncLangCheck.IsChecked == true) scope |= GameSettingsScope.Language;
        if (SyncPacksCheck.IsChecked == true) scope |= GameSettingsScope.ResourcePacks;
        return scope;
    }

    private DataSyncSelection BuildSelection() => new(
        BuildScope(),
        SyncXaeroCheck.IsChecked == true,
        SyncJourneyCheck.IsChecked == true,
        _paths.ToList());

    private void SaveState()
    {
        if (_loading) return;
        var s = LauncherApp.I.Settings;
        s.AutoSyncData = AutoSyncCheck.IsChecked == true;
        s.SyncSourceVersionId = (SyncSourceCombo.SelectedItem as VersionInfo)?.Id ?? "";
        s.SyncOptionsScope = (int)BuildScope();
        s.SyncXaero = SyncXaeroCheck.IsChecked == true;
        s.SyncJourneyMap = SyncJourneyCheck.IsChecked == true;
        s.SyncCustomPaths = _paths.ToList();
        LauncherApp.I.Save();
    }

    private void SyncPathsList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void AutoSync_Changed(object sender, RoutedEventArgs e) => SaveState();
    private void SyncItem_Changed(object sender, RoutedEventArgs e) => SaveState();
    private void SyncSource_Changed(object sender, SelectionChangedEventArgs e) => SaveState();

    private void AddPath_Click(object sender, RoutedEventArgs e)
    {
        var path = SyncPathBox.Text.Trim().Replace('/', '\\').TrimStart('\\');
        if (string.IsNullOrWhiteSpace(path)) return;
        if (path.Contains("..", StringComparison.Ordinal))
        {
            SyncStatusText.Text = "路径不能包含 ..";
            return;
        }
        if (_paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        _paths.Add(path);
        SyncPathBox.Clear();
        SaveState();
    }

    private void RemovePath_Click(object sender, RoutedEventArgs e)
    {
        if (SyncPathsList.SelectedItem is not string path) return;
        _paths.Remove(path);
        SaveState();
    }

    private void SyncOne_Click(object sender, RoutedEventArgs e)
    {
        if (SyncSourceCombo.SelectedItem is not VersionInfo source ||
            SyncTargetCombo.SelectedItem is not VersionInfo target)
        {
            SyncStatusText.Text = "先选源实例和目标实例";
            return;
        }
        if (source.Id == target.Id)
        {
            SyncStatusText.Text = "源和目标不能是同一个实例";
            return;
        }
        RunSync(source, new[] { target });
    }

    private void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        if (SyncSourceCombo.SelectedItem is not VersionInfo source)
        {
            SyncStatusText.Text = "先选源实例";
            return;
        }
        var targets = LauncherApp.I.Versions.Versions.Where(v => v.Id != source.Id).ToList();
        if (targets.Count == 0)
        {
            SyncStatusText.Text = "没有其他实例";
            return;
        }
        if (!MessageDialog.Show(Window.GetWindow(this), "同步所有版本",
                $"把「{source.DisplayName}」的数据同步到另外 {targets.Count} 个实例？", "开始同步", "取消")) return;
        RunSync(source, targets);
    }

    private async void RunSync(VersionInfo source, IReadOnlyList<VersionInfo> targets)
    {
        SaveState();
        var selection = BuildSelection();
        if (selection.OptionsScope == GameSettingsScope.None && !selection.Xaero && !selection.JourneyMap &&
            selection.CustomPaths.Count == 0)
        {
            SyncStatusText.Text = "至少勾选一项同步内容";
            return;
        }
        SyncStatusText.Text = "正在同步…";
        try
        {
            var result = await Task.Run(() => GameDataSyncService.Apply(source, targets, selection));
            SyncStatusText.Text = $"同步完成：{result.TargetsChanged} 个实例，{result.FilesCopied} 个文件";
            if (result.Errors.Count > 0)
                MessageDialog.Show(Window.GetWindow(this), "部分同步失败",
                    string.Join("\n", result.Errors.Take(8)), "知道了", danger: true);
            LauncherApp.I.Notify("数据同步完成", SyncStatusText.Text);
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = "同步失败：" + ex.Message;
            MessageDialog.Show(Window.GetWindow(this), "同步失败", ex.Message, "知道了", danger: true);
        }
    }
}
