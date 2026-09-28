using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;

namespace AIRadio.Desktop.Tests;

public sealed class AudiusProviderTests
{
    [Fact]
    public async Task SearchAndResolve_UseAnonymousReadOnlyEndpoints_AndExcludeGatedTracks()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            var body = request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? """
                  {"data":[
                    null,
                    {"id":"aB123","title":"Open song","duration":185,"is_streamable":true,"is_available":true,"is_stream_gated":false,"access":{"stream":true},"user":{"name":"Singer"},"album_backlink":{"playlist_name":"Album"}},
                    {"id":"gated","title":"Locked","is_streamable":true,"is_stream_gated":true,"access":{"stream":false}},
                    {"id":"offline","title":"Offline","is_streamable":false}
                  ]}
                  """
                : """{"data":{"id":"aB123","is_streamable":true,"is_available":true,"is_stream_gated":false,"access":{"stream":true}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var provider = new AudiusProvider(http);
        var track = Assert.Single(await provider.SearchAsync("open song", 10, CancellationToken.None));
        Assert.Equal("audius:aB123", track.Id);
        Assert.Equal("Singer", track.Artist);
        Assert.Equal("Album", track.Album);
        Assert.Equal(185000, track.DurationMs);
        var result = await provider.ResolveAsync(new ProviderTrackRef("audius", "aB123"), null,
            CancellationToken.None);
        Assert.Equal(PlaybackFailureKind.None, result.Failure);
        Assert.Equal("https://api.audius.co/v1/tracks/aB123/stream?app_name=TuanRadio",
            result.Media!.RawUrl);
        Assert.Equal(2, requests.Count);
        Assert.Equal("open song", System.Web.HttpUtility.ParseQueryString(requests[0].Query)["query"]);
    }

    [Fact]
    public async Task Resolve_RejectsNewlyGatedTrackAndInvalidId()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":{"id":"locked","is_streamable":true,"is_stream_gated":true,"access":{"stream":false}}}""")
        }));
        var provider = new AudiusProvider(http);
        var blocked = await provider.ResolveAsync(new ProviderTrackRef("audius", "locked"), null,
            CancellationToken.None);
        Assert.Equal(PlaybackFailureKind.AuthRequired, blocked.Failure);
        var invalid = await provider.ResolveAsync(new ProviderTrackRef("audius", "../admin"), null,
            CancellationToken.None);
        Assert.Equal(PlaybackFailureKind.NotFound, invalid.Failure);
    }

    [Fact]
    public async Task ApiKey_IsStoredAndUsedForReadOnlyRequests_ButNeverEmbeddedInPlaybackUrl()
    {
        var storage = new MemoryStorage();
        var requests = new List<Uri>();
        var keys = new List<string?>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            keys.Add(request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null);
            var body = request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? """{"data":[{"id":"aB123","title":"Open song","is_streamable":true}]}"""
                : """{"data":{"id":"aB123","is_streamable":true}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var provider = new AudiusProvider(http, storage);
        await provider.SaveApiKeyAsync("client-key");
        Assert.True(provider.HasApiKey);
        var song = Assert.Single(await provider.SearchAsync("Open", 1, CancellationToken.None));
        var resolution = await provider.ResolveAsync(new ProviderTrackRef("audius", "aB123"),
            null, CancellationToken.None);
        Assert.All(keys, key => Assert.Equal("client-key", key));
        Assert.All(requests, uri => Assert.DoesNotContain("client-key", uri.OriginalString));
        Assert.DoesNotContain("client-key", resolution.Media!.RawUrl);

        var restored = new AudiusProvider(http, storage);
        await restored.LoadAsync();
        Assert.True(restored.HasApiKey);
        restored.ClearApiKey();
        Assert.False(restored.HasApiKey);
        requests.Clear();
        keys.Clear();
        await restored.SearchAsync("Open", 1, CancellationToken.None);
        Assert.Equal("TuanRadio", System.Web.HttpUtility.ParseQueryString(requests[0].Query)["app_name"]);
        Assert.Null(keys[0]);
        Assert.Null(storage.Value);
    }

    private sealed class MemoryStorage : ISecureStorage
    {
        public string? Value { get; private set; }
        public Task SaveApiKeyAsync(string service, string apiKey)
        {
            Value = apiKey;
            return Task.CompletedTask;
        }
        public Task<string?> GetApiKeyAsync(string service) => Task.FromResult(Value);
        public void DeleteApiKey(string service) => Value = null;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
