using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;

namespace AIRadio.Desktop.Tests;

public sealed class MusicProviderPreferencesTests
{
    [Fact]
    public async Task ReorderingAndDisabling_ChangesSearchAndPrefixedResolution()
    {
        var first = new StubProvider("first");
        var second = new StubProvider("second");
        var broker = new MusicSourceBroker(first, second);

        broker.ConfigureProviders(["second", "first"], []);
        var preferred = await broker.SearchAsync("song", 5, MusicSearchIntent.Automatic, CancellationToken.None);
        Assert.Equal("second:1", Assert.Single(preferred).Id);
        Assert.Equal(0, first.SearchCalls);
        var cachedTrack = preferred[0];
        Assert.Equal("https://203.0.113.2/stream.mp3",
            await broker.GetPlayUrlAsync(cachedTrack, CancellationToken.None));
        var secondResolves = second.ResolveCalls;

        broker.ConfigureProviders(["second", "first"], ["second"]);
        var fallback = await broker.SearchAsync("song", 5, MusicSearchIntent.Automatic, CancellationToken.None);
        Assert.Equal("first:1", Assert.Single(fallback).Id);
        Assert.Null(await broker.GetPlayUrlAsync("second:1", CancellationToken.None));
        Assert.Equal("https://203.0.113.1/stream.mp3",
            await broker.GetPlayUrlAsync(cachedTrack, CancellationToken.None));
        Assert.Equal(secondResolves, second.ResolveCalls);
    }

    [Fact]
    public void SupplementalProvider_FollowsEstablishedOnlineProviders()
    {
        using var http = new System.Net.Http.HttpClient();
        var broker = new MusicSourceBroker(http, null, null,
            new IMusicProvider[] { new StubProvider("private") },
            new IMusicProvider[] { new StubProvider("open") });
        var ids = broker.GetProviderDescriptors().Select(item => item.Id).ToArray();
        Assert.Equal("private", ids[0]);
        Assert.True(Array.IndexOf(ids, "open") > Array.IndexOf(ids, "kugou"));
    }

    [Fact]
    public async Task SlowProviderAtTop_DoesNotEnterAutomaticSearch()
    {
        var slow = new StubProvider("slow", isSlow: true);
        var broker = new MusicSourceBroker(slow);
        broker.ConfigureProviders(["slow"], []);

        Assert.Empty(await broker.SearchAsync("song", 5, MusicSearchIntent.Automatic, CancellationToken.None));
        Assert.Equal(0, slow.SearchCalls);
        Assert.Equal("slow:1", Assert.Single(await broker.SearchAsync(
            "song", 5, MusicSearchIntent.Explicit, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task DeferredSlowSearch_OnlyStartsWhenExplicitlyRequested()
    {
        var slow = new StubProvider("slow", isSlow: true);
        var broker = new MusicSourceBroker(slow);

        var fast = await broker.SearchFastWithReportAsync("song", 5, CancellationToken.None);
        Assert.Empty(fast.Tracks);
        Assert.Equal(0, slow.SearchCalls);

        var deferred = await broker.SearchSlowWithReportAsync("song", 5, CancellationToken.None);
        Assert.Equal("slow:1", Assert.Single(deferred.Tracks).Id);
        Assert.Equal(1, slow.SearchCalls);

        broker.ConfigureProviders(["slow"], ["slow"]);
        Assert.Empty((await broker.SearchSlowWithReportAsync("song", 5, CancellationToken.None)).Tracks);
        Assert.Equal(1, slow.SearchCalls);
    }

    [Fact]
    public async Task RejectedMediaUri_DoesNotCountAsSuccessfulResolution()
    {
        var provider = new StubProvider("blocked", mediaUrl: "http://127.0.0.2/stream.mp3");
        var broker = new MusicSourceBroker(provider);

        Assert.Null(await broker.GetPlayUrlAsync("blocked:1", CancellationToken.None));
        var health = Assert.Single(broker.GetHealthSnapshots());
        Assert.Null(health.LastResolutionSuccessUtc);
        Assert.Equal(0, health.RecentSuccessCount);
    }

    [Fact]
    public async Task DeclaredMediaInfo_FlowsThroughResolveResultAndCache()
    {
        var provider = new StubProvider("decl", codec: "FLAC", bitrateKbps: 900, isPreview: true);
        var broker = new MusicSourceBroker(provider);
        var track = new OnlineTrack { Id = "decl:1", Title = "Song", Artist = "Artist", Source = "decl" };

        var result = await broker.ResolveTrackAsync(track, forceRefresh: true, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result.FellBack);
        Assert.Equal("FLAC", result.Codec);
        Assert.Equal(900, result.BitrateKbps);
        Assert.True(result.IsPreview);

        // 缓存读路径（GetPlayUrlAsync 之外的曲目级入口）同样携带声明值
        var cached = await broker.ResolveTrackAsync(track, forceRefresh: false, CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal("FLAC", cached.Codec);
        Assert.Equal(900, cached.BitrateKbps);
        Assert.True(cached.IsPreview);
    }

    [Fact]
    public async Task RedirectProbe_RejectsPrivateHopWithoutTransportFailureCount()
    {
        var provider = new StubProvider("redir", mediaUrl: "https://203.0.113.7/stream.mp3");
        var broker = new MusicSourceBroker(provider)
        {
            // 初始 URL 公网放行，但探测发现第一跳 302 指向私网
            RedirectGuard = new PlaybackRedirectGuard(new HttpClient(new RedirectToPrivateHandler()))
        };

        Assert.Null(await broker.GetPlayUrlAsync("redir:1", CancellationToken.None));
        var health = Assert.Single(broker.GetHealthSnapshots());
        // 策略事件不记熔断：无传输故障累计、无成功记录
        Assert.Equal(0, health.ConsecutiveTransportFailures);
        Assert.Null(health.LastResolutionSuccessUtc);
        Assert.Equal(0, health.RecentSuccessCount);
    }

    private sealed class RedirectToPrivateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("http://192.168.0.9/stream.mp3") }
            });
    }

    private sealed class StubProvider(string id, bool isSlow = false, string? mediaUrl = null,
        string? codec = null, int? bitrateKbps = null, bool isPreview = false) : IMusicProvider
    {
        public MusicProviderDescriptor Descriptor { get; } = new(id, id,
            ProviderNetworkScope.PublicInternet, IsSlowSource: isSlow);
        public int SearchCalls { get; private set; }
        public int ResolveCalls { get; private set; }

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
        {
            SearchCalls++;
            return Task.FromResult(new List<OnlineTrack>
            {
                new() { Id = id + ":1", Title = "Song", Artist = "Artist", Source = id }
            });
        }

        public Task<MediaResolutionResult> ResolveAsync(ProviderTrackRef track,
            IReadOnlyDictionary<string, string>? providerMetadata, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(MediaResolutionResult.Playable(
                new ResolvedMedia(track, new Uri(mediaUrl ?? ("https://203.0.113." +
                    (id == "second" ? "2" : "1") + "/stream.mp3")),
                    Codec: codec, BitrateKbps: bitrateKbps, IsPreview: isPreview)));
        }
    }
}
