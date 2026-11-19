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

public class JamendoProviderTests
{
    [Fact]
    public async Task SearchAsync_WhenUnconfigured_ReturnsEmptyWithoutHttpCalls()
    {
        var calls = 0;
        var provider = new JamendoProvider(new HttpClient(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));

        Assert.False(provider.HasClientId);
        Assert.False(provider.IsConfigured);

        var tracks = await provider.SearchAsync("ambient", 10, CancellationToken.None);

        Assert.Empty(tracks);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ResolveAsync_WhenUnconfigured_ReturnsAuthRequiredWithoutHttpCalls()
    {
        var calls = 0;
        var provider = new JamendoProvider(new HttpClient(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));

        var result = await provider.ResolveAsync(
            new ProviderTrackRef("jamendo", "12345"),
            null,
            CancellationToken.None);

        Assert.Null(result.Media);
        Assert.Equal(PlaybackFailureKind.AuthRequired, result.Failure);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DiagnoseAsync_WhenUnconfigured_ReportsDisabledWithClientIdHint()
    {
        var calls = 0;
        var provider = new JamendoProvider(new HttpClient(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));
        var broker = new MusicSourceBroker(provider);

        var report = await broker.DiagnoseAsync(CancellationToken.None);

        var status = Assert.Single(report);
        Assert.Equal("Jamendo", status.Name);
        Assert.Equal("disabled", status.Status);
        Assert.Equal(0, calls);

        var outcome = await broker.SearchWithReportAsync("ambient", 10, CancellationToken.None);
        Assert.Empty(outcome.Tracks);
        var searchStatus = Assert.Single(outcome.Report);
        Assert.Equal("Jamendo", searchStatus.Name);
        Assert.Equal("disabled", searchStatus.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SearchAsync_WithClientId_ParsesResultsAndFiltersInvalidEntries()
    {
        var storage = new MemoryStorage { Value = "client-123" };
        Uri? capturedUri = null;
        const string json = """
        {
          "headers": { "status": "success", "code": 0 },
          "results": [
            {
              "id": "1884567",
              "name": "Nocturnal Drift",
              "duration": 215,
              "artist_name": "Quiet Static",
              "album_name": "Late Tape",
              "audio": "https://prod-1.storage.jamendo.com/?trackid=1884567&format=mp32"
            },
            {
              "id": "../bad",
              "name": "Bad Id",
              "duration": 100,
              "artist_name": "Nope",
              "audio": "https://prod-1.storage.jamendo.com/bad.mp3"
            },
            {
              "id": "999",
              "name": "Missing Audio Scheme",
              "duration": 90,
              "artist_name": "Nope",
              "audio": "javascript:alert(1)"
            },
            {
              "id": "2001",
              "name": "   ",
              "duration": 60,
              "artist_name": "Blank Title",
              "audio": "https://prod-1.storage.jamendo.com/blank.mp3"
            }
          ]
        }
        """;

        var provider = new JamendoProvider(new HttpClient(new Handler(req =>
        {
            capturedUri = req.RequestUri;
            return JsonResponse(json);
        })), storage);

        await provider.LoadAsync();
        Assert.True(provider.IsConfigured);

        var tracks = await provider.SearchAsync("nocturnal drift", 10, CancellationToken.None);

        Assert.NotNull(capturedUri);
        Assert.Contains("client_id=client-123", capturedUri!.Query);
        Assert.Contains("audioformat=mp32", capturedUri.Query);
        Assert.Contains("limit=10", capturedUri.Query);

        var track = Assert.Single(tracks);
        Assert.Equal("jamendo:1884567", track.Id);
        Assert.Equal("Nocturnal Drift", track.Title);
        Assert.Equal("Quiet Static", track.Artist);
        Assert.Equal("Late Tape", track.Album);
        Assert.Equal(215_000, track.DurationMs);
        Assert.Equal("Jamendo", track.Source);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SearchAsync_WhenApiReturnsAuthErrorCode_ThrowsAuthExpired(int errorCode)
    {
        var storage = new MemoryStorage { Value = "bad-client-id" };
        var json = $$"""
        {
          "headers": { "status": "failed", "code": {{errorCode}}, "error_message": "Invalid Client Id" },
          "results": []
        }
        """;

        var provider = new JamendoProvider(
            new HttpClient(new Handler(_ => JsonResponse(json))),
            storage);
        await provider.LoadAsync();

        var ex = await Assert.ThrowsAsync<MusicSourceBusinessException>(() =>
            provider.SearchAsync("lofi", 5, CancellationToken.None));

        Assert.Equal(MusicSourceFailureKind.AuthExpired, ex.Kind);
    }

    [Fact]
    public async Task ResolveAsync_WithValidTrack_ReturnsMp3192PlayableMedia()
    {
        var storage = new MemoryStorage { Value = "client-abc" };
        const string streamUrl = "https://prod-1.storage.jamendo.com/?trackid=1884567&format=mp32";
        const string json = $$"""
        {
          "headers": { "status": "success", "code": 0 },
          "results": [
            {
              "id": "1884567",
              "name": "Nocturnal Drift",
              "audio": "{{streamUrl}}"
            }
          ]
        }
        """;

        var provider = new JamendoProvider(
            new HttpClient(new Handler(_ => JsonResponse(json))),
            storage);
        await provider.LoadAsync();

        var result = await provider.ResolveAsync(
            new ProviderTrackRef("jamendo", "1884567"),
            null,
            CancellationToken.None);

        Assert.Equal(PlaybackFailureKind.None, result.Failure);
        Assert.NotNull(result.Media);
        Assert.Equal(new Uri(streamUrl), result.Media!.Uri);
        Assert.Equal("MP3", result.Media.Codec);
        Assert.Equal(192, result.Media.BitrateKbps);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("\"5\"")]
    public async Task ResolveAsync_WhenApiReturnsAuthErrorCode_ReturnsAuthRequired(string rawErrorCode)
    {
        var storage = new MemoryStorage { Value = "bad-id" };
        var json = $$"""
        {
          "headers": { "status": "failed", "code": {{rawErrorCode}} },
          "results": []
        }
        """;

        var provider = new JamendoProvider(
            new HttpClient(new Handler(_ => JsonResponse(json))),
            storage);
        await provider.LoadAsync();

        var result = await provider.ResolveAsync(
            new ProviderTrackRef("jamendo", "1884567"),
            null,
            CancellationToken.None);

        Assert.Null(result.Media);
        Assert.Equal(PlaybackFailureKind.AuthRequired, result.Failure);
    }

    [Fact]
    public async Task ResolveAsync_WhenRedirectOrNonNumericId_FailsSafely()
    {
        var storage = new MemoryStorage { Value = "client-abc" };
        var provider = new JamendoProvider(new HttpClient(new Handler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.Location = new Uri("https://169.254.169.254/latest");
            return resp;
        })), storage);
        await provider.LoadAsync();

        var redirectResult = await provider.ResolveAsync(
            new ProviderTrackRef("jamendo", "1884567"),
            null,
            CancellationToken.None);
        Assert.Null(redirectResult.Media);
        Assert.Equal(PlaybackFailureKind.TransportRejected, redirectResult.Failure);

        var badIdResult = await provider.ResolveAsync(
            new ProviderTrackRef("jamendo", "abc"),
            null,
            CancellationToken.None);
        Assert.Null(badIdResult.Media);
        Assert.Equal(PlaybackFailureKind.NotFound, badIdResult.Failure);
    }

    [Fact]
    public async Task SaveAndClearClientId_ValidatesPersistsAndClearsBrokerCache()
    {
        var storage = new MemoryStorage();
        var resolveCalls = 0;
        var provider = new JamendoProvider(new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.Query.Contains("search="))
            {
                return JsonResponse("""
                {
                  "headers": { "status": "success", "code": 0 },
                  "results": [
                    {
                      "id": "42",
                      "name": "Warm Horizon",
                      "duration": 180,
                      "artist_name": "Signal",
                      "audio": "https://prod-1.storage.jamendo.com/42.mp3"
                    }
                  ]
                }
                """);
            }
            resolveCalls++;
            return JsonResponse($$"""
            {
              "headers": { "status": "success", "code": 0 },
              "results": [
                {
                  "id": "42",
                  "name": "Warm Horizon",
                  "audio": "https://93.184.216.34/jamendo/{{resolveCalls}}.mp3"
                }
              ]
            }
            """);
        })), storage);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SaveClientIdAsync("has space"));

        var changedCount = 0;
        provider.ConfigurationChanged += (_, _) => changedCount++;

        var broker = new MusicSourceBroker(provider);
        await provider.SaveClientIdAsync("  client-one  ");
        Assert.True(provider.IsConfigured);
        Assert.Equal("client-one", storage.Value);
        Assert.Equal(1, changedCount);

        var first = await broker.ResolveTrackAsync(
            new OnlineTrack { Id = "jamendo:42", Title = "Warm Horizon", Artist = "Signal", Source = "Jamendo" },
            forceRefresh: false,
            CancellationToken.None);
        Assert.Equal(1, resolveCalls);
        Assert.Contains("/1.mp3", first!.Url);

        // Saving a rotated Client ID raises ConfigurationChanged and clears the cached URL.
        await provider.SaveClientIdAsync("client-two");
        Assert.Equal(2, changedCount);

        var second = await broker.ResolveTrackAsync(
            new OnlineTrack { Id = "jamendo:42", Title = "Warm Horizon", Artist = "Signal", Source = "Jamendo" },
            forceRefresh: false,
            CancellationToken.None);
        Assert.Equal(2, resolveCalls);
        Assert.Contains("/2.mp3", second!.Url);

        provider.ClearClientId();
        Assert.False(provider.IsConfigured);
        Assert.Null(storage.Value);
        Assert.Equal(3, changedCount);
    }

    [Fact]
    public async Task SaveClientIdAsync_ResetsOpenCircuitBreakerInBroker()
    {
        var storage = new MemoryStorage { Value = "initial-id" };
        var failSearch = true;
        var provider = new JamendoProvider(new HttpClient(new Handler(_ =>
        {
            if (failSearch)
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            return JsonResponse("""
            {
              "headers": { "status": "success", "code": 0 },
              "results": [
                {
                  "id": "77",
                  "name": "Morning Mist",
                  "duration": 150,
                  "artist_name": "Dawn",
                  "audio": "https://prod-1.storage.jamendo.com/77.mp3"
                }
              ]
            }
            """);
        })), storage);
        await provider.LoadAsync();
        var broker = new MusicSourceBroker(provider);

        // Trip the circuit breaker via consecutive 502 transport failures.
        for (var i = 0; i < 4; i++)
            await broker.SearchWithReportAsync("mist", 5, CancellationToken.None);

        var openOutcome = await broker.SearchWithReportAsync("mist", 5, CancellationToken.None);
        Assert.Equal("disabled", Assert.Single(openOutcome.Report).Status);

        // Saving a rotated Client ID resets the circuit breaker immediately.
        failSearch = false;
        await provider.SaveClientIdAsync("rotated-id");

        var recovered = await broker.SearchWithReportAsync("mist", 5, CancellationToken.None);
        Assert.Equal("ok", Assert.Single(recovered.Report).Status);
        Assert.Single(recovered.Tracks);
    }

    [Fact]
    public void SourcePolicyAndLocalization_IncludeJamendo()
    {
        Assert.Equal(1, SourcePolicyClasses.GetClass("Jamendo", MusicSourcePolicy.Stability));
        Assert.Equal(2, SourcePolicyClasses.GetClass("Jamendo", MusicSourcePolicy.Quality));
        Assert.Equal(1, SourcePolicyClasses.GetClass("jamendo", MusicSourcePolicy.Stability));
        Assert.Equal(2, SourcePolicyClasses.GetClass("jamendo", MusicSourcePolicy.Quality));
        Assert.Equal("Jamendo", AppLanguage.MusicSourceName("jamendo"));
        Assert.True(AppLanguage.GetStringTable("zh").ContainsKey("S_JamendoTitle"));
        Assert.True(AppLanguage.GetStringTable("en").ContainsKey("S_JamendoTitle"));
    }

    [Fact]
    public async Task SettingsViewModel_LoadsSavesAndClearsJamendoClientId()
    {
        var storage = new MemoryStorage();
        var provider = new JamendoProvider(new HttpClient(new Handler(_ => JsonResponse("{}"))), storage);
        var settingsDir = Path.Combine(Path.GetTempPath(), "AIRadio_JamendoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        var settingsFile = Path.Combine(settingsDir, "settings.json");

        try
        {
            using var vm = new SettingsViewModel(
                new Mock<ILLMService>().Object,
                storage,
                settingsFile,
                jamendo: provider);

            await vm.LoadAsync();
            Assert.False(provider.HasClientId);
            Assert.False(string.IsNullOrWhiteSpace(vm.JamendoStatus));

            // Empty and whitespace-containing Client IDs must report validation errors without persisting.
            vm.JamendoClientIdInput = "   ";
            await vm.SaveJamendoClientIdCommand.Execute().FirstAsync();
            Assert.False(provider.HasClientId);
            Assert.Null(storage.Value);
            Assert.Contains("Client ID", vm.JamendoStatus);

            vm.JamendoClientIdInput = "invalid id";
            await vm.SaveJamendoClientIdCommand.Execute().FirstAsync();
            Assert.False(provider.HasClientId);
            Assert.Null(storage.Value);
            Assert.Contains("Client ID", vm.JamendoStatus);

            vm.JamendoClientIdInput = "my-jamendo-client";
            await vm.SaveJamendoClientIdCommand.Execute().FirstAsync();

            Assert.True(provider.HasClientId);
            Assert.Equal("my-jamendo-client", storage.Value);
            Assert.Equal(string.Empty, vm.JamendoClientIdInput);
            Assert.NotEmpty(vm.JamendoStatus);

            await vm.ClearJamendoClientIdCommand.Execute().FirstAsync();

            Assert.False(provider.HasClientId);
            Assert.Null(storage.Value);
            Assert.NotEmpty(vm.JamendoStatus);
        }
        finally
        {
            try { Directory.Delete(settingsDir, recursive: true); } catch { }
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class MemoryStorage : ISecureStorage
    {
        public string? Value { get; set; }
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
