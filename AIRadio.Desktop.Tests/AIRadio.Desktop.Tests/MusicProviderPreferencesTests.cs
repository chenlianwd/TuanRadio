using System;
using System.Collections.Generic;
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

    private sealed class StubProvider(string id, bool isSlow = false, string? mediaUrl = null) : IMusicProvider
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
                    (id == "second" ? "2" : "1") + "/stream.mp3")))));
        }
    }
}
