using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class PodcastPlaybackAndDigestTests
{
    private static PodcastEpisode MakeEpisode(string key = "ep-1", long resumeMs = 0, bool isCompleted = false, string summary = "本期聊聊 AI 与开源桌面客户端的架构演进。")
        => new()
        {
            EpisodeKey = key,
            FeedUrl = "https://example.com/rss.xml",
            FeedTitle = "极客电台",
            Title = "第 42 期：本地语音与播客",
            Author = "Host",
            AudioUrl = "https://cdn.example.com/ep42.mp3",
            Duration = TimeSpan.FromMinutes(30),
            PublishedAt = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero),
            Summary = summary,
            Codec = "MP3",
            ResumePositionMs = resumeMs,
            IsCompleted = isCompleted
        };

    [Fact]
    public void TrackMetadata_BuildsPodcastTrack_WithBreakpointAndRecoveryBypass()
    {
        var ep = MakeEpisode(resumeMs: 125_500);
        var track = ep.ToTrack();

        Assert.True(track.IsPodcastEpisode);
        Assert.False(track.IsLiveStream);
        Assert.True(AudioService.TryBuildPodcastStartTimeOption(track, out var resumeMs, out var option));
        Assert.Equal(125_500, resumeMs);
        Assert.Equal(":start-time=125.5", option);

        // 从头收听不应携带 start-time
        var freshTrack = ep.ToTrack(startFromBeginning: true);
        Assert.False(AudioService.TryBuildPodcastStartTimeOption(freshTrack, out _, out _));

        // 播客失败不进入跨源搜歌回退
        Assert.Equal(AudioService.EarlyEndRecoveryAction.RefreshCurrentSource, AudioService.GetRecoveryActionForTrack(track, 0));
        Assert.Equal(AudioService.EarlyEndRecoveryAction.Advance, AudioService.GetRecoveryActionForTrack(track, 1));
    }

    [Fact]
    public async Task PodcastPlayback_BypassesLyricsAndListeningProfile()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var ep = MakeEpisode();
        var track = ep.ToTrack();

        // 1. 歌词服务直接短路 NoLyrics
        var audioMock = new Mock<IAudioService>();
        var trackSubject = new Subject<Track?>();
        audioMock.SetupGet(x => x.TrackChanged).Returns(trackSubject);
        audioMock.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
        var lyricServiceMock = new Mock<ILyricService>();
        using var lyricsVm = new LyricsViewModel(audioMock.Object, lyricServiceMock.Object);
        trackSubject.OnNext(track);
        Assert.False(lyricsVm.HasLyrics);
        Assert.Equal(AppLanguage.T("暂无歌词", "No lyrics available"), lyricsVm.LyricStatusText);
        lyricServiceMock.Verify(x => x.GetLyricsAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()), Times.Never);

        // 2. 听歌画像跳过播客播放事件
        var profileDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profileDir);
        try
        {
            using var profile = new ListeningProfileService(
                new Mock<ILLMService>().Object,
                Path.Combine(profileDir, "profile.json"));
            profile.NotifyPlaybackStarted(track);
            profile.NotifyPositionSampled(track, TimeSpan.FromMinutes(10));
            profile.NotifyPlaybackEndedNaturally(track);
            await Task.Delay(50);
            Assert.Equal(0, profile.GetSnapshot().TotalEventsIngested);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
            try { Directory.Delete(profileDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DjService_PodcastDigest_SupportsLlmAndZeroConfigFallback()
    {
        var originalLanguage = AppLanguage.Current;
        AppLanguage.Apply("zh");
        try
        {
            var ep = MakeEpisode(summary: "第一句讲清楚本地客户端如何安全解析 RSS。第二句展开讲断点续播与 AI 速读的协同设计。");
            var profile = new DJProfile { Name = "小音", Language = "zh" };

            // 1. Prompt 在专注/伴眠下保持 calm 且注入清理后的 Shownotes
            var prompt = DJService.BuildPodcastDigestPrompt(profile, ep, RadioSceneMode.Focus);
            Assert.Contains("极客电台", prompt);
            Assert.Contains("第 42 期：本地语音与播客", prompt);
            Assert.Contains("[calm]", prompt);

            // 2. 无 LLM 兜底直接提炼 Shownotes 且不含“请先配置 AI 服务”
            var fallback = DJService.BuildPodcastDigestFallback(profile, ep, RadioSceneMode.Default);
            Assert.Contains("极客电台", fallback.Text);
            Assert.Contains("第一句讲清楚本地客户端", fallback.Text);
            Assert.DoesNotContain("配置 AI", fallback.Text);

            // 3. 有 LLM 路径
            var llmMock = new Mock<ILLMService>();
            llmMock.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
                .ReturnsAsync("大家好，这期节目聊了播客安全解析和断点续播。 **亮点** 很实用！ [happy]");
            var dj = new DJService(llmMock.Object, new Mock<ITtsService>().Object);
            dj.Initialize(profile);

            var script = await dj.GeneratePodcastDigestAsync(ep);
            Assert.Equal("happy", script.Emotion);
            Assert.DoesNotContain("**", script.Text);
            Assert.DoesNotContain("[happy]", script.Text);
        }
        finally
        {
            AppLanguage.Apply(originalLanguage);
        }
    }

    [AvaloniaFact]
    public void PodcastTab6_InPlaylistDrawer_ListsEpisodesTriggersDigestAndSyncsResume()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        var originalLanguage = AppLanguage.Current;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var tempDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var audioMock = new Mock<IAudioService>();
        var trackEnded = new Subject<Track?>();
        var naturalCompleted = new Subject<Track?>();
        var trackChanged = new Subject<Track?>();
        var stateChanged = new Subject<PlaybackState>();
        var posChanged = new Subject<TimeSpan>();
        audioMock.SetupGet(x => x.TrackEnded).Returns(trackEnded);
        audioMock.SetupGet(x => x.NaturalTrackCompleted).Returns(naturalCompleted);
        audioMock.SetupGet(x => x.TrackChanged).Returns(trackChanged);
        audioMock.SetupGet(x => x.StateChanged).Returns(stateChanged);
        audioMock.SetupGet(x => x.PositionChanged).Returns(posChanged);
        audioMock.SetupGet(x => x.SpectrumData).Returns(Observable.Empty<float[]>());
        audioMock.SetupGet(x => x.TtsStateChanged).Returns(Observable.Empty<bool>());
        audioMock.SetupGet(x => x.TtsError).Returns(Observable.Empty<string>());
        audioMock.SetupGet(x => x.Playlist).Returns(Array.Empty<Track>());
        audioMock.SetupGet(x => x.RepeatMode).Returns("radio");

        var ep = MakeEpisode(resumeMs: 90_000);
        var feed = new PodcastFeed { FeedId = "f1", Title = "测试播客", RssUrl = "https://example.com/rss.xml" };
        var rssMock = new Mock<IPodcastRssService>();
        rssMock.Setup(x => x.FetchFeedAsync("https://example.com/rss.xml", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PodcastFeedParseResult(true, feed, [ep]));

        using var storage = new PodcastStorage(Path.Combine(tempDir, "podcasts.json"));
        storage.AddOrUpdateFeedAsync(feed).GetAwaiter().GetResult();

        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.DjName).Returns("小音");
        djMock.Setup(x => x.GeneratePodcastDigestAsync(ep, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DJScript { Text = "这一期核心看点已提炼完毕。", Emotion = "calm", Expression = "calm", Motion = "idle" });

        var vm = new MainWindowViewModel(
            audioMock.Object,
            djMock.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(tempDir, "playlist.json"),
            Path.Combine(tempDir, "settings.json"),
            podcastRssService: rssMock.Object,
            podcastStorage: storage);

        var theme = new FluentTheme();
        Application.Current!.Styles.Add(theme);
        Window? window = null;
        try
        {
            AppLanguage.Apply("zh");
            window = new MainWindow { DataContext = vm, Width = 943, Height = 893 };
            var strings = new ResourceDictionary();
            foreach (var pair in AppLanguage.GetStringTable("zh"))
                strings[pair.Key] = pair.Value;
            window.Resources.MergedDictionaries.Add(strings);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.PlaylistVM.SetPodcastAudioUriValidatorForTests((_, _) => Task.FromResult(true));

            // 打开 Tab 6 播客页
            vm.OpenPodcastsCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsLibraryOpen);
            Assert.Equal(6, vm.PlaylistVM.TabIndex);
            Assert.True(vm.PlaylistVM.HasPodcastEpisodes);
            Assert.Single(vm.PlaylistVM.PodcastEpisodes);

            // 点击“AI 速读” -> 写入聊天区气泡
            vm.PlaylistVM.DigestPodcastEpisodeCommand.Execute(ep).Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(vm.ChatVM.Messages, m => m.Content.Contains("这一期核心看点已提炼完毕。"));

            // 点击“收听” -> 触发 StartPlaybackContext 且非直播流
            vm.PlaylistVM.PlayPodcastEpisodeCommand.Execute(ep).Subscribe();
            audioMock.Verify(x => x.StartPlaybackContext(
                It.Is<IEnumerable<Track>>(list => list.First().IsPodcastEpisode && !list.First().IsLiveStream),
                0,
                false,
                It.IsAny<string>()), Times.Once);

            // 模拟播放进度推进与播完归零
            var playingTrack = ep.ToTrack();
            audioMock.SetupGet(x => x.CurrentTrack).Returns(playingTrack);
            trackChanged.OnNext(playingTrack);
            posChanged.OnNext(TimeSpan.FromMinutes(12));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TimeSpan.FromMinutes(12).TotalMilliseconds, ep.ResumePositionMs);
            Assert.Equal(((long)TimeSpan.FromMinutes(12).TotalMilliseconds).ToString(), playingTrack.ProviderMetadata["resume_ms"]);

            // 通过 NaturalTrackCompleted（覆盖多集队列切换时未触发 TrackEnded 的场景）标记已播完
            naturalCompleted.OnNext(playingTrack);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ep.IsCompleted);
            Assert.Equal(0, ep.ResumePositionMs);
            Assert.Equal("0", playingTrack.ProviderMetadata["resume_ms"]);
        }
        finally
        {
            window?.Close();
            Application.Current!.Styles.Remove(theme);
            vm.Dispose();
            AppLanguage.Apply(originalLanguage);
            RxApp.MainThreadScheduler = originalScheduler;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PlaybackPreflightAndPlayPodcast_ValidatePodcastUrls_WithoutBroker()
    {
        var broker = new Mock<IMusicSourceBroker>();
        var audioMock = new Mock<IAudioService>();
        audioMock.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
        audioMock.SetupGet(x => x.TrackChanged).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.Playlist).Returns(Array.Empty<Track>());

        var publicEp = new PodcastEpisode
        {
            EpisodeKey = "pub-1",
            FeedUrl = "https://93.184.216.34/rss.xml",
            Title = "Public Ep",
            AudioUrl = "https://93.184.216.34/audio.mp3"
        };
        var evilEp = new PodcastEpisode
        {
            EpisodeKey = "evil-1",
            FeedUrl = "https://93.184.216.34/rss.xml",
            Title = "Loopback Ep",
            AudioUrl = "http://127.0.0.1:8080/internal.mp3"
        };

        var current = new Track { Id = "cur", Title = "Cur" };
        var publicTrack = publicEp.ToTrack();
        var evilTrack = evilEp.ToTrack();

        audioMock.SetupGet(x => x.CurrentTrack).Returns(current);
        audioMock.SetupGet(x => x.PlaybackQueue).Returns(new[] { current, publicTrack, evilTrack });

        using var preflight = new PlaybackPreflightService(audioMock.Object, broker.Object);
        await preflight.RunAsync();

        Assert.Equal(TrackPlayability.Playable, publicTrack.Playability);
        Assert.Equal(TrackPlayability.Failed, evilTrack.Playability);
        Assert.Equal(PlaybackFailureKind.SourceUnavailable, evilTrack.PlaybackFailure);
        broker.Verify(x => x.ResolveTrackDetailedAsync(
            It.IsAny<OnlineTrack>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        // 点击播放回环地址单集时应直接拦截，不得调用 StartPlaybackContext
        var tempPlaylistFile = Path.Combine(Path.GetTempPath(), $"tuanradio-preflight-{Guid.NewGuid():N}.json");
        using var playlistVm = new PlaylistViewModel(audioMock.Object, broker.Object, playlistFile: tempPlaylistFile);
        await playlistVm.PlayPodcastEpisodeAsync(evilEp);
        audioMock.Verify(x => x.StartPlaybackContext(
            It.IsAny<IEnumerable<Track>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never);

        // 播放合法单集时，同列表内不安全 URL 单集应从连播上下文过滤掉
        playlistVm.PodcastEpisodes.Add(publicEp);
        playlistVm.PodcastEpisodes.Add(evilEp);
        await playlistVm.PlayPodcastEpisodeAsync(publicEp);
        audioMock.Verify(x => x.StartPlaybackContext(
            It.Is<IEnumerable<Track>>(q => q.Count() == 1 && q.First().Title == "Public Ep"),
            0,
            false,
            It.IsAny<string>()), Times.Once);
        try { File.Delete(tempPlaylistFile); } catch { }
    }

    [Fact]
    public async Task PodcastFavorite_IsIsolatedFromSongPlaylistAndProfile_AndRetryResolvesAudioUrl()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var tempDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var ep = new PodcastEpisode
        {
            EpisodeKey = "pod-retry-1",
            FeedUrl = "https://93.184.216.34/rss.xml",
            FeedTitle = "Tech Cast",
            Title = "Episode Retry",
            AudioUrl = "https://93.184.216.34/audio.mp3"
        };
        var track = ep.ToTrack();

        var audioMock = new Mock<IAudioService>();
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.TrackChanged).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
        audioMock.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
        audioMock.SetupGet(x => x.SpectrumData).Returns(Observable.Never<float[]>());
        audioMock.SetupGet(x => x.TtsStateChanged).Returns(Observable.Never<bool>());
        audioMock.SetupGet(x => x.TtsError).Returns(Observable.Never<string>());
        audioMock.SetupGet(x => x.Playlist).Returns(Array.Empty<Track>());
        audioMock.SetupGet(x => x.CurrentTrack).Returns(track);

        var brokerMock = new Mock<IMusicSourceBroker>();
        brokerMock.Setup(x => x.GetProviderDescriptors()).Returns(Array.Empty<MusicProviderDescriptor>());
        var recMock = new Mock<IRecommendationService>();

        var vm = new MainWindowViewModel(
            audioMock.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            brokerMock.Object,
            new Mock<ISttService>().Object,
            Path.Combine(tempDir, "playlist.json"),
            Path.Combine(tempDir, "settings.json"),
            recommendationService: recMock.Object);

        try
        {
            // 1. 红心收藏不将播客塞入主歌单/收藏/音乐反馈
            vm.ToggleCurrentFavoriteCommand.Execute().Subscribe();
            vm.PlaylistVM.ToggleFavoriteCommand.Execute(track).Subscribe();
            Assert.Empty(vm.PlaylistVM.Tracks);
            Assert.Empty(vm.PlaylistVM.Favorites);
            Assert.False(vm.IsCurrentFavorite);
            recMock.Verify(x => x.RecordFeedback(It.IsAny<UserMusicFeedback>()), Times.Never);

            // 2. 播客断流重试直接解析 audio_url，不走音乐源 Broker
            var resolved = await vm.ResolveTrackUrlAsync(track, requireAlternative: false, CancellationToken.None);
            Assert.NotNull(resolved);
            Assert.Equal("https://93.184.216.34/audio.mp3", resolved!.Url);
            Assert.Null(await vm.ResolveTrackUrlAsync(track, requireAlternative: true, CancellationToken.None));
            brokerMock.Verify(x => x.ResolveTrackAsync(It.IsAny<OnlineTrack>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
