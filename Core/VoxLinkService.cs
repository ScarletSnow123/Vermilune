using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

/// <summary>
/// VoxLink CLI 的房间码联机封装。CLI 负责打洞、TURN 回退和本地桥接；
/// 它返回的 127.0.0.1 地址就是 Minecraft 要连接的地址。
/// </summary>
public sealed class VoxLinkService : IDisposable
{
    private Process? _process;
    private TaskCompletionSource<bool>? _ready;
    private bool _isHost;

    public bool IsRunning => _process is { HasExited: false };
    public bool IsHostRoom => _isHost;
    public string? RoomCode { get; private set; }
    public string? BridgeAddress { get; private set; }
    public string? Status { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;
    public event Action<string>? LogReceived;

    public string ToolDirectory => Path.Combine(AppPaths.DataDir, "Tools", "VoxLink");

    public async Task StartHostAsync(string roomName, int localPort,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var args = new[]
        {
            "room", "create",
            "--name", roomName,
            "--port", localPort.ToString(),
            "--client-tag", "Vermilune",
            "--events", "ndjson",
            "--timeout", "0"
        };
        await StartAsync(args, true, progress, ct);
    }

    public async Task JoinAsync(string code,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var args = new[]
        {
            "room", "join",
            "--code", code,
            "--modsync", "off",
            "--events", "ndjson",
            "--timeout", "0"
        };
        await StartAsync(args, false, progress, ct);
    }

    private async Task StartAsync(string[] args, bool host,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Stop();
        RoomCode = null;
        BridgeAddress = null;
        LastError = null;
        Status = "准备中";
        _isHost = host;
        var exe = await EnsureInstalledAsync(progress, ct);
        Directory.CreateDirectory(ToolDirectory);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = ToolDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            LogReceived?.Invoke(e.Data);
        };
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process))
            {
                Status = "已停止";
                if (LastError is null) LastError = $"VoxLink 已退出（代码 {process.ExitCode}）";
                _ready?.TrySetException(new InvalidOperationException(LastError));
                Changed?.Invoke();
            }
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动 VoxLink CLI");
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _ready.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastError = "VoxLink 连接超时，请检查网络或换一个房间码";
            Stop();
            throw new InvalidOperationException(LastError);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private void HandleLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        LogReceived?.Invoke(line);
        try
        {
            var node = JsonNode.Parse(line) as JsonObject;
            if (node is null) return;
            if (node["ok"]?.GetValue<bool>() == false)
            {
                LastError = node["message"]?.GetValue<string>() ?? node["error"]?.GetValue<string>() ?? "VoxLink 返回错误";
                Status = "失败";
                _ready?.TrySetException(new InvalidOperationException(LastError));
                Changed?.Invoke();
                return;
            }
            var data = node["data"] as JsonObject;
            var dataRoom = data?["room"] as JsonObject;
            var dataCode = dataRoom?["code"]?.GetValue<string>() ?? dataRoom?["roomCode"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(dataCode))
            {
                RoomCode = dataCode;
                Status = "房间已创建";
                if (_isHost) _ready?.TrySetResult(true);
                Changed?.Invoke();
            }
            var eventName = node["event"]?.GetValue<string>();
            if (eventName == "session:state")
            {
                Status = node["state"]?.GetValue<string>() ?? Status;
                var room = node["room"] as JsonObject;
                var code = room?["code"]?.GetValue<string>() ?? room?["roomCode"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    RoomCode = code;
                    if (_isHost) _ready?.TrySetResult(true);
                }
                Changed?.Invoke();
            }
            else if (eventName == "conn:state")
            {
                var address = node["address"]?.GetValue<string>();
                var status = node["status"]?.GetValue<string>();
                if (status == "success" && !string.IsNullOrWhiteSpace(address))
                {
                    BridgeAddress = address;
                    Status = "已连接";
                    _ready?.TrySetResult(true);
                }
                else if (status == "failed")
                {
                    LastError = node["detail"]?.GetValue<string>() ?? "P2P 连接失败";
                    Status = "连接失败";
                }
                Changed?.Invoke();
            }
            else if (eventName == "net:log")
            {
                var state = node["msg"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(state)) LogReceived?.Invoke(state);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("解析 VoxLink 输出失败：" + ex.Message);
        }
    }

    private async Task<string> EnsureInstalledAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var exe = Path.Combine(ToolDirectory, "voxlink-cli.exe");
        if (File.Exists(exe) && new FileInfo(exe).Length > 1_000_000) return exe;
        Directory.CreateDirectory(ToolDirectory);
        progress?.Report(new DownloadProgress { Stage = "下载 VoxLink", Detail = "voxlink-cli" });
        var urls = new[]
        {
            "https://github.com/AUGUHDAR/VoxLink-app/releases/download/1.0.2/VoxLink-CLI-windows-amd64.exe",
            "https://ghproxy.net/https://github.com/AUGUHDAR/VoxLink-app/releases/download/1.0.2/VoxLink-CLI-windows-amd64.exe"
        };
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await HttpService.DownloadFileAsync(url, exe, "", null, ct, 2);
                if (new FileInfo(exe).Length > 1_000_000) return exe;
            }
            catch (Exception ex)
            {
                last = ex;
                try { File.Delete(exe); } catch { }
            }
        }
        throw new InvalidOperationException("下载 VoxLink CLI 失败：" + (last?.Message ?? "未知错误"));
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        _ready = null;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { process.Dispose(); } catch { }
        }
        RoomCode = null;
        BridgeAddress = null;
        Status = "已停止";
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
