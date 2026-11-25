using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.Services.WebRemote;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class WebRemoteViewModelTests
{
    private sealed class StubWebRemoteService : IWebRemoteService
    {
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public int ConnectedClientCount { get; set; }
        public string PrimaryUrl { get; private set; } = string.Empty;
        public IReadOnlyList<string> AccessUrls => string.IsNullOrEmpty(PrimaryUrl) ? Array.Empty<string>() : [PrimaryUrl];
        public string AccessToken { get; private set; } = string.Empty;
        public string LastError { get; private set; } = string.Empty;
        public int NotifyCount { get; private set; }

        public Func<WebRemoteSnapshot>? SnapshotProvider { get; private set; }
        public Func<WebRemoteCommand, Task<WebRemoteCommandResult>>? CommandHandler { get; private set; }
        public Func<WebRemoteBrowseRequest, CancellationToken, Task<WebRemoteBrowseResponse>>? BrowseHandler { get; private set; }

        public event Action? StatusChanged;

        public void ConfigureHandlers(
            Func<WebRemoteSnapshot> snapshotProvider,
            Func<WebRemoteCommand, Task<WebRemoteCommandResult>> commandHandler)
        {
            SnapshotProvider = snapshotProvider;
            CommandHandler = commandHandler;
        }

        public void ConfigureBrowseHandler(
            Func<WebRemoteBrowseRequest, CancellationToken, Task<WebRemoteBrowseResponse>> browseHandler)
        {
            BrowseHandler = browseHandler;
        }

        public Task<bool> StartAsync(int preferredPort, string token, IPAddress? bindAddress = null, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            Port = preferredPort <= 0 ? 37260 : preferredPort;
            AccessToken = token;
            PrimaryUrl = $"http://192.168.1.55:{Port}/?token={Uri.EscapeDataString(token)}";
            LastError = string.Empty;
            StatusChanged?.Invoke();
            return Task.FromResult(true);
        }

        public Task StopAsync()
        {
            IsRunning = false;
            Port = 0;
            PrimaryUrl = string.Empty;
            StatusChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task RotateTokenAsync(string newToken)
        {
            AccessToken = newToken;
            if (IsRunning)
                PrimaryUrl = $"http://192.168.1.55:{Port}/?token={Uri.EscapeDataString(newToken)}";
            StatusChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void NotifyStateChanged(bool immediate = false) => NotifyCount++;

        public void Dispose() => IsRunning = false;
    }

    [AvaloniaFact]
    public async Task ToggleWebRemote_StartsStopsRotatesToken_AndDispatchesCommandsOnMainThread()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var originalLanguage = AppLanguage.Current;

        var tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-webremote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var audioMock = new Mock<IAudioService>();
        var currentTrack = new Track
        {
            Id = "song-1",
            SourceId = "netease:1",
            Title = "Test Track",
            Artist = "Test Artist",
            Duration = TimeSpan.FromMinutes(3)
        };
        var currentTrackSubj = new BehaviorSubject<Track?>(currentTrack);
        audioMock.SetupGet(x => x.CurrentTrack).Returns(() => currentTrackSubj.Value);
        audioMock.SetupGet(x => x.TrackChanged).Returns(currentTrackSubj);
        audioMock.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
        audioMock.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.PlaybackRecoveryNotices).Returns(Observable.Never<PlaybackRecoveryNotice>());
        audioMock.SetupGet(x => x.TrackMediaInfo).Returns(Observable.Never<TrackMediaInfo>());
        audioMock.SetupGet(x => x.SpectrumData).Returns(Observable.Never<float[]>());
        audioMock.SetupGet(x => x.TtsStateChanged).Returns(Observable.Never<bool>());
        audioMock.SetupGet(x => x.TtsError).Returns(Observable.Never<string>());
        audioMock.SetupGet(x => x.Playlist).Returns(new[] { currentTrack });
        audioMock.SetupGet(x => x.RepeatMode).Returns("radio");
        audioMock.SetupGet(x => x.Duration).Returns(TimeSpan.FromMinutes(3));
        audioMock.SetupProperty(x => x.Volume, 0.8f);

        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.DjName).Returns("Haru");
        djMock.SetupGet(x => x.IsLlmConfigured).Returns(true);
        var chatCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        djMock.Setup(x => x.GenerateChatResponseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => chatCalled.TrySetResult(true))
            .ReturnsAsync("Got it, playing next!");
        djMock.Setup(x => x.GenerateChatResponseAsync(It.IsAny<string>()))
            .Callback(() => chatCalled.TrySetResult(true))
            .ReturnsAsync("Got it, playing next!");

        var llmMock = new Mock<ILLMService>();
        var secureMock = new Mock<ISecureStorage>();
        var searchMock = new Mock<IMusicSearchService>();
        var sttMock = new Mock<ISttService>();
        var webRemote = new StubWebRemoteService();

        var vm = new MainWindowViewModel(
            audioMock.Object,
            djMock.Object,
            llmMock.Object,
            secureMock.Object,
            searchMock.Object,
            sttMock.Object,
            Path.Combine(tempDir, "playlist.json"),
            Path.Combine(tempDir, "settings.json"),
            webRemoteService: webRemote);

        try
        {
            // 默认关闭
            Assert.False(vm.IsWebRemoteRunning);
            Assert.Null(vm.WebRemoteQrImage);
            Assert.Equal(string.Empty, vm.WebRemoteUrl);

            // 开启开关 -> 服务启动、生成二维码并更新 URL
            await vm.ToggleWebRemoteCommand!.Execute();
            Assert.True(vm.IsWebRemoteRunning);
            Assert.True(vm.SettingsVM.WebRemoteEnabled);
            Assert.NotNull(vm.WebRemoteQrImage);
            Assert.Contains("http://192.168.1.55:37260/?token=", vm.WebRemoteUrl);

            var oldToken = webRemote.AccessToken;
            var snap = webRemote.SnapshotProvider!.Invoke();
            Assert.Equal("Test Track", snap.Title);
            Assert.Equal("Test Artist", snap.Artist);
            Assert.True(snap.CanSeek);

            // 刷新令牌 -> 旧令牌替换、二维码链接更新
            await vm.RefreshWebRemoteTokenCommand!.Execute();
            Assert.NotEqual(oldToken, webRemote.AccessToken);
            Assert.Contains(webRemote.AccessToken, vm.WebRemoteUrl);

            // 派发核心遥控命令
            var playRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "toggle_play" });
            Assert.True(playRes.Ok);
            audioMock.Verify(x => x.Play(), Times.Once);

            var nextRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "next" });
            Assert.True(nextRes.Ok);
            audioMock.Verify(x => x.Next(), Times.Once);

            var volRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "set_volume", Value = 0.45 });
            Assert.True(volRes.Ok);
            Assert.Equal(0.45f, audioMock.Object.Volume, 2);

            var seekRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "seek", Value = 42.0 });
            Assert.True(seekRes.Ok);
            audioMock.Verify(x => x.Seek(TimeSpan.FromSeconds(42.0)), Times.Once);

            var focusRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "toggle_focus_mode" });
            Assert.True(focusRes.Ok);
            Assert.True(vm.IsFocusRadioMode);

            var sleepRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "set_sleep_timer", IntValue = 15 });
            Assert.True(sleepRes.Ok);
            Assert.True(vm.IsSleepTimerActive);

            var chatRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "chat", Text = "hello dj" });
            Assert.True(chatRes.Ok);
            await chatCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            djMock.Verify(x => x.GenerateChatResponseAsync("hello dj", It.IsAny<CancellationToken>()), Times.AtLeastOnce());

            // 直播流禁止 Seek
            currentTrackSubj.OnNext(new Track
            {
                Id = "live-1",
                SourceId = "radio-browser:abc",
                Title = "Jazz FM",
                FilePath = "https://stream.example.com/jazz.mp3"
            });
            var liveSeekRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "seek", Value = 10 });
            Assert.False(liveSeekRes.Ok);
            Assert.Equal("live_stream_not_seekable", liveSeekRes.Message);

            // 播客不支持收藏与音乐 Like 反馈
            currentTrackSubj.OnNext(new Track
            {
                Id = "podcast:ep-1",
                SourceId = "podcast:ep-1",
                Title = "Podcast Ep 1",
                FilePath = "https://cdn.example.com/ep1.mp3"
            });
            var podFavRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "toggle_favorite" });
            Assert.False(podFavRes.Ok);
            Assert.Equal("podcast_favorite_unsupported", podFavRes.Message);
            var podLikeRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "feedback", Text = "like" });
            Assert.False(podLikeRes.Ok);
            Assert.Equal("podcast_favorite_unsupported", podLikeRes.Message);

            // 再次点击关闭遥控
            await vm.ToggleWebRemoteCommand!.Execute();
            Assert.False(vm.IsWebRemoteRunning);
            Assert.False(vm.SettingsVM.WebRemoteEnabled);
            Assert.Null(vm.WebRemoteQrImage);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply(originalLanguage);
            RxApp.MainThreadScheduler = originalScheduler;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public void MainWindowAndSettingsView_ExposeWebRemoteControls_WithExpectedBindings()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        var theme = new FluentTheme();
        var app = Application.Current!;
        app.Styles.Add(theme);
        try
        {
            window = new MainWindow { DataContext = vm, Width = 943, Height = 893 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var headerBtn = window.FindControl<Button>("WebRemoteButton");
            Assert.NotNull(headerBtn);

            vm.OpenWebRemoteSettingsCommand!.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSettingsOpen);
            Assert.Equal(3, vm.SettingsVM.TabIndex);

            var settingsView = window.FindDescendantOfType<SettingsView>();
            Assert.NotNull(settingsView);
            var portBox = settingsView!.FindControl<TextBox>("WebRemotePortBox");
            Assert.NotNull(portBox);
            Assert.Equal("37260", portBox!.Text);
            var statusBlock = settingsView.FindControl<TextBlock>("SettingsWebRemoteStatus");
            Assert.NotNull(statusBlock);
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task SettingsViewModel_PersistsWebRemoteConfig_AcrossReload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-wr-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            var store = new Dictionary<string, string>(StringComparer.Ordinal);
            var secure = new Mock<ISecureStorage>();
            secure.Setup(s => s.GetApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync((string key) => store.TryGetValue(key, out var v) ? v : null);
            secure.Setup(s => s.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string key, string val) =>
                {
                    store[key] = val;
                    return Task.CompletedTask;
                });
            using (var s1 = new SettingsViewModel(
                new Mock<ILLMService>().Object,
                secure.Object,
                settingsPath))
            {
                await s1.LoadAsync();
                Assert.False(s1.WebRemoteEnabled);
                Assert.Equal(37260, s1.WebRemotePort);
                Assert.False(string.IsNullOrWhiteSpace(s1.WebRemoteToken));

                s1.WebRemoteEnabled = true;
                s1.WebRemotePortText = "38888";
                s1.WebRemoteToken = "CustomPinnedToken_99";
                await s1.SaveCommand.Execute();
                Assert.Equal(38888, s1.WebRemotePort);
            }

            using (var s2 = new SettingsViewModel(
                new Mock<ILLMService>().Object,
                secure.Object,
                settingsPath))
            {
                await s2.LoadAsync();
                Assert.True(s2.WebRemoteEnabled);
                Assert.Equal(38888, s2.WebRemotePort);
                Assert.Equal("38888", s2.WebRemotePortText);
                Assert.Equal("CustomPinnedToken_99", s2.WebRemoteToken);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task BrowseSections_AndExtendedCommands_WorkEndToEndWithoutPollutingDesktopFilters()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var originalLanguage = AppLanguage.Current;

        var tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-wr-browse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var audioMock = new Mock<IAudioService>();
        var currentTrackSubj = new BehaviorSubject<Track?>(null);
        audioMock.SetupGet(x => x.CurrentTrack).Returns(() => currentTrackSubj.Value);
        audioMock.SetupGet(x => x.TrackChanged).Returns(currentTrackSubj);
        audioMock.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
        audioMock.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.PlaybackRecoveryNotices).Returns(Observable.Never<PlaybackRecoveryNotice>());
        audioMock.SetupGet(x => x.TrackMediaInfo).Returns(Observable.Never<TrackMediaInfo>());
        audioMock.SetupGet(x => x.SpectrumData).Returns(Observable.Never<float[]>());
        audioMock.SetupGet(x => x.TtsStateChanged).Returns(Observable.Never<bool>());
        audioMock.SetupGet(x => x.TtsError).Returns(Observable.Never<string>());
        audioMock.SetupGet(x => x.Playlist).Returns(Array.Empty<Track>());
        audioMock.SetupGet(x => x.RepeatMode).Returns("radio");
        audioMock.SetupProperty(x => x.Volume, 0.8f);

        var progPlayable = new Track
        {
            Id = "prog-1",
            SourceId = "netease:1001",
            Title = "Midnight Drive",
            Artist = "The Midnight",
            FilePath = "https://cdn.example.com/prog1.mp3",
            Duration = TimeSpan.FromMinutes(4)
        };
        var progUnplayable = new Track
        {
            Id = "prog-2",
            SourceId = "netease:1002",
            Title = "Blocked Track",
            Artist = "Restricted",
            FilePath = string.Empty
        };
        var program = new RadioProgram
        {
            Title = "Late Night Mix",
            DjOpening = "Welcome to Late Night Mix",
            Tracks =
            [
                new RecommendedTrack { Track = progPlayable, IsPlayable = true, Tags = ["Synthwave"], Reason = "Mood match" },
                new RecommendedTrack { Track = progUnplayable, IsPlayable = false, Tags = ["Locked"] }
            ]
        };

        var refreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshGate = new TaskCompletionSource<RadioProgram>(TaskCreationOptions.RunContinuationsAsynchronously);
        var programCallCount = 0;
        var recMock = new Mock<IRecommendationService>();
        recMock.SetupGet(x => x.CurrentProgram).Returns(program);
        recMock.SetupGet(x => x.RecentlyPlayed).Returns(Array.Empty<Track>());
        recMock.Setup(x => x.CreateProgramAsync(It.IsAny<RecommendationRequest>()))
            .Returns(() =>
            {
                programCallCount++;
                if (programCallCount == 1)
                    return Task.FromResult(program);
                refreshStarted.TrySetResult(true);
                return refreshGate.Task;
            });

        var jazzStation = new RadioStation
        {
            StationUuid = "jazz-01",
            Name = "Smooth Jazz 247",
            StreamUrl = "https://stream.example.com/jazz.mp3",
            Tags = "jazz,smooth",
            Codec = "MP3",
            Bitrate = 128
        };
        var bbcStation = new RadioStation
        {
            StationUuid = "bbc-01",
            Name = "BBC World",
            StreamUrl = "https://stream.example.com/bbc.mp3",
            Tags = "news",
            Codec = "AAC",
            Bitrate = 64
        };
        var radioMock = new Mock<IRadioBrowserService>();
        radioMock.Setup(x => x.GetStationsByCategoryAsync("jazz", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([jazzStation]);
        radioMock.Setup(x => x.SearchStationsAsync("BBC", It.IsAny<CancellationToken>()))
            .ReturnsAsync([bbcStation]);

        var feed1 = new PodcastFeed { FeedId = "f1", Title = "Tech Talk", RssUrl = "https://example.com/tech.xml" };
        var epSafe = new PodcastEpisode
        {
            EpisodeKey = "ep-safe",
            FeedUrl = feed1.RssUrl,
            FeedTitle = feed1.Title,
            Title = "Episode Safe",
            AudioUrl = "https://cdn.example.com/ep1.mp3",
            Summary = "Detailed episode shownotes for AI digest.",
            Duration = TimeSpan.FromMinutes(25),
            ResumePositionMs = 45_000
        };
        var epUnsafe = new PodcastEpisode
        {
            EpisodeKey = "ep-unsafe",
            FeedUrl = feed1.RssUrl,
            FeedTitle = feed1.Title,
            Title = "Episode Unsafe",
            AudioUrl = "http://127.0.0.1/blocked.mp3",
            Summary = string.Empty,
            Duration = TimeSpan.FromMinutes(10)
        };
        var rssMock = new Mock<IPodcastRssService>();
        rssMock.Setup(x => x.FetchFeedAsync(feed1.RssUrl, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PodcastFeedParseResult(true, feed1, [epSafe, epUnsafe]));

        using var podStorage = new PodcastStorage(Path.Combine(tempDir, "podcasts.json"));
        await podStorage.AddOrUpdateFeedAsync(feed1);

        var digestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var digestGate = new TaskCompletionSource<DJScript>(TaskCreationOptions.RunContinuationsAsynchronously);
        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.DjName).Returns("Haru");
        djMock.Setup(x => x.GeneratePodcastDigestAsync(epSafe, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                digestStarted.TrySetResult(true);
                return digestGate.Task;
            });

        var webRemote = new StubWebRemoteService();
        var vm = new MainWindowViewModel(
            audioMock.Object,
            djMock.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(tempDir, "playlist.json"),
            Path.Combine(tempDir, "settings.json"),
            recommendationService: recMock.Object,
            radioBrowserService: radioMock.Object,
            podcastRssService: rssMock.Object,
            podcastStorage: podStorage,
            webRemoteService: webRemote);

        try
        {
            AppLanguage.Apply("zh");
            await vm.ToggleWebRemoteCommand!.Execute();
            Assert.NotNull(webRemote.BrowseHandler);

            // 初始化播客安全校验桩：允许 https://cdn.example.com，拒绝 127.0.0.1
            vm.PlaylistVM.SetPodcastAudioUriValidatorForTests((uri, _) =>
                Task.FromResult(uri.Host.Equals("cdn.example.com", StringComparison.OrdinalIgnoreCase)));

            // 1. Program 切片与指令
            var progBrowse = await webRemote.BrowseHandler!(new WebRemoteBrowseRequest { Section = "program" }, CancellationToken.None);
            Assert.True(progBrowse.Ok);
            Assert.Equal("今日电台", progBrowse.ProgramTitle);
            Assert.Equal(2, progBrowse.ProgramTracks.Count);

            var playProgOk = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_program_track", Text = "prog-1" });
            Assert.True(playProgOk.Ok);
            audioMock.Verify(x => x.PlayOneShot(progPlayable, It.IsAny<string>()), Times.Once);

            var playProgBlocked = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_program_track", Text = "prog-2" });
            Assert.False(playProgBlocked.Ok);
            Assert.Equal("track_not_playable", playProgBlocked.Message);

            var playProgMissing = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_program_track", Text = "missing" });
            Assert.False(playProgMissing.Ok);
            Assert.Equal("track_not_found", playProgMissing.Message);

            // refresh_program 应非阻塞立即返回 Success
            var refreshTask = webRemote.CommandHandler!(new WebRemoteCommand { Action = "refresh_program" });
            var refreshRes = await refreshTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(refreshRes.Ok);
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            refreshGate.TrySetResult(program);

            // 2. Library 切片：纯内存过滤不污染桌面端 LibraryFilterText
            var lib1 = new Track { Id = "lib-1", SourceId = "local:1", Title = "Alpha Song", Artist = "Artist A", Duration = TimeSpan.FromMinutes(3) };
            var lib2 = new Track { Id = "lib-2", SourceId = "local:2", Title = "Beta Tune", Artist = "Artist B", Duration = TimeSpan.FromMinutes(4) };
            vm.PlaylistVM.Tracks.Add(lib1);
            vm.PlaylistVM.Tracks.Add(lib2);

            var libBrowse = await webRemote.BrowseHandler!(new WebRemoteBrowseRequest { Section = "library", Query = "Beta" }, CancellationToken.None);
            Assert.True(libBrowse.Ok);
            var matchedLib = Assert.Single(libBrowse.LibraryTracks);
            Assert.Equal("lib-2", matchedLib.Id);
            Assert.Equal(1, matchedLib.Index);
            Assert.Equal(string.Empty, vm.PlaylistVM.LibraryFilterText);

            var playLib = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_library_track", Text = "lib-2" });
            Assert.True(playLib.Ok);
            audioMock.Verify(x => x.PlayTrack(lib2), Times.Once);

            var nextLib = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "enqueue_library_track", Text = "lib-1" });
            Assert.True(nextLib.Ok);
            audioMock.Verify(x => x.PlayNextInQueue(lib1), Times.Once);

            var tailLib = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "enqueue_library_track", Text = "lib-1", IntValue = 2 });
            Assert.True(tailLib.Ok);
            audioMock.Verify(x => x.AddToQueue(lib1), Times.Once);

            // 3. Favorites 切片：歌曲/电台隔离与不污染 FavoriteFilterText
            vm.PlaylistVM.ToggleFavoriteCommand.Execute(lib1).Subscribe();
            var favStationTrack = new Track
            {
                Id = "radio-browser:fav-st",
                SourceId = "radio-browser:fav-st",
                Title = "Jazz Favorite FM",
                Artist = "US · jazz",
                FilePath = "https://stream.example.com/favjazz.mp3"
            };
            vm.PlaylistVM.ToggleFavoriteCommand.Execute(favStationTrack).Subscribe();

            var favBrowse = await webRemote.BrowseHandler!(new WebRemoteBrowseRequest { Section = "favorites", Query = "Alpha" }, CancellationToken.None);
            Assert.True(favBrowse.Ok);
            Assert.Single(favBrowse.FavoriteSongs);
            Assert.Empty(favBrowse.FavoriteStations);
            Assert.Equal(string.Empty, vm.PlaylistVM.FavoriteFilterText);

            var favAllBrowse = await webRemote.BrowseHandler!(new WebRemoteBrowseRequest { Section = "favorites" }, CancellationToken.None);
            Assert.Single(favAllBrowse.FavoriteSongs);
            Assert.Single(favAllBrowse.FavoriteStations);

            var playFavRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_favorite", Text = "lib-1" });
            Assert.True(playFavRes.Ok);
            audioMock.Verify(x => x.StartPlaybackContext(
                It.Is<IReadOnlyList<Track>>(l => l.Any(t => t.Id == "lib-1")),
                0,
                false,
                It.IsAny<string>()), Times.Once);

            // 4. Radio 切片：分类切换、搜索及电台收藏/取消收藏闭环
            var radioCatBrowse = await webRemote.BrowseHandler!(new WebRemoteBrowseRequest { Section = "radio", Category = "jazz" }, CancellationToken.None);
            Assert.True(radioCatBrowse.Ok);
            Assert.Equal("jazz", radioCatBrowse.SelectedRadioCategory);
            Assert.Equal("jazz", vm.PlaylistVM.SelectedRadioCategory?.Key);
            Assert.Single(radioCatBrowse.RadioStations);

            var playRadioRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_radio_station", Text = "jazz-01" });
            Assert.True(playRadioRes.Ok);
            audioMock.Verify(x => x.PlayRadioSoundFx(RadioFxKind.TuningSweep), Times.Once);

            // 收藏 -> Favorites 应新增，再调用一次 -> 应完整移除（验证取消收藏修复）
            var favRadioAdd = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "toggle_radio_favorite", Text = "jazz-01" });
            Assert.True(favRadioAdd.Ok);
            Assert.Contains(vm.PlaylistVM.Favorites, t => t.Id == "radio-browser:jazz-01");

            var favRadioRemove = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "toggle_radio_favorite", Text = "jazz-01" });
            Assert.True(favRadioRemove.Ok);
            Assert.DoesNotContain(vm.PlaylistVM.Favorites, t => t.Id == "radio-browser:jazz-01");

            var radioSearchBrowse = await webRemote.BrowseHandler!(new WebRemoteBrowseRequest { Section = "radio", Query = "BBC" }, CancellationToken.None);
            Assert.Single(radioSearchBrowse.RadioStations);
            Assert.Equal("bbc-01", radioSearchBrowse.RadioStations[0].StationUuid);

            var missingStationRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_radio_station", Text = "no-such-uuid" });
            Assert.False(missingStationRes.Ok);
            Assert.Equal("station_not_found", missingStationRes.Message);

            // 5. Podcasts 切片与指令：续播、从头播、URI 安全策略拦截与非阻塞 AI 速读
            var podBrowse = await webRemote.BrowseHandler!(
                new WebRemoteBrowseRequest { Section = "podcasts", Feed = feed1.RssUrl, Query = "Episode Safe" },
                CancellationToken.None);
            Assert.True(podBrowse.Ok);
            Assert.Contains(podBrowse.PodcastFeeds, f => f.RssUrl == feed1.RssUrl);
            Assert.Equal(feed1.RssUrl, podBrowse.SelectedPodcastFeedUrl);
            Assert.Single(podBrowse.PodcastEpisodes);
            Assert.Equal("ep-safe", podBrowse.PodcastEpisodes[0].EpisodeKey);
            Assert.Equal(string.Empty, vm.PlaylistVM.PodcastFilterText);

            var playPodResume = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_podcast_episode", Text = "ep-safe", IntValue = 0 });
            Assert.True(playPodResume.Ok);
            audioMock.Verify(x => x.StartPlaybackContext(
                It.Is<IReadOnlyList<Track>>(l => l.Any(t =>
                    t.ProviderMetadata.ContainsKey("resume_ms") && t.ProviderMetadata["resume_ms"] == "45000")),
                0,
                false,
                It.IsAny<string>()), Times.Once);

            var playPodRestart = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_podcast_episode", Text = "ep-safe", IntValue = 1 });
            Assert.True(playPodRestart.Ok);
            audioMock.Verify(x => x.StartPlaybackContext(
                It.Is<IReadOnlyList<Track>>(l => l.Any(t =>
                    t.ProviderMetadata.ContainsKey("resume_ms") && t.ProviderMetadata["resume_ms"] == "0")),
                0,
                false,
                It.IsAny<string>()), Times.Once);

            var playPodUnsafe = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "play_podcast_episode", Text = "ep-unsafe" });
            Assert.False(playPodUnsafe.Ok);
            Assert.Equal("unsafe_audio_url", playPodUnsafe.Message);

            var emptyShownotesRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "digest_podcast_episode", Text = "ep-unsafe" });
            Assert.False(emptyShownotesRes.Ok);
            Assert.Equal("empty_shownotes", emptyShownotesRes.Message);

            // digest_podcast_episode 必须立即返回（不阻塞等待 LLM/TTS 完成）
            var digestCmdTask = webRemote.CommandHandler!(new WebRemoteCommand { Action = "digest_podcast_episode", Text = "ep-safe" });
            var digestCmdRes = await digestCmdTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(digestCmdRes.Ok);
            await digestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("ep-safe", webRemote.SnapshotProvider!().DigestingEpisodeKey);

            digestGate.TrySetResult(new DJScript { Text = "AI 速读已完成！", Emotion = "calm", Expression = "calm", Motion = "idle" });
            await Task.Delay(50);
            Assert.Contains(vm.ChatVM.Messages, m => m.Content.Contains("AI 速读已完成！"));
            Assert.Equal(string.Empty, webRemote.SnapshotProvider!().DigestingEpisodeKey);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply(originalLanguage);
            RxApp.MainThreadScheduler = originalScheduler;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
