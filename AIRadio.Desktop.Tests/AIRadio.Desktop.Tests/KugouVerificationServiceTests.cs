using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 酷狗 20028 滑块验证：响应体形状分类、冷却/互斥状态机、探测三态与轮询结局语义。
/// </summary>
public class KugouVerificationServiceTests
{
    private static (HttpClient client, Func<int> probeCount) CreateChallengeProbeClient(
        params string[] probeBodies)
    {
        var index = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            var body = index < probeBodies.Length ? probeBodies[index] : probeBodies[^1];
            index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        });
        return (new HttpClient(handler), () => index);
    }

    // ========== ClassifyPlayUrlResponse：四种已观测形状 ==========

    [Fact]
    public void Classify_PlayableShape_ExtractsUrl()
    {
        var shape = Classify("{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/a.mp3\"}]}",
            out var eventId, out var playUrl);

        Assert.Equal(KugouVerificationService.KugouPlayUrlShape.Playable, shape);
        Assert.Null(eventId);
        Assert.Equal("https://cdn.example/a.mp3", playUrl);
    }

    [Fact]
    public void Classify_ChallengeWithSsaCode_ExtractsEventId()
    {
        var body = "{\"fail_process\":[\"pkg\"],\"errcode\":20028,\"status\":2," +
                   "\"error\":\"本次请求需要验证\",\"edt\":\"EDT\",\"sid\":\"SID\"," +
                   "\"ssaCode\":\"gz_tx_event_abc123\"}";

        var shape = Classify(body, out var eventId, out _);

        Assert.Equal(KugouVerificationService.KugouPlayUrlShape.Challenge, shape);
        Assert.Equal("gz_tx_event_abc123", eventId);
    }

    [Fact]
    public void Classify_ErrCode20028WithoutSsaCode_IsSuspectChallengeWithoutEventId()
    {
        var shape = Classify("{\"errcode\":20028,\"status\":2,\"error\":\"本次请求需要验证\"}",
            out var eventId, out var playUrl);

        Assert.Equal(KugouVerificationService.KugouPlayUrlShape.Challenge, shape);
        Assert.Null(eventId);
        Assert.Null(playUrl);
    }

    [Fact]
    public void Classify_BareStatusTwo_IsSuspectChallenge()
    {
        var shape = Classify("{\"status\":2}", out var eventId, out _);

        Assert.Equal(KugouVerificationService.KugouPlayUrlShape.Challenge, shape);
        Assert.Null(eventId);
    }

    [Theory]
    [InlineData("{\"status\":2,\"error\":\"版权限制\"}")]
    [InlineData("{\"status\":1}")]
    [InlineData("{\"status\":1,\"data\":[]}")]
    [InlineData("{\"status\":0,\"error\":\"upstream down\"}")]
    [InlineData("{\"errcode\":30005,\"status\":2,\"error\":\"其它错误\"}")]
    public void Classify_OtherFailures_AreUnavailable(string body)
    {
        var shape = Classify(body, out _, out _);

        Assert.Equal(KugouVerificationService.KugouPlayUrlShape.Unavailable, shape);
    }

    [Fact]
    public void Classify_MalformedMinimalBody_DoesNotThrow()
    {
        // 字段缺失/类型异常时必须安全返回，不允许把播放主流程炸掉
        var shape = Classify("{}", out var eventId, out var playUrl);

        Assert.Equal(KugouVerificationService.KugouPlayUrlShape.Unavailable, shape);
        Assert.Null(eventId);
        Assert.Null(playUrl);
    }

    private static KugouVerificationService.KugouPlayUrlShape Classify(
        string json, out string? eventId, out string? playUrl)
    {
        using var doc = JsonDocument.Parse(json);
        return KugouVerificationService.ClassifyPlayUrlResponse(doc.RootElement, out eventId, out playUrl);
    }

    // ========== 挑战记录与事件 ==========

    [Fact]
    public void RecordChallenge_UpdatesLastChallengeAndRaisesEvent()
    {
        var service = new KugouVerificationService(new HttpClient());
        KugouChallenge? received = null;
        service.ChallengeDetected += c => received = c;

        var challenge = new KugouChallenge("gz_tx_event_x", "HASH1");
        service.RecordChallenge(challenge);

        Assert.Equal(challenge, service.LastChallenge);
        Assert.Equal(challenge, received);
    }

    // ========== 冷却与互斥 ==========

    [Fact]
    public void AutoTrigger_FirstAllowed_SecondBlockedByCooldown_EvenAfterEnd()
    {
        var service = new KugouVerificationService(new HttpClient())
        {
            // 冷却调短，验证“冷却过期后恢复自动触发”
            AutoTriggerCooldown = TimeSpan.FromMilliseconds(50)
        };

        Assert.True(service.TryBeginAutoTrigger());
        Assert.False(service.TryBeginAutoTrigger()); // 互斥
        Assert.False(service.TryBeginManual());     // 手动同样互斥
        service.EndVerification();
        Assert.False(service.TryBeginAutoTrigger()); // 冷却仍在

        Thread.Sleep(80);
        Assert.True(service.TryBeginAutoTrigger());  // 冷却过期后恢复
        service.EndVerification();
    }

    [Fact]
    public void Manual_BlocksAutoUntilEnded()
    {
        var service = new KugouVerificationService(new HttpClient());

        Assert.True(service.TryBeginManual());
        Assert.False(service.TryBeginAutoTrigger());
        service.EndVerification();
        Assert.True(service.TryBeginAutoTrigger());
    }

    // ========== 探测三态 ==========

    [Fact]
    public async Task DetectChallenge_ChallengeBody_ReturnsEventIdAndProbeHash()
    {
        var (client, _) = CreateChallengeProbeClient(
            "{\"errcode\":20028,\"status\":2,\"ssaCode\":\"gz_tx_event_probe\"}");
        var service = new KugouVerificationService(client);

        var probe = await service.DetectChallengeAsync("token=T;userid=1;dfid=D", "HASH9", CancellationToken.None);

        Assert.Equal(KugouProbeStatus.Challenge, probe.Status);
        Assert.Equal("gz_tx_event_probe", probe.Challenge!.EventId);
        Assert.Equal("HASH9", probe.Challenge.Hash);
    }

    [Fact]
    public async Task DetectChallenge_PlayableBody_ReturnsPlayable()
    {
        var (client, _) = CreateChallengeProbeClient(
            "{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/ok.mp3\"}]}");
        var service = new KugouVerificationService(client);

        var probe = await service.DetectChallengeAsync("token=T;userid=1;dfid=D", "HASH9", CancellationToken.None);

        Assert.Equal(KugouProbeStatus.Playable, probe.Status);
        Assert.Null(probe.Challenge);
    }

    [Fact]
    public async Task DetectChallenge_NoPermissionBody_ReturnsNoPermission()
    {
        var (client, _) = CreateChallengeProbeClient("{\"status\":2,\"error\":\"版权限制\"}");
        var service = new KugouVerificationService(client);

        var probe = await service.DetectChallengeAsync("token=T;userid=1;dfid=D", "HASH9", CancellationToken.None);

        Assert.Equal(KugouProbeStatus.NoPermission, probe.Status);
    }

    // ========== 验证页 URL：登录态绝不进 URL ==========

    [Fact]
    public void VerifyPageUrl_ContainsOnlySessionId()
    {
        var url = KugouVerificationService.VerifyPageUrl("abc123");

        Assert.StartsWith("http://127.0.0.1:37251/verify_auto.html?session=", url);
        Assert.Contains("session=abc123", url);
        Assert.DoesNotContain("token", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userid", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dfid", url, StringComparison.OrdinalIgnoreCase);
    }

    // ========== 轮询结局语义 ==========

    [Fact]
    public async Task WaitUntilVerified_ChallengeThenPlayable_ReturnsVerified()
    {
        var (client, _) = CreateChallengeProbeClient(
            "{\"errcode\":20028,\"status\":2,\"ssaCode\":\"gz_tx_event_w1\"}",
            "{\"status\":1,\"data\":[{\"url\":\"https://cdn.example/ok.mp3\"}]}");
        var service = FastPollingService(client);

        var outcome = await service.WaitUntilVerifiedAsync("token=T;userid=1;dfid=D", "HASH", CancellationToken.None);

        Assert.Equal(KugouVerifyOutcome.Verified, outcome);
    }

    [Fact]
    public async Task WaitUntilVerified_ChallengeThenNoPermission_ReturnsVerifiedButUnavailable()
    {
        var (client, _) = CreateChallengeProbeClient(
            "{\"errcode\":20028,\"status\":2,\"ssaCode\":\"gz_tx_event_w2\"}",
            "{\"status\":2,\"error\":\"版权限制\"}");
        var service = FastPollingService(client);

        var outcome = await service.WaitUntilVerifiedAsync("token=T;userid=1;dfid=D", "HASH", CancellationToken.None);

        Assert.Equal(KugouVerifyOutcome.VerifiedButUnavailable, outcome);
    }

    [Fact]
    public async Task WaitUntilVerified_PersistentChallenge_TimesOut()
    {
        var (client, _) = CreateChallengeProbeClient(
            "{\"errcode\":20028,\"status\":2,\"ssaCode\":\"gz_tx_event_w3\"}");
        var service = FastPollingService(client);

        var outcome = await service.WaitUntilVerifiedAsync("token=T;userid=1;dfid=D", "HASH", CancellationToken.None);

        Assert.Equal(KugouVerifyOutcome.Timeout, outcome);
    }

    private static KugouVerificationService FastPollingService(HttpClient client)
        => new(client)
        {
            VerifyInitialDelay = TimeSpan.FromMilliseconds(5),
            VerifyPollInterval = TimeSpan.FromMilliseconds(5),
            VerifyWaitBudget = TimeSpan.FromMilliseconds(200)
        };

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

