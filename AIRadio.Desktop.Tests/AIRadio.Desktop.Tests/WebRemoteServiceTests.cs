using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services.WebRemote;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class WebRemoteServiceTests
{
    [Fact]
    public async Task RootHtml_IsServedWithoutAuth_AndLeaksNoTokenOrState()
    {
        using var server = new WebRemoteService();
        const string secret = "SuperSecretToken_9988";
        var snapshot = new WebRemoteSnapshot
        {
            Title = "PrivateSecretSongTitle",
            Artist = "PrivateArtist",
            DjName = "Haru",
            IsPlaying = true
        };
        server.ConfigureHandlers(() => snapshot, _ => Task.FromResult(WebRemoteCommandResult.Success()));
        Assert.True(await server.StartAsync(0, secret, IPAddress.Loopback));

        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{server.Port}/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("no-store", resp.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(resp.Headers.TryGetValues("X-Frame-Options", out var xfo) && string.Join(",", xfo) == "DENY");

        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("TuanRadio", html);
        Assert.DoesNotContain(secret, html);
        Assert.DoesNotContain("PrivateSecretSongTitle", html);
    }

    [Fact]
    public async Task RestEndpoints_EnforceTokenAuth_AndDispatchCommands()
    {
        using var server = new WebRemoteService();
        const string token = "ValidToken_Abc12345";
        var received = new List<WebRemoteCommand>();
        var currentTitle = "Moonlight Serenade";

        server.ConfigureHandlers(
            () => new WebRemoteSnapshot { Title = currentTitle, IsPlaying = true, Volume = 0.65f },
            cmd =>
            {
                received.Add(cmd);
                if (cmd.Action == "next") currentTitle = "Blue Train";
                return Task.FromResult(WebRemoteCommandResult.Success("ok"));
            });

        Assert.True(await server.StartAsync(0, token, IPAddress.Loopback));
        var baseUrl = $"http://127.0.0.1:{server.Port}";
        using var http = new HttpClient();

        // 未带令牌 -> 401
        var unauthState = await http.GetAsync($"{baseUrl}/api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthState.StatusCode);

        var wrongCmd = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/command")
        {
            Content = new StringContent("""{"action":"next"}""", Encoding.UTF8, "application/json")
        };
        wrongCmd.Headers.Add("X-Remote-Token", "wrong-token");
        var unauthCmd = await http.SendAsync(wrongCmd);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthCmd.StatusCode);
        Assert.Empty(received);

        // Bearer Token 获取状态
        var reqState = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/state");
        reqState.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var stateResp = await http.SendAsync(reqState);
        Assert.Equal(HttpStatusCode.OK, stateResp.StatusCode);
        using (var doc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Moonlight Serenade", doc.RootElement.GetProperty("title").GetString());
            Assert.True(doc.RootElement.GetProperty("isPlaying").GetBoolean());
        }

        // X-Remote-Token 发送控制命令
        var reqCmd = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/command")
        {
            Content = new StringContent("""{"action":"next"}""", Encoding.UTF8, "application/json")
        };
        reqCmd.Headers.Add("X-Remote-Token", token);
        var cmdResp = await http.SendAsync(reqCmd);
        Assert.Equal(HttpStatusCode.OK, cmdResp.StatusCode);
        Assert.Single(received);
        Assert.Equal("next", received[0].Action);
        Assert.Equal("Blue Train", currentTitle);
    }

    [Fact]
    public async Task WebSocket_RejectsBadTokenWith4001_PushesState_AndHandlesTokenRotation()
    {
        using var server = new WebRemoteService();
        const string initialToken = "WsToken_Initial_01";
        var title = "Track One";
        var handled = new List<string>();

        server.ConfigureHandlers(
            () => new WebRemoteSnapshot { Title = title, HasTrack = true, IsPlaying = true },
            cmd =>
            {
                handled.Add(cmd.Action);
                return Task.FromResult(WebRemoteCommandResult.Success());
            });

        Assert.True(await server.StartAsync(0, initialToken, IPAddress.Loopback));

        // 1) 错误令牌触发关闭码 4001
        using (var badWs = new ClientWebSocket())
        {
            await badWs.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?token=invalid"), CancellationToken.None);
            var buf = new byte[1024];
            var res = await badWs.ReceiveAsync(buf, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Close, res.MessageType);
            Assert.Equal((WebSocketCloseStatus)4001, res.CloseStatus);
        }

        // 2) 合法令牌连接收到首帧状态、指令 Ack 与实时更新推送
        using var goodWs = new ClientWebSocket();
        await goodWs.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws?token={initialToken}"), CancellationToken.None);

        var initialMsg = await ReceiveTextAsync(goodWs);
        Assert.Equal(1, server.ConnectedClientCount);
        using (var doc = JsonDocument.Parse(initialMsg))
        {
            Assert.Equal("state", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("Track One", doc.RootElement.GetProperty("data").GetProperty("title").GetString());
        }

        // 经 WS 发命令应收到 Ack
        var cmdBytes = Encoding.UTF8.GetBytes("""{"action":"toggle_play"}""");
        await goodWs.SendAsync(cmdBytes, WebSocketMessageType.Text, true, CancellationToken.None);
        var ackMsg = await ReceiveTextAsync(goodWs);
        using (var doc = JsonDocument.Parse(ackMsg))
        {
            Assert.Equal("ack", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("toggle_play", doc.RootElement.GetProperty("action").GetString());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Single(handled, "toggle_play");

        // NotifyStateChanged(immediate: true) 推送最新状态
        title = "Track Two";
        server.NotifyStateChanged(immediate: true);
        var pushMsg = await ReceiveTextAsync(goodWs);
        using (var doc = JsonDocument.Parse(pushMsg))
        {
            Assert.Equal("state", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("Track Two", doc.RootElement.GetProperty("data").GetProperty("title").GetString());
        }

        // 3) 刷新令牌应断开旧连接并立即拒绝对旧令牌的 REST 访问
        const string rotatedToken = "WsToken_Rotated_02";
        await server.RotateTokenAsync(rotatedToken);
        var closeFrame = await goodWs.ReceiveAsync(new byte[256], CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, closeFrame.MessageType);
        Assert.Equal((WebSocketCloseStatus)4001, closeFrame.CloseStatus);

        using var http = new HttpClient();
        var oldReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/api/state");
        oldReq.Headers.Add("X-Remote-Token", initialToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(oldReq)).StatusCode);

        var newReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/api/state");
        newReq.Headers.Add("X-Remote-Token", rotatedToken);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(newReq)).StatusCode);
    }

    [Fact]
    public async Task StartAsync_FallsBackCleanly_WhenPreferredPortIsOccupied()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        try
        {
            using var server = new WebRemoteService();
            var started = await server.StartAsync(busyPort, "FallbackToken_01", IPAddress.Loopback);
            Assert.True(started);
            Assert.True(server.Port > busyPort && server.Port <= busyPort + 5);
            Assert.Contains($":{server.Port}/?token=FallbackToken_01", server.PrimaryUrl);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void LanAddressResolver_GeneratesValidTokens_AndClassifiesRfc1918()
    {
        var t1 = LanAddressResolver.GenerateAccessToken();
        var t2 = LanAddressResolver.GenerateAccessToken();
        Assert.NotEqual(t1, t2);
        Assert.True(t1.Length >= 20);
        Assert.DoesNotContain("+", t1);
        Assert.DoesNotContain("/", t1);
        Assert.DoesNotContain("=", t1);

        Assert.True(LanAddressResolver.IsRfc1918Private([192, 168, 1, 10]));
        Assert.True(LanAddressResolver.IsRfc1918Private([10, 0, 0, 1]));
        Assert.True(LanAddressResolver.IsRfc1918Private([172, 16, 0, 1]));
        Assert.True(LanAddressResolver.IsRfc1918Private([172, 31, 255, 254]));
        Assert.False(LanAddressResolver.IsRfc1918Private([172, 32, 0, 1]));
        Assert.False(LanAddressResolver.IsRfc1918Private([169, 254, 10, 2]));
        Assert.False(LanAddressResolver.IsRfc1918Private([8, 8, 8, 8]));
    }

    [Fact]
    public async Task MalformedQueryTokenReturns401_AndOversizedBodyReturns413()
    {
        using var server = new WebRemoteService();
        const string token = "ValidToken_EdgeCase";
        server.ConfigureHandlers(() => new WebRemoteSnapshot(), _ => Task.FromResult(WebRemoteCommandResult.Success()));
        Assert.True(await server.StartAsync(0, token, IPAddress.Loopback));

        var baseUrl = $"http://127.0.0.1:{server.Port}";
        using var http = new HttpClient();

        // 畸形 %ZZ 查询转义应稳定返回 401
        var malformedResp = await http.GetAsync($"{baseUrl}/api/state?token=%ZZ");
        Assert.Equal(HttpStatusCode.Unauthorized, malformedResp.StatusCode);

        // 超过 32KB 上限的请求体应直接返回 413
        var bigPayload = new string('x', 40 * 1024);
        var oversizedReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/command")
        {
            Content = new StringContent($$"""{"action":"chat","text":"{{bigPayload}}"}""", Encoding.UTF8, "application/json")
        };
        oversizedReq.Headers.Add("X-Remote-Token", token);
        var oversizedResp = await http.SendAsync(oversizedReq);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResp.StatusCode);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer, cts.Token);
            if (res.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"Unexpected close: {res.CloseStatus}");
            sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            if (res.EndOfMessage)
                return sb.ToString();
        }
    }
}
