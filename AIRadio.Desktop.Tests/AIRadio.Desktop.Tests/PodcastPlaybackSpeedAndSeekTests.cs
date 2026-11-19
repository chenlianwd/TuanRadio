using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.WebRemote;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class PodcastPlaybackSpeedAndSeekTests
{
    [Theory]
    [InlineData(float.NaN, 1.0f)]
    [InlineData(float.PositiveInfinity, 1.0f)]
    [InlineData(float.NegativeInfinity, 1.0f)]
    [InlineData(0f, 1.0f)]
    [InlineData(-1.5f, 1.0f)]
    [InlineData(0.2f, 0.5f)]
    [InlineData(0.75f, 0.75f)]
    [InlineData(1.25f, 1.25f)]
    [InlineData(1.5f, 1.5f)]
    [InlineData(2.0f, 2.0f)]
    [InlineData(4.0f, 2.5f)]
    public void NormalizePlaybackRate_ClampsAndSanitizes(float input, float expected)
    {
        Assert.Equal(expected, AudioService.NormalizePlaybackRate(input));
    }

    [Fact]
    public void ComputeRelativeSeekTarget_HandlesNormalAndBoundaryClamping()
    {
        var duration = TimeSpan.FromMinutes(30);

        // 正常快退与快进
        Assert.Equal(TimeSpan.FromSeconds(45),
            AudioService.ComputeRelativeSeekTarget(TimeSpan.FromSeconds(60), duration, TimeSpan.FromSeconds(-15)));
        Assert.Equal(TimeSpan.FromSeconds(90),
            AudioService.ComputeRelativeSeekTarget(TimeSpan.FromSeconds(60), duration, TimeSpan.FromSeconds(30)));

        // 越过 0:00 下限夹逼
        Assert.Equal(TimeSpan.Zero,
            AudioService.ComputeRelativeSeekTarget(TimeSpan.FromSeconds(8), duration, TimeSpan.FromSeconds(-15)));

        // 接近尾部留 1 秒防误触完播跳集
        Assert.Equal(duration - TimeSpan.FromSeconds(1),
            AudioService.ComputeRelativeSeekTarget(duration - TimeSpan.FromSeconds(10), duration, TimeSpan.FromSeconds(30)));

        // 未知总时长时允许顺推且仍保下限
        Assert.Equal(TimeSpan.FromSeconds(40),
            AudioService.ComputeRelativeSeekTarget(TimeSpan.FromSeconds(10), TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero,
            AudioService.ComputeRelativeSeekTarget(TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(-15)));
    }

    [Fact]
    public void AudioService_SetPlaybackRate_WhenNotPodcast_StoresPreferenceWithoutAlteringEffectiveRate()
    {
        using var audio = new AudioService();

        audio.SetPlaybackRate(1.5f);

        Assert.Equal(1.5f, audio.PodcastPlaybackRate);
        Assert.Equal(1.0f, audio.PlaybackRate);
    }

    [Fact]
    public void AudioService_SeekRelative_WhenPaused_UpdatesResumeAndEmitsPositionChanged()
    {
        using var audio = new AudioService();
        var podcast = CreatePodcastTrack();
        audio.LoadTracks(new[] { podcast });

        var currentIndexField = typeof(AudioService).GetField("_currentIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var currentStateField = typeof(AudioService).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var lastPosField = typeof(AudioService).GetField("_lastPositionMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        currentIndexField.SetValue(audio, 0);
        currentStateField.SetValue(audio, PlaybackState.Paused);
        lastPosField.SetValue(audio, 60_000L);

        var emitted = new List<TimeSpan>();
        using var sub = audio.PositionChanged.Subscribe(emitted.Add);

        audio.SeekRelative(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(90), audio.CurrentPosition);
        Assert.Equal(new[] { TimeSpan.FromSeconds(90) }, emitted);
        Assert.Equal("90000", podcast.ProviderMetadata["resume_ms"]);

        audio.SeekRelative(TimeSpan.FromSeconds(-15));

        Assert.Equal(TimeSpan.FromSeconds(75), audio.CurrentPosition);
        Assert.Equal(new[] { TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(75) }, emitted);
        Assert.Equal("75000", podcast.ProviderMetadata["resume_ms"]);
    }

    [Fact]
    public void PlayerViewModel_CyclePlaybackRateAndSkip_TracksPodcastStateAndResetsOnMusic()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var trackChanged = new Subject<Track?>();
            var stateChanged = new Subject<PlaybackState>();
            var posChanged = new Subject<TimeSpan>();
            var rateChanged = new Subject<float>();

            Track? currentTrack = null;
            float podcastRate = 1.0f;
            float effectiveRate = 1.0f;

            var audio = new Mock<IAudioService>();
            audio.Setup(x => x.TrackChanged).Returns(trackChanged);
            audio.Setup(x => x.StateChanged).Returns(stateChanged);
            audio.Setup(x => x.PositionChanged).Returns(posChanged);
            audio.Setup(x => x.PlaybackRateChanged).Returns(rateChanged);
            audio.SetupGet(x => x.CurrentTrack).Returns(() => currentTrack);
            audio.SetupGet(x => x.PodcastPlaybackRate).Returns(() => podcastRate);
            audio.SetupGet(x => x.PlaybackRate).Returns(() => effectiveRate);
            audio.Setup(x => x.SetPlaybackRate(It.IsAny<float>()))
                .Callback<float>(r =>
                {
                    podcastRate = AudioService.NormalizePlaybackRate(r);
                    effectiveRate = currentTrack?.IsPodcastEpisode == true ? podcastRate : 1.0f;
                    rateChanged.OnNext(effectiveRate);
                });

            using var vm = new PlayerViewModel(audio.Object);
            var persistedRates = new List<float>();
            vm.PodcastPlaybackRateChanged += r => persistedRates.Add(r);

            // 1. 切入播客单集
            var podcast = CreatePodcastTrack();
            currentTrack = podcast;
            trackChanged.OnNext(podcast);

            Assert.True(vm.IsPodcast);
            Assert.Equal("1.0x", vm.PlaybackRateText);
            Assert.False(vm.IsNonDefaultPlaybackRate);

            // 2. 完整循环 6 档倍速：1.0 -> 1.25 -> 1.5 -> 1.75 -> 2.0 -> 0.75 -> 1.0
            var expectedTexts = new[] { "1.25x", "1.5x", "1.75x", "2.0x", "0.75x", "1.0x" };
            foreach (var expected in expectedTexts)
            {
                vm.CyclePlaybackRateCommand.Execute().Subscribe();
                Assert.Equal(expected, vm.PlaybackRateText);
            }
            Assert.Equal(new[] { 1.25f, 1.5f, 1.75f, 2.0f, 0.75f, 1.0f }, persistedRates);

            // 3. 设成 1.5x，验证快退 -15s 与快进 +30s
            vm.SetPlaybackRate(1.5f);
            Assert.Equal("1.5x", vm.PlaybackRateText);
            Assert.True(vm.IsNonDefaultPlaybackRate);

            vm.SkipBackwardCommand.Execute().Subscribe();
            vm.SkipForwardCommand.Execute().Subscribe();
            audio.Verify(x => x.SeekRelative(TimeSpan.FromSeconds(-15)), Times.Once);
            audio.Verify(x => x.SeekRelative(TimeSpan.FromSeconds(30)), Times.Once);

            // 4. 切回普通音乐：播客控制隐藏且 UI 倍速归 1.0x
            var song = new Track { Id = "netease:1", SourceId = "netease:1", Title = "晴天", Artist = "周杰伦", Duration = TimeSpan.FromMinutes(4) };
            currentTrack = song;
            effectiveRate = 1.0f;
            trackChanged.OnNext(song);

            Assert.False(vm.IsPodcast);
            Assert.Equal(1.0f, vm.PlaybackRate);
            Assert.Equal("1.0x", vm.PlaybackRateText);
            Assert.False(vm.IsNonDefaultPlaybackRate);

            // 5. 切回直播电台：快进快退严格短路
            var live = new Track { Id = "radio-browser:1", SourceId = "radio-browser:1", Title = "LoFi Radio" };
            currentTrack = live;
            trackChanged.OnNext(live);
            Assert.True(vm.IsLiveStream);

            vm.SkipBackwardCommand.Execute().Subscribe();
            vm.SkipForwardCommand.Execute().Subscribe();
            audio.Verify(x => x.SeekRelative(It.IsAny<TimeSpan>()), Times.Exactly(2));
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void PlayerDeck_SwapsFeedbackRowWithPodcastControlRow_OnPodcastTrack()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, audio, trackChanged, _, dir) = CreateHostViewModel();
        Window? window = null;
        try
        {
            var deck = new PlayerDeck { DataContext = vm };
            window = new Window { Width = 920, Height = 200, Content = deck };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var feedbackRow = deck.FindControl<StackPanel>("FeedbackRow")!;
            var podcastRow = deck.FindControl<StackPanel>("PodcastControlRow")!;
            var speedBtn = deck.FindControl<Button>("PodcastSpeedButton")!;
            var rewindBtn = deck.FindControl<Button>("PodcastRewindButton")!;
            var forwardBtn = deck.FindControl<Button>("PodcastForwardButton")!;

            // 默认歌曲/空闲态：显示歌曲反馈行、隐藏播客控制条
            Assert.True(feedbackRow.IsVisible);
            Assert.False(podcastRow.IsVisible);

            // 切入播客单集：同位互斥显示播客控制条
            var podcast = CreatePodcastTrack();
            audio.SetupGet(x => x.CurrentTrack).Returns(podcast);
            trackChanged.OnNext(podcast);
            Dispatcher.UIThread.RunJobs();

            Assert.False(feedbackRow.IsVisible);
            Assert.True(podcastRow.IsVisible);
            Assert.Equal("1.0x", speedBtn.Content);
            Assert.DoesNotContain("active", speedBtn.Classes);

            // 驱动倍速与快退/快进绑定命令
            speedBtn.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1.25x", speedBtn.Content);
            Assert.Contains("active", speedBtn.Classes);

            rewindBtn.Command!.Execute(null);
            forwardBtn.Command!.Execute(null);
            audio.Verify(x => x.SeekRelative(TimeSpan.FromSeconds(-15)), Times.Once);
            audio.Verify(x => x.SeekRelative(TimeSpan.FromSeconds(30)), Times.Once);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task WebRemote_PodcastSpeedAndRelativeSeek_CommandsAndGuards()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, audio, trackChanged, webRemote, dir) = CreateHostViewModel();
        try
        {
            await webRemote.StartAsync(37260, "test-token");

            // 1. 歌曲态调用倍速指令被拒
            var song = new Track { Id = "netease:1", SourceId = "netease:1", Title = "晴天", Artist = "周杰伦", Duration = TimeSpan.FromMinutes(4) };
            audio.SetupGet(x => x.CurrentTrack).Returns(song);
            trackChanged.OnNext(song);

            var nonPodRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "cycle_playback_rate" });
            Assert.False(nonPodRes.Ok);
            Assert.Equal("podcast_only_action", nonPodRes.Message);

            // 2. 直播态调用相对跳转被拒
            var live = new Track { Id = "radio-browser:1", SourceId = "radio-browser:1", Title = "FM" };
            audio.SetupGet(x => x.CurrentTrack).Returns(live);
            trackChanged.OnNext(live);

            var liveSeekRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "seek_relative", Value = 30 });
            Assert.False(liveSeekRes.Ok);
            Assert.Equal("live_stream_not_seekable", liveSeekRes.Message);

            // 3. 播客态允许倍速切换、显式倍速与相对快退/快进（含 ±300s 夹逼）
            var podcast = CreatePodcastTrack();
            audio.SetupGet(x => x.CurrentTrack).Returns(podcast);
            trackChanged.OnNext(podcast);

            var setRateRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "set_playback_rate", Value = 1.5 });
            Assert.True(setRateRes.Ok);
            Assert.Equal(1.5f, vm.PlayerVM.PlaybackRate);
            Assert.Equal(1.5f, webRemote.SnapshotProvider!().PlaybackRate);

            var cycleRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "cycle_playback_rate" });
            Assert.True(cycleRes.Ok);
            Assert.Equal(1.75f, vm.PlayerVM.PlaybackRate);
            Assert.Equal(1.75f, webRemote.SnapshotProvider!().PlaybackRate);

            var backRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "seek_relative", Value = -15 });
            var clampedFwdRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "seek_relative", Value = 999 });
            Assert.True(backRes.Ok);
            Assert.True(clampedFwdRes.Ok);
            audio.Verify(x => x.SeekRelative(TimeSpan.FromSeconds(-15)), Times.Once);
            audio.Verify(x => x.SeekRelative(TimeSpan.FromSeconds(300)), Times.Once);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task Settings_PodcastPlaybackRate_PersistsAndRestoresAcrossReload()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settingsPath = Path.Combine(dir, "settings.json");

        try
        {
            var settings1 = CreateSettingsViewModel(settingsPath);
            settings1.PodcastPlaybackRate = 1.5f;
            await settings1.SaveUiStateCommand.Execute().FirstAsync();

            var settings2 = CreateSettingsViewModel(settingsPath);
            await settings2.LoadAsync();

            Assert.Equal(1.5f, settings2.PodcastPlaybackRate);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
            TryDeleteDir(dir);
        }
    }

    private static SettingsViewModel CreateSettingsViewModel(string settingsPath)
    {
        var llm = new Mock<ILLMService>();
        var secure = new Mock<ISecureStorage>();
        return new SettingsViewModel(llm.Object, secure.Object, settingsPath);
    }

    private static Track CreatePodcastTrack() => new()
    {
        Id = "podcast:ep-1",
        SourceId = "podcast:ep-1",
        Title = "EP 42: 播客技术漫谈",
        Artist = "声动早咖啡",
        Duration = TimeSpan.FromMinutes(40),
        FilePath = "https://203.0.113.20/ep42.mp3"
    };

    private static (MainWindowViewModel Vm, Mock<IAudioService> Audio, Subject<Track?> TrackChanged, StubWebRemoteService WebRemote, string Dir)
        CreateHostViewModel()
    {
        var trackChanged = new Subject<Track?>();
        var stateChanged = new Subject<PlaybackState>();
        var posChanged = new Subject<TimeSpan>();
        var rateChanged = new Subject<float>();

        float podcastRate = 1.0f;
        float effectiveRate = 1.0f;
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(trackChanged);
        audio.Setup(x => x.StateChanged).Returns(stateChanged);
        audio.Setup(x => x.PositionChanged).Returns(posChanged);
        audio.Setup(x => x.PlaybackRateChanged).Returns(rateChanged);
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => new List<Track>().AsReadOnly());
        audio.SetupGet(x => x.Volume).Returns(0.8f);
        audio.SetupGet(x => x.RepeatMode).Returns("radio");
        audio.SetupGet(x => x.PodcastPlaybackRate).Returns(() => podcastRate);
        audio.SetupGet(x => x.PlaybackRate).Returns(() => effectiveRate);
        audio.Setup(x => x.SetPlaybackRate(It.IsAny<float>()))
            .Callback<float>(r =>
            {
                podcastRate = AudioService.NormalizePlaybackRate(r);
                effectiveRate = audio.Object.CurrentTrack?.IsPodcastEpisode == true ? podcastRate : 1.0f;
                rateChanged.OnNext(effectiveRate);
            });

        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dj = new Mock<IDJService>();
        var rec = new Mock<IRecommendationService>();
        rec.Setup(x => x.FeedbackHistory).Returns(new List<UserMusicFeedback>());
        rec.Setup(x => x.RecentlyPlayed).Returns(new List<Track>());
        var webRemote = new StubWebRemoteService();

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            settingsFile: Path.Combine(dir, "settings.json"),
            recommendationService: rec.Object,
            webRemoteService: webRemote);
        return (vm, audio, trackChanged, webRemote, dir);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private sealed class StubWebRemoteService : IWebRemoteService
    {
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public int ConnectedClientCount { get; set; }
        public string PrimaryUrl { get; private set; } = string.Empty;
        public IReadOnlyList<string> AccessUrls => string.IsNullOrEmpty(PrimaryUrl) ? Array.Empty<string>() : [PrimaryUrl];
        public string AccessToken { get; private set; } = string.Empty;
        public string LastError { get; private set; } = string.Empty;
        public Func<WebRemoteSnapshot>? SnapshotProvider { get; private set; }
        public Func<WebRemoteCommand, Task<WebRemoteCommandResult>>? CommandHandler { get; private set; }
        public event Action? StatusChanged;

        public void ConfigureHandlers(
            Func<WebRemoteSnapshot> snapshotProvider,
            Func<WebRemoteCommand, Task<WebRemoteCommandResult>> commandHandler)
        {
            SnapshotProvider = snapshotProvider;
            CommandHandler = commandHandler;
        }

        public Task<bool> StartAsync(int preferredPort, string token, IPAddress? bindAddress = null, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            Port = preferredPort;
            AccessToken = token;
            PrimaryUrl = $"http://192.168.1.10:{Port}/?token={token}";
            StatusChanged?.Invoke();
            return Task.FromResult(true);
        }

        public Task StopAsync()
        {
            IsRunning = false;
            StatusChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task RotateTokenAsync(string newToken)
        {
            AccessToken = newToken;
            StatusChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void NotifyStateChanged(bool immediate = false) { }
        public void Dispose() { }
    }
}
