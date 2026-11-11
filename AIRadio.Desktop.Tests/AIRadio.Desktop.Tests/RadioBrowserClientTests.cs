using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

public sealed class RadioBrowserClientTests
{
    private const string SampleStationJson = """
        [
          {
            "stationuuid": "test-uuid-1",
            "name": "Lofi Beats &amp; Chill",
            "url": "http://stream.fallback.org/live",
            "url_resolved": "https://stream.example.com/lofi.mp3",
            "tags": "lofi,chillhop,beats",
            "country": "United States",
            "countrycode": "US",
            "codec": "MP3",
            "bitrate": 128,
            "votes": 120,
            "clickcount": 550,
            "lastcheckok": 1
          }
        ]
        """;

    [Fact]
    public async Task GetStationsByCategoryAsync_SuccessfulMirrorResponse_ParsesStationsAndCaches()
    {
        var callCount = 0;
        using var http = new HttpClient(new Handler(req =>
        {
            callCount++;
            Assert.Contains("/json/stations/bytag/lofi", req.RequestUri!.PathAndQuery);
            Assert.Equal("TuanRadio/1.0.1 (+https://github.com/chenlianwd/TuanRadio)",
                req.Headers.UserAgent.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStationJson)
            };
        }));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid"]);
        var result = await client.GetStationsByCategoryAsync("lofi", forceRefresh: false, CancellationToken.None);

        var station = Assert.Single(result);
        Assert.Equal("test-uuid-1", station.StationUuid);
        Assert.Equal("Lofi Beats & Chill", station.Name); // HTML decoded
        Assert.Equal("https://stream.example.com/lofi.mp3", station.StreamUrl);
        Assert.Equal("lofi,chillhop,beats", station.Tags);
        Assert.Equal("United States", station.Country);
        Assert.Equal("US", station.CountryCode);
        Assert.Equal("MP3", station.Codec);
        Assert.Equal(128, station.Bitrate);
        Assert.Equal("MP3 128k", station.DisplayQuality);
        Assert.Equal("United States · lofi,chillhop,beats", station.DisplayCountryAndTags);
        Assert.Equal(1, callCount);

