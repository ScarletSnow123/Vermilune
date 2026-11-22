using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class MorePage : UserControl, IPage
{
    public MorePage()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateEnv();
    }

    public void OnShown() => UpdateEnv();
    public void RefreshState() => UpdateEnv();

    private void UpdateEnv()
    {
        try
        {
            EnvText.Text = $"Vermilune 1.3.5\n.NET {Environment.Version}\n{Environment.OSVersion}\n数据目录：{AppPaths.DataDir}";
        }
        catch { }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(AppPaths.DataDir);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Shell.OpenLogs();
    private void GoError_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("error");

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) Shell.OpenUrl(url);
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Show(Window.GetWindow(this), "导出诊断包",
                "会打包启动日志、游戏 latest.log、崩溃报告和环境信息\n日志可能包含账号名、路径和服务器地址，分享前请检查",
                "选择保存位置", "取消")) return;
        var dialog = new SaveFileDialog
        {
            Title = "保存诊断包",
            Filter = "Zip 压缩包|*.zip",
            FileName = $"Vermilune-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var result = await DiagnosticsService.ExportAsync(LauncherApp.I.CurrentVersion, dialog.FileName);
            LauncherApp.I.Notify("诊断包已导出", $"{result.Files} 个文件");
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Window.GetWindow(this), "导出失败", ex.Message, "知道了", danger: true);
        }
    }

    private void CopyEnv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(EnvText.Text);
            LauncherApp.I.Notify("环境信息已复制", "可以粘贴到求助帖中");
        }
        catch { }
    }
}
