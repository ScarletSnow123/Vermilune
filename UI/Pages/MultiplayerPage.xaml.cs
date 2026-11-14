using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vermilune.Core;
using Vermilune.UI.Dialogs;

namespace Vermilune.UI.Pages;

public partial class MultiplayerPage : UserControl, IPage
{
    private readonly DispatcherTimer _timer;
    private bool _starting;
    private int _lanPort;
    private int _serverPort;

    public MultiplayerPage()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Tick();
        LauncherApp.I.Server.LogReceived += line => Dispatcher.BeginInvoke(new Action(() => AppendServerLog(line)));
        LauncherApp.I.Server.Ready += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            ServerStatusText.Text = "服务端已就绪。";
            _serverPort = LauncherApp.I.Server.Port;
            UpdateAddress();
        }));
    }

    public void RefreshState() => UpdateAddress();

    public void OnShown()
    {
        var s = LauncherApp.I.Settings;
        RoomBox.Text = s.MultiplayerRoom;
        SecretBox.Text = s.MultiplayerSecret;
        NodeBox.Text = s.MultiplayerPublicNode;
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
        StopButton.IsEnabled = LauncherApp.I.Multiplayer.IsRunning;
        StartButton.IsEnabled = !LauncherApp.I.Multiplayer.IsRunning;
        UpdateAddress();
        if (LauncherApp.I.Multiplayer.IsRunning)
        {
            _timer.Start();
            StatusText.Text = "联机组件运行中。";
        }
        else
        {
            StatusText.Text = "未启动。";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartNetworkAsync();

    private async Task<bool> StartNetworkAsync()
    {
        if (_starting) return false;
        if (LauncherApp.I.Multiplayer.IsRunning) return true;
        var room = RoomBox.Text.Trim();
        var secret = SecretBox.Text.Trim();
        var node = NodeBox.Text.Trim();
        if (room.Length < 3)
        {
            MessageDialog.Show(Window.GetWindow(this), "房间码太短", "房间码至少需要 3 个字符。", "知道了", danger: true);
            return false;
        }
        if (secret.Length < 6)
        {
            MessageDialog.Show(Window.GetWindow(this), "密码太短", "房间密码至少需要 6 个字符。", "知道了", danger: true);
            return false;
        }
        var s = LauncherApp.I.Settings;
        s.MultiplayerRoom = room;
        s.MultiplayerSecret = secret;
        s.MultiplayerPublicNode = string.IsNullOrWhiteSpace(node) ? MultiplayerService.DefaultPublicNode : node;
        LauncherApp.I.Save();

        var core = Path.Combine(LauncherApp.I.Multiplayer.ToolDirectory, "easytier-core.exe");
        if (!File.Exists(core))
        {
            if (!MessageDialog.Show(Window.GetWindow(this), "安装联机组件",
                    "首次使用会从 GitHub 下载 EasyTier（约 31 MB）。\n它会创建虚拟网卡，可能需要管理员权限。",
                    "开始下载", "取消")) return false;
        }

        _starting = true;
        SetBusy(true);
        StatusText.Text = "正在准备联机组件…";
        var progress = new Progress<DownloadProgress>(p =>
        {
            StatusText.Text = p.Stage + (string.IsNullOrWhiteSpace(p.Detail) ? "" : " · " + p.Detail);
        });
        try
        {
            await LauncherApp.I.Multiplayer.StartAsync(room, secret, node, progress, CancellationToken.None);
            await LauncherApp.I.Multiplayer.RefreshVirtualIpAsync(CancellationToken.None);
            StatusText.Text = "联机组件已启动，正在获取虚拟 IP…";
            StopButton.IsEnabled = true;
            StartButton.IsEnabled = false;
            _timer.Start();
            UpdateAddress();
            LauncherApp.I.Notify("联机已启动", "虚拟 IP：" + LauncherApp.I.Multiplayer.VirtualIp);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动联机失败");
            StatusText.Text = "启动失败：" + ex.Message;
            MessageDialog.Show(Window.GetWindow(this), "联机启动失败", ex.Message, "关闭", danger: true);
            return false;
        }
        finally
        {
            _starting = false;
            SetBusy(false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        LauncherApp.I.Multiplayer.Stop();
        _timer.Stop();
        _lanPort = 0;
        StatusText.Text = "已停止。";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
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
        LauncherApp.I.Settings.ServerMemoryMb = Math.Clamp(memory, 1024, 16384);
        LauncherApp.I.Settings.ServerPort = Math.Clamp(port, 1024, 65535);
        LauncherApp.I.Save();

        if (!await StartNetworkAsync()) return;
        var s = LauncherApp.I.Settings;
        ServerStartButton.IsEnabled = false;
        ServerStatusText.Text = "正在准备服务端…";
        var progress = new Progress<DownloadProgress>(p => ServerStatusText.Text = p.Stage +
            (string.IsNullOrWhiteSpace(p.Detail) ? "" : " · " + p.Detail));
        try
        {
            await LauncherApp.I.Server.StartAsync(version, s.ServerMemoryMb, s.ServerPort, true, progress, CancellationToken.None);
            _serverPort = s.ServerPort;
            ServerStopButton.IsEnabled = true;
            ServerStatusText.Text = "服务端启动中，首次启动可能需要下载运行库。";
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
        _serverPort = 0;
        ServerStartButton.IsEnabled = true;
        ServerStatusText.Text = "服务器已关闭。";
        UpdateAddress();
    }

    private void AppendServerLog(string line)
    {
        if (ServerLogText.Text == "等待服务端输出…") ServerLogText.Text = "";
        var lines = (ServerLogText.Text + "\n" + line).Split('\n');
        ServerLogText.Text = string.Join("\n", lines.TakeLast(40));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = AddressText.Text?.Trim() ?? "";
        if (text.Length == 0 || text.StartsWith("启动联机", StringComparison.Ordinal)) return;
        try { Clipboard.SetText(text); LauncherApp.I.Notify("已复制", text); } catch { }
    }

    private void Detect_Click(object sender, RoutedEventArgs e) => DetectLanPort();

    private async void Tick()
    {
        if (!LauncherApp.I.Multiplayer.IsRunning)
        {
            _timer.Stop();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StatusText.Text = LauncherApp.I.Multiplayer.LastError ?? "联机组件已停止。";
            UpdateAddress();
            return;
        }
        if (string.IsNullOrWhiteSpace(LauncherApp.I.Multiplayer.VirtualIp))
            await LauncherApp.I.Multiplayer.RefreshVirtualIpAsync();
        DetectLanPort();
        UpdateAddress();
    }

    private void DetectLanPort()
    {
        try
        {
            var version = LauncherApp.I.CurrentVersion;
            if (version is null) return;
            var instance = InstanceStore.Load(version.Id);
            var gameDir = LauncherApp.I.Versions.GetGameDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation);
            var log = Path.Combine(gameDir, "logs", "latest.log");
            if (!File.Exists(log)) return;
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var patterns = new[]
            {
                @"(?:Started serving on|Local game hosted on port)\s+(\d{2,5})",
                @"端口号为\s*\[?(\d{2,5})\]?",
                @"端口号\D{0,6}(\d{2,5})"
            };
            var match = patterns
                .SelectMany(p => Regex.Matches(text, p, RegexOptions.IgnoreCase).Cast<Match>())
                .OrderBy(m => m.Index)
                .LastOrDefault();
            if (match is null) return;
            var port = int.Parse(match.Groups[1].Value);
            if (port == _lanPort) return;
            _lanPort = port;
            LanHint.Text = "已检测到局域网端口 " + port + "。";
            UpdateAddress();
        }
        catch (Exception ex)
        {
            Log.Warn("检测 LAN 端口失败：" + ex.Message);
        }
    }

    private void UpdateAddress()
    {
        var ip = LauncherApp.I.Multiplayer.VirtualIp;
        var port = _serverPort > 0 ? _serverPort : _lanPort;
        AddressText.Text = string.IsNullOrWhiteSpace(ip)
            ? "正在获取虚拟 IP…"
            : (port > 0 ? $"{ip}:{port}" : $"{ip}  （开服或开放局域网后显示端口）");
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy && !LauncherApp.I.Multiplayer.IsRunning;
        StopButton.IsEnabled = !busy && LauncherApp.I.Multiplayer.IsRunning;
        RoomBox.IsEnabled = !busy;
        SecretBox.IsEnabled = !busy;
        NodeBox.IsEnabled = !busy;
    }
}
