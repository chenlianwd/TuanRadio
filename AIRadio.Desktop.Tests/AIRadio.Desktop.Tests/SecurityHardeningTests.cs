using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.Services.WebRemote;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public sealed class SecurityHardeningTests
{
    [Fact]
    public void YouTubeBuildSearchArguments_IsolatesInjectionPayloadInSingleArgument()
    {
        var payload = "hello\" --exec calc.exe --proxy http://evil.local\r\n--cookies-from-browser chrome";
        var args = YouTubeMusicService.BuildSearchArguments(payload, 20, null);

        Assert.Equal(4, args.Count);
        Assert.Equal("ytsearch20:hello\" --exec calc.exe --proxy http://evil.local--cookies-from-browser chrome", args[0]);
        Assert.Equal("--dump-json", args[1]);
        Assert.Equal("--no-download", args[2]);
        Assert.Equal("--no-warnings", args[3]);
        Assert.DoesNotContain("--exec", args);
        Assert.DoesNotContain("--proxy", args);
    }

    [Fact]
    public void YouTubeBuildPlayUrlArguments_SeparatesCookieBrowserAndUrl()
    {
        var args = YouTubeMusicService.BuildPlayUrlArguments("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "edge");

        Assert.Equal(new[]
        {
            "-f", "ba", "--get-url", "--no-warnings", "--ignore-errors",
            "--cookies-from-browser", "edge",
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
        }, args);
    }

    [Fact]
    public async Task RadioStreamProbe_BlocksRedirectToLoopback()
    {
        var requestCount = 0;
        var handler = new StubHttpHandler((req, _) =>
        {
            requestCount++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://127.0.0.1:37250/secret");
            return Task.FromResult(redirect);
        });

        using var client = new HttpClient(handler);
        using var svc = new RadioStreamProbeService(client);
        var result = await svc.ProbeAsync("https://1.1.1.1/stream.mp3");

        Assert.False(result.Success);
        Assert.Equal(1, requestCount);
    }

    [Theory]
    [InlineData(null, "192.168.1.5:37260", true)]
    [InlineData("http://192.168.1.5:37260", "192.168.1.5:37260", true)]
    [InlineData("http://127.0.0.1:37260", "127.0.0.1:37260", true)]
    [InlineData("https://evil.example", "192.168.1.5:37260", false)]
    [InlineData("http://192.168.1.5:9999", "192.168.1.5:37260", false)]
    [InlineData("null", "192.168.1.5:37260", false)]
    [InlineData("http://192.168.1.5:37260", "", false)]
    public void WebRemoteOriginCheck_EnforcesSameHostAndPort(string? origin, string host, bool expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (origin != null)
            headers["Origin"] = origin;
        if (!string.IsNullOrEmpty(host))
            headers["Host"] = host;

        Assert.Equal(expected, WebRemoteService.IsAllowedWebSocketOrigin(headers));
    }

    [Fact]
    public async Task WebRemoteWsEndpoint_RejectsCrossOriginWith403()
    {
        using var server = new WebRemoteService();
        const string token = "OriginTestToken_1234";
        server.ConfigureHandlers(
            () => new WebRemoteSnapshot(),
            _ => Task.FromResult(WebRemoteCommandResult.Success()));
        Assert.True(await server.StartAsync(0, token, IPAddress.Loopback));

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/ws?token={token}");
        req.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
        req.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        req.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        req.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");

        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public void SanitizeUrlForLog_StripsQueryFragmentAndUserInfo()
    {
        var raw = "https://user:pass@feeds.example.com:8443/private/feed.xml?token=secret123&uid=42#top";
        var sanitized = SensitiveDataSanitizer.SanitizeUrlForLog(raw);

        Assert.Equal("https://feeds.example.com:8443/private/feed.xml", sanitized);
        Assert.DoesNotContain("secret123", sanitized);
        Assert.DoesNotContain("pass", sanitized);
    }

    [Fact]
    public async Task PlaylistFileParser_RejectsOversizedFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"tuanradio-oversized-{Guid.NewGuid():N}.m3u");
        try
        {
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(PlaylistFileParser.MaxFileSizeBytes + 1024);
            }

            var parser = new PlaylistFileParser();
            var result = await parser.ParseFileAsync(tempFile);

            Assert.Empty(result.Stations);
            Assert.True(result.WasTruncated);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SettingsViewModel_MigratesLegacyWebRemoteTokenToSecureStorage_AndStripsFromJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        try
        {
            await File.WriteAllTextAsync(settingsPath, """
                {
                  "web_remote_enabled": true,
                  "web_remote_port": 37260,
                  "web_remote_token": "LegacyPlaintextToken_8899"
                }
                """);

            var store = new Dictionary<string, string>(StringComparer.Ordinal);
            var secureMock = new Mock<ISecureStorage>();
            secureMock.Setup(s => s.GetApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync((string key) => store.TryGetValue(key, out var v) ? v : null);
            secureMock.Setup(s => s.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string key, string val) =>
                {
                    store[key] = val;
                    return Task.CompletedTask;
                });

            var llmMock = new Mock<ILLMService>();

            using var vm = new SettingsViewModel(llmMock.Object, secureMock.Object, settingsPath);
            await vm.LoadAsync();

            Assert.Equal("LegacyPlaintextToken_8899", vm.WebRemoteToken);
            Assert.True(store.TryGetValue("web-remote-token-v1", out var migrated));
            Assert.Equal("LegacyPlaintextToken_8899", migrated);

            await vm.SaveCommand.Execute().FirstAsync();

            var savedJson = await File.ReadAllTextAsync(settingsPath);
            Assert.DoesNotContain("web_remote_token", savedJson);
            Assert.DoesNotContain("LegacyPlaintextToken_8899", savedJson);
            using var doc = JsonDocument.Parse(savedJson);
            Assert.False(doc.RootElement.TryGetProperty("web_remote_token", out _));

            // 即使 WebRemote 关闭，主动刷新令牌后经 SaveUiStateCommand 也必须写入安全存储
            vm.WebRemoteEnabled = false;
            vm.WebRemoteToken = "RotatedWhileDisabled_7788";
            await vm.SaveUiStateCommand.Execute().FirstAsync();
            Assert.Equal("RotatedWhileDisabled_7788", store["web-remote-token-v1"]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