/// <summary>
/// KugouMusicService 与验证服务的联动：命中 20028 挑战时上报，正常失败不上报。
/// </summary>
public class KugouChallengeReportingTests
{
    [Fact]
    public async Task GetPlayUrlAsync_ChallengeBody_ReportsToVerificationService()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"errcode\":20028,\"status\":2,\"error\":\"本次请求需要验证\",\"edt\":\"E\",\"sid\":\"S\"," +
                "\"ssaCode\":\"gz_tx_event_report\"}")
        }));
        using var client = new HttpClient(handler);
        var accounts = await CreateLoggedInStoreAsync();
        var verification = new KugouVerificationService(client);
        KugouChallenge? reported = null;
        verification.ChallengeDetected += c => reported = c;
        var service = new KugouMusicService(client, accounts, verification);

        var url = await service.GetPlayUrlAsync("kugou:HASHR");

        Assert.Null(url); // 挑战响应无可播地址
        Assert.NotNull(reported);
        Assert.Equal("gz_tx_event_report", reported!.EventId);
        Assert.Equal("HASHR", reported.Hash);
    }

    [Fact]
    public async Task GetPlayUrlAsync_NonChallengeFailure_DoesNotReport()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":2,\"error\":\"版权限制\"}")
        }));
        using var client = new HttpClient(handler);
        var accounts = await CreateLoggedInStoreAsync();
        var verification = new KugouVerificationService(client);
        var reported = 0;
        verification.ChallengeDetected += _ => reported++;
        var service = new KugouMusicService(client, accounts, verification);

        await service.GetPlayUrlAsync("kugou:HASHN");

        Assert.Equal(0, reported);
    }

    [Fact]
    public async Task GetPlayUrlAsync_NoVerificationService_DoesNotThrow()
    {
        // 旧构造路径（测试/未接线）不传验证服务必须照常工作
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"errcode\":20028,\"status\":2,\"ssaCode\":\"gz_tx_event_noverify\"}")
        }));
        using var client = new HttpClient(handler);
        var accounts = await CreateLoggedInStoreAsync();
        var service = new KugouMusicService(client, accounts);

        var url = await service.GetPlayUrlAsync("kugou:HASHX");

        Assert.Null(url);
    }

    private static async Task<MusicAccountStore> CreateLoggedInStoreAsync()
    {
        var storage = new Moq.Mock<ISecureStorage>();
        var store = new MusicAccountStore(storage.Object);
        await store.SetKugouCookieAsync("token=SECRET;userid=42;dfid=DF");
        return store;
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
