using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 重定向逐跳复检回归：探测只跟随 3xx 并在请求下一跳前校验目标（违规地址绝不发出请求）；
/// 4xx/5xx/传输异常/缺 Location 属"探测不可得"，不构成拒绝依据。
/// </summary>
public class PlaybackRedirectGuardTests
{
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(Uri Uri, string? Range)> Requests { get; } = new();

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Range?.ToString()));
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Redirect(Uri location)
        => new(HttpStatusCode.Found) { Headers = { Location = location } };

    [Fact]
    public async Task TwoHopsThenSuccess_VerifiesEachHopWithRangeProbe()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/start" => Redirect(new Uri("https://203.0.113.5/hop1")),
            "/hop1" => Redirect(new Uri("/final", UriKind.Relative)), // 相对 Location 引用
            _ => new HttpResponseMessage(HttpStatusCode.PartialContent)
        });
        var validated = new List<Uri>();
        var guard = new PlaybackRedirectGuard(new HttpClient(handler));

        var result = await guard.VerifyAsync(
            new Uri("https://203.0.113.9/start"),
            (uri, _) => { validated.Add(uri); return Task.FromResult(true); },
            CancellationToken.None);

        Assert.Equal(RedirectWalkResult.Verified, result);
        // 相对 Location 按当前跳基址解析为绝对地址后再校验
        Assert.Equal(
            new[] { new Uri("https://203.0.113.5/hop1"), new Uri("https://203.0.113.5/final") },
            validated);
        // 探测形态：GET + Range: bytes=0-0，逐跳顺序 start→hop1→final
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, item => Assert.Equal("bytes=0-0", item.Range));
    }

    [Fact]
    public async Task ForbiddenHop_RejectedWithoutRequestingIt()
    {
        var handler = new RoutingHandler(_ => Redirect(new Uri("http://192.168.0.5/stream.mp3")));
        var guard = new PlaybackRedirectGuard(new HttpClient(handler));

        var result = await guard.VerifyAsync(
            new Uri("https://203.0.113.9/start"),
            (uri, _) => Task.FromResult(false),
            CancellationToken.None);

        Assert.Equal(RedirectWalkResult.Rejected, result);
        // 违规跳转目标只被校验、绝不能发出请求（否则探测本身变成 SSRF 向量）
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "192.168.0.5");
    }

    [Fact]
    public async Task HopLimitExceeded_Rejected()
    {
        var handler = new RoutingHandler(request =>
            Redirect(new Uri("https://203.0.113.5/next-" + (int.TryParse(
                request.RequestUri!.AbsolutePath.Substring("/next-".Length), out var n) ? n + 1 : 0))));
        var guard = new PlaybackRedirectGuard(new HttpClient(handler));

        var result = await guard.VerifyAsync(
            new Uri("https://203.0.113.9/next-0"),
            (uri, _) => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(RedirectWalkResult.Rejected, result);
        Assert.True(handler.Requests.Count <= 6); // 上限 5 跳 + 首请求
    }

    [Fact]
    public async Task NonRedirectErrorStatus_Inconclusive()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var guard = new PlaybackRedirectGuard(new HttpClient(handler));

        Assert.Equal(RedirectWalkResult.Inconclusive, await guard.VerifyAsync(
            new Uri("https://203.0.113.9/start"),
            (uri, _) => Task.FromResult(true),
            CancellationToken.None));
    }

    [Fact]
    public async Task TransportFailure_Inconclusive()
    {
        var handler = new ThrowingHandler();
        var guard = new PlaybackRedirectGuard(new HttpClient(handler));

        Assert.Equal(RedirectWalkResult.Inconclusive, await guard.VerifyAsync(
            new Uri("https://203.0.113.9/start"),
            (uri, _) => Task.FromResult(true),
            CancellationToken.None));
    }

    [Fact]
    public async Task RedirectWithoutLocation_Inconclusive()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));
        var guard = new PlaybackRedirectGuard(new HttpClient(handler));

        Assert.Equal(RedirectWalkResult.Inconclusive, await guard.VerifyAsync(
            new Uri("https://203.0.113.9/start"),
            (uri, _) => Task.FromResult(true),
            CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_TreatedAsInconclusiveNotRejection()
    {
        var handler = new CancellingHandler();
        var guard = new PlaybackRedirectGuard(new HttpClient(handler));

        // 探测超时只说明"观察不到跳转"，不构成拒绝依据（可用性优先）
        Assert.Equal(RedirectWalkResult.Inconclusive, await guard.VerifyAsync(
            new Uri("https://203.0.113.9/start"),
            (uri, _) => Task.FromResult(true),
            CancellationToken.None));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused (test)");
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));
    }

    // 日志脱敏形状：播放 URL 的 query 携带 OpenSubsonic token（u/t/s，无过期可重放）与
    // 各源签名参数，落日志必须剥除 query/fragment，只留 scheme://host[:port]/path
    [Theory]
    [InlineData(
        "https://navidrome.example.com/rest/stream?id=42&u=demo&t=abc123&s=deadbeef",
        "https://navidrome.example.com/rest/stream")]
    [InlineData(
        "https://music.example.com/audio/a.mp3?authSecret=xyz&sign=1#frag",
        "https://music.example.com/audio/a.mp3")]
    [InlineData(
        "http://203.0.113.9:8080/song.flac?token=x",
        "http://203.0.113.9:8080/song.flac")]
    [InlineData(
        "https://music.example.com/track",
        "https://music.example.com/track")]
    public void UrlForLog_StripsQueryAndFragment_KeepsNonDefaultPort(string input, string expected)
    {
        Assert.Equal(expected, PlaybackRedirectGuard.UrlForLog(new Uri(input)));
    }
}
