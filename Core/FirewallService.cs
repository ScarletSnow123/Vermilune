using System.Diagnostics;
using System.Text;

namespace Vermilune.Core;

public static class FirewallService
{
    public static async Task<string> EnsureTcpRuleAsync(int port, CancellationToken ct = default)
    {
        var ruleName = $"Vermilune Minecraft Server {port}";
        try
        {
            var show = await RunAsync($"advfirewall firewall show rule name=\"{ruleName}\"", ct);
            if (show.ExitCode == 0) return "Windows 防火墙规则已存在。";
            var add = await RunAsync(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}",
                ct);
            return add.ExitCode == 0
                ? "Windows 防火墙已放行端口。"
                : "Windows 防火墙规则创建失败，请以管理员身份运行或手动放行端口。";
        }
        catch (Exception ex)
        {
            return "Windows 防火墙配置失败：" + ex.Message;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法运行 netsh。");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output);
    }
}
