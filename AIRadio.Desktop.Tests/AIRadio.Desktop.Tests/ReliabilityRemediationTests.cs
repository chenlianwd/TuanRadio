using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ReliabilityRemediationTests
{
    [Fact]
    public async Task ResolveSlowAlternativeTrackAsync_OnlyQueriesSlowProviders_AndFastAlternativeLeavesSlowUntouched()
    {
        var fastProvider = new FakeSlowAwareProvider("netease", "网易云音乐", isSlow: false, _ => null);
        var slowSearchCount = 0;
        var slowProvider = new FakeSlowAwareProvider("youtube", "YouTube", isSlow: true, query =>
        {
            slowSearchCount++;
            return new OnlineTrack
            {
                Id = "youtube:dQw4w9WgXcQ",
                Title = "晴天 (Official Music Video)",
                Artist = "周杰伦",
                DurationMs = 269000,
                Source = "YouTube"
            };
        }, resolvedUrl: "https://203.0.113.42/yt.webm");

        var broker = new MusicSourceBroker(fastProvider, slowProvider)
        {
            RedirectGuard = new PlaybackRedirectGuard(new HttpClient(new StaticOkHandler()))
        };

        var target = new OnlineTrack
        {
            Id = "netease:1001",
            Title = "晴天",
            Artist = "周杰伦",
            DurationMs = 269000,
            Source = "网易云音乐"
        };

        // 快速回退契约不得改变：ResolveAlternativeTrackAsync 绝不触发慢源
        var fastResult = await broker.ResolveAlternativeTrackAsync(target, CancellationToken.None);
        Assert.Null(fastResult);
        Assert.Equal(0, slowSearchCount);

        // 独立慢源后台代理契约：使用慢源、剥离标题修饰、通过安全校验并返回 youtube 浏览器 UA
        var slowResult = await broker.ResolveSlowAlternativeTrackAsync(target, CancellationToken.None);
        Assert.NotNull(slowResult);
        Assert.Equal(1, slowSearchCount);
        Assert.True(slowResult!.FellBack);
        Assert.Equal("youtube:dQw4w9WgXcQ", slowResult.Track.ToSourceId());
        Assert.Equal("https://203.0.113.42/yt.webm", slowResult.Url);
        Assert.Equal(PlaybackHeaderProfiles.DesktopBrowserUserAgent, slowResult.Headers?.UserAgent);
    }

    [Fact]
    public async Task AudioService_SlowBackgroundRecovery_ResolvesSingleTrack_AndCancelsWhenRequestVersionChanges()
    {
        // 1) 单曲播放快源全失时，后台慢源代理可在请求代次一致下完成恢复并更新轨道
        using var svc1 = new AudioService();
        svc1.AddTracks(new List<Track>
        {
            new() { SourceId = "netease:1", Title = "晴天", Artist = "周杰伦", Duration = TimeSpan.FromSeconds(269) }
        });
        svc1.SetTrackUrlResolver((_, _) => Task.FromResult<TrackUrlResolution?>(null));
        var slowInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc1.SetSlowFallbackTrackUrlResolver(async (snapshot, ct) =>
        {
            slowInvoked.TrySetResult();
            await releaseSlow.Task;
            return new TrackUrlResolution(
                "https://203.0.113.55/slow-recovered.mp3",
                "youtube:abc12345678",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Codec: "OPUS",
                BitrateKbps: 128);
        });

        svc1.PlayAtIndex(0);
        await slowInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 超过 PlaybackCallbackReleaseDelay(100ms) 后再返回，验证后台慢源恢复不会被过早 skip/取消
        await Task.Delay(180);
        releaseSlow.SetResult();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && svc1.Playlist[0].SourceId != "youtube:abc12345678")
            await Task.Delay(20);

        Assert.Equal("youtube:abc12345678", svc1.Playlist[0].SourceId);
        Assert.Equal("https://203.0.113.55/slow-recovered.mp3", svc1.Playlist[0].FilePath);

        // 1b) 慢源也返回 null 时仍应触发连续失败记录与下一首调度（不静默停摆）
        using var svcFail = new AudioService();
        svcFail.AddTracks(new List<Track>
        {
            new() { SourceId = "netease:fail", Title = "Fail Song", Artist = "A" }
        });
        svcFail.SetTrackUrlResolver((_, _) => Task.FromResult<TrackUrlResolution?>(null));
        svcFail.SetSlowFallbackTrackUrlResolver((_, _) => Task.FromResult<TrackUrlResolution?>(null));
        var noticeTcs = new TaskCompletionSource<PlaybackRecoveryNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var noticeSub = svcFail.PlaybackRecoveryNotices
            .Where(n => n.ConsecutiveFailures > 0)
            .Subscribe(n => noticeTcs.TrySetResult(n));
        svcFail.PlayAtIndex(0);
        var notice = await noticeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(notice.ConsecutiveFailures >= 1);

        // 2) 请求代次隔离：切歌或 Stop 后，即使慢源忽略取消强行晚到返回，也绝不改写旧曲目或劫持新播放
        using var svc2 = new AudioService();
        var trackA = new Track { SourceId = "netease:a", Title = "Song A", Artist = "A" };
        var trackB = new Track { SourceId = "local:b", Title = "Song B", Artist = "B", FilePath = @"C:\nonexistent\b.mp3" };
        svc2.AddTracks(new List<Track> { trackA, trackB });
        svc2.SetTrackUrlResolver((_, _) => Task.FromResult<TrackUrlResolution?>(null));

        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLateResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc2.SetSlowFallbackTrackUrlResolver(async (_, ct) =>
        {
            slowStarted.TrySetResult();
            ct.Register(() => slowCancelled.TrySetResult());
            await releaseLateResult.Task;
            // 故意忽略 ct 返回迟到结果，验证 AudioService 请求代次防线
            return new TrackUrlResolution("https://203.0.113.99/late.mp3", "youtube:late0000001");
        });

        svc2.PlayAtIndex(0);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        svc2.Stop(); // 推进 _playRequestId 并取消 _slowRecoveryCts
        await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseLateResult.SetResult();
        await Task.Delay(120);

        Assert.Equal("netease:a", trackA.SourceId);
        Assert.Equal(string.Empty, trackA.FilePath);
    }

    [Fact]
    public async Task DiagnoseSources_RendersEntitlementDeviceRiskAndVersionWithoutLeakingSecrets()
    {
        var secretCookie = "MUSIC_U=super-secret-netease-cookie-12345";
        var kugouSecret = "token=kg-secret-tok-999;userid=888888;dfid=dfid-secret-777;vip_type=6;vip_token=vt-secret;auth=auth-secret";
        var storage = new MemoryStorage
        {
            [MusicAccountStore.NeteaseCredentialService] = secretCookie,
            [MusicAccountStore.KugouCredentialService] = kugouSecret,
        };
        var accounts = new MusicAccountStore(storage);
        await accounts.LoadAsync();

        var handler = new RoutingHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login/status"))
                return JsonResponse("""{"data":{"code":200,"profile":{"nickname":"小团子","vipType":11}}}""");
            if (path.EndsWith("/login/token"))
                return JsonResponse("""{"status":1,"data":{"token":"t","userid":"888","vip_type":6,"vip_token":"vt"}}""");
            if (path.EndsWith("/user/verify"))
                return JsonResponse("""{"status":1,"data":{"auth":"a"}}""");
            if (path.EndsWith("/user/detail"))
                return JsonResponse("""{"status":1,"data":{"nickname":"酷狗测试号"}}""");
            if (path.EndsWith("/user/vip/detail"))
                return JsonResponse("""{"status":1}""");
            if (path.EndsWith("/search"))
                return JsonResponse("""{"code":200,"result":{"songs":[]}}""");
            return JsonResponse("""{"status":1,"data":{"lists":[]}}""");
        });
        using var http = new HttpClient(handler);
        var verification = new KugouVerificationService(http);
        verification.RecordChallenge(new KugouChallenge("ssa-secret-event-id", "HASH123456"));

        var broker = new MusicSourceBroker(
            http,
            accounts,
            verification,
            Array.Empty<IMusicProvider>(),
            Array.Empty<IMusicProvider>(),
            new YouTubeMusicService(YtdlpManager.GetYtdlpPath(), accounts));

        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-diag-{Guid.NewGuid():N}.json");
        using var vm = new SettingsViewModel(
            new Mock<ILLMService>().Object,
            storage,
            settingsFile,
            accountStore: accounts,
            httpClient: http,
            kugouVerification: verification,
            musicSearch: broker);

        try
        {
            await vm.DiagnoseSourcesCommand.Execute();

            var text = vm.SourceDiagnosticsText;
            Assert.Contains("启用状态：已启用", text);
            Assert.Contains("黑胶 VIP", text);
            Assert.Contains("会员类型 6", text);
            Assert.Contains("20028", text);
            Assert.Contains("yt-dlp", text);
            Assert.Contains("NeteaseCloudMusicApi", text);
            Assert.Contains("KuGouMusicApi", text);

            // 安全门禁：诊断文本严禁泄漏任何凭据或风控事件 ID
            Assert.DoesNotContain("super-secret-netease-cookie", text);
            Assert.DoesNotContain("kg-secret-tok-999", text);
            Assert.DoesNotContain("dfid-secret-777", text);
            Assert.DoesNotContain("ssa-secret-event-id", text);

            vm.SelectedLanguage = "en";
            Assert.Contains("Enabled: yes", vm.SourceDiagnosticsText);
            Assert.Contains("Vinyl VIP", vm.SourceDiagnosticsText);
            Assert.Contains("VIP type 6", vm.SourceDiagnosticsText);
            Assert.Contains("20028", vm.SourceDiagnosticsText);

            // 全部显式停用后，诊断必须报告“已停用”而非回退为全启用
            foreach (var p in vm.MusicProviders)
                p.Enabled = false;
            await vm.DiagnoseSourcesCommand.Execute();
            Assert.Contains("Enabled: no", vm.SourceDiagnosticsText);
        }
        finally
        {
            AppLanguage.Apply("zh");
            try { File.Delete(settingsFile); } catch { }
        }
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StaticOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handle(request));
    }

    private sealed class MemoryStorage : Dictionary<string, string>, ISecureStorage
    {
        public Task SaveApiKeyAsync(string serviceName, string apiKey)
        {
            this[serviceName] = apiKey;
            return Task.CompletedTask;
        }

        public Task<string?> GetApiKeyAsync(string serviceName)
            => Task.FromResult(TryGetValue(serviceName, out var value) ? value : null);

        public void DeleteApiKey(string serviceName) => Remove(serviceName);
    }

    private sealed class FakeSlowAwareProvider(
        string id,
        string displayName,
        bool isSlow,
        Func<string, OnlineTrack?> onSearch,
        string? resolvedUrl = null) : IMusicProvider
    {
        public MusicProviderDescriptor Descriptor { get; } = new(
            id,
            displayName,
            IsExperimental: true,
            IsSlowSource: isSlow,
            SearchBudget: isSlow ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5),
            ResolveBudget: isSlow ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5));

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
        {
            var list = new List<OnlineTrack>();
            if (onSearch(keyword) is { } track)
                list.Add(track);
            return Task.FromResult(list);
        }

        public Task<MediaResolutionResult> ResolveAsync(
            ProviderTrackRef track,
            IReadOnlyDictionary<string, string>? providerMetadata,
            CancellationToken cancellationToken)
        {
            if (resolvedUrl == null)
                return Task.FromResult(MediaResolutionResult.Failed(Descriptor.Id, PlaybackFailureKind.NotFound));
            var headers = PlaybackHeaderProfiles.TryGet(Descriptor.Id, out var profile) ? profile : null;
            return Task.FromResult(MediaResolutionResult.Playable(
                new ResolvedMedia(track, new Uri(resolvedUrl), Headers: headers)));
        }
    }
}
