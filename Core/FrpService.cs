using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Vermilune.Core;

/// <summary>
/// 可选的自建 FRP 中继客户端。用户在公网服务器上运行 frps，本机把 Minecraft 端口转发出去。
/// </summary>
public sealed class FrpService : IDisposable
{
    private const string FrpVersion = "0.72.0";
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };
    public string? LastError { get; private set; }
    public event Action<string>? LogReceived;

    public string ToolDirectory => Path.Combine(AppPaths.DataDir, "Tools", "FRP");

    public async Task EnsureInstalledAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var client = Path.Combine(ToolDirectory, "frpc.exe");
        if (File.Exists(client)) return;
        Directory.CreateDirectory(ToolDirectory);
        var zip = Path.Combine(AppPaths.CacheDir, $"frp_{FrpVersion}_windows_amd64.zip");
        if (!File.Exists(zip) || new FileInfo(zip).Length < 1024 * 1024)
        {
            var url = $"https://github.com/fatedier/frp/releases/download/v{FrpVersion}/frp_{FrpVersion}_windows_amd64.zip";
            progress?.Report(new DownloadProgress { Stage = "下载 FRP 组件", Detail = "frpc " + FrpVersion });
            var downloadProgress = progress is null
                ? null
                : new Progress<(long Done, long Total)>(p => progress.Report(new DownloadProgress
                {
                    Stage = "下载 FRP 组件",
                    Current = p.Done,
                    Total = p.Total,
                    Detail = p.Total > 0 ? $"{p.Done * 100 / p.Total}%" : "frpc " + FrpVersion
                }));
            await HttpService.DownloadFileAsync(url, zip, "", downloadProgress, ct, 3);
        }
        var temp = Path.Combine(AppPaths.CacheDir, "frp-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            ZipFile.ExtractToDirectory(zip, temp, true);
            var source = Directory.EnumerateFiles(temp, "frpc.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (source is null) throw new InvalidOperationException("FRP 压缩包中缺少 frpc.exe。");
            File.Copy(source, client, true);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    public async Task StartAsync(string server, int serverPort, string token, int localPort, int remotePort,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Stop();
        await EnsureInstalledAsync(progress, ct);
        Directory.CreateDirectory(ToolDirectory);
        var config = Path.Combine(ToolDirectory, "frpc.toml");
        var safeName = "minecraft-" + localPort;
        File.WriteAllText(config,
            "serverAddr = \"" + server.Replace("\"", "") + "\"\n" +
            "serverPort = " + serverPort + "\n" +
            "auth.method = \"token\"\n" +
            "auth.token = \"" + token.Replace("\"", "") + "\"\n" +
            "loginFailExit = false\n\n" +
            "[[proxies]]\n" +
            "name = \"" + safeName + "\"\n" +
            "type = \"tcp\"\n" +
            "localIP = \"127.0.0.1\"\n" +
            "localPort = " + localPort + "\n" +
            "remotePort = " + remotePort + "\n");

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(ToolDirectory, "frpc.exe"),
            WorkingDirectory = ToolDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(config);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process))
            {
                LastError = $"FRP 客户端已退出（代码 {process.ExitCode}）。";
                LogReceived?.Invoke(LastError);
            }
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动 FRP 客户端。");
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await Task.Delay(1000, ct);
        if (process.HasExited)
            throw new InvalidOperationException(LastError ?? "FRP 客户端启动失败，请检查服务器地址、端口和 token。");
    }

    private void Append(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        LogReceived?.Invoke(line);
        if (line.Contains("start proxy success", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("login to server success", StringComparison.OrdinalIgnoreCase))
            LastError = null;
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); } catch { }
        try { process.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
