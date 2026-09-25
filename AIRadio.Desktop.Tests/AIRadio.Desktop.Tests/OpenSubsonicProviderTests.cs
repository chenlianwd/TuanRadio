using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;

namespace AIRadio.Desktop.Tests;

public sealed class OpenSubsonicProviderTests
{
    [Fact]
    public async Task ConnectSearchAndResolve_UseConfiguredOriginAndSecureStorage()
    {
        var storage = new MemoryStorage();
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            var body = request.RequestUri!.AbsolutePath.EndsWith("/ping.view", StringComparison.Ordinal)
                ? """{"subsonic-response":{"status":"ok"}}"""
                : """{"subsonic-response":{"status":"ok","searchResult3":{"song":[{"id":"song-1","title":"Night Drive","artist":"A","album":"B","duration":185,"suffix":"flac","transcodedSuffix":"mp3","transcodedBitRate":192}]}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var provider = new OpenSubsonicProvider(storage, http);
        await provider.ConnectAsync("http://127.0.0.1:4533/navidrome", "alice", "secret");
        Assert.True(provider.IsConfigured);
        Assert.Contains("secret", storage.Value);
        Assert.DoesNotContain("secret", requests[0].OriginalString);

        var song = Assert.Single(await provider.SearchAsync("Night", 5, CancellationToken.None));
        Assert.Equal("opensubsonic:song-1", song.Id);
        Assert.Equal(185_000, song.DurationMs);
        var media = (await provider.ResolveAsync(new ProviderTrackRef("opensubsonic", "song-1"),
            song.ProviderMetadata, CancellationToken.None)).Media;
        Assert.NotNull(media);
        Assert.True(provider.IsAllowedMediaUri(media.Uri));
        Assert.Equal("/navidrome/rest/stream.view", media.Uri.AbsolutePath);
        Assert.Equal("mp3", media.Codec);
        Assert.Equal(192, media.BitrateKbps);
        Assert.DoesNotContain("secret", media.Uri.OriginalString);
        Assert.False(provider.IsAllowedMediaUri(new Uri("http://127.0.0.1:4534/navidrome/rest/stream.view")));
        Assert.False(provider.IsAllowedMediaUri(new Uri("http://127.0.0.1:4533/other/rest/stream.view")));

        var restored = new OpenSubsonicProvider(storage, http);
        await restored.LoadAsync();
        Assert.Equal(provider.ServerUrl, restored.ServerUrl);
        Assert.Equal("alice", restored.Username);
        provider.Disconnect();
        Assert.False(provider.IsConfigured);
        Assert.Null(storage.Value);
    }

    [Fact]
    public async Task FailedPing_DoesNotReplaceSavedConfiguration()
    {
        var storage = new MemoryStorage();
        using var goodHttp = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"subsonic-response":{"status":"ok"}}""") }));
        var provider = new OpenSubsonicProvider(storage, goodHttp);
        await provider.ConnectAsync("https://music.example.com", "alice", "secret");
        var saved = storage.Value;
        using var badHttp = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("http://127.0.0.1/admin") } }));
        var candidate = new OpenSubsonicProvider(storage, badHttp);
        await candidate.LoadAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            candidate.ConnectAsync("http://127.0.0.1:4533", "bob", "other"));
        Assert.Equal(saved, storage.Value);
        Assert.Equal("alice", candidate.Username);
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
