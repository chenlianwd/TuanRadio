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

    private static readonly MethodInfo TryPlayNextCanonicalTrackMethod = typeof(AudioService).GetMethod(
        "TryPlayNextCanonicalTrack", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static bool TryAdvanceWithoutWrap(AudioService svc)
        => (bool)TryPlayNextCanonicalTrackMethod.Invoke(svc, new object[] { false })!;

    [Fact]
    public void NoneMode_Shuffle_PlaysEachTrackOnceThenStops_AndIsNotSequential()
    {
        using var svc = new AudioService();
        var titles = new[] { "One", "Two", "Three", "Four", "Five" };
        svc.LoadTracks(titles.Select((title, i) => new Track
        {
            SourceId = $"local:{i + 1}",
            Title = title,
            FilePath = $"http://example.com/{i + 1}.mp3"
        }));
        svc.SetRepeatMode("none");
        svc.SetShuffle(true);

        var sawNonSequentialOrder = false;
        for (var pass = 0; pass < 20; pass++)
        {
            if (pass > 0)
            {
                // 手动选歌开启新一轮，已播集合随之重置
                svc.PlayAtIndex(0);
                Assert.Equal("One", svc.CurrentTrack?.Title);
            }

            var played = new System.Collections.Generic.List<string> { "One" };
            for (var step = 0; step < titles.Length - 1; step++)
            {
                Assert.True(TryAdvanceWithoutWrap(svc));
                var title = svc.CurrentTrack?.Title;
                Assert.NotNull(title);
                // 未播尽不重复
                Assert.DoesNotContain(title, played);
                played.Add(title);
            }

            // 整单播完即停：不回绕、不重复
            Assert.False(TryAdvanceWithoutWrap(svc));

            if (!played.SequenceEqual(titles))
                sawNonSequentialOrder = true;
        }

        // 4 次抽取全等于顺序排列的概率是 1/24 每轮，20 轮全顺序在真实随机下不可能发生
        Assert.True(sawNonSequentialOrder);
    }

    [Fact]
    public async Task NoneMode_Shuffle_ManualNextRespectsPlayedSet_AndStopsWhenExhausted()
    {
        using var svc = new AudioService();
        svc.LoadTracks(new[]
        {
            new Track { SourceId = "local:1", Title = "One", FilePath = "http://example.com/1.mp3" },
            new Track { SourceId = "local:2", Title = "Two", FilePath = "http://example.com/2.mp3" }
        });
        svc.SetRepeatMode("none");
        svc.SetShuffle(true);

        // 2 首：仅剩一首未播，手动 Next 必然切到它；再按已无未播曲目，保持不动（不回绕重播）
        svc.Next();
        await Task.Delay(150);
        Assert.Equal("Two", svc.CurrentTrack?.Title);

        svc.Next();
        await Task.Delay(150);
        Assert.Equal("Two", svc.CurrentTrack?.Title);
    }

    private static AudioService CreateExhaustedSequentialShuffle(out Track[] tracks)
    {
        tracks = new[]
        {
            new Track { SourceId = "local:1", Title = "One", FilePath = "http://example.com/1.mp3" },
            new Track { SourceId = "local:2", Title = "Two", FilePath = "http://example.com/2.mp3" }
        };
        var svc = new AudioService();
        svc.LoadTracks(tracks);
        svc.SetRepeatMode("none");
        svc.SetShuffle(true);
        // 2 首歌单：推进一次后已播尽，此后任何一次成功推进都只能来自已播集合被重置
        Assert.True(TryAdvanceWithoutWrap(svc));
        Assert.False(TryAdvanceWithoutWrap(svc));
        return svc;
    }

    private static void AssertCanAdvanceOnceAfterReset(AudioService svc)
    {
        Assert.True(TryAdvanceWithoutWrap(svc));
        Assert.False(TryAdvanceWithoutWrap(svc));
    }

    [Fact]
    public void NoneMode_Shuffle_PlayedSetResets_OnRepeatModeToggle()
    {
        using var svc = CreateExhaustedSequentialShuffle(out _);
        svc.SetRepeatMode("list");
        svc.SetRepeatMode("none");
        AssertCanAdvanceOnceAfterReset(svc);
    }

    [Fact]
    public void NoneMode_Shuffle_PlayedSetResets_OnSetShuffle()
    {
        using var svc = CreateExhaustedSequentialShuffle(out _);
        svc.SetShuffle(false);
        svc.SetShuffle(true);
        AssertCanAdvanceOnceAfterReset(svc);
    }

    [Fact]
    public void NoneMode_Shuffle_PlayedSetResets_OnShuffleToggle()
    {
        using var svc = CreateExhaustedSequentialShuffle(out _);
        svc.Shuffle();
        svc.Shuffle();
        AssertCanAdvanceOnceAfterReset(svc);
    }

    [Fact]
    public void NoneMode_Shuffle_PlayedSetResets_OnManualPick()
    {
        using var svc = CreateExhaustedSequentialShuffle(out _);
        svc.PlayAtIndex(0);
        AssertCanAdvanceOnceAfterReset(svc);
    }

    [Fact]
    public void NoneMode_Shuffle_PlayedSetResets_OnPlayTrack()
    {
        using var svc = CreateExhaustedSequentialShuffle(out var tracks);
        svc.PlayTrack(tracks[1]);
        AssertCanAdvanceOnceAfterReset(svc);
    }

    [Fact]
    public void NoneMode_Shuffle_PlayedSetResets_OnReload()
    {
        using var svc = CreateExhaustedSequentialShuffle(out var tracks);
        // 同一批曲目实例重新载入
        svc.LoadTracks(tracks);
        AssertCanAdvanceOnceAfterReset(svc);
    }

    [Fact]
    public void NoneMode_PreviousAtFirstTrack_DoesNotWrap_OtherModesStillDo()
    {
        using var svc = new AudioService();
        svc.LoadTracks(new[]
        {
            new Track { SourceId = "local:1", Title = "One", FilePath = "http://example.com/1.mp3" },
            new Track { SourceId = "local:2", Title = "Two", FilePath = "http://example.com/2.mp3" },
            new Track { SourceId = "local:3", Title = "Three", FilePath = "http://example.com/3.mp3" }
        });

        svc.SetRepeatMode("none");
        svc.Previous();
        Assert.Equal("One", svc.CurrentTrack?.Title);

        // 其余模式维持原有的回绕语义
        foreach (var mode in new[] { "list", "single" })
        {
            svc.SetRepeatMode(mode);
            svc.PlayAtIndex(0);
            svc.Previous();
            Assert.Equal("Three", svc.CurrentTrack?.Title);
        }
    }

    [Fact]
    public void SleepTimer_EndOfTrack_NotConsumedByNaturalTrackCompletedAlone()
    {
        var trackEnded = new Subject<Track?>();
        var naturalCompleted = new Subject<Track?>();
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.TrackEnded).Returns(trackEnded);
        audio.SetupGet(x => x.NaturalTrackCompleted).Returns(naturalCompleted);
        audio.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(x => x.CurrentTrack).Returns(new Track { Title = "Song" });

        using var timer = new SleepTimerService(audio.Object, CurrentThreadScheduler.Instance, CurrentThreadScheduler.Instance);
        timer.StartEndOfTrackTimer();

        // 自然完播只由宿主安装的拦截回调消费；服务若也消费，两者就是无序的一次性标志竞争消费者
        naturalCompleted.OnNext(new Track { Title = "Song" });
        Assert.Equal(SleepTimerMode.EndOfTrack, timer.Mode);
        audio.Verify(x => x.Pause(), Times.Never);

        // 未安装拦截时的电台交接路径仍由 TrackEnded 兜底
        trackEnded.OnNext(new Track { Title = "Song" });
        Assert.Equal(SleepTimerMode.Disabled, timer.Mode);
        audio.Verify(x => x.Pause(), Times.Once);
    }

    [Fact]
    public async Task SleepTimer_EndOfTrack_InterceptIsSoleConsumer_AndBlocksAutoAdvanceInListMode()
    {
        // 同步 UI 调度器把「UI 线程先于拦截回调消费」固定成必然顺序：
        // 服务若合并订阅自然完播事件，标志被吃掉后拦截拿到 false，列表循环照常续播到下一首
        using var svc = new AudioService();
        svc.LoadTracks(new[]
        {
            new Track { SourceId = "local:1", Title = "One", FilePath = "http://example.com/1.mp3" },
            new Track { SourceId = "local:2", Title = "Two", FilePath = "http://example.com/2.mp3" }
        });
        svc.SetRepeatMode("list");
        using var timer = new SleepTimerService(svc, CurrentThreadScheduler.Instance, CurrentThreadScheduler.Instance);
        svc.SetBeforeAutoAdvanceCallback(_ => timer.CheckAndConsumeEndOfTrack());
        timer.StartEndOfTrackTimer();
        Assert.Equal(SleepTimerMode.EndOfTrack, timer.Mode);

        SimulateNaturalEnd(svc);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (timer.Mode != SleepTimerMode.Disabled && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(SleepTimerMode.Disabled, timer.Mode);
        // 续播动作经 100ms 延迟调度，留足窗口确认它确实被拦截
        await Task.Delay(300);
        Assert.Equal("One", svc.CurrentTrack?.Title);
    }

    private static void SimulateNaturalEnd(AudioService svc)
    {
        var handler = typeof(AudioService).GetMethod(
            "OnPlayerEndReached", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(svc, new object?[] { null, EventArgs.Empty });
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
