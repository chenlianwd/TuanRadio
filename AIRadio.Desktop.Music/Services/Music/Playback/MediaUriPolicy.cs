using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 播放 URL 进入 LibVLC 前的统一安全校验（docs/plans 2026-09-20 §3.4 最小版）。
/// 校验对象是"交给播放器的播放 URL"，不是音源 API 请求——网易/酷狗的本地代理
/// （127.0.0.1:37250/37251）只是 API 通道，播放 URL 是上游 CDN 公网地址，不受影响。
/// 一律 fail-closed：scheme 不在白名单、DNS 解析失败、多地址任一落入禁段即拒绝。
/// 已知局限：LibVLC 内部跟随重定向，无法逐 hop 复检目标；只承诺初始 URL 不指向私网/回环。
/// </summary>
public static class MediaUriPolicy
{
    /// <summary>
    /// 校验播放地址是否允许交给播放器。DNS 主机名按解析后实际地址检查
    /// （多 A/AAAA 记录任一落入禁段即拒）；IP 字面量不触发 DNS。
    /// </summary>
    public static Task<bool> ValidateAsync(
        ProviderNetworkScope scope,
        Uri uri,
        CancellationToken cancellationToken)
        => ValidateAsync(scope, uri, ResolveHostAddressesAsync, cancellationToken);

    /// <summary>测试注入点：以固定地址解析器替代真实 DNS（单测零联网）。</summary>
    internal static Task<bool> ValidateAsync(
        ProviderNetworkScope scope,
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        if (uri == null || !uri.IsAbsoluteUri)
            return Task.FromResult(false);
        // 用户名/密码形式的 URL 不进入播放器，也避免其意外出现在底层日志中。
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return Task.FromResult(false);

        switch (scope)
        {
            case ProviderNetworkScope.LocalFileOnly:
                // 阶段 2 本地曲库专用；http(s) 播放流对它同样算越权
                return Task.FromResult(uri.Scheme == Uri.UriSchemeFile);

            case ProviderNetworkScope.UserConfiguredPrivateNetwork:
                // 具体的 scheme、主机、端口和路径由 IPrivateMediaOriginPolicy 再收紧。
                return Task.FromResult(IsHttpScheme(uri));

            case ProviderNetworkScope.PublicInternet:
                if (!IsHttpScheme(uri))
                    return Task.FromResult(false);
                return ValidatePublicHostAsync(uri.Host, resolve, cancellationToken);

            default:
                return Task.FromResult(false);
        }
    }

    private static bool IsHttpScheme(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static async Task<bool> ValidatePublicHostAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(host))
            return false;

        if (IPAddress.TryParse(host, out var literal))
            return !IsForbiddenAddress(literal);

        // DNS 主机名：解析失败或无地址 fail-closed 拒绝（不静默放行）
        IPAddress[] addresses;
        try
        {
            addresses = await resolve(host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }

        return addresses is { Length: > 0 } && addresses.All(address => !IsForbiddenAddress(address));
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    /// 禁段判定：loopback（127/8 全段）、RFC1918 私网、链路本地（含云元数据 169.254.169.254）、
    /// 组播、240/4 保留段（含受限广播 255.255.255.255）、未指定地址；IPv6 同口径
    /// （含 IPv4 映射/兼容地址解包）。全部按字节判定，不依赖 IPAddress 的 Is* 属性面。
    /// </summary>
    internal static bool IsForbiddenAddress(IPAddress address)
    {
        // IPv4-mapped（::ffff:a.b.c.d）与已弃用的 IPv4-compatible（::a.b.c.d）解包后按 IPv4 判定
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var v6 = address.GetAddressBytes();
            if (v6.Length == 16 &&
                v6[0] == 0 && v6[1] == 0 && v6[2] == 0 && v6[3] == 0 &&
                v6[4] == 0 && v6[5] == 0 && v6[6] == 0 && v6[7] == 0 &&
                v6[8] == 0 && v6[9] == 0 &&
                ((v6[10] == 0xFF && v6[11] == 0xFF) || (v6[10] == 0 && v6[11] == 0)))
                address = new IPAddress(v6[12..16]);
        }

        if (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 0 ||                                    // 0.0.0.0/8 未指定
                   bytes[0] == 127 ||                                  // 127/8 回环段（Windows 整段路由本机，只封 127.0.0.1 会留绕过面）
                   bytes[0] == 10 ||                                   // 10/8 RFC1918
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || // 172.16/12 RFC1918
                   bytes[0] == 192 && bytes[1] == 168 ||               // 192.168/16 RFC1918
                   bytes[0] == 169 && bytes[1] == 254 ||               // 169.254/16 链路本地+云元数据
                   (bytes[0] & 0xF0) == 0xE0 ||                        // 224/4 组播
                   (bytes[0] & 0xF0) == 0xF0;                          // 240/4 保留段（含受限广播 255.255.255.255）
        }

        if (bytes.Length == 16)
        {
            return bytes[0] == 0xFF ||                                 // ff00::/8 组播
                   (bytes[0] & 0xFE) == 0xFC ||                        // fc00::/7 唯一本地
                   (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) ||  // fe80::/10 链路本地
                   (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0);    // fec0::/10 站点本地（已弃用仍拒）
        }

        // 未知地址族：fail-closed
        return true;
    }
}
