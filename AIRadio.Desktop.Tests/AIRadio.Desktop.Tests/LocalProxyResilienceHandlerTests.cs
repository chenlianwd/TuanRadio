using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class LocalProxyResilienceHandlerTests
{
    private const int NeteasePort = 37250;
    private const int KugouPort = 37251;

    /// <summary>
    /// 脚本式内层 handler：按序消费步骤（异常=抛出，响应=返回），耗尽后重复最后一步。
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly object[] _steps;
        public int Calls { get; private set; }

        public ScriptedHandler(params object[] steps) => _steps = steps;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var step = _steps[Math.Min(Calls, _steps.Length) - 1];
            return step is Exception exception
                ? Task.FromException<HttpResponseMessage>(exception)
                : Task.FromResult((HttpResponseMessage)step);
        }
    }

    private static HttpRequestException ConnectionRefused()
        => new("connection refused", new SocketException((int)SocketError.ConnectionRefused));

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    private static HttpClient CreateClient(
        ScriptedHandler inner,
        Action<int> onResurrect,
        out ResurrectCounter counter,
        TimeSpan? cooldown = null,
        CancellationToken appLifetime = default)
    {
        counter = new ResurrectCounter();
        var captured = counter;
        var handler = new LocalProxyResilienceHandler(
            new[] { NeteasePort, KugouPort },
            (port, ct) =>
            {
                captured.Count++;
                onResurrect(port);
                return Task.CompletedTask;
            },
            appLifetime,
            inner,
            cooldown);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    internal sealed class ResurrectCounter
    {
        public int Count;
    }

    private static async Task AssertRefusedAsync(Func<Task<HttpResponseMessage>> request)
    {
        await Assert.ThrowsAsync<HttpRequestException>(request);
    }

    [Fact]
    public async Task SuccessOnFirstAttempt_DoesNotResurrect()
    {
        var inner = new ScriptedHandler(Ok());
        using var client = CreateClient(inner, _ => { }, out var counter);

        using var response = await client.GetAsync($"http://127.0.0.1:{NeteasePort}/search?keywords=t");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task ConnectionRefused_ResurrectsAndRetriesOnce()
    {
        var inner = new ScriptedHandler(ConnectionRefused(), Ok());
        using var client = CreateClient(inner, _ => { }, out var counter);

        using var response = await client.GetAsync($"http://127.0.0.1:{KugouPort}/login/qr/check?key=k");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task RetryStillRefused_PropagatesAndEntersCooldown()
    {
        var inner = new ScriptedHandler(ConnectionRefused(), ConnectionRefused(), ConnectionRefused());
        using var client = CreateClient(inner, _ => { }, out var counter);

        await AssertRefusedAsync(() => client.GetAsync($"http://127.0.0.1:{NeteasePort}/search"));
        Assert.Equal(2, inner.Calls);
        Assert.Equal(1, counter.Count);

        // 冷却期内：不再拉起，请求直接失败
        await AssertRefusedAsync(() => client.GetAsync($"http://127.0.0.1:{NeteasePort}/search"));
        Assert.Equal(3, inner.Calls);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task CooldownExpires_AllowsResurrectAgain()
    {
        var inner = new ScriptedHandler(
            ConnectionRefused(), ConnectionRefused(), ConnectionRefused(), ConnectionRefused(), Ok());
        using var client = CreateClient(inner, _ => { }, out var counter, cooldown: TimeSpan.FromMilliseconds(80));

        await AssertRefusedAsync(() => client.GetAsync($"http://127.0.0.1:{NeteasePort}/search"));
        await AssertRefusedAsync(() => client.GetAsync($"http://127.0.0.1:{NeteasePort}/search"));
        Assert.Equal(1, counter.Count);

        await Task.Delay(200);
        using var response = await client.GetAsync($"http://127.0.0.1:{NeteasePort}/search");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, inner.Calls);
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task ForeignPortRefused_DoesNotResurrect()
    {
        var inner = new ScriptedHandler(ConnectionRefused());
        using var client = CreateClient(inner, _ => { }, out var counter);

        await AssertRefusedAsync(() => client.GetAsync("http://127.0.0.1:9999/weather"));

        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task PostRequests_AreNotReplayed()
    {
        var inner = new ScriptedHandler(ConnectionRefused(), Ok());
        using var client = CreateClient(inner, _ => { }, out var counter);

        await AssertRefusedAsync(() => client.PostAsync(
            $"http://127.0.0.1:{KugouPort}/verify/bridge/report", new StringContent("x")));

        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task HttpErrorStatus_PassesThroughWithoutResurrect()
    {
        var inner = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = CreateClient(inner, _ => { }, out var counter);

        using var response = await client.GetAsync($"http://127.0.0.1:{KugouPort}/search?keywords=t");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task ResurrectThrows_StillRetriesAndEntersCooldown()
    {
        var inner = new ScriptedHandler(ConnectionRefused(), ConnectionRefused(), ConnectionRefused());
        var counter = new ResurrectCounter();
        var handler = new LocalProxyResilienceHandler(
            new[] { NeteasePort, KugouPort },
            (_, _) =>
            {
                counter.Count++;
                throw new InvalidOperationException("spawn failed");
            },
            CancellationToken.None,
            inner,
            TimeSpan.FromMilliseconds(80));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        await AssertRefusedAsync(() => client.GetAsync($"http://127.0.0.1:{KugouPort}/search"));
        Assert.Equal(1, counter.Count);

        // 冷却内快速失败且不再触发已抛异常的拉起
        await AssertRefusedAsync(() => client.GetAsync($"http://127.0.0.1:{KugouPort}/search"));
        Assert.Equal(3, inner.Calls);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task AppLifetimeCancelled_DoesNotResurrect()
    {
        var inner = new ScriptedHandler(ConnectionRefused());
        using var appCts = new CancellationTokenSource();
        await appCts.CancelAsync();
        var counter = new ResurrectCounter();
        var handler = new LocalProxyResilienceHandler(
            new[] { NeteasePort, KugouPort },
            (_, _) =>
            {
                counter.Count++;
                return Task.CompletedTask;
            },
            appCts.Token,
            inner);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        await AssertRefusedAsync(() => client.GetAsync($"http://127.0.0.1:{NeteasePort}/search"));

        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, counter.Count);
    }
}
