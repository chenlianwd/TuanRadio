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

public class SoundCloudProviderTests
{
    [Fact]
    public async Task SearchAsync_WhenUnconfigured_ReturnsEmptyWithoutHttpCalls()
    {
        var calls = 0;
        var provider = new SoundCloudProvider(new HttpClient(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));

        Assert.False(provider.HasCredentials);
        Assert.False(provider.IsConfigured);

        var tracks = await provider.SearchAsync("ambient", 10, CancellationToken.None);

        Assert.Empty(tracks);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ResolveAsync_WhenUnconfigured_ReturnsAuthRequiredWithoutHttpCalls()
    {
        var calls = 0;
        var provider = new SoundCloudProvider(new HttpClient(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));

        var result = await provider.ResolveAsync(
            new ProviderTrackRef("soundcloud", "987654"),
            null,
            CancellationToken.None);

        Assert.Null(result.Media);
        Assert.Equal(PlaybackFailureKind.AuthRequired, result.Failure);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DiagnoseAsync_WhenUnconfigured_ReportsDisabledWithCredentialHint()
    {
        var calls = 0;
        var provider = new SoundCloudProvider(new HttpClient(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));
        var broker = new MusicSourceBroker(provider);

        var report = await broker.DiagnoseAsync(CancellationToken.None);

        var status = Assert.Single(report);
        Assert.Equal("SoundCloud", status.Name);
        Assert.Equal("disabled", status.Status);
        Assert.Equal(0, calls);

        var outcome = await broker.SearchWithReportAsync("ambient", 10, CancellationToken.None);
        Assert.Empty(outcome.Tracks);
        var searchStatus = Assert.Single(outcome.Report);
        Assert.Equal("SoundCloud", searchStatus.Name);
        Assert.Equal("disabled", searchStatus.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SearchAsync_AcquiresOAuthToken_FiltersOffPlatformPlayability_AndPreservesAttribution()
    {
        var storage = new MemoryStorage
        {
            ["soundcloud-client-id-v1"] = "sc-id-123",
            ["soundcloud-client-secret-v1"] = "sc-secret-456"
        };
        var tokenCalls = 0;
        Uri? capturedSearchUri = null;
        string? capturedSearchAuth = null;

        const string searchJson = """
        {
          "collection": [
            {
              "id": 2001,
              "title": "Midnight Signal",
              "duration": 215000,
              "streamable": true,
              "access": "playable",
              "permalink_url": "https://soundcloud.com/artist-one/midnight-signal",
              "license": "cc-by",
              "user": { "username": "Artist One" }
            },
            {
              "id": 2002,
              "title": "Preview Only Cut",
              "duration": 180000,
              "streamable": true,
              "access": "preview",
              "permalink_url": "https://soundcloud.com/artist-two/preview-only",
              "user": { "username": "Artist Two" }
            },
            {
              "id": 2003,
              "title": "Stream Disabled",
              "duration": 190000,
              "streamable": false,
              "access": "playable",
              "permalink_url": "https://soundcloud.com/artist-three/disabled",
              "user": { "username": "Artist Three" }
            },
            {
              "id": 2004,
              "title": "Missing Source Link",
              "duration": 190000,
              "streamable": true,
              "access": "playable",
              "user": { "username": "Artist Four" }
            }
          ]
        }
        """;

        var provider = new SoundCloudProvider(new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == "https://secure.soundcloud.com/oauth/token")
            {
                tokenCalls++;
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("Basic", req.Headers.Authorization?.Scheme);
                var expectedBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes("sc-id-123:sc-secret-456"));
                Assert.Equal(expectedBasic, req.Headers.Authorization?.Parameter);
                return JsonResponse("""{"access_token":"tok-abc","expires_in":3600,"token_type":"Bearer"}""");
            }
            capturedSearchUri = req.RequestUri;
            capturedSearchAuth = req.Headers.Authorization?.ToString();
            return JsonResponse(searchJson);
        })), storage);
        await provider.LoadAsync();

        var tracks = await provider.SearchAsync("midnight", 10, CancellationToken.None);

        Assert.Equal(1, tokenCalls);
        Assert.NotNull(capturedSearchUri);
        Assert.Contains("access=playable", capturedSearchUri!.Query);
        Assert.Equal("OAuth tok-abc", capturedSearchAuth);

