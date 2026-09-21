using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class MediaUriPolicyTests
{
    private static Task<IPAddress[]> Resolve(params IPAddress[] addresses)
        => Task.FromResult(addresses);

    private static Task<IPAddress[]> ResolveFailure(string host, CancellationToken _)
        => throw new SocketException((int)SocketError.HostNotFound);

    private static Task<bool> Validate(
        ProviderNetworkScope scope,
        string url,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        var uri = new Uri(url, UriKind.Absolute);
        return MediaUriPolicy.ValidateAsync(
            scope,
            uri,
            resolver ?? ((_, _) => Resolve(IPAddress.Parse("203.0.113.10"))),
            CancellationToken.None);
    }

    [Theory]
    [InlineData("http://media.example.com/stream.mp3")]
    [InlineData("https://cdn.example.com/aac")]
    public async Task PublicInternet_HttpUrlWithPublicDns_Allowed(string url)
        => Assert.True(await Validate(ProviderNetworkScope.PublicInternet, url));

    [Theory]
    [InlineData("ftp://media.example.com/file")]
    [InlineData("file://E:/music/song.mp3")]
    public async Task PublicInternet_NonHttpScheme_Rejected(string url)
        => Assert.False(await Validate(ProviderNetworkScope.PublicInternet, url));

    [Fact]
    public async Task PublicInternet_IpLiteralForbiddenRanges_RejectedWithoutDns()
    {
        var dnsQueried = false;
        Task<IPAddress[]> Resolver(string host, CancellationToken ct)
        {
            dnsQueried = true;
            return Resolve(IPAddress.Parse("203.0.113.10"));
        }

        var forbidden = new[]
        {
            "http://127.0.0.1/stream",
            "http://10.1.2.3/stream",
            "http://172.16.0.1/stream",
            "http://172.31.255.255/stream",
            "http://192.168.1.1/stream",
            "http://169.254.169.254/latest/meta-data",
            "http://224.0.0.1/stream",
            "http://0.0.0.0/stream",
            "http://[::1]/stream",
            "http://[fe80::1]/stream",
            "http://[fc00::1]/stream",
            "http://[ff02::1]/stream",
            "http://[::ffff:127.0.0.1]/stream",
            "http://[::]/stream"
        };

        foreach (var url in forbidden)
            Assert.False(await Validate(ProviderNetworkScope.PublicInternet, url, Resolver), url);

        Assert.False(dnsQueried, "IP 字面量不得触发 DNS 查询");
    }

    [Fact]
    public async Task PublicInternet_PublicIpLiteral_AllowedWithoutDns()
    {
        var dnsQueried = false;
        Task<IPAddress[]> Resolver(string host, CancellationToken ct)
        {
            dnsQueried = true;
            return Resolve(IPAddress.Parse("203.0.113.10"));
        }

        Assert.True(await Validate(ProviderNetworkScope.PublicInternet, "http://203.0.113.10/stream", Resolver));
        Assert.True(await Validate(ProviderNetworkScope.PublicInternet, "http://[2606:4700::1]/stream", Resolver));
        Assert.False(dnsQueried);
    }

    [Fact]
    public async Task PublicInternet_DnsAnyForbiddenAddress_Rejected()
        => Assert.False(await Validate(
            ProviderNetworkScope.PublicInternet,
            "http://cdn.example.com/stream",
            (_, _) => Resolve(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("192.168.0.1"))));

    [Fact]
    public async Task PublicInternet_DnsFailure_FailClosed()
        => Assert.False(await Validate(
            ProviderNetworkScope.PublicInternet,
            "http://cdn.example.com/stream",
            ResolveFailure));

    [Fact]
    public async Task PublicInternet_DnsEmptyResult_FailClosed()
        => Assert.False(await Validate(
            ProviderNetworkScope.PublicInternet,
            "http://cdn.example.com/stream",
            (_, _) => Resolve()));

    [Fact]
    public async Task LocalFileOnly_AllowsFileSchemeAndRejectsHttp()
    {
        Assert.True(await Validate(ProviderNetworkScope.LocalFileOnly, "file://E:/music/song.mp3"));
        Assert.False(await Validate(ProviderNetworkScope.LocalFileOnly, "http://media.example.com/stream"));
    }

    [Fact]
    public async Task UserConfiguredPrivateNetwork_AllowsHttpButStillSchemeGated()
    {
        Assert.True(await Validate(ProviderNetworkScope.UserConfiguredPrivateNetwork, "http://192.168.1.10/stream"));
        Assert.True(await Validate(ProviderNetworkScope.UserConfiguredPrivateNetwork, "https://navidrome.local/stream"));
        Assert.False(await Validate(ProviderNetworkScope.UserConfiguredPrivateNetwork, "ftp://navidrome.local/stream"));
    }
}
