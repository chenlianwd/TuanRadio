using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
            Assert.Equal("/song/url/auth/merge", request.RequestUri.AbsolutePath);
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
        Assert.Equal(new[] { "/register/dev", "/song/url/auth/merge", "/song/url" },
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
    public async Task SearchAsync_HttpFailureIsSurfacedForCircuitBreaker()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"status\":1,\"data\":{\"info\":[{\"hash\":\"abc\",\"OriSongName\":\"歌\",\"SingerName\":\"手\"}]}}")
        }));
        using var client = new HttpClient(handler);
        var accounts = await CreateLoggedInStoreAsync();
        var service = new KugouMusicService(client, accounts);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("测试", 5));
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

    [Fact]
    public async Task GetPlayUrlAsync_PrefersAuthMergeAndFallsBackOnceToLegacy()
    {
        var (client, capture) = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/song/url/auth/merge" => "{\"status\":0,\"error\":\"auth unavailable\"}",
            "/song/url" => "{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/legacy.mp3\"}]}",
            _ => "{\"status\":1}"
        });
        var service = new KugouMusicService(client, await CreateLoggedInStoreAsync());

        var url = await service.GetPlayUrlAsync("kugou:abc");

        Assert.Equal("https://cdn.example/legacy.mp3", url);
        Assert.Equal(new[] { "/song/url/auth/merge", "/song/url" },
            capture.Requests.Select(request => request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task GetPlayUrlAsync_MultipleHashesStillUseLegacyOnlyOnce()
    {
        var (client, capture) = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/song/url/auth/merge" => "{\"status\":0,\"error\":\"auth unavailable\"}",
            "/song/url" => "{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/legacy.mp3\"}]}",
            _ => "{\"status\":1}"
        });
        var service = new KugouMusicService(client, await CreateLoggedInStoreAsync());
        var track = new OnlineTrack
        {
            Id = "kugou:PRIMARY",
            ProviderMetadata = new Dictionary<string, string>
            {
                ["hash_std"] = "STANDARD",
                ["hash_128"] = "H128"
            }
        };

        var url = await service.GetPlayUrlAsync(track, CancellationToken.None);

        Assert.Equal("https://cdn.example/legacy.mp3", url);
        Assert.Equal(3, capture.Requests.Count(request =>
            request.RequestUri!.AbsolutePath == "/song/url/auth/merge"));
        var legacy = Assert.Single(capture.Requests.Where(request =>
            request.RequestUri!.AbsolutePath == "/song/url"));
        Assert.Contains("hash=PRIMARY", legacy.RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QrConfirmation_RefreshesCompleteSessionBeforeReturningCookie()
    {
        var (client, capture) = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/login/qr/check" => "{\"data\":{\"status\":4,\"token\":\"SECRET\",\"userid\":42}}",
            "/login/token" => "{\"status\":1,\"data\":{\"token\":\"SECRET\",\"userid\":42,\"t1\":\"T1\",\"vip_type\":2,\"vip_token\":\"VIP\"}}",
            "/register/dev" => "{\"status\":1,\"data\":{\"dfid\":\"DF\"}}",
            "/user/verify" => "{\"status\":1,\"data\":{\"auth\":\"AUTH\"}}",
            _ => "{\"status\":0}"
        });
        var account = new KugouAccountService(client, "http://localhost");

        var result = await account.CheckQrAsync("KEY");

        Assert.Equal(QrState.Confirmed, result.State);
        Assert.Equal(
            "token=SECRET;userid=42;t1=T1;vip_type=2;vip_token=VIP;dfid=DF;auth=AUTH",
            result.Cookie);
        Assert.Equal(
            new[] { "/login/qr/check", "/login/token", "/register/dev", "/user/verify" },
            capture.Requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.All(capture.Requests.Skip(1), request =>
            Assert.DoesNotContain("SECRET", request.RequestUri!.Query));
    }

    [Fact]
    public async Task GetNicknameAsync_SendsAuthorizationHeader()
    {
        var (client, capture) = CreateClient(_ =>
            "{\"status\":1,\"data\":{\"nickname\":\"测试用户\"}}");
        var account = new KugouAccountService(client, "http://localhost");

        var nickname = await account.GetNicknameAsync("token=SECRET;userid=42;dfid=DF");

        Assert.Equal("测试用户", nickname);
        Assert.Equal("token=SECRET;userid=42;dfid=DF",
            capture.Last!.Headers.GetValues("Authorization").Single());
        Assert.DoesNotContain("SECRET", capture.Last.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task AccountStore_PersistsStableDeviceAndDropsDfidFromUnknownOldDevice()
    {
        var values = new Dictionary<string, string>
        {
            [MusicAccountStore.KugouCredentialService] = "token=SECRET;userid=42;dfid=OLD"
        };
        var storage = new Mock<ISecureStorage>();
        storage.Setup(x => x.GetApiKeyAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => values.TryGetValue(key, out var value) ? value : null);
        storage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((key, value) => values[key] = value)
            .Returns(Task.CompletedTask);

        var first = new MusicAccountStore(storage.Object);
        await first.LoadAsync();
        var firstEnvironment = first.GetKugouProxyEnvironment();

        Assert.Equal("token=SECRET;userid=42", first.KugouCookie);
        Assert.Matches("^[0-9a-f]{32}$", firstEnvironment["KUGOU_API_GUID"]);

        var second = new MusicAccountStore(storage.Object);
        await second.LoadAsync();

        var secondEnvironment = second.GetKugouProxyEnvironment();
        Assert.All(firstEnvironment, item =>
            Assert.Equal(item.Value, secondEnvironment[item.Key]));
        Assert.Equal("token=SECRET;userid=42", second.KugouCookie);
    }

    [Fact]
    public async Task AccountStore_KeepsDfidOnlyWhenOwnerMatchesStableDevice()
    {
        var identity = KugouDeviceIdentity.Create();
        var values = new Dictionary<string, string>
        {
            [MusicAccountStore.KugouCredentialService] = "token=SECRET;userid=42;dfid=DF",
            [MusicAccountStore.KugouDeviceIdentityService] = identity.Serialize(),
            [MusicAccountStore.KugouDfidOwnerService] = identity.DeviceGuid
        };
        var storage = new Mock<ISecureStorage>();
        storage.Setup(x => x.GetApiKeyAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => values.TryGetValue(key, out var value) ? value : null);
        storage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((key, value) => values[key] = value)
            .Returns(Task.CompletedTask);
        var store = new MusicAccountStore(storage.Object);

        await store.LoadAsync();

        Assert.Equal("token=SECRET;userid=42;dfid=DF", store.KugouCookie);
    }

    [Fact]
    public void DeviceIdentity_MatchesOnlyExpectedProxyHandshake()
    {
        var identity = KugouDeviceIdentity.Create();
        var response = JsonSerializer.Serialize(new
        {
            status = 1,
            service = "tuanradio-kugou-proxy",
            protocol = 1,
            device_hash = identity.ComputeProxyIdentityHash()
        });
        var anotherIdentity = KugouDeviceIdentity.Create();

        Assert.True(identity.MatchesProxyIdentityResponse(response));
        Assert.False(anotherIdentity.MatchesProxyIdentityResponse(response));
        Assert.False(identity.MatchesProxyIdentityResponse("{\"status\":1}"));
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
    [InlineData("vip_token=secret;t1=refresh;auth=grant", "vip_token=<redacted>;t1=<redacted>;auth=<redacted>")]
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
