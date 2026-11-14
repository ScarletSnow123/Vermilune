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

    public MultiplayerPage()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Tick();
    }

    public void RefreshState() => UpdateAddress();

    public void OnShown()
    {
        var s = LauncherApp.I.Settings;
        RoomBox.Text = s.MultiplayerRoom;
        SecretBox.Text = s.MultiplayerSecret;
        IpBox.Text = s.MultiplayerIp;
        NodeBox.Text = s.MultiplayerPublicNode;
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

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_starting || LauncherApp.I.Multiplayer.IsRunning) return;
        var room = RoomBox.Text.Trim();
        var secret = SecretBox.Text.Trim();
        var ip = IpBox.Text.Trim();
        var node = NodeBox.Text.Trim();
        if (room.Length < 3)
        {
            MessageDialog.Show(Window.GetWindow(this), "房间码太短", "房间码至少需要 3 个字符。", "知道了", danger: true);
            return;
        }
        if (secret.Length < 6)
        {
            MessageDialog.Show(Window.GetWindow(this), "密码太短", "房间密码至少需要 6 个字符。", "知道了", danger: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(ip))
        {
            MessageDialog.Show(Window.GetWindow(this), "虚拟 IP 为空", "请填写 10.126.126.1 或 10.126.126.2 这类地址。", "知道了", danger: true);
            return;
        }

        var s = LauncherApp.I.Settings;
        s.MultiplayerRoom = room;
        s.MultiplayerSecret = secret;
        s.MultiplayerIp = ip;
        s.MultiplayerPublicNode = string.IsNullOrWhiteSpace(node) ? MultiplayerService.DefaultPublicNode : node;
        LauncherApp.I.Save();

        var core = Path.Combine(LauncherApp.I.Multiplayer.ToolDirectory, "easytier-core.exe");
        if (!File.Exists(core))
        {
            if (!MessageDialog.Show(Window.GetWindow(this), "安装联机组件",
                    "首次使用会从 GitHub 下载 EasyTier（约 31 MB）。\n它会创建虚拟网卡，可能需要管理员权限。",
                    "开始下载", "取消")) return;
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
            await LauncherApp.I.Multiplayer.StartAsync(room, secret, ip, node, progress, CancellationToken.None);
            StatusText.Text = "联机组件已启动。";
            StopButton.IsEnabled = true;
            StartButton.IsEnabled = false;
            _timer.Start();
            UpdateAddress();
            LauncherApp.I.Notify("联机已启动", "虚拟 IP：" + LauncherApp.I.Multiplayer.VirtualIp);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动联机失败");
            StatusText.Text = "启动失败：" + ex.Message;
            MessageDialog.Show(Window.GetWindow(this), "联机启动失败", ex.Message, "关闭", danger: true);
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

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = AddressText.Text?.Trim() ?? "";
        if (text.Length == 0 || text.StartsWith("启动联机", StringComparison.Ordinal)) return;
        try { Clipboard.SetText(text); LauncherApp.I.Notify("已复制", text); } catch { }
    }

    private void Detect_Click(object sender, RoutedEventArgs e) => DetectLanPort();

    private void Tick()
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
        DetectLanPort();
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
        if (string.IsNullOrWhiteSpace(ip)) ip = IpBox.Text.Trim().Split('/')[0];
        AddressText.Text = string.IsNullOrWhiteSpace(ip)
            ? "启动联机后显示虚拟 IP。"
            : (_lanPort > 0 ? $"{ip}:{_lanPort}" : $"{ip}  （开放局域网后显示端口）");
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy && !LauncherApp.I.Multiplayer.IsRunning;
        StopButton.IsEnabled = !busy && LauncherApp.I.Multiplayer.IsRunning;
        RoomBox.IsEnabled = !busy;
        SecretBox.IsEnabled = !busy;
        IpBox.IsEnabled = !busy;
        NodeBox.IsEnabled = !busy;
    }
}
