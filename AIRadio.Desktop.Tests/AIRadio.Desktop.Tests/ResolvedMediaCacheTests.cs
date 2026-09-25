using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ResolvedMediaCacheTests
{
    [Fact]
    public void DefaultTtl_EntriesExpireAfterTenMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new ResolvedMediaCache(() => now);
        var key = new ProviderTrackRef("netease", "1");
        var result = MakeResult("https://203.0.113.9/a.mp3");

        cache.Set(key, result, expiresAt: null);
        Assert.True(cache.TryGet(key, out var hit));
        Assert.Same(result, hit);

        now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void ExplicitExpiry_MinusSixtySeconds()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new ResolvedMediaCache(() => now);
        var key = new ProviderTrackRef("netease", "2");

        cache.Set(key, MakeResult("https://203.0.113.9/a.mp3"), expiresAt: now + TimeSpan.FromSeconds(120));

        now += TimeSpan.FromSeconds(59);
        Assert.True(cache.TryGet(key, out _));

        now += TimeSpan.FromSeconds(2);
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void NearExpiry_DoesNotEnterCache()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new ResolvedMediaCache(() => now);
        var key = new ProviderTrackRef("netease", "3");

        // 过期时间距现在不足 60s：ExpiresAt−60 已是过去，不进缓存
        cache.Set(key, MakeResult("https://203.0.113.9/a.mp3"), expiresAt: now + TimeSpan.FromSeconds(30));
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void ClearProvider_MatchesCaseInsensitively_AndOnlyThatProvider()
    {
        var cache = new ResolvedMediaCache();
        var neteaseKey = new ProviderTrackRef("netease", "1");
        var kugouKey = new ProviderTrackRef("Kugou", "2");

        cache.Set(neteaseKey, MakeResult("https://203.0.113.9/a.mp3"), null);
        cache.Set(kugouKey, MakeResult("https://203.0.113.9/b.mp3"), null);

        cache.ClearProvider("NETEASE");

        Assert.False(cache.TryGet(neteaseKey, out _));
        Assert.True(cache.TryGet(kugouKey, out _));
    }

    [Fact]
    public void Evict_RemovesSingleEntry()
    {
        var cache = new ResolvedMediaCache();
        var key = new ProviderTrackRef("netease", "1");
        cache.Set(key, MakeResult("https://203.0.113.9/a.mp3"), null);

        cache.Evict(key);

        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void Capacity_SweepsExpiredThenEvictsSoonestExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new ResolvedMediaCache(() => now);

        // 257 条同 TTL：超过 256 上限时最先插入的条目（剩余寿命并列最短，稳定排序逐出）应被清出
        for (var i = 0; i < 257; i++)
            cache.Set(new ProviderTrackRef("netease", $"cap-{i}"), MakeResult("https://203.0.113.9/a.mp3"), null);

        Assert.False(cache.TryGet(new ProviderTrackRef("netease", "cap-0"), out _));
        Assert.True(cache.TryGet(new ProviderTrackRef("netease", "cap-1"), out _));
        Assert.True(cache.TryGet(new ProviderTrackRef("netease", "cap-256"), out _));

        // 已过期条目在超限清扫时优先整体让位，不动存活条目
        now += TimeSpan.FromMinutes(11);
        cache.Set(new ProviderTrackRef("netease", "fresh"), MakeResult("https://203.0.113.9/b.mp3"), null);
        Assert.True(cache.TryGet(new ProviderTrackRef("netease", "fresh"), out _));
    }

    [Fact]
    public void Capacity_SameKeyOverwrite_DoesNotSweepOthers()
    {
        var cache = new ResolvedMediaCache();

        // 同 key 反复覆盖是计数不变路径（ContainsKey 短路），不得触发清扫逐出其它条目
        var stable = new ProviderTrackRef("netease", "stable");
        cache.Set(stable, MakeResult("https://203.0.113.9/a.mp3"), null);
        for (var i = 0; i < 300; i++)
            cache.Set(new ProviderTrackRef("kugou", "hot"), MakeResult("https://203.0.113.9/h.mp3"), null);

        Assert.True(cache.TryGet(stable, out _));
        Assert.True(cache.TryGet(new ProviderTrackRef("kugou", "hot"), out _));
    }

    private static ResolveTrackResult MakeResult(string url)
        => new(url, new ProviderTrackRef("netease", "1"), "网易", new Dictionary<string, string>(0));
}

