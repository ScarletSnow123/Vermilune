using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vermilune.Core;

namespace Vermilune.UI.Pages;

public sealed class FindingRow
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Suggestion { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string SeverityText { get; init; } = "";
    public Brush SeverityBrush { get; init; } = Brushes.Gray;
    public bool HasEvidence => !string.IsNullOrWhiteSpace(Evidence);
}

public partial class ErrorPage : UserControl, IPage
{
    private VersionInfo? _selected;
    private CrashAnalysis? _analysis;
    private bool _busy;

    public ErrorPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (LauncherApp.I.Initialized) ReloadVersions(); };
    }

    public void OnShown() => ReloadVersions();

    public void RefreshState() => ReloadVersions();

    private void ReloadVersions()
    {
        var current = _selected?.Id ?? LauncherApp.I.Settings.LastVersion;
        var list = LauncherApp.I.Versions.Versions;
        VersionCombo.ItemsSource = list;
        VersionCombo.DisplayMemberPath = "DisplayName";
        _selected = list.FirstOrDefault(v => string.Equals(v.Id, current, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
        if (_selected is null)
        {
            SummaryTitle.Text = "没有可分析的版本";
            SummarySub.Text = "先安装一个版本，或在设置里检查 Minecraft 目录。";
            FindingList.ItemsSource = null;
            return;
        }
        VersionCombo.SelectedItem = _selected;
        if (LauncherApp.I.LastAnalysis is { } cached && string.Equals(cached.VersionId, _selected.Id, StringComparison.OrdinalIgnoreCase))
        {
            _analysis = cached;
            Render(cached);
        }
        else
        {
            _ = AnalyzeAsync(_selected);
        }
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionCombo.SelectedItem is not VersionInfo v) return;
        if (_selected?.Id == v.Id) return;
        _selected = v;
        LauncherApp.I.Settings.LastVersion = v.Id;
        _ = AnalyzeAsync(v);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        await AnalyzeAsync(_selected);
    }

    private async Task AnalyzeAsync(VersionInfo version)
    {
        if (_busy) return;
        _busy = true;
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            var instance = LauncherApp.I.Versions.GetInstance(version.Id);
            var gameDir = LauncherApp.I.Versions.GetGameDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation);
            var analysis = await Task.Run(() => CrashAnalyzer.Analyze(version.Id, gameDir, version.Loader, version.MinecraftVersion,
                sinceUtc: DateTime.UtcNow.AddDays(-7)));
            _analysis = analysis;
            LauncherApp.I.LastAnalysis = analysis;
            Render(analysis);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "报错分析失败");
            SummaryTitle.Text = "分析失败";
            SummarySub.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Render(CrashAnalysis analysis)
    {
        var critical = Math.Max(analysis.CriticalCount, analysis.Mods?.CriticalCount ?? 0);
        if (analysis.HasCrash)
        {
            SummaryTitle.Text = "检测到崩溃 · " + (string.IsNullOrEmpty(analysis.Description) ? "需要排查" : analysis.Description);
            SummaryIcon.Data = (Geometry)Application.Current.Resources["IconWarning"];
            SummaryIcon.Stroke = (Brush)Application.Current.Resources["DangerBrush"];
            SummaryIconBox.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xEB, 0x6B, 0x6B));
        }
        else if (critical > 0)
        {
            SummaryTitle.Text = $"发现 {critical} 个需要处理的问题";
            SummaryIcon.Data = (Geometry)Application.Current.Resources["IconWarning"];
            SummaryIcon.Stroke = (Brush)Application.Current.Resources["WarnBrush"];
            SummaryIconBox.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF0, 0xC8, 0x6A));
        }
        else
        {
            SummaryTitle.Text = "没有发现明确的错误";
            SummaryIcon.Data = (Geometry)Application.Current.Resources["IconCheck"];
            SummaryIcon.Stroke = (Brush)Application.Current.Resources["SuccessBrush"];
            SummaryIconBox.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x7B, 0xD8, 0x8F));
        }

        var crashText = analysis.CrashFile is null ? "未找到崩溃报告" : "崩溃报告：" + analysis.CrashFile;
        SummarySub.Text = $"{crashText}\n日志：{analysis.LogFile ?? "未找到 latest.log"}";
        LogPathText.Text = analysis.LogFile ?? "未找到 latest.log";
        CrashPathText.Text = analysis.CrashFile ?? "未找到 crash-reports";
        ModEnabled.Text = (analysis.Mods?.EnabledCount ?? 0).ToString();
        ModIssues.Text = (analysis.Mods?.CriticalCount ?? 0).ToString();
        ModSummaryText.Text = analysis.Mods is null
            ? "未扫描 Mods。"
            : $"启用 {analysis.Mods.EnabledCount} · 禁用 {analysis.Mods.DisabledCount} · 目录 {analysis.Mods.ModsDir}";

        var rows = analysis.Findings.Select(f => new FindingRow
        {
            Title = f.Title,
            Detail = f.Detail,
            Suggestion = f.Suggestion,
            Evidence = f.Evidence,
            SeverityText = f.Severity switch
            {
                ModIssueSeverity.Critical => "严重",
                ModIssueSeverity.Warning => "警告",
                _ => "信息"
            },
            SeverityBrush = (Brush)Application.Current.Resources[f.Severity switch
            {
                ModIssueSeverity.Critical => "DangerBrush",
                ModIssueSeverity.Warning => "WarnBrush",
                _ => "InfoBrush"
            }]
        }).ToList();
        FindingList.ItemsSource = rows;
        FindingCount.Text = $"{rows.Count} 条";
        NoFindings.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string GameDir()
    {
        if (_selected is null) return LauncherApp.I.MinecraftDir;
        var instance = LauncherApp.I.Versions.GetInstance(_selected.Id);
        return LauncherApp.I.Versions.GetGameDir(_selected.Id, instance, LauncherApp.I.Settings.VersionIsolation);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var latest = Path.Combine(GameDir(), "logs", "latest.log");
        if (File.Exists(latest)) Shell.OpenFile(latest);
        else Shell.OpenFolder(Path.Combine(GameDir(), "logs"));
    }

    private void OpenCrashFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(GameDir(), "crash-reports");
        Directory.CreateDirectory(dir);
        Shell.OpenFolder(dir);
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var dir = LauncherApp.I.Versions.GetModsDir(_selected.Id, null, LauncherApp.I.Settings.VersionIsolation);
        Directory.CreateDirectory(dir);
        Shell.OpenFolder(dir);
    }

    private void OpenLauncherLog_Click(object sender, RoutedEventArgs e) => Shell.OpenLogs();

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("Vermilune 诊断报告");
        sb.AppendLine("版本：" + _analysis.VersionId);
        sb.AppendLine("时间：" + _analysis.AnalyzedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("崩溃：" + (_analysis.CrashFile ?? "无"));
        sb.AppendLine("日志：" + (_analysis.LogFile ?? "无"));
        sb.AppendLine("摘要：" + _analysis.Summary);
        sb.AppendLine();
        foreach (var f in _analysis.Findings)
            sb.AppendLine($"[{f.Severity}] {f.Title}\n{f.Detail}\n建议：{f.Suggestion}\n");
        try
        {
            Clipboard.SetText(sb.ToString());
            LauncherApp.I.Notify("报告已复制", "可以粘贴到求助帖中。");
        }
        catch { }
    }
}
