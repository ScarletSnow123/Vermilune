using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Open.Nat;

namespace Vermilune.Core;

/// <summary>
/// 使用路由器 UPnP 自动映射 Minecraft 服务端端口。没有公网 IP 或 UPnP 不可用时只返回本地地址。
/// </summary>
public sealed class UpnpService
{
    private NatDevice? _device;
    private Mapping? _mapping;

    public string? PublicIp { get; private set; }
    public string? LocalIp { get; private set; }
    public string Status { get; private set; } = "未检测";

    public async Task<(string? PublicAddress, string? LocalAddress, string Status)> MapAsync(int port, CancellationToken ct)
    {
        await UnmapAsync();
        LocalIp = FindLocalIpv4();
        try
        {
            var discoverer = new NatDiscoverer();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            _device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, timeout);
            if (_device is null)
            {
                Status = "路由器未启用 UPnP，请手动转发端口。";
                return (null, LocalIp, Status);
            }

            var external = await _device.GetExternalIPAsync();
            _mapping = new Mapping(Protocol.Tcp, port, port, 86400, "Vermilune Minecraft Server");
            await _device.CreatePortMapAsync(_mapping);
            var externalText = external?.ToString() ?? "";
            if (IsPrivate(external))
            {
                // 路由器 WAN 口是内网地址时，UPnP 映射通常无法从公网访问，不能把公网 IP 检测结果当成可连接地址。
                PublicIp = null;
                Status = "UPnP 已创建映射，但路由器 WAN 是内网地址，可能是 CGNAT / 双重 NAT；请使用本地地址或在路由器手动转发端口。";
            }
            else if (string.IsNullOrWhiteSpace(externalText) || externalText == "0.0.0.0")
            {
                Status = "UPnP 已连接，但没有获取到公网 IP。";
                PublicIp = null;
            }
            else
            {
                PublicIp = externalText;
                Status = "UPnP 端口映射成功。";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Status = "UPnP 失败：" + ex.Message;
            PublicIp = null;
            Log.Warn("UPnP 端口映射失败：" + ex.Message);
        }
        return (PublicIp, LocalIp, Status);
    }

    public async Task UnmapAsync()
    {
        if (_device is null || _mapping is null) return;
        try { await _device.DeletePortMapAsync(_mapping); } catch { }
        _mapping = null;
        PublicIp = null;
    }

    private static bool IsPrivate(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork) return true;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               bytes[0] == 127 ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private static async Task<string?> TryGetPublicIpAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var text = await HttpService.GetStringAsync("https://api.ipify.org", timeout.Token);
            return IPAddress.TryParse(text.Trim(), out var ip) ? ip.ToString() : null;
        }
        catch { return null; }
    }

    public static string? FindGlobalIpv6()
    {
        try
        {
            foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                         .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                         .Select(a => a.Address)
                         .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 &&
                                     !IPAddress.IsLoopback(a) &&
                                     !a.IsIPv6LinkLocal &&
                                     !a.IsIPv6SiteLocal &&
                                     !a.IsIPv6Multicast &&
                                     !a.IsIPv6Teredo))
            {
                var bytes = address.GetAddressBytes();
                // 公网 IPv6 一般是 2000::/3；排除 ULA fc00::/7。
                if ((bytes[0] & 0xE0) == 0x20) return address.ToString();
            }
        }
        catch { }
        return null;
    }

    private static string? FindLocalIpv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .OrderBy(a => a.StartsWith("192.168.") || a.StartsWith("10.") || a.StartsWith("172.") ? 0 : 1)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
