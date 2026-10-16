using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 网易代理收养判定的身份握手校验：仅受 start.js 控制的自有进程持有注入的
/// TUANRADIO_PROXY_IDENTITY（安装级令牌），端口上的冒充者/手动实例答不上来。
/// 哈希常量与代理侧 sha256(token) 实证一致（sha256sum 校验）。
/// </summary>
public class MusicApiServerProxyIdentityTests
{
    private const string ServiceName = "tuanradio-netease-proxy";

    // sha256("test-token-123") 的十六进制小写，与 server/start.js 的 digest('hex') 同口径
    private const string TokenHash = "19b6b086eebb807f54e6327309dec0ff347a6c3c30bf3bb396f167513eba3475";

    [Fact]
    public void ProxyIdentityHash_MatchesProxySideSha256Hex()
    {
        Assert.Equal(TokenHash, MusicApiServer.ProxyIdentityHash("test-token-123"));
    }

    [Fact]
    public void MatchesProxyIdentityResponse_AcceptsOnlyExactHandshake()
    {
        var ok = $$"""{"status":1,"service":"{{ServiceName}}","protocol":1,"device_hash":"{{TokenHash}}"}""";
        Assert.True(MusicApiServer.MatchesProxyIdentityResponse(ok, ServiceName, TokenHash));
        // 摘要比对大小写不敏感（代理侧恒为小写，此处放宽口径防未来形态变化）
        var upperHash = $$"""{"status":1,"service":"{{ServiceName}}","protocol":1,"device_hash":"{{TokenHash.ToUpperInvariant()}}"}""";
        Assert.True(MusicApiServer.MatchesProxyIdentityResponse(upperHash, ServiceName, TokenHash));

        // 冒充者：令牌不一致（手动实例/端口抢占进程没有注入的安装令牌）
        var wrongHash = $$"""{"status":1,"service":"{{ServiceName}}","protocol":1,"device_hash":"deadbeef"}""";
        Assert.False(MusicApiServer.MatchesProxyIdentityResponse(wrongHash, ServiceName, TokenHash));
        // 服务名/协议版不符（其他代理或上游原生实例）
        Assert.False(MusicApiServer.MatchesProxyIdentityResponse(
            $$"""{"status":1,"service":"tuanradio-kugou-proxy","protocol":1,"device_hash":"{{TokenHash}}"}""",
            ServiceName, TokenHash));
        // 上游搜索响应形状（旧判定口径）不再被认作自有实例
        Assert.False(MusicApiServer.MatchesProxyIdentityResponse(
            """{"code":200,"result":{"songs":[]}}""", ServiceName, TokenHash));
        // 垃圾输入
        Assert.False(MusicApiServer.MatchesProxyIdentityResponse(null, ServiceName, TokenHash));
        Assert.False(MusicApiServer.MatchesProxyIdentityResponse("not json", ServiceName, TokenHash));
    }
}
