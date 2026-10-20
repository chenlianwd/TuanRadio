using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class MainWindowViewModelTests
{
    private static string CreateTempPlaylistFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "playlist.json");
    }

    private static string CreateTempSettingsFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static Mock<IAudioService> CreateAudioMock(List<Track> playlist, Func<Track?> currentTrack)
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => playlist.AsReadOnly());
        audio.Setup(x => x.CurrentTrack).Returns(currentTrack);
        audio.Setup(x => x.RepeatMode).Returns("radio");
        audio.Setup(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()))
            .Callback<IEnumerable<Track>>(tracks => playlist.AddRange(tracks));
        return audio;
    }

    [Fact]
    public async Task RadioNext_AddsRecommendedTrackToDisplayedPlaylist()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audio = new AudioService();
        var dj = new Mock<IDJService>();
        var minimax = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        var recommended = new Track
        {
            Id = "recommended",
            SourceId = "test:recommended",
            Title = "Recommended",
            Artist = "AIRadio",
            FilePath = "http://example.com/recommended.mp3"
        };

        dj.Setup(x => x.RecommendNextTrackAsync(It.IsAny<Track?>()))
            .ReturnsAsync(recommended);

        var vm = new MainWindowViewModel(
            audio,
            dj.Object,
            minimax.Object,
            storage.Object,
            search.Object,
            stt.Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile());

        try
        {
            vm.PlaylistVM.AddExternalTrack(new Track
            {
                Id = "current",
                SourceId = "test:current",
                Title = "Current",
                Artist = "AIRadio",
                FilePath = "http://example.com/current.mp3"
            });

            vm.PlayerVM.NextCommand.Execute().Subscribe();
            await Task.Delay(250);

            Assert.Contains(vm.PlaylistVM.Tracks, t => t.SourceId == "test:recommended");
            Assert.Single(vm.PlaylistVM.Tracks, t => t.SourceId == "test:recommended");
            Assert.Single(audio.Playlist, t => t.SourceId == "test:recommended");
        }
        finally
        {
            vm.Dispose();
            audio.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task AutoRadio_DoesNotPlayStaleRecommendationAfterCurrentTrackChanges()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var current = new Track
        {
            Id = "current",
            SourceId = "test:current",
            Title = "Current",
            Artist = "AIRadio",
            FilePath = "http://example.com/current.mp3"
        };
        var next = new Track
        {
            Id = "next",
            SourceId = "test:next",
            Title = "Next",
            Artist = "AIRadio",
            FilePath = "http://example.com/next.mp3"
        };
        var playlist = new List<Track>();
        Track? currentTrack = current;
        var audio = CreateAudioMock(playlist, () => currentTrack);
        var dj = new Mock<IDJService>();
        var minimax = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        dj.SetupGet(x => x.TtsEnabled).Returns(false);
        dj.Setup(x => x.GenerateTrackIntroductionAsync(current, next))
            .Callback(() => currentTrack = next)
            .ReturnsAsync(new DJScript { Text = "Next up", Expression = "smile", Motion = "wave" });

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            minimax.Object,
            storage.Object,
            search.Object,
            stt.Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile());

        try
        {
            vm.PlaylistVM.AddExternalTrack(current);
            vm.PlaylistVM.AddExternalTrack(next);

            var method = typeof(MainWindowViewModel).GetMethod("HandleAutoRadioTrackEndedAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            await (Task)method!.Invoke(vm, new object[] { current })!;

            audio.Verify(x => x.PlayAtIndex(It.IsAny<int>()), Times.Never);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void DislikeCurrentTrackCommand_RecordsCurrentTrackFeedback()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var current = new Track
        {
            Id = "local-current",
            SourceId = "netease:current",
            Title = "Current",
            Artist = "AIRadio",
            FilePath = "http://example.com/current.mp3"
        };
        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => current);
        var dj = new Mock<IDJService>();
        var minimax = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();
        var recommendations = new Mock<IRecommendationService>();
        recommendations.SetupGet(x => x.FeedbackHistory).Returns(Array.Empty<UserMusicFeedback>());

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            minimax.Object,
            storage.Object,
            search.Object,
            stt.Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile(),
            recommendationService: recommendations.Object);

        try
        {
            vm.DislikeCurrentTrackCommand.Execute().Subscribe();

            recommendations.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(feedback =>
                feedback.TrackId == "netease:current" &&
                feedback.Action == MusicFeedbackAction.Dislike)), Times.Once);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void PlaybackHistory_RecordsOnlyWhenTrackActuallyStartsPlaying()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var current = new Track
        {
            Id = "current",
            SourceId = "test:current",
            Title = "Current",
            Artist = "AIRadio",
            FilePath = "http://example.com/current.mp3"
        };
        var audio = CreateAudioMock(new List<Track> { current }, () => current);
        var trackChanged = Assert.IsType<Subject<Track?>>(audio.Object.TrackChanged);
        var stateChanged = Assert.IsType<Subject<PlaybackState>>(audio.Object.StateChanged);
        var recommendations = new Mock<IRecommendationService>();
        recommendations.SetupGet(x => x.RecentlyPlayed).Returns(Array.Empty<Track>());
        recommendations.SetupGet(x => x.FeedbackHistory).Returns(Array.Empty<UserMusicFeedback>());

        var vm = new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile(),
            recommendationService: recommendations.Object);

        try
        {
            trackChanged.OnNext(current);
            recommendations.Verify(x => x.RecordPlayedTrack(It.IsAny<Track>()), Times.Never);

            stateChanged.OnNext(PlaybackState.Playing);
            recommendations.Verify(x => x.RecordPlayedTrack(current), Times.Once);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void Dispose_DoesNotSynchronouslyStopContainerOwnedAudioService()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => null);
        var vm = new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile());

        try
        {
            vm.Dispose();

            audio.Verify(x => x.StopTts(), Times.Never);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task LoadLocalStateAsync_RestoresLocalStateWithoutAnnouncements()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audio = CreateAudioMock(new List<Track>(), () => null);
        var dj = new Mock<IDJService>();
        dj.SetupGet(x => x.TtsEnabled).Returns(true);
        var recommendations = new Mock<IRecommendationService>();
        recommendations.SetupGet(x => x.FeedbackHistory).Returns(Array.Empty<UserMusicFeedback>());
        var settingsDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        var settingsFile = Path.Combine(settingsDir, "settings.json");
        await File.WriteAllTextAsync(settingsFile, """
            {
              "llm_provider": "anthropic",
              "llm_model": "glm-5.3",
              "is_dark_mode": false,
              "start_in_compact_mode": true
            }
            """);

        MainWindowViewModel? vm = null;
        try
        {
            vm = new MainWindowViewModel(
                audio.Object,
                dj.Object,
                new Mock<ILLMService>().Object,
                new Mock<ISecureStorage>().Object,
                new Mock<IMusicSearchService>().Object,
                new Mock<ISttService>().Object,
                CreateTempPlaylistFile(),
                settingsFile: settingsFile,
                recommendationService: recommendations.Object);

            await vm.LoadLocalStateAsync();

            Assert.Equal("anthropic", vm.SettingsVM.SelectedProvider);
            Assert.Equal("glm-5.3", vm.SettingsVM.Model);
            Assert.False(vm.IsDarkMode);
            Assert.True(vm.IsCompactMode);
            // 本地恢复阶段不做任何播报，开场留给会话阶段
            dj.Verify(x => x.GenerateSpeechAsync(It.IsAny<string>()), Times.Never);
            audio.Verify(x => x.Play(), Times.Never);
        }
        finally
        {
            vm?.Dispose();
            try { Directory.Delete(settingsDir, true); } catch { }
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task StartSessionAsync_AnnouncesWelcome()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audio = CreateAudioMock(new List<Track>(), () => null);
        var dj = new Mock<IDJService>();
        dj.SetupGet(x => x.TtsEnabled).Returns(true);
        dj.Setup(x => x.GenerateSpeechAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<byte>());
        var recommendations = new Mock<IRecommendationService>();
        recommendations.SetupGet(x => x.FeedbackHistory).Returns(Array.Empty<UserMusicFeedback>());

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile(),
            recommendationService: recommendations.Object);

        try
        {
            await vm.StartSessionAsync();

            // 欢迎语播报属于会话阶段
            dj.Verify(x => x.GenerateSpeechAsync(It.IsAny<string>()), Times.AtLeastOnce);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    private static void SetupStartupSessionMocks(
        Mock<IDJService> dj,
        Mock<IRecommendationService> recommendations)
    {
        dj.SetupGet(x => x.TtsEnabled).Returns(true);
        dj.Setup(x => x.GenerateSpeechAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<byte>());
        dj.Setup(x => x.RecommendNextTrackAsync(It.IsAny<Track?>()))
            .ReturnsAsync((Track?)null);
        dj.Setup(x => x.GenerateTrackIntroductionAsync(It.IsAny<Track>(), It.IsAny<Track>()))
            .ReturnsAsync(new DJScript { Text = "intro" });
        recommendations.SetupGet(x => x.FeedbackHistory).Returns(Array.Empty<UserMusicFeedback>());
    }

    private static MainWindowViewModel CreateStartupSessionViewModel(
        Mock<IAudioService> audio,
        Mock<IDJService> dj,
        Mock<IRecommendationService> recommendations)
        => new(
            audio.Object,
            dj.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile(),
            recommendationService: recommendations.Object);

    private static Track CreateStartupPlaylistTrack(string suffix)
        => new()
        {
            Id = "existing-" + suffix,
            SourceId = "test:existing-" + suffix,
            Title = "Existing " + suffix,
            Artist = "Someone " + suffix,
            FilePath = "http://example.com/" + suffix + ".mp3"
        };

    [Fact]
    public async Task StartSessionAsync_PlaysStartupRecommendationInsteadOfPlaylistFirst()
    {
        // 真实语义：LoadTracks 后 CurrentTrack 恒为歌单第 1 首（未播放态），
        // 开台推荐链的 staleness 守卫必须按它工作而非按 null
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => playlist.FirstOrDefault());
        var dj = new Mock<IDJService>();
        var recommendations = new Mock<IRecommendationService>();
        SetupStartupSessionMocks(dj, recommendations);
        var recommended = new Track
        {
            Id = "recommended",
            SourceId = "test:recommended",
            Title = "Recommended",
            Artist = "AIRadio",
            FilePath = "http://example.com/recommended.mp3"
        };
        recommendations.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .ReturnsAsync(recommended);

        MainWindowViewModel? vm = null;
        try
        {
            vm = CreateStartupSessionViewModel(audio, dj, recommendations);
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("a"));
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("b"));

            await vm.StartSessionAsync();

            // 开台即播 DJ 推荐新歌：不再回落歌单第 1 首
            audio.Verify(x => x.PlayTrack(It.Is<Track>(t => t.SourceId == "test:recommended")), Times.Once);
            audio.Verify(x => x.Play(), Times.Never);
            Assert.Contains(vm.PlaylistVM.Tracks, t => t.SourceId == "test:recommended");
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task StartSessionAsync_PrefetchesRecommendationDuringWelcomeSpeech()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => playlist.FirstOrDefault());
        var dj = new Mock<IDJService>();
        var recommendations = new Mock<IRecommendationService>();
        SetupStartupSessionMocks(dj, recommendations);
        var recommended = new Track
        {
            Id = "recommended",
            SourceId = "test:recommended",
            Title = "Recommended",
            Artist = "AIRadio",
            FilePath = "http://example.com/recommended.mp3"
        };
        var recommendationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        recommendations.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .Returns((RecommendationRequest _) =>
            {
                recommendationStarted.TrySetResult(true);
                return Task.FromResult<Track?>(recommended);
            });
        // 并行锚点：首次欢迎语 TTS 未返回期间，推荐搜索必须已在途（串行实现会等 2s 超时）。
        // SpeakDjTextAsync 会吞回调异常，判定结果记录下来由测试主体裁决
        var welcomeSawRecommendation = false;
        var ttsCallIndex = 0;
        dj.Setup(x => x.GenerateSpeechAsync(It.IsAny<string>()))
            .Returns((string _) =>
            {
                if (++ttsCallIndex == 1)
                    welcomeSawRecommendation = recommendationStarted.Task.Wait(2000);
                return Task.FromResult<byte[]?>(Array.Empty<byte>());
            });

        MainWindowViewModel? vm = null;
        try
        {
            vm = CreateStartupSessionViewModel(audio, dj, recommendations);
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("a"));

            await vm.StartSessionAsync();

            Assert.True(welcomeSawRecommendation, "推荐应与欢迎语 TTS 并行发起");
            audio.Verify(x => x.PlayTrack(It.Is<Track>(t => t.SourceId == "test:recommended")), Times.Once);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task StartSessionAsync_FallsBackToPlaylistRotationWhenRecommendationFails()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => playlist.FirstOrDefault());
        var dj = new Mock<IDJService>();
        var recommendations = new Mock<IRecommendationService>();
        SetupStartupSessionMocks(dj, recommendations);
        // 节目单推荐与 DJ 单曲推荐双双落空
        recommendations.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .ReturnsAsync((Track?)null);

        MainWindowViewModel? vm = null;
        try
        {
            vm = CreateStartupSessionViewModel(audio, dj, recommendations);
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("a"));
            var second = CreateStartupPlaylistTrack("b");
            vm.PlaylistVM.AddExternalTrack(second);

            await vm.StartSessionAsync();

            // 轮换以开台基线（第 1 首）为"上一首"，从其余曲目中选：单曲池必选第二首
            audio.Verify(x => x.PlayTrack(It.Is<Track>(t => t.SourceId == second.SourceId)), Times.Once);
            audio.Verify(x => x.Play(), Times.Never);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task StartSessionAsync_PlaysFirstTrackWhenStartupChainFails()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => playlist.FirstOrDefault());
        var dj = new Mock<IDJService>();
        var recommendations = new Mock<IRecommendationService>();
        SetupStartupSessionMocks(dj, recommendations);
        recommendations.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .ReturnsAsync((Track?)null);
        // 轮换链的介绍词生成炸掉：开台播放不能静默无声，须兜底播歌单第 1 首
        dj.Setup(x => x.GenerateTrackIntroductionAsync(It.IsAny<Track>(), It.IsAny<Track>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        MainWindowViewModel? vm = null;
        try
        {
            vm = CreateStartupSessionViewModel(audio, dj, recommendations);
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("a"));
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("b"));

            await vm.StartSessionAsync();

            audio.Verify(x => x.Play(), Times.Once);
            audio.Verify(x => x.PlayTrack(It.IsAny<Track>()), Times.Never);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task StartSessionAsync_YieldsWhenUserAlreadyPlaying()
    {
        // 开台链进入时用户已在播（如欢迎语前自己点了歌）：让位不抢播
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => playlist.FirstOrDefault());
        audio.SetupGet(x => x.IsPlaying).Returns(true);
        var dj = new Mock<IDJService>();
        var recommendations = new Mock<IRecommendationService>();
        SetupStartupSessionMocks(dj, recommendations);

        MainWindowViewModel? vm = null;
        try
        {
            vm = CreateStartupSessionViewModel(audio, dj, recommendations);
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("a"));

            await vm.StartSessionAsync();

            audio.Verify(x => x.PlayTrack(It.IsAny<Track>()), Times.Never);
            audio.Verify(x => x.Play(), Times.Never);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task StartSessionAsync_YieldsWhenUserStartsPlaybackDuringIntroSpeech()
    {
        // 中危回归锚点：介绍词 TTS 的长窗口内用户手动开播（全局播放键播的正是基线曲，
        // CurrentTrack 引用不变、staleness 拦不住）——推荐/轮换/兜底全部让位不抢播
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => playlist.FirstOrDefault());
        var userStarted = false;
        audio.SetupGet(x => x.IsPlaying).Returns(() => userStarted);
        var dj = new Mock<IDJService>();
        var recommendations = new Mock<IRecommendationService>();
        SetupStartupSessionMocks(dj, recommendations);
        var recommended = new Track
        {
            Id = "recommended",
            SourceId = "test:recommended",
            Title = "Recommended",
            Artist = "AIRadio",
            FilePath = "http://example.com/recommended.mp3"
        };
        recommendations.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .ReturnsAsync(recommended);
        var ttsCallIndex = 0;
        dj.Setup(x => x.GenerateSpeechAsync(It.IsAny<string>()))
            .Returns((string _) =>
            {
                // 第二次 TTS = 推荐介绍词：此刻模拟用户按下播放键
                if (++ttsCallIndex == 2)
                    userStarted = true;
                return Task.FromResult<byte[]?>(Array.Empty<byte>());
            });

        MainWindowViewModel? vm = null;
        try
        {
            vm = CreateStartupSessionViewModel(audio, dj, recommendations);
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("a"));
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("b"));

            await vm.StartSessionAsync();

            audio.Verify(x => x.PlayTrack(It.IsAny<Track>()), Times.Never);
            audio.Verify(x => x.Play(), Times.Never);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task StartSessionAsync_AbandonedPrefetchDoesNotPushProgramSideEffects()
    {
        // 中危回归锚点：让位/超时放弃预取后，迟到的推荐结果不得再推节目单开场白气泡
        // 与台呼（兜底播的正是基线曲时 CurrentTrack 未变，staleness 闸门拦不住，
        // 必须靠 linked CTS 显式取消作废）
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => playlist.FirstOrDefault());
        audio.SetupGet(x => x.IsPlaying).Returns(true); // 用户已在播：开台链让位
        var dj = new Mock<IDJService>();
        var recommendations = new Mock<IRecommendationService>();
        SetupStartupSessionMocks(dj, recommendations);
        var pending = new TaskCompletionSource<Track?>(TaskCreationOptions.RunContinuationsAsynchronously);
        recommendations.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .Returns((RecommendationRequest _) => pending.Task);
        recommendations.SetupGet(x => x.CurrentProgram)
            .Returns(new RadioProgram
            {
                DjOpening = "late-program-opening",
                Tracks = { new RecommendedTrack { Track = new Track(), IsPlayable = true } },
            });

        MainWindowViewModel? vm = null;
        try
        {
            vm = CreateStartupSessionViewModel(audio, dj, recommendations);
            vm.PlaylistVM.AddExternalTrack(CreateStartupPlaylistTrack("a"));

            await vm.StartSessionAsync();
            audio.Verify(x => x.Play(), Times.Never);

            // 迟到的推荐结果到达：被 linked CTS 取消作废，零副作用落地
            pending.TrySetResult(new Track
            {
                Id = "late",
                SourceId = "test:late",
                Title = "Late",
                Artist = "AIRadio",
                FilePath = "http://example.com/late.mp3"
            });
            await Task.Delay(300);

            Assert.DoesNotContain(vm.ChatVM.Messages, m => m.Content.Contains("late-program-opening"));
            audio.Verify(x => x.PlayRadioSoundFx(It.IsAny<RadioFxKind>()), Times.Never);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task StartSessionAsync_EmptyPlaylistKeepsGuidanceWithoutStationPlayback()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audio = CreateAudioMock(new List<Track>(), () => null);
        var dj = new Mock<IDJService>();
        var recommendations = new Mock<IRecommendationService>();
        SetupStartupSessionMocks(dj, recommendations);
        recommendations.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .ReturnsAsync((Track?)null);

        MainWindowViewModel? vm = null;
        try
        {
            vm = CreateStartupSessionViewModel(audio, dj, recommendations);

            await vm.StartSessionAsync();

            // 空歌单保留延迟引导开场（fire-and-forget，1.4s 延迟），轮询等待气泡落地
            var found = false;
            for (var i = 0; i < 40 && !found; i++)
            {
                await Task.Delay(100);
                found = vm.ChatVM.Messages.Any(m =>
                    m.Content.Contains("歌单还是空的") || m.Content.Contains("No tracks yet"));
            }
            Assert.True(found, "空歌单应保留引导开场气泡");

            // 空歌单不发起推荐、也不开播
            recommendations.Verify(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()), Times.Never);
            audio.Verify(x => x.Play(), Times.Never);
            audio.Verify(x => x.PlayTrack(It.IsAny<Track>()), Times.Never);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void ToggleCompactModeCommand_TogglesModeAndClosesOverlays()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audio = CreateAudioMock(new List<Track>(), () => null);
        var dj = new Mock<IDJService>();
        var llm = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        MainWindowViewModel? vm = null;
        try
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsDir);
            vm = new MainWindowViewModel(
                audio.Object,
                dj.Object,
                llm.Object,
                storage.Object,
                search.Object,
                stt.Object,
                CreateTempPlaylistFile(),
                settingsFile: Path.Combine(settingsDir, "settings.json"));

            vm.IsSettingsOpen = true;
            vm.IsLibraryOpen = true;
            vm.IsCharacterPickerOpen = true;

            vm.ToggleCompactModeCommand.Execute(System.Reactive.Unit.Default).Subscribe();

            Assert.True(vm.IsCompactMode);
            Assert.False(vm.IsSettingsOpen);
            Assert.False(vm.IsLibraryOpen);
            Assert.False(vm.IsCharacterPickerOpen);

            vm.ToggleCompactModeCommand.Execute(System.Reactive.Unit.Default).Subscribe();
            Assert.False(vm.IsCompactMode);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void EnglishLanguage_LocalizesClockDateImmediately()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var audio = CreateAudioMock(new List<Track>(), () => null);
        var vm = new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile());

        try
        {
            vm.SettingsVM.SelectedLanguage = "en";

            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", vm.LocalizedDayOfWeek);
            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", vm.LocalizedDate);
            Assert.Contains("-", vm.LocalizedDate);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply("zh");
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void LanguageChange_ReplacesProgramInstanceForFullRebind()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => null);
        var dj = new Mock<IDJService>();
        var minimax = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            minimax.Object,
            storage.Object,
            search.Object,
            stt.Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile());

        try
        {
            vm.CurrentRadioProgram = new RadioProgram
            {
                Title = "节目单",
                Tracks =
                {
                    new RecommendedTrack
                    {
                        Track = new Track { Id = "p1", Title = "T", Artist = "A" },
                        IsPlayable = true,
                        Source = "网易云音乐"
                    }
                }
            };
            var original = vm.CurrentRadioProgram;

            try
            {
                AppLanguage.Apply("en");

                // 链式绑定与 ItemsSource 对同实例短路：必须换新实例(含新行对象)才能整体重绘
                Assert.NotSame(original, vm.CurrentRadioProgram);
                Assert.NotSame(original.Tracks[0], vm.CurrentRadioProgram!.Tracks[0]);
                // 共享的 Track 实例只刷新本地化属性，不复制
                Assert.Same(original.Tracks[0].Track, vm.CurrentRadioProgram.Tracks[0].Track);
                Assert.Equal("NetEase Cloud Music", vm.CurrentRadioProgram.Tracks[0].Source);
            }
            finally
            {
                AppLanguage.Apply("zh");
            }
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void UpdateCurrentProgram_RelocalizesStaleProgramBeforeDisplay()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => null);
        var dj = new Mock<IDJService>();
        var minimax = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            minimax.Object,
            storage.Object,
            search.Object,
            stt.Object,
            CreateTempPlaylistFile(),
            settingsFile: CreateTempSettingsFile());

        try
        {
            var program = new RadioProgram
            {
                Tracks =
                {
                    new RecommendedTrack
                    {
                        Track = new Track { Id = "stale", Title = "T", Artist = "A" },
                        IsPlayable = true
                    }
                }
            };
            try
            {
                // 服务侧节目单停留在英文：模拟语言翻转后未重建的旧节目单
                AppLanguage.Apply("en");
                RecommendationService.ApplyLocalization(program);
                Assert.Equal("Today's Radio", program.Title);

                AppLanguage.Apply("zh");

                // 自动续播把服务侧旧节目单回灌 VM 前必须按当前语言重译
                var method = typeof(MainWindowViewModel).GetMethod("UpdateCurrentProgram", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(method);
                method!.Invoke(vm, new object?[] { program });

                Assert.Equal("今日电台", vm.CurrentRadioProgram!.Title);
            }
            finally
            {
                AppLanguage.Apply("zh");
            }
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
