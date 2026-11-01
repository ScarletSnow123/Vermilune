using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Vermilune.Core;

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
            EnvText.Text = $"Vermilune 1.2.19-beta\n.NET {Environment.Version}\n{Environment.OSVersion}\n数据目录：{AppPaths.DataDir}";
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

    private void CopyEnv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(EnvText.Text);
            LauncherApp.I.Notify("环境信息已复制", "可以粘贴到求助帖中。");
        }
        catch { }
    }
}
