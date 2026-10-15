using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 扫码轮询的状态映射：传输层失败（本地代理不可达）必须与业务失败（Failed）区分，
/// 供设置页展示"服务不可用"而非误导性的"登录失败"。
/// </summary>
public class AccountQrCheckTests
{
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly System.Func<HttpRequestMessage, Task<HttpResponseMessage>> _send;
        public RoutingHandler(System.Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _send(request);
    }

    private static Task<HttpResponseMessage> ConnectionRefused()
        => Task.FromException<HttpResponseMessage>(new HttpRequestException(
            "connection refused", new SocketException((int)SocketError.ConnectionRefused)));

    private static Task<HttpResponseMessage> Json(string body)
        => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        });

    [Fact]
    public async Task Kugou_CheckQr_ConnectionFailure_MapsToServiceUnavailable()
    {
        var service = new KugouAccountService(new HttpClient(
            new RoutingHandler(_ => ConnectionRefused())));

        var result = await service.CheckQrAsync("key");

        Assert.Equal(QrState.ServiceUnavailable, result.State);
    }

    [Fact]
    public async Task Netease_CheckQr_ConnectionFailure_MapsToServiceUnavailable()
    {
        var service = new NeteaseAccountService(new HttpClient(
            new RoutingHandler(_ => ConnectionRefused())));

        var result = await service.CheckQrAsync("key");

        Assert.Equal(QrState.ServiceUnavailable, result.State);
    }

    [Fact]
    public async Task Kugou_CheckQr_MalformedShape_StillMapsToFailed()
    {
        var service = new KugouAccountService(new HttpClient(
            new RoutingHandler(_ => Json("""{"unrelated":true}"""))));

        var result = await service.CheckQrAsync("key");

        Assert.Equal(QrState.Failed, result.State);
    }

    // 活着的代理用非 2xx 表达业务失败（酷狗代理即 502 + JSON 契约）：
    // GetStringAsync 对非 2xx 也抛 HttpRequestException，但不得误判为服务不可用
    [Fact]
    public async Task Kugou_CheckQr_HttpErrorFromLiveServer_MapsToFailedNotUnavailable()
    {
        var service = new KugouAccountService(new HttpClient(
            new RoutingHandler(_ => Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.BadGateway)))));

        var result = await service.CheckQrAsync("key");

        Assert.Equal(QrState.Failed, result.State);
    }

    [Fact]
    public async Task Netease_CheckQr_HttpErrorFromLiveServer_MapsToFailedNotUnavailable()
    {
        var service = new NeteaseAccountService(new HttpClient(
            new RoutingHandler(_ => Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.InternalServerError)))));

        var result = await service.CheckQrAsync("key");

        Assert.Equal(QrState.Failed, result.State);
    }

    [Fact]
    public async Task Netease_CheckQr_UnknownCode_StillMapsToFailed()
    {
        var service = new NeteaseAccountService(new HttpClient(
            new RoutingHandler(_ => Json("""{"code":999}"""))));

        var result = await service.CheckQrAsync("key");

        Assert.Equal(QrState.Failed, result.State);
    }

    [Fact]
    public async Task Kugou_CreateQrSession_TransientFailure_RetriesOnceAndSucceeds()
    {
        // 实机观察：活代理以 502+JSON 表达上游瞬时超时（axios 15s ECONNABORTED），
        // 立即复测即恢复——重试一次可自愈，用户无需手动再点
        var calls = 0;
        var service = new KugouAccountService(new HttpClient(new RoutingHandler(_ =>
        {
            calls++;
            return calls == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException(
                    "simulated upstream timeout (502)"))
                : Json("""{"data":{"qrcode":"qr-key-1","qrcode_img":"data:image/png;base64,AAAA"}}""");
        })));

        var session = await service.CreateQrSessionAsync();

        Assert.NotNull(session);
        Assert.Equal("qr-key-1", session!.Key);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Kugou_CreateQrSession_SecondFailureAlsoFails_ReturnsNull()
    {
        var calls = 0;
        var service = new KugouAccountService(new HttpClient(new RoutingHandler(_ =>
        {
            calls++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                "simulated upstream timeout (502)"));
        })));

        var session = await service.CreateQrSessionAsync();

        Assert.Null(session);
        Assert.Equal(2, calls);
    }
}
