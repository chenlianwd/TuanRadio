using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AIRadio.Desktop.Services.WebRemote;

/// <summary>
/// 发现当前活跃的局域网 IPv4 地址（优先 Wi-Fi / 有线物理网卡 RFC 1918 私网段，过滤链路本地与常见虚拟网卡），
/// 并生成 URL 安全随机配对令牌。
/// </summary>
public static class LanAddressResolver
{
    public const int DefaultPort = 37260;

    private static readonly string[] VirtualAdapterHints =
    [
        "vethernet", "hyper-v", "wsl", "docker", "vmware", "virtualbox", "vbox",
        "zerotier", "tailscale", "tap", "tun", "npcap", "loopback"
    ];

    public static string GenerateAccessToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static IReadOnlyList<string> GetPreferredLanIpv4Addresses()
    {
        var candidates = new List<(string Address, int Score)>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var label = $"{nic.Name} {nic.Description}".ToLowerInvariant();
                var isVirtual = VirtualAdapterHints.Any(h => label.Contains(h, StringComparison.Ordinal));
                var hasGateway = false;
                IPInterfaceProperties ipProps;
                try
                {
                    ipProps = nic.GetIPProperties();
                    hasGateway = ipProps.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(g.Address) &&
                        !g.Address.Equals(IPAddress.Any));
                }
                catch
                {
                    continue;
                }

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(ip))
                        continue;

                    var bytes = ip.GetAddressBytes();
                    // 排除 169.254.0.0/16 链路本地与 0.0.0.0
                    if (bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254))
                        continue;

                    var score = 0;
                    if (!isVirtual) score += 100;
                    if (hasGateway) score += 50;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 20;
                    else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 15;
                    if (IsRfc1918Private(bytes)) score += 30;
                    if (bytes[0] == 192 && bytes[1] == 168) score += 10;

                    candidates.Add((ip.ToString(), score));
                }
            }
        }
        catch
        {
            // 网络枚举异常时安全回退本地回环地址
        }

        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Address, StringComparer.Ordinal)
            .Select(c => c.Address)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ordered.Count == 0)
            ordered.Add("127.0.0.1");

        return ordered;
    }

    public static bool IsRfc1918Private(byte[] bytes)
    {
        if (bytes.Length != 4) return false;
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