        var track = Assert.Single(tracks);
        Assert.Equal("soundcloud:2001", track.Id);
        Assert.Equal("Midnight Signal", track.Title);
        Assert.Equal("Artist One", track.Artist);
        Assert.Equal(215000, track.DurationMs);
        Assert.Equal("SoundCloud", track.Source);
        Assert.Equal("https://soundcloud.com/artist-one/midnight-signal", track.ProviderMetadata["permalink_url"]);
        Assert.Equal("cc-by", track.ProviderMetadata["license"]);
    }

    [Fact]
    public async Task ResolveAsync_SelectsHlsOrProgressiveStream_AndRejectsPreviewOnly()
    {
        var storage = new MemoryStorage
        {
            ["soundcloud-client-id-v1"] = "sc-id",
            ["soundcloud-client-secret-v1"] = "sc-secret"
        };

        var provider = new SoundCloudProvider(new HttpClient(new Handler(req =>
        {
            var uri = req.RequestUri!.AbsoluteUri;
            if (uri == "https://secure.soundcloud.com/oauth/token")
                return JsonResponse("""{"access_token":"tok-1","expires_in":3600}""");
            if (uri == "https://api.soundcloud.com/tracks/101")
                return JsonResponse("""{"id":101,"streamable":true,"access":"playable"}""");
            if (uri == "https://api.soundcloud.com/tracks/101/streams")
                return JsonResponse("""{"hls_aac_160_url":"https://cf-hls-media.sndcdn.com/playlist/101.m3u8","hls_aac_96_url":"https://cf-hls-media.sndcdn.com/playlist/96.m3u8"}""");
            if (uri == "https://api.soundcloud.com/tracks/102")
                return JsonResponse("""{"id":102,"streamable":true,"access":"playable"}""");
            if (uri == "https://api.soundcloud.com/tracks/102/streams")
                return JsonResponse("""{"preview_mp3_128_url":"https://cf-preview-media.sndcdn.com/preview/102.mp3"}""");
            if (uri == "https://api.soundcloud.com/tracks/103")
                return JsonResponse("""{"id":103,"streamable":true,"access":"blocked"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })), storage);
        await provider.LoadAsync();

        var ok = await provider.ResolveAsync(new ProviderTrackRef("soundcloud", "101"), null, CancellationToken.None);
        Assert.NotNull(ok.Media);
        Assert.Equal("https://cf-hls-media.sndcdn.com/playlist/101.m3u8", ok.Media!.RawUrl);
        Assert.Equal("AAC", ok.Media.Codec);
        Assert.Equal(160, ok.Media.BitrateKbps);

        var previewOnly = await provider.ResolveAsync(new ProviderTrackRef("soundcloud", "102"), null, CancellationToken.None);
        Assert.Null(previewOnly.Media);
        Assert.Equal(PlaybackFailureKind.PreviewOnly, previewOnly.Failure);

        var blocked = await provider.ResolveAsync(new ProviderTrackRef("soundcloud", "103"), null, CancellationToken.None);
        Assert.Null(blocked.Media);
        Assert.Equal(PlaybackFailureKind.RegionRestricted, blocked.Failure);

        var missing = await provider.ResolveAsync(new ProviderTrackRef("soundcloud", "404"), null, CancellationToken.None);
        Assert.Null(missing.Media);
        Assert.Equal(PlaybackFailureKind.NotFound, missing.Failure);
    }

    [Fact]
    public async Task SearchAsync_RefreshesTokenOnceOn401_AndMapsRejectedCredentialsToAuthExpired()
    {
        var storage = new MemoryStorage
        {
            ["soundcloud-client-id-v1"] = "sc-id",
            ["soundcloud-client-secret-v1"] = "sc-secret"
        };
        var tokenCount = 0;
        var provider = new SoundCloudProvider(new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == "https://secure.soundcloud.com/oauth/token")
            {
                tokenCount++;
                return JsonResponse(tokenCount == 1
                    ? """{"access_token":"expired-tok","expires_in":3600}"""
                    : """{"access_token":"fresh-tok","expires_in":3600}""");
            }
            if (req.Headers.Authorization?.Parameter == "expired-tok")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return JsonResponse("""[{"id":777,"title":"Recovered","duration":120000,"streamable":true,"access":"playable","permalink_url":"https://soundcloud.com/u/recovered","user":{"username":"U"}}]""");
        })), storage);
        await provider.LoadAsync();

        var results = await provider.SearchAsync("recovered", 5, CancellationToken.None);
        Assert.Single(results);
        Assert.Equal(2, tokenCount);

        var badStorage = new MemoryStorage
        {
            ["soundcloud-client-id-v1"] = "bad-id",
            ["soundcloud-client-secret-v1"] = "bad-secret"
        };
        var rejecting = new SoundCloudProvider(new HttpClient(new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized))), badStorage);
        await rejecting.LoadAsync();

        var ex = await Assert.ThrowsAsync<MusicSourceBusinessException>(() =>
            rejecting.SearchAsync("test", 5, CancellationToken.None));
        Assert.Equal(MusicSourceFailureKind.AuthExpired, ex.Kind);

        var resolveAuth = await rejecting.ResolveAsync(
            new ProviderTrackRef("soundcloud", "101"), null, CancellationToken.None);
        Assert.Null(resolveAuth.Media);
        Assert.Equal(PlaybackFailureKind.AuthRequired, resolveAuth.Failure);
    }

    [Fact]
    public async Task Broker_ClearsSoundCloudCacheAndResetsHealth_WhenCredentialsChange()
    {
        var storage = new MemoryStorage
        {
            ["soundcloud-client-id-v1"] = "id-1",
            ["soundcloud-client-secret-v1"] = "sec-1"
        };
        var failSearch = true;
        var streamVersion = 1;
        var provider = new SoundCloudProvider(new HttpClient(new Handler(req =>
        {
            var uri = req.RequestUri!.AbsoluteUri;
            if (uri == "https://secure.soundcloud.com/oauth/token")
                return JsonResponse("""{"access_token":"tok","expires_in":3600}""");
            if (uri.Contains("/tracks?"))
                return failSearch
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : JsonResponse("""{"collection":[]}""");
            if (uri == "https://api.soundcloud.com/tracks/55")
                return JsonResponse("""{"id":55,"streamable":true,"access":"playable"}""");
            if (uri == "https://api.soundcloud.com/tracks/55/streams")
                return JsonResponse($$"""{"http_mp3_128_url":"https://203.0.113.20/sc-{{streamVersion}}.mp3"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })), storage);
        await provider.LoadAsync();

        var broker = new MusicSourceBroker(provider)
        {
            RedirectGuard = new PlaybackRedirectGuard(new HttpClient(new Handler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK))))
        };

        var track = new OnlineTrack { Id = "soundcloud:55", Title = "Drift", Artist = "A" };
        var first = await broker.ResolveTrackAsync(track, forceRefresh: false, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("https://203.0.113.20/sc-1.mp3", first!.Url);

        for (var i = 0; i < 3; i++)
            await broker.SearchWithReportAsync("ambient", 5, CancellationToken.None);

        var whileCircuitOpen = await broker.DiagnoseAsync(CancellationToken.None);
        Assert.Equal("disabled", Assert.Single(whileCircuitOpen).Status);

        failSearch = false;
        streamVersion = 2;
        await provider.SaveCredentialsAsync("id-2", "sec-2");

        var afterSave = await broker.ResolveTrackAsync(track, forceRefresh: false, CancellationToken.None);
        Assert.NotNull(afterSave);
        Assert.Equal("https://203.0.113.20/sc-2.mp3", afterSave!.Url);
        var report = await broker.DiagnoseAsync(CancellationToken.None);
        Assert.Equal("ok", Assert.Single(report).Status);

        Assert.Equal(1, SourcePolicyClasses.GetClass("soundcloud", MusicSourcePolicy.Stability));
        Assert.Equal(2, SourcePolicyClasses.GetClass("soundcloud", MusicSourcePolicy.Quality));
    }

    [Fact]
    public async Task SettingsViewModel_SavesAndClearsSoundCloudCredentials_WithoutLeakingToSettingsJson()
    {
        var storage = new MemoryStorage();
        var soundCloud = new SoundCloudProvider(storage: storage);
        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-sc-{Guid.NewGuid():N}.json");
        try
        {
            using var vm = new SettingsViewModel(
                new Mock<ILLMService>().Object,
                storage,
                settingsFile,
                soundCloud: soundCloud);
            await vm.LoadAsync();

            Assert.False(soundCloud.IsConfigured);
            Assert.Contains("未配置", vm.SoundCloudStatus);

            vm.SoundCloudClientIdInput = "sc-client-id-secret-value";
            vm.SoundCloudClientSecretInput = "sc-client-secret-top-secret";
            await vm.SaveSoundCloudCredentialsCommand.Execute();

            Assert.True(soundCloud.IsConfigured);
            Assert.Empty(vm.SoundCloudClientIdInput);
            Assert.Empty(vm.SoundCloudClientSecretInput);
            Assert.Contains("安全保存", vm.SoundCloudStatus);

            await vm.SaveUiStateCommand.Execute();
            var json = await File.ReadAllTextAsync(settingsFile);
            Assert.DoesNotContain("sc-client-id-secret-value", json);
            Assert.DoesNotContain("sc-client-secret-top-secret", json);

            AppLanguage.Apply("en");
            Assert.Equal("Credentials saved securely", vm.SoundCloudStatus);

            vm.ClearSoundCloudCredentialsCommand.Execute().Subscribe();
            Assert.False(soundCloud.IsConfigured);
            Assert.Contains("Not configured", vm.SoundCloudStatus);
        }
        finally
        {
            AppLanguage.Apply("zh");
            try { File.Delete(settingsFile); } catch { }
            try { File.Delete(settingsFile + ".bak"); } catch { }
        }
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
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
}
