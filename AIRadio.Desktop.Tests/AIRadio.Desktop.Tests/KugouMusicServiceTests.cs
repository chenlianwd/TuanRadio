using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 酷狗本地代理交互的安全边界：登录 Cookie 必须走 Authorization 头，
/// 不得进入 URL 查询串（会随 URL 泄露到日志、缓存键与诊断输出）。
/// </summary>
public class KugouMusicServiceTests
{
    private static (HttpClient client, RequestCapture capture) CreateClient(Func<HttpRequestMessage, string> respond)
    {
        var capture = new RequestCapture();
        var handler = new DelegateHandler((request, _) =>
        {
            capture.Record(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request))
            });
        });
        return (new HttpClient(handler), capture);
    }

    private static async Task<MusicAccountStore> CreateLoggedInStoreAsync()
    {
        var storage = new Mock<ISecureStorage>();
        var store = new MusicAccountStore(storage.Object);
        await store.SetKugouCookieAsync("token=SECRET;userid=42;dfid=DF");
        return store;
    }

    [Fact]
    public async Task SearchAsync_SendsCookieInAuthorizationHeaderNotQuery()
    {
        var (client, capture) = CreateClient(_ =>
            "{\"status\":1,\"data\":{\"info\":[{\"hash\":\"abc\",\"OriSongName\":\"歌\",\"SingerName\":\"手\",\"Duration\":100}]}}");
        var accounts = await CreateLoggedInStoreAsync();
        var service = new KugouMusicService(client, accounts);

        var results = await service.SearchAsync("测试", 5);

        Assert.Single(results);
        var url = capture.Last!.RequestUri!.AbsoluteUri;
        Assert.DoesNotContain("SECRET", url);
        Assert.DoesNotContain("cookie=", url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("token=SECRET;userid=42;dfid=DF", capture.Last.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task GetPlayUrlAsync_SendsCookieInAuthorizationHeaderNotQuery()
    {
        var (client, capture) = CreateClient(request =>
            request.RequestUri!.AbsoluteUri.Contains("/song/url", StringComparison.Ordinal)
                ? "{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/a.mp3\"}]}"
                : "{\"status\":1,\"data\":{\"info\":[]}}");
        var accounts = await CreateLoggedInStoreAsync();
        var service = new KugouMusicService(client, accounts);

        var playUrl = await service.GetPlayUrlAsync("kugou:abc");

        Assert.Equal("https://cdn.example/a.mp3", playUrl);
        var url = capture.Last!.RequestUri!.AbsoluteUri;
        Assert.DoesNotContain("SECRET", url);
        Assert.DoesNotContain("cookie=", url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("token=SECRET;userid=42;dfid=DF", capture.Last.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task GetPlayUrlAsync_UsesPlaylistMetadataAndRetriesStableHash()
    {
        var (client, capture) = CreateClient(request =>
        {
            var query = request.RequestUri!.Query;
            return query.Contains("hash=STD", StringComparison.OrdinalIgnoreCase)
                ? "{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/stable.mp3\"}]}"
                : "{\"status\":1,\"data\":[]}";
        });
        var accounts = await CreateLoggedInStoreAsync();
        var service = new KugouMusicService(client, accounts);
        var track = new OnlineTrack
        {
            Id = "kugou:OLD",
            Title = "歌曲",
            Artist = "歌手",
            ProviderMetadata = new Dictionary<string, string>
            {
                ["album_id"] = "12",
                ["album_audio_id"] = "34",
                ["hash_std"] = "STD"
            }
        };

        var playUrl = await service.GetPlayUrlAsync(track, CancellationToken.None);

        Assert.Equal("https://cdn.example/stable.mp3", playUrl);
        Assert.Equal(2, capture.Requests.Count);
        Assert.All(capture.Requests, request =>
        {
            var query = request.RequestUri!.Query;
            Assert.Contains("album_id=12", query);
            Assert.Contains("album_audio_id=34", query);
            Assert.DoesNotContain("SECRET", query);
        });
        Assert.Contains("hash=OLD", capture.Requests[0].RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hash=STD", capture.Requests[1].RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPlayUrlAsync_RepairsLegacyCookieByRegisteringDfid()
    {
        var storage = new Mock<ISecureStorage>();
        storage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var accounts = new MusicAccountStore(storage.Object);
        await accounts.SetKugouCookieAsync("token=SECRET;userid=42");

        var (client, capture) = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/register/dev" => "{\"status\":1,\"data\":{\"dfid\":\"NEW_DFID\"}}",
            "/song/url" => "{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/legacy.mp3\"}]}",
            _ => "{\"status\":1}"
        });
        var service = new KugouMusicService(client, accounts);

        var playUrl = await service.GetPlayUrlAsync("kugou:abc");

        Assert.Equal("https://cdn.example/legacy.mp3", playUrl);
        Assert.Equal(new[] { "/register/dev", "/song/url" },
            capture.Requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.Equal("token=SECRET;userid=42",
            capture.Requests[0].Headers.GetValues("Authorization").Single());
        Assert.Equal("token=SECRET;userid=42;dfid=NEW_DFID",
            capture.Requests[1].Headers.GetValues("Authorization").Single());
        storage.Verify(x => x.SaveApiKeyAsync(
            MusicAccountStore.KugouCredentialService,
            "token=SECRET;userid=42;dfid=NEW_DFID"), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_NotLoggedIn_ThrowsBusinessException()
    {
        var (client, _) = CreateClient(_ => "{\"status\":1}");
        var service = new KugouMusicService(client, accounts: null);

        await Assert.ThrowsAsync<MusicSourceBusinessException>(() => service.SearchAsync("测试", 5));
    }

    [Fact]
    public async Task SearchAsync_HttpFailureDoesNotAcceptSuccessShapedBody()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"status\":1,\"data\":{\"info\":[{\"hash\":\"abc\",\"OriSongName\":\"歌\",\"SingerName\":\"手\"}]}}")
        }));
        using var client = new HttpClient(handler);
        var accounts = await CreateLoggedInStoreAsync();
        var service = new KugouMusicService(client, accounts);

        var results = await service.SearchAsync("测试", 5);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetPlayUrlAsync_DfidEnrichmentDoesNotOverwriteConcurrentRelogin()
    {
        var storage = new Mock<ISecureStorage>();
        storage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var accounts = new MusicAccountStore(storage.Object);
        await accounts.SetKugouCookieAsync("token=SECRET;userid=42");

        var handler = new DelegateHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/register/dev")
            {
                // 模拟补齐网络请求期间用户重新扫码登录，store 被写入新登录态
                await accounts.SetKugouCookieAsync("token=NEWLOGIN;userid=99;dfid=DF2");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1,\"data\":{\"dfid\":\"NEW_DFID\"}}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/a.mp3\"}]}")
            };
        });
        using var client = new HttpClient(handler);
        var service = new KugouMusicService(client, accounts);

        var playUrl = await service.GetPlayUrlAsync("kugou:abc");

        Assert.Equal("https://cdn.example/a.mp3", playUrl);
        // 旧 token+dfid 不得覆盖并发写入的新登录态
        Assert.Equal("token=NEWLOGIN;userid=99;dfid=DF2", accounts.KugouCookie);
        storage.Verify(x => x.SaveApiKeyAsync(
            MusicAccountStore.KugouCredentialService,
            "token=SECRET;userid=42;dfid=NEW_DFID"), Times.Never);
    }

    private sealed class RequestCapture
    {
        private readonly List<HttpRequestMessage> _requests = new();

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;
        public HttpRequestMessage? Last => _requests.LastOrDefault();

        public void Record(HttpRequestMessage request)
        {
            lock (_requests)
                _requests.Add(request);
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}

public class SensitiveDataSanitizerTests
{
    [Theory]
    [InlineData(
        "https://a.example/x?token=abc123&userid=42",
        "https://a.example/x?token=<redacted>&userid=<redacted>")]
    [InlineData(
        "token=abc; userid=42; dfid=zzz",
        "token=<redacted>; userid=<redacted>; dfid=<redacted>")]
    [InlineData(
        "https://a.example/x?signature=deadbeef&cookie=tok",
        "https://a.example/x?signature=<redacted>&cookie=<redacted>")]
    [InlineData("https://a.example/x?sign=1", "https://a.example/x?sign=<redacted>")]
    [InlineData("https://a.example/x?author=jane", "https://a.example/x?author=jane")]
    public void Sanitize_MasksSensitiveQueryAndCookieValues(string input, string expected)
    {
        Assert.Equal(expected, AIRadio.Desktop.Services.SensitiveDataSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_LeavesPlainMessagesAndNullUntouched()
    {
        Assert.Null(SensitiveDataSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, SensitiveDataSanitizer.Sanitize(string.Empty));
        Assert.Equal("连接被拒绝", SensitiveDataSanitizer.Sanitize("连接被拒绝"));
    }
}