public class MusicSourceBrokerCacheTests
{
    [Fact]
    public async Task GetPlayUrlAsync_SecondCallHitsCache_UnderlyingResolvedOnce()
    {
        // 缓存读路径是 GetPlayUrlAsync(OnlineTrack)：点歌/推荐/歌单的重复解析在此命中
        var provider = new CountingProvider(_ => "https://203.0.113.9/a.mp3");
        var broker = new MusicSourceBroker(provider);
        var track = new OnlineTrack { Id = "Fake:1", Title = "T", Artist = "A" };

        var first = await broker.GetPlayUrlAsync(track, CancellationToken.None);
        var second = await broker.GetPlayUrlAsync(track, CancellationToken.None);

        Assert.Equal("https://203.0.113.9/a.mp3", first);
        Assert.Equal(first, second);
        Assert.Equal(1, provider.Resolves);
    }

    [Fact]
    public async Task ResolveTrackAsync_ForceRefresh_BypassesCacheAndReplacesEntry()
    {
        var provider = new CountingProvider(attempt => attempt == 1 ? "https://203.0.113.9/a.mp3" : "https://203.0.113.9/b.mp3");
        var broker = new MusicSourceBroker(provider);
        var track = new OnlineTrack { Id = "Fake:1", Title = "T", Artist = "A" };

        await broker.GetPlayUrlAsync(track, CancellationToken.None);
        var refreshed = await broker.ResolveTrackAsync(track, forceRefresh: true, CancellationToken.None);
        var third = await broker.GetPlayUrlAsync(track, CancellationToken.None);

        Assert.Equal(2, provider.Resolves);
        Assert.Equal("https://203.0.113.9/b.mp3", refreshed!.Url);
        Assert.Equal("https://203.0.113.9/b.mp3", third);
    }

    [Fact]
    public async Task ResolveTrackAsync_ForceRefreshFailure_DoesNotRestoreStaleEntry()
    {
        // 播放失败 → 恢复链路 forceRefresh：旧缓存必须被逐出；
        // 恢复解析失败返回 null 后，下一次普通解析拿到的是新结果而非陈旧旧值
        var provider = new CountingProvider(attempt => attempt switch
        {
            1 => "https://203.0.113.9/stale.mp3",
            2 => null,
            _ => "https://203.0.113.9/fresh.mp3"
        });
        var broker = new MusicSourceBroker(provider);
        var track = new OnlineTrack { Id = "Fake:1", Title = "T", Artist = "A" };

        await broker.GetPlayUrlAsync(track, CancellationToken.None);
        var failedRecovery = await broker.ResolveTrackAsync(track, forceRefresh: true, CancellationToken.None);
        var next = await broker.GetPlayUrlAsync(track, CancellationToken.None);

        Assert.Null(failedRecovery);
        Assert.Equal(3, provider.Resolves);
        Assert.Equal("https://203.0.113.9/fresh.mp3", next);
    }

