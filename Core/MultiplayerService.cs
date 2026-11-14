using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Security.Principal;

namespace Vermilune.Core;

/// <summary>
/// 基于 EasyTier 的虚拟局域网联机。EasyTier 首次使用时下载到启动器数据目录。
/// </summary>
public sealed class MultiplayerService : IDisposable
{
    public const string DefaultPublicNode = "tcp://easytier-us.slarker.me:11010";
    private const string EasyTierVersion = "2.6.4";
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };
    public string? VirtualIp { get; private set; }
    public string? LastError { get; private set; }
    public event Action<string>? LogReceived;

    public string ToolDirectory => Path.Combine(AppPaths.DataDir, "Tools", "EasyTier");

    public async Task EnsureInstalledAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var core = Path.Combine(ToolDirectory, "easytier-core.exe");
        var cli = Path.Combine(ToolDirectory, "easytier-cli.exe");
        if (File.Exists(core) && File.Exists(cli)) return;

        Directory.CreateDirectory(ToolDirectory);
        var zip = Path.Combine(AppPaths.CacheDir, $"easytier-windows-x86_64-v{EasyTierVersion}.zip");
        if (!File.Exists(zip) || new FileInfo(zip).Length < 1024 * 1024)
        {
            var url = $"https://github.com/EasyTier/EasyTier/releases/download/v{EasyTierVersion}/easytier-windows-x86_64-v{EasyTierVersion}.zip";
            progress?.Report(new DownloadProgress { Stage = "下载联机组件", Detail = "EasyTier " + EasyTierVersion });
            var downloadProgress = progress is null
                ? null
                : new Progress<(long Done, long Total)>(p => progress.Report(new DownloadProgress
                {
                    Stage = "下载联机组件",
                    Current = p.Done,
                    Total = p.Total,
                    Detail = p.Total > 0 ? $"{p.Done * 100 / p.Total}%" : "EasyTier " + EasyTierVersion
                }));
            await HttpService.DownloadFileAsync(url, zip, "", downloadProgress, ct, 3);
        }

        var temp = Path.Combine(AppPaths.CacheDir, "easytier-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            ZipFile.ExtractToDirectory(zip, temp, true);
            var source = Directory.EnumerateFiles(temp, "easytier-core.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (source is null) throw new InvalidOperationException("EasyTier 压缩包中缺少 easytier-core.exe。");
            var sourceDir = Path.GetDirectoryName(source)!;
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var name = Path.GetFileName(file);
                if (name is "easytier-core.exe" or "easytier-cli.exe" or "wintun.dll" or "WinDivert64.sys" or "Packet.dll")
                    File.Copy(file, Path.Combine(ToolDirectory, name), true);
            }
            if (!File.Exists(core)) throw new InvalidOperationException("EasyTier 组件解压失败。");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    public async Task StartAsync(string roomCode, string password, string virtualIp, string publicNode,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Stop();
        await EnsureInstalledAsync(progress, ct);
        var core = Path.Combine(ToolDirectory, "easytier-core.exe");
        if (!File.Exists(core)) throw new InvalidOperationException("EasyTier 组件不存在。");

        var network = "Vermilune-" + roomCode.Trim();
        var ip = virtualIp.Trim();
        if (!ip.Contains('/')) ip += "/24";
        var node = string.IsNullOrWhiteSpace(publicNode) ? DefaultPublicNode : publicNode.Trim();

        var psi = new ProcessStartInfo
        {
            FileName = core,
            WorkingDirectory = ToolDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[]
                 {
                     "-i", ip,
                     "--network-name", network,
                     "--network-secret", password,
                     "-p", node
                 })
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var log = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (log) log.AppendLine(e.Data);
            LogReceived?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (log) log.AppendLine(e.Data);
            LogReceived?.Invoke(e.Data);
        };
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_process, process))
            {
                lock (log)
                    LastError = $"联机组件已退出（代码 {process.ExitCode}）：{Tail(log.ToString())}";
                LogReceived?.Invoke(LastError);
            }
        };
        if (!process.Start()) throw new InvalidOperationException("无法启动联机组件。");
        _process = process;
        VirtualIp = ip.Split('/')[0];
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await Task.Delay(1200, ct);
        if (process.HasExited && process.ExitCode != 0)
        {
            if (IsAdministrator())
            {
                LastError = "联机组件启动失败：" + Tail(log.ToString());
                throw new InvalidOperationException(LastError);
            }
            process.Dispose();
            _process = StartElevated(core, ToolDirectory, ip, network, password, node);
            await Task.Delay(800, ct);
        }
        if (_process.HasExited)
        {
            LastError = "联机组件启动失败，请确认已同意管理员权限并检查网络。";
            throw new InvalidOperationException(LastError);
        }
    }

    private static Process StartElevated(string core, string workdir, string ip, string network, string secret, string node)
    {
        if (!IsAdministrator())
        {
            var psi = new ProcessStartInfo
            {
                FileName = core,
                WorkingDirectory = workdir,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var arg in new[] { "-i", ip, "--network-name", network, "--network-secret", secret, "-p", node })
                psi.ArgumentList.Add(arg);
            try
            {
                return Process.Start(psi) ?? throw new InvalidOperationException("无法启动提权后的联机组件。");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException("需要管理员权限才能创建虚拟网卡；已取消授权。", ex);
            }
        }

        var direct = new ProcessStartInfo
        {
            FileName = core,
            WorkingDirectory = workdir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-i", ip, "--network-name", network, "--network-secret", secret, "-p", node })
            direct.ArgumentList.Add(arg);
        return Process.Start(direct) ?? throw new InvalidOperationException("无法启动联机组件。");
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        VirtualIp = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch { }
        try { process.Dispose(); } catch { }
    }

    public void Dispose() => Stop();

    private static string Tail(string text) => text.Length > 1600 ? text[^1600..] : text;
}
