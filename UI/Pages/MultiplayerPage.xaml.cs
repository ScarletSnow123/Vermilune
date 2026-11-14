using System.Windows;
using System.Windows.Controls;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class MultiplayerPage : UserControl, IPage
{
    public MultiplayerPage()
    {
        InitializeComponent();
        LauncherApp.I.Server.LogReceived += line => Dispatcher.BeginInvoke(new Action(() => AppendServerLog(line)));
        LauncherApp.I.Server.Ready += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            ServerStatusText.Text = "服务端已就绪。";
            UpdateAddress();
        }));
    }

    public void RefreshState() => UpdateAddress();

    public void OnShown()
    {
        var s = LauncherApp.I.Settings;
        var serverVersions = LauncherApp.I.Versions.Versions
            .Where(v => v.Loader is LoaderKind.Vanilla or LoaderKind.Fabric)
            .ToList();
        ServerVersionCombo.ItemsSource = serverVersions;
        ServerVersionCombo.SelectedItem = LauncherApp.I.CurrentVersion is { } current &&
                                           serverVersions.Any(v => v.Id == current.Id)
            ? serverVersions.First(v => v.Id == current.Id)
            : serverVersions.FirstOrDefault();
        ServerMemoryBox.Text = s.ServerMemoryMb.ToString();
        ServerPortBox.Text = s.ServerPort.ToString();
        ServerStartButton.IsEnabled = !LauncherApp.I.Server.IsRunning;
        ServerStopButton.IsEnabled = LauncherApp.I.Server.IsRunning;
        ServerStatusText.Text = LauncherApp.I.Server.IsRunning ? "服务端运行中。" : "未启动。";
        UpdateAddress();
    }

    private async void StartServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerEulaCheck.IsChecked != true)
        {
            MessageDialog.Show(Window.GetWindow(this), "需要同意 EULA",
                "启动 Minecraft 服务端前，需要先阅读并同意 Minecraft 最终用户许可协议。",
                "知道了", danger: true);
            return;
        }
        if (ServerVersionCombo.SelectedItem is not VersionInfo version)
        {
            MessageDialog.Show(Window.GetWindow(this), "没有可用实例", "请先安装一个原版或 Fabric 实例。", "知道了", danger: true);
            return;
        }
        if (!int.TryParse(ServerMemoryBox.Text.Trim(), out var memory)) memory = 2048;
        if (!int.TryParse(ServerPortBox.Text.Trim(), out var port)) port = 25565;
        var s = LauncherApp.I.Settings;
        s.ServerMemoryMb = Math.Clamp(memory, 1024, 16384);
        s.ServerPort = Math.Clamp(port, 1024, 65535);
        LauncherApp.I.Save();

        ServerStartButton.IsEnabled = false;
        ServerStatusText.Text = "正在准备服务端…";
        var progress = new Progress<DownloadProgress>(p =>
            ServerStatusText.Text = p.Stage + (string.IsNullOrWhiteSpace(p.Detail) ? "" : " · " + p.Detail));
        try
        {
            await LauncherApp.I.Server.StartAsync(version, s.ServerMemoryMb, s.ServerPort, true, progress, CancellationToken.None);
            ServerStopButton.IsEnabled = true;
            ServerStatusText.Text = "服务端启动中，正在尝试 UPnP 端口映射…";
            UpdateAddress();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "一键开服失败");
            ServerStartButton.IsEnabled = true;
            ServerStatusText.Text = "开服失败：" + ex.Message;
            MessageDialog.Show(Window.GetWindow(this), "开服失败", ex.Message, "关闭", danger: true);
        }
    }

    private async void StopServer_Click(object sender, RoutedEventArgs e)
    {
        ServerStopButton.IsEnabled = false;
        ServerStatusText.Text = "正在关闭服务器…";
        await LauncherApp.I.Server.StopAsync();
        ServerStartButton.IsEnabled = true;
        ServerStatusText.Text = "服务器已关闭。";
        UpdateAddress();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = AddressText.Text?.Trim() ?? "";
        if (text.Length == 0 || text.StartsWith("开服后", StringComparison.Ordinal)) return;
        try { Clipboard.SetText(text); LauncherApp.I.Notify("已复制", text); } catch { }
    }

    private void AppendServerLog(string line)
    {
        if (ServerLogText.Text == "等待服务端输出…") ServerLogText.Text = "";
        var lines = (ServerLogText.Text + "\n" + line).Split('\n');
        ServerLogText.Text = string.Join("\n", lines.TakeLast(40));
    }

    private void UpdateAddress()
    {
        var server = LauncherApp.I.Server;
        var port = server.Port > 0 ? server.Port : LauncherApp.I.Settings.ServerPort;
        var address = server.PublicAddress ?? server.LocalAddress;
        if (!server.IsRunning)
        {
            AddressText.Text = "开服后显示公网或本地地址。";
            MappingText.Text = "未启动服务端。";
            return;
        }
        AddressText.Text = string.IsNullOrWhiteSpace(address)
            ? "正在获取地址…"
            : $"{address}:{port}";
        MappingText.Text = server.MappingStatus ?? (string.IsNullOrWhiteSpace(server.PublicAddress)
            ? "未获取到公网 IP，请检查路由器 UPnP 或手动转发端口。"
            : "端口映射成功。");
    }
}
