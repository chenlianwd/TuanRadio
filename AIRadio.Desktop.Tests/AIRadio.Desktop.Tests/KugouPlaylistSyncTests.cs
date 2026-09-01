using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class KugouPlaylistServiceTests
{
    [Fact]
    public async Task ReadsUserPlaylistsAndTracks_WithStableKugouIdentity()
    {
        HttpRequestMessage? lastRequest = null;
        var requestCount = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            requestCount++;
            lastRequest = request;
            var path = request.RequestUri!.AbsolutePath;
            var body = path == "/user/playlist"
                ? """
                  {"status":1,"data":{"info":[
                    {"listid":123,"name":"我的歌单","count":2}
                  ]}}
                  """
                : """
                  {"status":1,"data":{"songs":[
                    {"audio_info":{"hash":"ABC","hash_std":"STD","hash_128":"H128","audio_name":"歌手一 - 歌曲一.mp3","album_id":12,"mixsongid":34,"album_name":"专辑一","timelen":123000}},
                    {"hash":"DEF","filename":"歌手二 - 歌曲二.mp3","singername":"歌手二","duration":245}
                  ]}}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        });
        using var client = new HttpClient(handler);
        var accounts = await CreateLoggedInStoreAsync();
        var service = new KugouPlaylistService(client, accounts, "http://localhost");

        var playlists = await service.GetUserPlaylistsAsync();
        var tracks = await service.GetPlaylistTracksAsync(playlists.Single().Id);

        Assert.Equal("123", playlists.Single().Id);
        Assert.Equal("我的歌单", playlists.Single().Name);
        Assert.Equal(2, playlists.Single().TrackCount);
        Assert.Collection(tracks,
            first =>
            {
                Assert.Equal("kugou:ABC", first.Id);
                Assert.Equal("歌曲一", first.Title);
                Assert.Equal("歌手一", first.Artist);
                Assert.Equal(123000, first.DurationMs);
                Assert.Equal("12", first.ProviderMetadata["album_id"]);
                Assert.Equal("34", first.ProviderMetadata["album_audio_id"]);
                Assert.Equal("STD", first.ProviderMetadata["hash_std"]);
                Assert.Equal("H128", first.ProviderMetadata["hash_128"]);
            },
            second =>
            {
                Assert.Equal("kugou:DEF", second.Id);
                Assert.Equal("歌曲二", second.Title);
                Assert.Equal(245000, second.DurationMs);
            });
        Assert.NotNull(lastRequest);
        Assert.DoesNotContain("SECRET", lastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("token=SECRET;userid=42;dfid=DF",
            lastRequest.Headers.GetValues("Authorization").Single());
        // 无 total 的 1 参重载会多探一页确认是否到底：user/playlist + 曲目页 x2
        Assert.Equal(3, requestCount);
    }

    [Fact]
    public async Task TrackPaging_UsesReportedTotalWhenServerCapsRequestedPageSize()
    {
        var requestedPages = new List<int>();
        var handler = new DelegateHandler((request, _) =>
        {
            var page = GetQueryInt(request.RequestUri!, "page");
            requestedPages.Add(page);
            // 模拟上游把请求的 pagesize=30 进一步限制为 20，但同时返回准确 total。
            var count = page < 3 ? 20 : 1;
            var offset = (page - 1) * 20;
            var songs = Enumerable.Range(offset, count).Select(i => new
            {
                hash = $"H{i}",
                songname = $"Song {i}",
                singername = "Artist",
                duration = 180
            });
            var body = JsonSerializer.Serialize(new { status = 1, data = new { total = 41, songs } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        });
        using var client = new HttpClient(handler);
        var service = new KugouPlaylistService(client, await CreateLoggedInStoreAsync(), "http://localhost");

        var tracks = await service.GetPlaylistTracksAsync("99");

        Assert.Equal(41, tracks.Count);
        Assert.Equal(new[] { 1, 2, 3 }, requestedPages);
    }

    [Fact]
    public async Task TrackPaging_FallbackWithoutTotal_KeepsFetchingWhenServerCapsPageSize()
    {
        var requestedPages = new List<int>();
        var handler = new DelegateHandler((request, _) =>
        {
            var page = GetQueryInt(request.RequestUri!, "page");
            requestedPages.Add(page);
            // 无 total、无摘要 TrackCount；上游把请求的 pagesize=30 压成 20，共 45 首
            var count = page < 3 ? 20 : 5;
            var offset = (page - 1) * 20;
            var songs = Enumerable.Range(offset, count).Select(i => new
            {
                hash = $"H{i}",
                songname = $"Song {i}",
                singername = "Artist",
                duration = 180
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { status = 1, data = new { songs } }))
            });
        });
        using var client = new HttpClient(handler);
        var service = new KugouPlaylistService(client, await CreateLoggedInStoreAsync(), "http://localhost");

        var tracks = await service.GetPlaylistTracksAsync("99");

        // 短页判定必须以首页实际条数(20)为基准：按固定 30 判会把首页当成末页，静默截断成 20 首
        Assert.Equal(45, tracks.Count);
        Assert.Equal(new[] { 1, 2, 3 }, requestedPages);
    }

    [Fact]
    public async Task PlaylistRequest_RetriesWhileLocalProxyIsStarting()
    {
        var calls = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
                throw new HttpRequestException("connection refused");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1,\"data\":{\"info\":[]}}")
            });
        });
        using var client = new HttpClient(handler);
        var service = new KugouPlaylistService(client, await CreateLoggedInStoreAsync(), "http://localhost");

        var playlists = await service.GetUserPlaylistsAsync();

        Assert.Empty(playlists);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task PlaylistRequest_DoesNotRetryExplicitBusinessFailure()
    {
        var calls = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("{\"status\":0,\"error_code\":152,\"error_msg\":\"Parameter Error\"}")
            });
        });
        using var client = new HttpClient(handler);
        var service = new KugouPlaylistService(client, await CreateLoggedInStoreAsync(), "http://localhost");

        var exception = await Assert.ThrowsAsync<MusicSourceBusinessException>(
            () => service.GetUserPlaylistsAsync());

        Assert.Contains("Parameter Error", exception.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TrackPaging_UsesPlaylistTrackCountAndPreservesPageOrder()
    {
        var requestedPages = new System.Collections.Concurrent.ConcurrentBag<int>();
        var handler = new DelegateHandler(async (request, _) =>
        {
            var page = GetQueryInt(request.RequestUri!, "page");
            requestedPages.Add(page);
            await Task.Delay(10);
            var start = (page - 1) * 30;
            var songs = Enumerable.Range(start, page == 1 ? 30 : 1)
                .Select(i => new { hash = $"H{i}", songname = $"Song {i}", duration = 180 });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    status = 1,
                    data = new { songs }
                }))
            };
        });
        using var client = new HttpClient(handler);
        var service = new KugouPlaylistService(client, await CreateLoggedInStoreAsync(), "http://localhost");

        var tracks = await service.GetPlaylistTracksAsync("99", expectedTrackCount: 150);

        Assert.Equal(34, tracks.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, requestedPages.OrderBy(page => page));
    }

    [Fact]
    public async Task NotLoggedIn_RejectsBeforeSendingRequest()
    {
        var calls = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler);
        var storage = new Mock<ISecureStorage>();
        var service = new KugouPlaylistService(client, new MusicAccountStore(storage.Object), "http://localhost");

        await Assert.ThrowsAsync<MusicSourceBusinessException>(() => service.GetUserPlaylistsAsync());
        Assert.Equal(0, calls);
    }

    private static async Task<MusicAccountStore> CreateLoggedInStoreAsync()
    {
        var storage = new Mock<ISecureStorage>();
        var store = new MusicAccountStore(storage.Object);
        await store.SetKugouCookieAsync("token=SECRET;userid=42;dfid=DF");
        return store;
    }

    private static int GetQueryInt(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == name && int.TryParse(pair[1], out var value))
                return value;
        }

        return 0;
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}

public class KugouPlaylistViewModelTests
{
    [Fact]
    public async Task LoadAndImport_AddsStableTracksWithoutTemporaryUrls_AndSkipsDuplicates()
    {
        var playlistFile = CreateTempPlaylistFile();
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new System.Reactive.Subjects.Subject<PlaybackState>());
        audio.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        var search = new Mock<IMusicSearchService>();
        var kugou = new Mock<IKugouPlaylistService>();
        kugou.SetupGet(x => x.IsLoggedIn).Returns(true);
        kugou.Setup(x => x.GetUserPlaylistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new KugouPlaylistInfo { Id = "7", Name = "通勤", TrackCount = 2 } });
        kugou.Setup(x => x.GetPlaylistTracksAsync("7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new OnlineTrack
                {
                    Id = "kugou:A", Title = "甲", Artist = "歌手", DurationMs = 180000, Source = "酷狗",
                    ProviderMetadata = new Dictionary<string, string>
                    {
                        ["album_id"] = "12",
                        ["album_audio_id"] = "34"
                    }
                },
                new OnlineTrack { Id = "kugou:B", Title = "乙", Artist = "歌手", DurationMs = 200000, Source = "酷狗" }
            });

        using var vm = new PlaylistViewModel(
            audio.Object,
            search.Object,
            playlistFile,
            kugouPlaylistService: kugou.Object);

        await vm.LoadKugouPlaylistsAsync();
        await WaitUntilAsync(() => vm.KugouPlaylistTracks.Count == 2);
        vm.KugouFilterText = "乙";
        await WaitUntilAsync(() => vm.FilteredKugouPlaylistTracks.Count == 1);
        Assert.Equal("乙", vm.FilteredKugouPlaylistTracks.Single().Title);
        vm.PlayKugouTrackCommand.Execute(vm.KugouPlaylistTracks[1]).Subscribe();
        audio.Verify(x => x.StartPlaybackContext(
            It.Is<IEnumerable<Track>>(tracks => tracks.Select(track => track.SourceId).SequenceEqual(new[] { "kugou:A", "kugou:B" })),
            1,
            false,
            "酷狗 · 通勤"), Times.Once);
        await vm.ImportKugouPlaylistCommand.Execute().ToTask();
        await vm.ImportKugouPlaylistCommand.Execute().ToTask();

        Assert.Equal(2, vm.Tracks.Count);
        Assert.All(vm.Tracks, track =>
        {
            Assert.StartsWith("kugou:", track.SourceId);
            Assert.Equal(string.Empty, track.FilePath);
        });
        audio.Verify(x => x.AddTracks(It.Is<IEnumerable<Track>>(tracks => tracks.Count() == 2)), Times.Once);
        Assert.Contains("保留 2 首", vm.KugouStatusMessage);

        var json = await File.ReadAllTextAsync(playlistFile);
        Assert.Contains("\"ProviderId\": \"kugou\"", json);
        Assert.Contains("\"album_id\": \"12\"", json);
        Assert.Contains("\"album_audio_id\": \"34\"", json);
        Assert.Contains("\"RemoteId\": \"7\"", json);
        Assert.Equal("通勤", vm.SyncedPlaylists.Single().Name);
        Assert.Equal(new[] { "kugou:A", "kugou:B" }, vm.SyncedPlaylists.Single().TrackSourceIds);
        vm.SelectedSyncedPlaylist = vm.SyncedPlaylists.Single();
        Assert.Equal(new[] { "kugou:A", "kugou:B" }, vm.VisibleLibraryTracks.Select(track => track.SourceId));
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportExistingKugouTrack_RefreshesProviderMetadataAndPersistsIt()
    {
        var playlistFile = CreateTempPlaylistFile();
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new System.Reactive.Subjects.Subject<PlaybackState>());
        audio.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        var search = new Mock<IMusicSearchService>();
        var kugou = new Mock<IKugouPlaylistService>();
        kugou.SetupGet(x => x.IsLoggedIn).Returns(true);
        kugou.Setup(x => x.GetUserPlaylistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new KugouPlaylistInfo { Id = "9", Name = "旧收藏", TrackCount = 1 } });
        kugou.Setup(x => x.GetPlaylistTracksAsync("9", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new OnlineTrack
                {
                    Id = "kugou:OLD",
                    Title = "旧歌",
                    Artist = "歌手",
                    Source = "酷狗",
                    ProviderMetadata = new Dictionary<string, string>
                    {
                        ["album_id"] = "12",
                        ["album_audio_id"] = "34",
                        ["hash_std"] = "STD"
                    }
                }
            });

        using var vm = new PlaylistViewModel(
            audio.Object,
            search.Object,
            playlistFile,
            kugouPlaylistService: kugou.Object);
        vm.Tracks.Add(new Track
        {
            Id = "kugou:OLD",
            SourceId = "kugou:OLD",
            Title = "旧歌",
            Artist = "歌手"
        });
        await vm.SaveAsync();

        await vm.LoadKugouPlaylistsAsync();
        await WaitUntilAsync(() => vm.KugouPlaylistTracks.Count == 1);
        await vm.ImportKugouPlaylistCommand.Execute().ToTask();

        var existing = Assert.Single(vm.Tracks);
        Assert.Equal("12", existing.ProviderMetadata["album_id"]);
        Assert.Equal("34", existing.ProviderMetadata["album_audio_id"]);
        Assert.Equal("STD", existing.ProviderMetadata["hash_std"]);
        audio.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);

        var json = await File.ReadAllTextAsync(playlistFile);
        Assert.Contains("\"Version\": 3", json);
        Assert.Contains("\"album_id\": \"12\"", json);
        Assert.Contains("\"hash_std\": \"STD\"", json);
    }

    [Fact]
    public async Task LoadPlaylists_WhenLoggedOut_ShowsGuidanceWithoutCallingApi()
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new System.Reactive.Subjects.Subject<PlaybackState>());
        audio.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        var search = new Mock<IMusicSearchService>();
        var kugou = new Mock<IKugouPlaylistService>();
        kugou.SetupGet(x => x.IsLoggedIn).Returns(false);

        using var vm = new PlaylistViewModel(
            audio.Object,
            search.Object,
            CreateTempPlaylistFile(),
            kugouPlaylistService: kugou.Object);

        await vm.LoadKugouPlaylistsAsync();

        Assert.Contains("登录酷狗", vm.KugouStatusMessage);
        kugou.Verify(x => x.GetUserPlaylistsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Import_WhenFirstSaveFails_RetryPersistsExistingImportedTracks()
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new System.Reactive.Subjects.Subject<PlaybackState>());
        audio.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        var search = new Mock<IMusicSearchService>();
        var kugou = new Mock<IKugouPlaylistService>();
        kugou.SetupGet(x => x.IsLoggedIn).Returns(true);
        kugou.Setup(x => x.GetUserPlaylistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new KugouPlaylistInfo { Id = "8", Name = "重试", TrackCount = 1 } });
        kugou.Setup(x => x.GetPlaylistTracksAsync("8", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new OnlineTrack { Id = "kugou:R", Title = "可重试", Artist = "歌手", Source = "酷狗" }
            });
        var saveAttempts = 0;
        Task Writer(string _, string __)
        {
            saveAttempts++;
            return saveAttempts == 1
                ? Task.FromException(new IOException("disk unavailable"))
                : Task.CompletedTask;
        }

        using var vm = new PlaylistViewModel(
            audio.Object,
            search.Object,
            CreateTempPlaylistFile(),
            Writer,
            kugou.Object);
        await vm.LoadKugouPlaylistsAsync();
        await WaitUntilAsync(() => vm.KugouPlaylistTracks.Count == 1);

        await vm.ImportKugouPlaylistCommand.Execute().ToTask();
        Assert.Single(vm.Tracks);
        Assert.Contains("保存失败", vm.KugouStatusMessage);

        await vm.ImportKugouPlaylistCommand.Execute().ToTask();
        Assert.Equal(2, saveAttempts);
        Assert.DoesNotContain("保存失败", vm.KugouStatusMessage);
        audio.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Once);
    }

    private static string CreateTempPlaylistFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "playlist.json");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
