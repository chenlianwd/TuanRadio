using System;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class RepeatModeRemediationTests
{
    [Fact]
    public async Task NoneMode_CanonicalPlaylist_AdvancesSequentiallyAndStopsAtEnd()
    {
        using var svc = new AudioService();
        svc.LoadTracks(new[]
        {
            new Track { SourceId = "local:1", Title = "One", FilePath = "http://example.com/1.mp3" },
            new Track { SourceId = "local:2", Title = "Two", FilePath = "http://example.com/2.mp3" },
            new Track { SourceId = "local:3", Title = "Three", FilePath = "http://example.com/3.mp3" }
        });
        svc.SetRepeatMode("none");

        Assert.Equal("One", svc.CurrentTrack?.Title);

        // 手动 Next 在关闭循环模式下顺序前进，到达末尾后不再绕回第一首
        svc.Next();
        await Task.Delay(150);
        Assert.Equal("Two", svc.CurrentTrack?.Title);

        svc.Next();
        await Task.Delay(150);
        Assert.Equal("Three", svc.CurrentTrack?.Title);

        svc.Next();
        await Task.Delay(150);
        Assert.Equal("Three", svc.CurrentTrack?.Title);
    }

    [Fact]
    public void NoneMode_TryPlayNextCanonicalTrack_DoesNotWrapAtEnd()
    {
        using var svc = new AudioService();
        svc.LoadTracks(new[]
        {
            new Track { SourceId = "local:1", Title = "One", FilePath = "http://example.com/1.mp3" },
            new Track { SourceId = "local:2", Title = "Two", FilePath = "http://example.com/2.mp3" }
        });

        var method = typeof(AudioService).GetMethod(
            "TryPlayNextCanonicalTrack",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 从索引 0 推进到索引 1
        var advanced1 = (bool)method.Invoke(svc, new object[] { false })!;
        Assert.True(advanced1);
        Assert.Equal("Two", svc.CurrentTrack?.Title);

        // 已在末尾：不回绕，返回 false
        var advanced2 = (bool)method.Invoke(svc, new object[] { false })!;
        Assert.False(advanced2);
        Assert.Equal("Two", svc.CurrentTrack?.Title);
    }

    [Fact]
    public async Task SingleAndNoneMode_ManualNextAtContextEnd_DoesNotJumpIntoCanonicalPlaylist()
    {
        foreach (var mode in new[] { "single", "none" })
        {
            using var svc = new AudioService();
            svc.LoadTracks(new[]
            {
                new Track { SourceId = "local:lib", Title = "Library", FilePath = "http://example.com/lib.mp3" }
            });
            svc.SetRepeatMode(mode);
            svc.StartPlaybackContext(new[]
            {
                new Track { SourceId = "kugou:a", Title = "A", FilePath = "http://example.com/a.mp3" },
                new Track { SourceId = "kugou:b", Title = "B", FilePath = "http://example.com/b.mp3" }
            }, startIndex: 1, contextName: "歌单");

            // 连续点击两次 Next，都不应清空上下文或突然跳进主歌单
            svc.Next();
            await Task.Delay(150);
            Assert.Equal("B", svc.CurrentTrack?.Title);
            Assert.Equal("歌单", svc.PlaybackContextName);

            svc.Next();
            await Task.Delay(150);
            Assert.Equal("B", svc.CurrentTrack?.Title);
            Assert.Equal("歌单", svc.PlaybackContextName);
        }
    }

    [Fact]
    public void SingleLoop_PodcastTrack_ClearsResumeMsBeforeReplay()
    {
        using var svc = new AudioService();
        var episode = new Track
        {
            SourceId = "podcast:ep1",
            Title = "Ep1",
            FilePath = "http://example.com/ep1.mp3",
            Duration = TimeSpan.FromMinutes(30)
        };
        episode.ProviderMetadata["is_podcast"] = "true";
        episode.ProviderMetadata["resume_ms"] = "1200000";

        svc.LoadTracks(new[] { episode });
        svc.SetRepeatMode("single");

        var method = typeof(AudioService).GetMethod(
            "TryReplayCurrentTrackForSingleLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var replayed = (bool)method.Invoke(svc, new object?[] { episode })!;
        Assert.True(replayed);
        Assert.False(episode.ProviderMetadata.ContainsKey("resume_ms"));
    }

    [Fact]
    public void Shuffle_CanonicalListLoop_NeverPicksSameTrackWhenMultipleExist()
    {
        using var svc = new AudioService();
        svc.LoadTracks(new[]
        {
            new Track { SourceId = "local:1", Title = "One", FilePath = "http://example.com/1.mp3" },
            new Track { SourceId = "local:2", Title = "Two", FilePath = "http://example.com/2.mp3" }
        });
        svc.SetRepeatMode("list");
        svc.SetShuffle(true);

        var method = typeof(AudioService).GetMethod(
            "TryPlayNextCanonicalTrack",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var previous = svc.CurrentTrack?.Title;
        for (var i = 0; i < 20; i++)
        {
            var advanced = (bool)method.Invoke(svc, new object[] { true })!;
            Assert.True(advanced);
            var current = svc.CurrentTrack?.Title;
            Assert.NotEqual(previous, current);
            previous = current;
        }
    }

    [Fact]
    public void Shuffle_PlaybackContextListLoop_NeverPicksSameTrackWhenMultipleExist()
    {
        using var svc = new AudioService();
        svc.SetRepeatMode("list");
        svc.SetShuffle(true);
        svc.StartPlaybackContext(new[]
        {
            new Track { SourceId = "kugou:a", Title = "A", FilePath = "http://example.com/a.mp3" },
            new Track { SourceId = "kugou:b", Title = "B", FilePath = "http://example.com/b.mp3" }
        }, startIndex: 0, contextName: "收藏");

        var method = typeof(AudioService).GetMethod(
            "TryAdvancePlaybackContext",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(bool), typeof(bool).MakeByRefType() },
            modifiers: null)!;

        var previous = svc.CurrentTrack?.Title;
        for (var i = 0; i < 20; i++)
        {
            var args = new object[] { true, false };
            var advanced = (bool)method.Invoke(svc, args)!;
            Assert.True(advanced);
            var current = svc.CurrentTrack?.Title;
            Assert.NotEqual(previous, current);
            previous = current;
        }
    }

    [Fact]
    public void SleepTimer_EndOfTrack_ConsumesOnNaturalTrackCompletedEvenWithoutTrackEnded()
    {
        var trackEnded = new Subject<Track?>();
        var naturalCompleted = new Subject<Track?>();
        var trackChanged = new Subject<Track?>();
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.TrackEnded).Returns(trackEnded);
        audio.SetupGet(x => x.NaturalTrackCompleted).Returns(naturalCompleted);
        audio.SetupGet(x => x.TrackChanged).Returns(trackChanged);
        audio.SetupGet(x => x.CurrentTrack).Returns(new Track { Title = "Song" });

        using var timer = new SleepTimerService(audio.Object, CurrentThreadScheduler.Instance, CurrentThreadScheduler.Instance);
        timer.StartEndOfTrackTimer();
        Assert.Equal(SleepTimerMode.EndOfTrack, timer.Mode);

        // 单曲循环/列表循环/歌单上下文自然播完只发 NaturalTrackCompleted，不发 TrackEnded
        naturalCompleted.OnNext(new Track { Title = "Song" });

        Assert.Equal(SleepTimerMode.Disabled, timer.Mode);
        audio.Verify(x => x.Pause(), Times.Once);
    }

    [Fact]
    public void PlayerViewModel_RepeatVisualStatesAndEvents_UpdateCorrectly()
    {
        AppLanguage.Apply("zh");
        var mode = "radio";
        var shuffled = false;
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.RepeatMode).Returns(() => mode);
        audio.SetupGet(x => x.IsShuffled).Returns(() => shuffled);
        audio.Setup(x => x.SetRepeatMode(It.IsAny<string>())).Callback<string>(m => mode = m);
        audio.Setup(x => x.Shuffle()).Callback(() => shuffled = !shuffled);
        audio.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());

        using var vm = new PlayerViewModel(audio.Object);
        string? lastModeEvent = null;
        bool? lastShuffleEvent = null;
        vm.RepeatModeChanged += m => lastModeEvent = m;
        vm.ShuffleChanged += s => lastShuffleEvent = s;

        Assert.True(vm.IsRepeatActive);
        Assert.False(vm.IsRepeatOff);
        Assert.False(vm.IsRepeatSingle);
        Assert.Equal(1.0, vm.RepeatButtonOpacity);
        Assert.Equal("随机播放：关", vm.ShuffleTip);

        vm.ToggleShuffleCommand.Execute().Subscribe();
        Assert.True(vm.IsShuffled);
        Assert.True(lastShuffleEvent);
        Assert.Equal("随机播放：开", vm.ShuffleTip);

        // radio -> list -> single -> none
        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("list", lastModeEvent);
        Assert.False(vm.IsRepeatSingle);
        Assert.False(vm.IsRepeatOff);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("single", lastModeEvent);
        Assert.True(vm.IsRepeatSingle);
        Assert.False(vm.IsRepeatOff);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("none", lastModeEvent);
        Assert.True(vm.IsRepeatOff);
        Assert.False(vm.IsRepeatActive);
        Assert.Equal(0.45, vm.RepeatButtonOpacity);

        // 外部恢复状态同步
        mode = "single";
        shuffled = false;
        vm.SyncPlaybackModesFromService();
        Assert.Equal("single", vm.RepeatMode);
        Assert.True(vm.IsRepeatSingle);
        Assert.False(vm.IsShuffled);
    }

    [Fact]
    public async Task SettingsViewModel_PersistsAndRestoresRepeatModeAndShuffle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio_repeat_persist_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var mockLlm = new Mock<ILLMService>();
        var mockStorage = new Mock<ISecureStorage>();
        mockStorage.Setup(x => x.GetApiKeyAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        try
        {
            using (var vm = new SettingsViewModel(mockLlm.Object, mockStorage.Object, settingsPath))
            {
                vm.RepeatMode = "single";
                vm.IsShuffled = true;
                vm.RequestUiStateSave();
                await vm.WaitForPendingUiSaveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            using (var reloaded = new SettingsViewModel(mockLlm.Object, mockStorage.Object, settingsPath))
            {
                await reloaded.LoadAsync();
                Assert.Equal("single", reloaded.RepeatMode);
                Assert.True(reloaded.IsShuffled);
            }

            // 非法值回退到 radio
            await File.WriteAllTextAsync(settingsPath, "{\"repeat_mode\":\"invalid_mode\",\"is_shuffled\":false}");
            using (var fallback = new SettingsViewModel(mockLlm.Object, mockStorage.Object, settingsPath))
            {
                await fallback.LoadAsync();
                Assert.Equal("radio", fallback.RepeatMode);
                Assert.False(fallback.IsShuffled);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MainWindowViewModel_NaturalTrackCompleted_NotifiesListeningProfileOnceWithoutTrackEnded()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio_profile_natural_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var trackEnded = new Subject<Track?>();
            var naturalCompleted = new Subject<Track?>();
            var trackChanged = new Subject<Track?>();
            var stateChanged = new Subject<PlaybackState>();
            var positionChanged = new Subject<TimeSpan>();
            var spectrumData = new Subject<float[]>();
            var ttsState = new Subject<bool>();
            var ttsError = new Subject<string>();
            var recoveryNotices = new Subject<PlaybackRecoveryNotice>();
            var mediaInfo = new Subject<TrackMediaInfo>();
            var playlist = new System.Collections.Generic.List<Track>();
            var song = new Track { SourceId = "netease:1001", Title = "Song", Artist = "Artist" };

            var audio = new Mock<IAudioService>();
            audio.SetupGet(x => x.TrackEnded).Returns(trackEnded);
            audio.SetupGet(x => x.NaturalTrackCompleted).Returns(naturalCompleted);
            audio.SetupGet(x => x.TrackChanged).Returns(trackChanged);
            audio.SetupGet(x => x.StateChanged).Returns(stateChanged);
            audio.SetupGet(x => x.PositionChanged).Returns(positionChanged);
            audio.SetupGet(x => x.SpectrumData).Returns(spectrumData);
            audio.SetupGet(x => x.TtsStateChanged).Returns(ttsState);
            audio.SetupGet(x => x.TtsError).Returns(ttsError);
            audio.SetupGet(x => x.PlaybackRecoveryNotices).Returns(recoveryNotices);
            audio.SetupGet(x => x.TrackMediaInfo).Returns(mediaInfo);
            audio.SetupGet(x => x.Playlist).Returns(() => playlist.AsReadOnly());
            audio.SetupGet(x => x.CurrentTrack).Returns(song);
            audio.SetupGet(x => x.RepeatMode).Returns("list");

            var dj = new Mock<IDJService>();
            var llm = new Mock<ILLMService>();
            var storage = new Mock<ISecureStorage>();
            var search = new Mock<IMusicSearchService>();
            var stt = new Mock<ISttService>();
            var profile = new Mock<IListeningProfileService>();

            using var vm = new MainWindowViewModel(
                audio.Object,
                dj.Object,
                llm.Object,
                storage.Object,
                search.Object,
                stt.Object,
                playlistFile: Path.Combine(tempDir, "playlist.json"),
                settingsFile: Path.Combine(tempDir, "settings.json"),
                listeningProfile: profile.Object);

            // 非电台模式（single / list / 上下文连播）只发 NaturalTrackCompleted、不发 TrackEnded
            naturalCompleted.OnNext(song);
            profile.Verify(x => x.NotifyPlaybackEndedNaturally(song), Times.Once);

            // 电台模式下后续还会发 TrackEnded，不应重复结算收听画像
            trackEnded.OnNext(song);
            profile.Verify(x => x.NotifyPlaybackEndedNaturally(song), Times.Once);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
