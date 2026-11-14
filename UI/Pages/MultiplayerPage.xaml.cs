using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class MultiplayerPage : UserControl, IPage
{
    private readonly System.Windows.Threading.DispatcherTimer _serverTimer;
    private bool _checkingPort;
    private bool _suppressFrpOpen;

    public MultiplayerPage()
    {
        InitializeComponent();
        _serverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _serverTimer.Tick += async (_, _) => await CheckServerPortAsync();
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
        UseFrpCheck.IsChecked = s.UseFrp;
        var providerIndex = string.Equals(s.FrpProvider, "custom", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _suppressFrpOpen = true;
        FrpProviderCombo.SelectedIndex = providerIndex;
        _suppressFrpOpen = false;
        OpenFrpTokenBox.Text = s.OpenFrpToken;
        OpenFrpTunnelBox.Text = s.OpenFrpTunnelId;
        OpenFrpAddressBox.Text = s.OpenFrpAddress;
        FrpServerBox.Text = s.FrpServer;
        FrpPortBox.Text = s.FrpServerPort.ToString();
        FrpTokenBox.Text = s.FrpToken;
        FrpRemotePortBox.Text = s.FrpRemotePort.ToString();
        FrpPanel.Visibility = s.UseFrp ? Visibility.Visible : Visibility.Collapsed;
        ServerStartButton.IsEnabled = !LauncherApp.I.Server.IsRunning;
        ServerStopButton.IsEnabled = LauncherApp.I.Server.IsRunning;
        ServerStatusText.Text = LauncherApp.I.Server.IsRunning ? "服务端运行中。" : "未启动。";
        if (LauncherApp.I.Server.IsRunning) _serverTimer.Start();
        UpdateAddress();
    }

    private void UseFrp_Checked(object sender, RoutedEventArgs e)
    {
        if (FrpPanel is not null)
            FrpPanel.Visibility = UseFrpCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FrpProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (OpenFrpPanel is null || FrpCustomPanel is null) return;
        var custom = (FrpProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "custom";
        OpenFrpPanel.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        FrpCustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (!custom && !_suppressFrpOpen) OpenOpenFrpSite();
    }

    private void OpenFrpSite_Click(object sender, RoutedEventArgs e) => OpenOpenFrpSite();

    private static void OpenOpenFrpSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://console.openfrp.net") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开 OpenFrp 控制台失败：" + ex.Message);
        }
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
        if (!int.TryParse(FrpPortBox.Text.Trim(), out var frpPort)) frpPort = 7000;
        if (!int.TryParse(FrpRemotePortBox.Text.Trim(), out var frpRemotePort)) frpRemotePort = 25565;
        var useFrp = UseFrpCheck.IsChecked == true;
        var frpProvider = (FrpProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "openfrp";
        if (useFrp && frpProvider == "custom" &&
            (string.IsNullOrWhiteSpace(FrpServerBox.Text.Trim()) || string.IsNullOrWhiteSpace(FrpTokenBox.Text.Trim())))
        {
            MessageDialog.Show(Window.GetWindow(this), "FRP 配置不完整",
                "自建 FRP 需要填写服务器地址和 Token。", "知道了", danger: true);
            return;
        }
        if (useFrp && frpProvider == "openfrp" &&
            (string.IsNullOrWhiteSpace(OpenFrpTokenBox.Text.Trim()) ||
             string.IsNullOrWhiteSpace(OpenFrpTunnelBox.Text.Trim()) ||
             string.IsNullOrWhiteSpace(OpenFrpAddressBox.Text.Trim())))
        {
            MessageDialog.Show(Window.GetWindow(this), "OpenFrp 配置不完整",
                "请填写 OpenFrp 访问密钥、隧道 ID 和面板上的公网地址。", "知道了", danger: true);
            return;
        }
        var s = LauncherApp.I.Settings;
        s.ServerMemoryMb = Math.Clamp(memory, 1024, 16384);
        s.ServerPort = Math.Clamp(port, 1024, 65535);
        s.UseFrp = useFrp;
        s.FrpProvider = frpProvider;
        s.OpenFrpToken = OpenFrpTokenBox.Text.Trim();
        s.OpenFrpTunnelId = OpenFrpTunnelBox.Text.Trim();
        s.OpenFrpAddress = OpenFrpAddressBox.Text.Trim();
        s.FrpServer = FrpServerBox.Text.Trim();
        s.FrpServerPort = Math.Clamp(frpPort, 1, 65535);
        s.FrpToken = FrpTokenBox.Text.Trim();
        s.FrpRemotePort = Math.Clamp(frpRemotePort, 1, 65535);
        LauncherApp.I.Save();

        ServerStartButton.IsEnabled = false;
        ServerStatusText.Text = "正在准备服务端…";
        var progress = new Progress<DownloadProgress>(p =>
            ServerStatusText.Text = p.Stage + (string.IsNullOrWhiteSpace(p.Detail) ? "" : " · " + p.Detail));
        try
        {
            await LauncherApp.I.Server.StartAsync(version, s.ServerMemoryMb, s.ServerPort, true,
                ServerWhitelistCheck.IsChecked == true, progress, CancellationToken.None);
            ServerStopButton.IsEnabled = true;
            _serverTimer.Start();
            if (s.UseFrp)
            {
                ServerStatusText.Text = frpProvider == "openfrp"
                    ? "服务端已启动，正在连接 OpenFrp…"
                    : "服务端已启动，正在连接 FRP 中继…";
                if (frpProvider == "openfrp")
                    await LauncherApp.I.Frp.StartOpenFrpAsync(s.OpenFrpToken, s.OpenFrpTunnelId, progress, CancellationToken.None);
                else
                    await LauncherApp.I.Frp.StartAsync(s.FrpServer, s.FrpServerPort, s.FrpToken,
                        s.ServerPort, s.FrpRemotePort, progress, CancellationToken.None);
                ServerStatusText.Text = "服务端与 FRP 中继已启动。";
            }
            else
            {
                ServerStatusText.Text = "服务端启动中，正在检查端口…";
            }
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
        _serverTimer.Stop();
        LauncherApp.I.Frp.Stop();
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

    private void CopyIpv6_Click(object sender, RoutedEventArgs e)
    {
        var text = Ipv6Text.Text?.Trim() ?? "";
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); LauncherApp.I.Notify("已复制 IPv6 地址", text); } catch { }
    }

    private async Task CheckServerPortAsync()
    {
        if (_checkingPort || !LauncherApp.I.Server.IsRunning) return;
        _checkingPort = true;
        try
        {
            var open = await LauncherApp.I.Server.TestLocalPortAsync();
            ServerStatusText.Text = open
                ? "服务端端口已监听。若朋友仍无法连接，请检查 UPnP、防火墙或公网 IP。"
                : "服务端仍在启动，端口尚未监听…";
            if (!open && LauncherApp.I.Server.LastError is { } error) ServerStatusText.Text = error;
            UpdateAddress();
        }
        finally { _checkingPort = false; }
    }

    private void AppendServerLog(string line)
    {
        if (ServerLogText.Text == "等待服务端输出…") ServerLogText.Text = "";
        var lines = (ServerLogText.Text + "\n" + line).Split('\n');
        ServerLogText.Text = string.Join("\n", lines.TakeLast(40));
        ServerLogScroll.ScrollToEnd();
    }

    private void UpdateAddress()
    {
        var server = LauncherApp.I.Server;
        var port = server.Port > 0 ? server.Port : LauncherApp.I.Settings.ServerPort;
        var address = server.PublicAddress ?? server.LocalAddress;
        if (!server.IsRunning)
        {
            AddressText.Text = "开服后显示公网或本地地址。";
            Ipv6Text.Visibility = Visibility.Collapsed;
            CopyIpv6Button.Visibility = Visibility.Collapsed;
            MappingCallout.Visibility = Visibility.Collapsed;
            return;
        }

        var s = LauncherApp.I.Settings;
        var frpActive = s.UseFrp && LauncherApp.I.Frp.IsRunning;
        var frpAddress = string.Equals(s.FrpProvider, "openfrp", StringComparison.OrdinalIgnoreCase)
            ? s.OpenFrpAddress
            : (string.IsNullOrWhiteSpace(s.FrpServer) ? "" : $"{s.FrpServer}:{s.FrpRemotePort}");
        var ipv4 = frpActive ? frpAddress : (server.PublicAddress ?? server.LocalAddress);
        var ipv6 = server.Ipv6Address;
        var portValue = frpActive ? s.FrpRemotePort : (server.Port > 0 ? server.Port : s.ServerPort);

        if (!string.IsNullOrWhiteSpace(frpActive ? frpAddress : null))
        {
            AddressText.Text = frpAddress;
        }
        else if (!string.IsNullOrWhiteSpace(ipv4))
        {
            AddressText.Text = $"{ipv4}:{portValue}";
        }
        else if (!string.IsNullOrWhiteSpace(ipv6))
        {
            AddressText.Text = $"[{ipv6}]:{portValue}";
        }
        else
        {
            AddressText.Text = "正在获取地址…";
        }

        Ipv6Text.Text = string.IsNullOrWhiteSpace(ipv6) ? "" : $"[{ipv6}]:{portValue}";
        Ipv6Text.Visibility = string.IsNullOrWhiteSpace(ipv6) ? Visibility.Collapsed : Visibility.Visible;
        CopyIpv6Button.Visibility = string.IsNullOrWhiteSpace(ipv6) ? Visibility.Collapsed : Visibility.Visible;

        var warning = (s.UseFrp && !frpActive) ||
                      (!frpActive && string.IsNullOrWhiteSpace(server.PublicAddress) && string.IsNullOrWhiteSpace(ipv6)) ||
                      (server.MappingStatus?.Contains("失败", StringComparison.OrdinalIgnoreCase) ?? false) ||
                      (server.MappingStatus?.Contains("内网", StringComparison.OrdinalIgnoreCase) ?? false) ||
                      (server.MappingStatus?.Contains("CGNAT", StringComparison.OrdinalIgnoreCase) ?? false) ||
                      (server.MappingStatus?.Contains("手动", StringComparison.OrdinalIgnoreCase) ?? false);
        MappingCallout.Visibility = Visibility.Visible;
        MappingTitle.Text = warning ? "无法从公网连接" : (frpActive ? "已通过 FRP 发布" :
            (string.IsNullOrWhiteSpace(server.PublicAddress) ? "可使用 IPv6 直连" : "端口映射成功"));
        MappingTitle.Foreground = (Brush)FindResource(warning ? "DangerBrush" : "AccentBrush");
        MappingIcon.Data = (Geometry)FindResource(warning ? "IconWarning" : "IconCheck");
        MappingIcon.Stroke = (Brush)FindResource(warning ? "DangerBrush" : "AccentBrush");
        MappingCallout.Background = new SolidColorBrush(warning
            ? Color.FromArgb(0x22, 0xEB, 0x6B, 0x6B)
            : Color.FromArgb(0x1A, 0x8F, 0xD6, 0xB4));
        MappingCallout.BorderBrush = new SolidColorBrush(warning
            ? Color.FromArgb(0x55, 0xEB, 0x6B, 0x6B)
            : Color.FromArgb(0x40, 0x8F, 0xD6, 0xB4));
        MappingText.Text = string.Join("\n", new[]
        {
            frpActive ? "FRP 客户端已连接，朋友连接上方服务器地址。" : null,
            s.UseFrp && !frpActive ? ("FRP 未连接：" + (LauncherApp.I.Frp.LastError ??
                (string.Equals(s.FrpProvider, "openfrp", StringComparison.OrdinalIgnoreCase)
                    ? "请检查 OpenFrp 访问密钥、隧道 ID 和隧道状态。"
                    : "请检查服务器地址、Token 和 frps 状态。"))) : null,
            frpActive ? null : server.FirewallStatus,
            frpActive ? null : server.MappingStatus,
            frpActive ? null : (string.IsNullOrWhiteSpace(server.PublicAddress)
                ? (string.IsNullOrWhiteSpace(ipv6) ? "未获取到公网 IPv4 或 IPv6 地址。" : "没有可用的公网 IPv4；如果双方都有 IPv6，可使用上方 IPv6 地址。")
                : "公网 IPv4 地址已获取。")
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
