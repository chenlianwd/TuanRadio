using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class MultiSourceMusicServiceTests
{
    [Fact]
    public async Task SearchAsync_CancellationStopsPrimaryRequest()
    {
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new MultiSourceMusicService(client);
        using var cancellation = new CancellationTokenSource();

        var searchTask = service.SearchAsync("测试", 5, cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searchTask);
    }

    [Fact]
    public async Task GetPlayUrlAsync_UsesMetadataFallbackWhenPreferredSourceHasNoUrl()
    {
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.Contains("/song/url", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"code\":200,\"data\":[{\"url\":null}]}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            });
        }));
        var service = new MultiSourceMusicService(client, new FallbackMusicService());
        var track = new OnlineTrack
        {
            Id = "netease:123",
            Title = "测试歌曲",
            Artist = "测试歌手"
        };

        var url = await service.GetPlayUrlAsync(
            track,
            CancellationToken.None);

        Assert.Equal("https://fallback.invalid/test.mp3", url);
        Assert.Equal("fallback:456", track.Id);
    }

    [Fact]
    public async Task GetPlayUrlAsync_DoesNotWaitForLowerPriorityHangingFallback()
    {
        var lowerPriority = new CancellationTrackingMusicService();
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            })));
        var service = new MultiSourceMusicService(
            client,
            new FallbackMusicService(),
            lowerPriority);
        var track = new OnlineTrack
        {
            Id = "unknown:1",
            Title = "测试歌曲",
            Artist = "测试歌手"
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var url = await service.GetPlayUrlAsync(track, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal("https://fallback.invalid/test.mp3", url);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Lower-priority hanging source delayed a playable candidate (elapsed {stopwatch.Elapsed})");
        Assert.Equal(1, lowerPriority.SearchCount);
        await lowerPriority.Canceled.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetPlayUrlAsync_TreatsNeteaseTrialStreamAsUnavailable()
    {
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.Contains("/song/url", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"code\":200,\"data\":[{\"url\":\"https://trial.invalid/30s.mp3\",\"freeTrialInfo\":{\"start\":0,\"end\":30},\"freeTrialPrivilege\":{\"listenType\":5,\"cannotListenReason\":1}}]}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            });
        }));
        var service = new MultiSourceMusicService(client, new FallbackMusicService());
        var track = new OnlineTrack
        {
            Id = "netease:123",
            Title = "测试歌曲",
            Artist = "测试歌手"
        };

        var url = await service.GetPlayUrlAsync(track, CancellationToken.None);

        Assert.Equal("https://fallback.invalid/test.mp3", url);
        Assert.Equal("fallback:456", track.Id);
    }

    [Fact]
    public async Task GetPlayUrlAsync_KeepsNeteaseFullStream()
    {
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            var body = url.Contains("/song/url", StringComparison.Ordinal)
                ? "{\"code\":200,\"data\":[{\"url\":\"https://preferred.invalid/full.mp3\",\"freeTrialInfo\":null,\"freeTrialPrivilege\":{\"listenType\":0,\"cannotListenReason\":0}}]}"
                : "{\"code\":0}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }));
        var service = new MultiSourceMusicService(client, new FallbackMusicService());
        var track = new OnlineTrack
        {
            Id = "netease:123",
            Title = "测试歌曲",
            Artist = "测试歌手"
        };

        var url = await service.GetPlayUrlAsync(track, CancellationToken.None);

        Assert.Equal("https://preferred.invalid/full.mp3", url);
        Assert.Equal("netease:123", track.Id);
    }

    [Fact]
    public async Task GetAlternativePlayUrlAsync_SkipsCurrentSource()
    {
        var preferredPlayRequests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.Contains("/song/url", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref preferredPlayRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"code\":200,\"data\":[{\"url\":\"https://preferred.invalid/test.mp3\"}]}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            });
        }));
        var service = new MultiSourceMusicService(client, new FallbackMusicService());
        var track = new OnlineTrack
        {
            Id = "netease:123",
            Title = "测试歌曲",
            Artist = "测试歌手"
        };

        var url = await service.GetAlternativePlayUrlAsync(track, CancellationToken.None);

        Assert.Equal("https://fallback.invalid/test.mp3", url);
        Assert.Equal("fallback:456", track.Id);
        Assert.Equal(0, preferredPlayRequests);
    }

    [Fact]
    public async Task GetPlayUrlAsync_CallerCancellationStopsLegacySourceThatIgnoresToken()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            })));
        var service = new MultiSourceMusicService(
            client,
            new IgnoringCancellationMusicService());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetPlayUrlAsync("ignoringcancellation:123", cancellation.Token));
    }

    [Fact]
    public async Task SearchAsync_AnnotatesPrimaryReportWhenTopResultsAreTrialOnly()
    {
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.Contains("/song/url", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"code\":200,\"data\":[{\"url\":\"https://trial.invalid/30s.mp3\",\"freeTrialInfo\":{\"start\":0,\"end\":30},\"freeTrialPrivilege\":{\"listenType\":5,\"cannotListenReason\":1}}]}")
                });
            }

            if (url.Contains("/search?", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"code\":200,\"result\":{\"songs\":[{\"id\":123,\"name\":\"测试歌曲\",\"artists\":[{\"name\":\"测试歌手\"}],\"album\":{\"name\":\"测试专辑\"},\"duration\":100000}]}}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            });
        }));
        var service = new MultiSourceMusicService(client);

        await service.SearchAsync("测试", 5, CancellationToken.None);

        var primary = service.LastSearchReport.Single(s => s.Name == "网易云音乐");
        Assert.Equal("ok", primary.Status);
        Assert.Equal(1, primary.Count);
        Assert.Equal("试听或失效片段，已过滤", primary.Note);
    }

    [Fact]
    public async Task GetAlternativePlayUrlAsync_BoundedByOverallDeadlineWhenSourcesHang()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            })));
        // 内置快速源立即空结果；3 个挂起源各吃满逐源预算。
        // 无整体 deadline 时串行累计 15s+；有 8s deadline 时第二个挂起源只能吃剩余预算
        var service = new MultiSourceMusicService(
            client,
            new HangingMusicService(),
            new HangingMusicService(),
            new HangingMusicService());
        var track = new OnlineTrack
        {
            Id = "hanging:1",
            Title = "测试歌曲",
            Artist = "测试歌手"
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var url = await service.GetAlternativePlayUrlAsync(track, CancellationToken.None);
        stopwatch.Stop();

        Assert.Null(url);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Overall deadline did not bound hanging sources (elapsed {stopwatch.Elapsed})");
    }

    [Fact]
    public async Task SearchAsync_SlowSourceUsesIndependentBudgetAndStillHonorsCallerCancellation()
    {
        // 快速源立即空结果，慢源挂起：慢源使用独立预算，但用户取消必须立刻向上传递。
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            })));
        var slowSource = new HangingSlowMusicService();
        var service = new MultiSourceMusicService(client, slowSource);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchAsync("测试", 5, cancellation.Token));
        stopwatch.Stop();

        Assert.True(slowSource.Started);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"slow source ignored caller cancellation (elapsed {stopwatch.Elapsed})");
    }

    [Fact]
    public async Task GetAlternativePlayUrlAsync_DoesNotStartSlowSource()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            })));
        var slowSource = new CountingSlowMusicService();
        var service = new MultiSourceMusicService(client, slowSource);
        var track = new OnlineTrack { Id = "unknown:1", Title = "歌", Artist = "手" };

        var url = await service.GetAlternativePlayUrlAsync(track, CancellationToken.None);

        Assert.Null(url);
        Assert.Equal(0, slowSource.SearchCount);
    }

    [Fact]
    public async Task SearchAsync_AutomaticIntentDoesNotStartSlowSource()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            })));
        var slowSource = new CountingSlowMusicService();
        var service = new MultiSourceMusicService(client, slowSource);

        var results = await service.SearchAsync(
            "测试",
            5,
            MusicSearchIntent.Automatic,
            CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, slowSource.SearchCount);
    }

    [Fact]
    public async Task SearchAsync_BuiltInHttpFailuresOpenCircuitAfterThreshold()
    {
        var neteaseRequests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri?.Port == 37250)
                Interlocked.Increment(ref neteaseRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("service unavailable")
            });
        }));
        var service = new MultiSourceMusicService(client);

        for (var attempt = 0; attempt <= SourceHealthRegistry.FailureThreshold; attempt++)
        {
            var results = await service.SearchAsync(
                "测试",
                5,
                MusicSearchIntent.Automatic,
                CancellationToken.None);
            Assert.Empty(results);
        }

        Assert.Equal(SourceHealthRegistry.FailureThreshold, neteaseRequests);
        var primary = service.LastSearchReport.Single(status => status.Name == "网易云音乐");
        Assert.Equal("disabled", primary.Status);
    }

    [Fact]
    public async Task GetPlayUrlAsync_BuiltInHttpFailuresOpenCircuitAfterThreshold()
    {
        var neteaseRequests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri?.Port == 37250)
                Interlocked.Increment(ref neteaseRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("service unavailable")
            });
        }));
        var service = new MultiSourceMusicService(client);

        for (var attempt = 0; attempt <= SourceHealthRegistry.FailureThreshold; attempt++)
            Assert.Null(await service.GetPlayUrlAsync("netease:123", CancellationToken.None));

        Assert.Equal(SourceHealthRegistry.FailureThreshold, neteaseRequests);
    }

    [Fact]
    public async Task SearchAsync_BusinessFailureBreaksTransportFailureSequence()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            })));
        var source = new InterleavedFailureMusicService();
        var service = new MultiSourceMusicService(client, source);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var results = await service.SearchAsync(
                "测试",
                5,
                MusicSearchIntent.Automatic,
                CancellationToken.None);
            Assert.Empty(results);
        }

        // 前两次传输失败后出现业务响应，计数应归零；后续两次失败尚不足以熔断第 5 次请求。
        Assert.Equal(5, source.SearchCount);
    }

    [Fact]
    public async Task PlaybackFallbackSearch_RespectsCircuitAndCredentialUpdateResetsIt()
    {
        var storage = new Mock<ISecureStorage>();
        storage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var accounts = new MusicAccountStore(storage.Object);
        await accounts.SetKugouCookieAsync("token=SECRET;userid=42;dfid=DF;t1=T1;vip_type=0;auth=AUTH");
        var kugouRequests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri?.Port == 37251)
            {
                Interlocked.Increment(ref kugouRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("service unavailable")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":200,\"result\":{\"songs\":[]}}")
            });
        }));
        var service = new MultiSourceMusicService(client, accounts);
        var track = new OnlineTrack { Id = "unknown:1", Title = "歌", Artist = "手" };

        for (var attempt = 0; attempt < SourceHealthRegistry.FailureThreshold; attempt++)
            Assert.Null(await service.GetAlternativePlayUrlAsync(track, CancellationToken.None));

        Assert.Null(await service.GetAlternativePlayUrlAsync(track, CancellationToken.None));
        Assert.Equal(SourceHealthRegistry.FailureThreshold, kugouRequests);

        // 即使用户重新提交的是同一账号值，也代表明确的重新登录/恢复意图。
        await accounts.SetKugouCookieAsync(accounts.KugouCookie);
        Assert.Null(await service.GetAlternativePlayUrlAsync(track, CancellationToken.None));
        Assert.Equal(SourceHealthRegistry.FailureThreshold + 1, kugouRequests);
    }

    private sealed class FallbackMusicService : IMusicSearchService
    {
        public string Name => "备用音源";

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => Task.FromResult(new List<OnlineTrack>
            {
                new()
                {
                    Id = "fallback:456",
                    Title = "测试歌曲",
                    Artist = "测试歌手",
                    Source = Name
                }
            });

        public Task<string?> GetPlayUrlAsync(string trackId)
            => Task.FromResult<string?>("https://fallback.invalid/test.mp3");
    }

    private sealed class IgnoringCancellationMusicService : IMusicSearchService
    {
        private readonly TaskCompletionSource<string?> _neverCompletes = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "忽略取消的旧音源";

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => Task.FromResult(new List<OnlineTrack>());

        public Task<string?> GetPlayUrlAsync(string trackId)
            => _neverCompletes.Task;
    }

    /// <summary>搜索请求永不返回且忽略取消令牌：模拟最坏情况的故障源。</summary>
    private sealed class HangingMusicService : IMusicSearchService
    {
        private readonly TaskCompletionSource<List<OnlineTrack>> _neverCompletes = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "挂起音源";

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => _neverCompletes.Task;

        public Task<string?> GetPlayUrlAsync(string trackId)
            => Task.FromResult<string?>(null);
    }

    /// <summary>用于验证高优先级源命中后，低优先级搜索会收到取消且包装任务被收尾。</summary>
    private sealed class CancellationTrackingMusicService : IMusicSearchService
    {
        private readonly TaskCompletionSource<bool> _canceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _searchCount;

        public string Name => "可取消挂起音源";
        public Task Canceled => _canceled.Task;
        public int SearchCount => Volatile.Read(ref _searchCount);

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => SearchAsync(keyword, limit, CancellationToken.None);

        public async Task<List<OnlineTrack>> SearchAsync(
            string keyword,
            int limit,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _searchCount);
            using var registration = cancellationToken.Register(
                () => _canceled.TrySetResult(true));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new List<OnlineTrack>();
        }

        public Task<string?> GetPlayUrlAsync(string trackId)
            => Task.FromResult<string?>(null);
    }

    private sealed class HangingSlowMusicService : IMusicSearchService
    {
        public string Name => "挂起慢源";
        public bool IsSlowSource => true;
        public bool Started { get; private set; }

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => SearchAsync(keyword, limit, CancellationToken.None);

        public async Task<List<OnlineTrack>> SearchAsync(
            string keyword,
            int limit,
            CancellationToken cancellationToken)
        {
            Started = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new List<OnlineTrack>();
        }

        public Task<string?> GetPlayUrlAsync(string trackId)
            => Task.FromResult<string?>(null);
    }

    private sealed class CountingSlowMusicService : IMusicSearchService
    {
        public string Name => "计数慢源";
        public bool IsSlowSource => true;
        public int SearchCount { get; private set; }

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
        {
            SearchCount++;
            return Task.FromResult(new List<OnlineTrack>());
        }

        public Task<string?> GetPlayUrlAsync(string trackId)
            => Task.FromResult<string?>(null);
    }

    private sealed class InterleavedFailureMusicService : IMusicSearchService
    {
        private int _searchCount;

        public string Name => "交错故障音源";
        public int SearchCount => Volatile.Read(ref _searchCount);

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => SearchAsync(keyword, limit, CancellationToken.None);

        public Task<List<OnlineTrack>> SearchAsync(
            string keyword,
            int limit,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _searchCount);
            if (attempt == 3)
                throw new MusicSourceBusinessException("simulated rights failure");
            throw new HttpRequestException("simulated transport failure");
        }

        public Task<string?> GetPlayUrlAsync(string trackId)
            => Task.FromResult<string?>(null);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