        // 第二次获取同流派命中缓存，不触发网络请求
        var cachedResult = await client.GetStationsByCategoryAsync("lofi", forceRefresh: false, CancellationToken.None);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetStationsByCategoryAsync_PrimaryMirror500_RotatesToSecondaryMirror()
    {
        var visitedMirrors = new List<string>();
        using var http = new HttpClient(new Handler(req =>
        {
            var host = req.RequestUri!.Host;
            visitedMirrors.Add(host);
            if (host == "mirror1.invalid")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStationJson)
            };
        }));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid", "https://mirror2.invalid"]);
        var result = await client.GetStationsByCategoryAsync("jazz", forceRefresh: true, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, visitedMirrors.Count);
        Assert.Equal("mirror1.invalid", visitedMirrors[0]);
        Assert.Equal("mirror2.invalid", visitedMirrors[1]);
    }

    [Fact]
    public async Task GetStationsByCategoryAsync_AllMirrorsFail_FallsBackToCuratedStations()
    {
        using var http = new HttpClient(new Handler(_ =>
            throw new HttpRequestException("Network failure")));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid", "https://mirror2.invalid"]);
        var result = await client.GetStationsByCategoryAsync("classical", forceRefresh: true, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.All(result, s => Assert.StartsWith("curated-classical-", s.StationUuid));
    }

    [Fact]
    public async Task SearchStationsAsync_ValidQuery_SendsCorrectSearchPathAndReturnsResults()
    {
        using var http = new HttpClient(new Handler(req =>
        {
            Assert.Contains("/json/stations/search?name=BBC", req.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStationJson)
            };
        }));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid"]);
        var result = await client.SearchStationsAsync("BBC", CancellationToken.None);

        Assert.Single(result);
    }

    [Fact]
    public async Task SearchStationsAsync_WhenAllMirrorsFail_FallsBackToCuratedSearch()
    {
        using var http = new HttpClient(new Handler(_ =>
            throw new HttpRequestException("Network down")));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid"]);
        var result = await client.SearchStationsAsync("SomaFM", CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.All(result, s => Assert.Contains("SomaFM", s.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveStationUrlAsync_CuratedStation_DirectlyResolvesWithoutNetwork()
    {
        var networkCalled = false;
        using var http = new HttpClient(new Handler(_ =>
        {
            networkCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid"]);
        var url = await client.ResolveStationUrlAsync("curated-lofi-1", CancellationToken.None);

        Assert.False(networkCalled);
        Assert.NotNull(url);
        Assert.Equal("https://play.streamafrica.net/lofigirl", url);
    }

    [Fact]
    public async Task ResolveStationUrlAsync_OnlineStation_ResolvesViaByUuidEndpoint()
    {
        using var http = new HttpClient(new Handler(req =>
        {
            Assert.Contains("/json/stations/byuuid?uuids=online-uuid-42", req.RequestUri!.PathAndQuery);
            var json = """
                [
                  {
                    "stationuuid": "online-uuid-42",
                    "name": "Online Radio",
                    "url": "http://stream.fallback.org/live",
                    "url_resolved": "https://stream.resolved.org/live.mp3",
                    "codec": "MP3",
                    "lastcheckok": 1
                  }
                ]
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid"]);
        var url = await client.ResolveStationUrlAsync("online-uuid-42", CancellationToken.None);

        Assert.Equal("https://stream.resolved.org/live.mp3", url);
    }

    [Fact]
    public async Task ParseStationElement_IcyProtocol_NormalizedToHttp()
    {
        var json = """
            [
              {
                "stationuuid": "icy-1",
                "name": "Shoutcast ICY Stream",
                "url": "icy://shoutcast.example.com:8000/stream",
                "codec": "MP3",
                "lastcheckok": 1
              }
            ]
            """;
        using var http = new HttpClient(new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid"]);
        var result = await client.GetStationsByCategoryAsync("top", forceRefresh: true, CancellationToken.None);

        var station = Assert.Single(result);
        Assert.Equal("http://shoutcast.example.com:8000/stream", station.StreamUrl);
    }

    [Fact]
    public async Task ParseStationElement_BrokenOrNonHttp_FilteredOut()
    {
        var json = """
            [
              {
                "stationuuid": "broken-1",
                "name": "Dead Stream",
                "url": "http://dead.example.com/stream",
                "lastcheckok": 0
              },
              {
                "stationuuid": "non-http-2",
                "name": "RTSP Stream",
                "url": "rtsp://camera.example.com/stream",
                "lastcheckok": 1
              },
              {
                "stationuuid": "valid-3",
                "name": "Healthy Stream",
                "url": "http://healthy.example.com/stream",
                "lastcheckok": 1
              }
            ]
            """;
        using var http = new HttpClient(new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));

        using var client = new RadioBrowserClient(http, ["https://mirror1.invalid"]);
        var result = await client.GetStationsByCategoryAsync("top", forceRefresh: true, CancellationToken.None);

        var station = Assert.Single(result);
        Assert.Equal("valid-3", station.StationUuid);
    }

    [Fact]
    public void ToTrack_SetsCorrectLiveStreamProperties()
    {
        var station = new RadioStation
        {
            StationUuid = "uuid-abc-123",
            Name = "Jazz Lounge",
            StreamUrl = "https://jazz.example.com/live",
            Tags = "jazz, smooth, lounge",
            Country = "Switzerland",
            CountryCode = "CH",
            Codec = "AAC",
            Bitrate = 256
        };

        var track = station.ToTrack();

        Assert.True(track.IsLiveStream);
        Assert.Equal("radio-browser:uuid-abc-123", track.Id);
        Assert.Equal("radio-browser:uuid-abc-123", track.SourceId);
        Assert.Equal("Jazz Lounge", track.Title);
        Assert.Equal("Switzerland · jazz, smooth, lounge", track.Artist);
        Assert.Equal(TimeSpan.Zero, track.Duration);
        Assert.Equal("https://jazz.example.com/live", track.FilePath);
        Assert.Equal("AAC", track.DeclaredCodec);
        Assert.Equal(256, track.DeclaredBitrateKbps);
        Assert.NotNull(track.ResolvedAtUtc);
        Assert.Equal("true", track.ProviderMetadata["is_live"]);
        Assert.Equal("https://jazz.example.com/live", track.ProviderMetadata["stream_url"]);
        Assert.Equal("uuid-abc-123", track.ProviderMetadata["station_uuid"]);
    }

    [Fact]
    public void Track_IsLiveStream_TrueForRadioBrowserSourceIdOrMetadata()
    {
        var trackA = new Track { SourceId = "radio-browser:uuid-xyz" };
        Assert.True(trackA.IsLiveStream);

        var trackB = new Track();
        trackB.ProviderMetadata["is_live"] = "true";
        Assert.True(trackB.IsLiveStream);

        var trackC = new Track { SourceId = "netease:12345" };
        Assert.False(trackC.IsLiveStream);

        var trackD = new Track { SourceId = "local:D:\\music\\song.mp3" };
        Assert.False(trackD.IsLiveStream);
    }

    [Fact]
    public void AppLanguage_MusicSourceName_RadioBrowser()
    {
        AppLanguage.Apply("zh");
        Assert.Equal("网络电台", AppLanguage.MusicSourceName("radio-browser"));
        Assert.Equal("网络电台", AppLanguage.MusicSourceName("radio"));
        Assert.Equal("网络电台", AppLanguage.MusicSourceName("Internet Radio"));

        AppLanguage.Apply("en");
        Assert.Equal("Internet Radio", AppLanguage.MusicSourceName("radio-browser"));
        Assert.Equal("Internet Radio", AppLanguage.MusicSourceName("radio"));
        Assert.Equal("Internet Radio", AppLanguage.MusicSourceName("网络电台"));

        AppLanguage.Apply("zh"); // restore
    }

    [Theory]
    [InlineData("lofi")]
    [InlineData("jazz")]
    [InlineData("classical")]
    [InlineData("synthwave")]
    [InlineData("news")]
    [InlineData("pop")]
    [InlineData("chinese")]
    [InlineData("top")]
    public void CuratedStationRegistry_ContainsAll8Categories(string category)
    {
        var stations = CuratedStationRegistry.GetCuratedStations(category);
        Assert.NotEmpty(stations);
        Assert.All(stations, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.StationUuid));
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.StreamUrl));
            Assert.True(s.StreamUrl.StartsWith("http://") || s.StreamUrl.StartsWith("https://"));
        });
    }

    [Fact]
    public async Task GetStationsByCategoryAsync_MirrorTimesOut_FailsOverToNextMirror()
    {
        var visited = new List<string>();
        using var http = new HttpClient(new Handler(req =>
        {
            visited.Add(req.RequestUri!.Host);
            if (req.RequestUri.Host == "slow.invalid")
            {
                // 模拟网络挂起/超时
                throw new TaskCanceledException("Mirror connection timed out");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleStationJson)
            };
        }));

        using var client = new RadioBrowserClient(http, ["https://slow.invalid", "https://fast.invalid"]);
        var result = await client.GetStationsByCategoryAsync("lofi", forceRefresh: true, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(new[] { "slow.invalid", "fast.invalid" }, visited);
    }

    [Theory]
    [InlineData("MP3", 128, "MP3 128k")]
    [InlineData("AAC", 64, "AAC 64k")]
    [InlineData("", 128, "128k")]
    [InlineData("UNKNOWN", 128, "128k")]
    [InlineData("unknown", 128, "128k")]
    [InlineData("MP3", 0, "")]
    [InlineData("UNKNOWN", 0, "")]
    [InlineData("unknown", 0, "")]
    [InlineData("", 0, "")]
    public void RadioStation_DisplayQuality_SanitizesAndHidesRedundantBadges(string codec, int bitrate, string expected)
    {
        var station = new RadioStation
        {
            StationUuid = "u1",
            Name = "Station",
            StreamUrl = "http://example.com/stream",
            Codec = codec,
            Bitrate = bitrate
        };

        Assert.Equal(expected, station.DisplayQuality);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