    [Fact]
    public async Task MusicAccountStore_NeteaseCookieChange_FiresEvent()
    {
        var store = new MusicAccountStore(new Mock<ISecureStorage>().Object);
        var fired = 0;
        store.NeteaseCookieChanged += (_, _) => fired++;

        await store.SetNeteaseCookieAsync(null);
        await store.SetNeteaseCookieAsync("MUSIC_U=sess");

        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task ResolveTrackAsync_PrefixedId_OnlyRequestsMatchingProvider()
    {
        // A3 回归锚：Descriptor.Id 前缀路由失效会退化为逐源 Try-all（错源请求+回退排除失效），
        // 既有 mock 测试对该失效不敏感（对任意请求都应答），必须显式锚定
        var preferred = new CountingProvider(_ => "https://203.0.113.9/a.mp3")
        {
            DescriptorOverride = new MusicProviderDescriptor("Fake", "假源A")
        };
        var other = new CountingProvider(_ => "https://203.0.113.9/b.mp3")
        {
            DescriptorOverride = new MusicProviderDescriptor("Other", "假源B")
        };
        var broker = new MusicSourceBroker(preferred, other);
        var track = new OnlineTrack { Id = "Fake:1", Title = "T", Artist = "A" };

        var result = await broker.ResolveTrackAsync(track, forceRefresh: false, CancellationToken.None);

        Assert.Equal("https://203.0.113.9/a.mp3", result!.Url);
        Assert.Equal(1, preferred.Resolves);
        Assert.Equal(0, other.Resolves);
    }

    [Fact]
    public async Task ResolveTrackAsync_FallbackResult_IsNotCached()
    {
        // 回退结果不入缓存：缓存命中不重放身份回写，若入缓存，同一输入身份换实例重解析
        // 会直接拿到旧回退 URL 而实例身份仍指 A 源——"Id 指向 A 源/URL 是 B 源直链"错位
        var sourceA = new ScriptedProvider("SrcA", "源A", _ => null);
        var sourceB = new ScriptedProvider("SrcB", "源B", id => id == "9" ? "https://203.0.113.9/fb.mp3" : null)
        {
            SearchResults = new List<OnlineTrack>
            {
                new() { Id = "SrcB:9", Title = "Song", Artist = "X", DurationMs = 200_000 }
            }
        };
        var broker = new MusicSourceBroker(sourceA, sourceB);

        var first = new OnlineTrack { Id = "SrcA:1", Title = "Song", Artist = "X", DurationMs = 200_000 };
        var r1 = await broker.ResolveTrackAsync(first, forceRefresh: false, CancellationToken.None);
        Assert.Equal("https://203.0.113.9/fb.mp3", r1!.Url);
        Assert.True(r1.FellBack);
        Assert.Equal("SrcB:9", first.Id);
        Assert.Equal(1, sourceA.Resolves);
        Assert.Equal(1, sourceB.Resolves);

        var second = new OnlineTrack { Id = "SrcA:1", Title = "Song", Artist = "X", DurationMs = 200_000 };
        var r2 = await broker.ResolveTrackAsync(second, forceRefresh: false, CancellationToken.None);
        // 未入缓存：同输入身份必须重新解析（两个源的计数都 +1），且身份回写对每个实例各自生效
        Assert.Equal(2, sourceA.Resolves);
        Assert.Equal(2, sourceB.Resolves);
        Assert.Equal("https://203.0.113.9/fb.mp3", r2!.Url);
        Assert.Equal("SrcB:9", second.Id);
    }

    /// <summary>按调用次数返回 URL 的假 Provider（attempt 从 1 起；null 模拟无可播地址）。</summary>
    private sealed class CountingProvider : IMusicProvider
    {
        private readonly Func<int, string?> _urlForAttempt;

        public int Resolves { get; private set; }

        public MusicProviderDescriptor Descriptor =>
            DescriptorOverride ?? new MusicProviderDescriptor("Fake", "假源");

        public MusicProviderDescriptor? DescriptorOverride { get; init; }

        public CountingProvider(Func<int, string?> urlForAttempt)
            => _urlForAttempt = urlForAttempt;

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
            => Task.FromResult(new List<OnlineTrack>());

        public Task<MediaResolutionResult> ResolveAsync(
            ProviderTrackRef track,
            IReadOnlyDictionary<string, string>? providerMetadata,
            CancellationToken cancellationToken)
        {
            var attempt = ++Resolves;
            var url = _urlForAttempt(attempt);
            return Task.FromResult(
                url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? MediaResolutionResult.Playable(new ResolvedMedia(track, uri))
                    : MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound));
        }
    }

    /// <summary>可配置搜索结果与解析映射的假 Provider。</summary>
    private sealed class ScriptedProvider : IMusicProvider
    {
        private readonly Func<string, string?> _resolveByUrl;

        public int Resolves { get; private set; }

        public List<OnlineTrack> SearchResults { get; init; } = new();

        public MusicProviderDescriptor Descriptor { get; }

        public ScriptedProvider(string id, string display, Func<string, string?> resolveByUrl)
        {
            Descriptor = new MusicProviderDescriptor(id, display);
            _resolveByUrl = resolveByUrl;
        }

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
            => Task.FromResult(SearchResults);

        public Task<MediaResolutionResult> ResolveAsync(
            ProviderTrackRef track,
            IReadOnlyDictionary<string, string>? providerMetadata,
            CancellationToken cancellationToken)
        {
            Resolves++;
            var url = _resolveByUrl(track.TrackId);
            return Task.FromResult(
                url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? MediaResolutionResult.Playable(new ResolvedMedia(track, uri))
                    : MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound));
        }
    }
}
