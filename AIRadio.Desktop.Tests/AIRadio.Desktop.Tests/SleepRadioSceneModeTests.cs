using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
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

public class SleepRadioSceneModeTests
{
    private static Track MakeTrack(string id, string title, string artist, bool isLive = false)
        => new()
        {
            Id = id,
            Title = title,
            Artist = artist,
            Duration = isLive ? TimeSpan.Zero : TimeSpan.FromMinutes(3),
            FilePath = "http://example.com/stream.mp3",
            ProviderMetadata = isLive
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["is_live"] = "true" }
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

    [Fact]
    public void SleepTimer_Activates_SleepRadioSceneMode_AndCancels()
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.TrackChanged).Returns(Observable.Never<Track?>());

        using var timerService = new SleepTimerService(audioMock.Object, new HistoricalScheduler(), ImmediateScheduler.Instance);
        using var sceneService = new SceneModeService(timerService, ImmediateScheduler.Instance);

        Assert.Equal(RadioSceneMode.Default, sceneService.CurrentMode);
        Assert.False(sceneService.IsSleepMode);

        // 启动睡眠定时
        timerService.StartDurationTimer(TimeSpan.FromMinutes(30));
        Assert.Equal(RadioSceneMode.Sleep, sceneService.CurrentMode);
        Assert.True(sceneService.IsSleepMode);

        // 取消睡眠定时
        timerService.CancelTimer();
        Assert.Equal(RadioSceneMode.Default, sceneService.CurrentMode);
        Assert.False(sceneService.IsSleepMode);
    }

    private sealed class SleepSceneHarness : IDisposable
    {
        private readonly string _tempDir;
        private readonly IScheduler _originalMainThreadScheduler;

        public Mock<IAudioService> AudioMock { get; } = new();
        public Mock<IDJService> DjMock { get; } = new();
        public HistoricalScheduler TimeScheduler { get; } = new();
        public SleepTimerService TimerService { get; }
        public SceneModeService SceneService { get; }
        public RecommendationService RecommendationService { get; }
        public MainWindowViewModel ViewModel { get; }

        public SleepSceneHarness()
        {
            _originalMainThreadScheduler = RxApp.MainThreadScheduler;
            RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

            _tempDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            AudioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
            AudioMock.SetupGet(x => x.TrackChanged).Returns(Observable.Never<Track?>());
            AudioMock.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
            AudioMock.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
            AudioMock.SetupGet(x => x.SpectrumData).Returns(Observable.Never<float[]>());
            AudioMock.SetupGet(x => x.TtsStateChanged).Returns(Observable.Never<bool>());
            AudioMock.SetupGet(x => x.TtsError).Returns(Observable.Never<string>());
            AudioMock.SetupGet(x => x.Playlist).Returns(Array.Empty<Track>());
            AudioMock.SetupGet(x => x.CurrentTrack).Returns((Track?)null);
            AudioMock.SetupGet(x => x.RepeatMode).Returns("radio");

            var llmMock = new Mock<ILLMService>();
            var storageMock = new Mock<ISecureStorage>();
            var searchMock = new Mock<IMusicSearchService>();
            var sttMock = new Mock<ISttService>();

            TimerService = new SleepTimerService(AudioMock.Object, TimeScheduler, ImmediateScheduler.Instance);
            SceneService = new SceneModeService(TimerService, ImmediateScheduler.Instance);
            RecommendationService = new RecommendationService(llmMock.Object, searchMock.Object);

            try
            {
                ViewModel = new MainWindowViewModel(
                    AudioMock.Object,
                    DjMock.Object,
                    llmMock.Object,
                    storageMock.Object,
                    searchMock.Object,
                    sttMock.Object,
                    Path.Combine(_tempDir, "playlist.json"),
                    Path.Combine(_tempDir, "settings.json"),
                    recommendationService: RecommendationService,
                    sleepTimerService: TimerService,
                    sceneModeService: SceneService);
            }
            catch
            {
                SceneService.Dispose();
                TimerService.Dispose();
                RxApp.MainThreadScheduler = _originalMainThreadScheduler;
                try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore temp cleanup races */ }
                throw;
            }
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            RxApp.MainThreadScheduler = _originalMainThreadScheduler;
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore temp cleanup races */ }
        }
    }

    [Fact]
    public void SleepTimer_Restores_OriginalMoodBias_OnCancel()
    {
        using var harness = new SleepSceneHarness();

        // 初始设置氛围为 energetic，验证 MainWindowViewModel 真实订阅接线在伴眠开启/取消时切换并原子复原
        harness.RecommendationService.SetMoodBias("energetic");
        Assert.Equal("energetic", harness.RecommendationService.CurrentMoodBias);

        // 激活伴眠模式 -> 真实接线切换为 Sleep 并锁定 calm
        harness.TimerService.StartDurationTimer(TimeSpan.FromMinutes(30));
        Assert.Equal(RadioSceneMode.Sleep, harness.ViewModel.CurrentSceneMode);
        Assert.True(harness.ViewModel.IsSleepRadioMode);
        Assert.Equal("calm", harness.RecommendationService.CurrentMoodBias);
        harness.DjMock.Verify(x => x.UpdateSceneMode(RadioSceneMode.Sleep), Times.Once);

        // 取消伴眠模式 -> 自动复原为 energetic 并通知 DJ 恢复默认场景
        harness.TimerService.CancelTimer();
        Assert.Equal(RadioSceneMode.Default, harness.ViewModel.CurrentSceneMode);
        Assert.False(harness.ViewModel.IsSleepRadioMode);
        Assert.Equal("energetic", harness.RecommendationService.CurrentMoodBias);
        harness.DjMock.Verify(x => x.UpdateSceneMode(RadioSceneMode.Default), Times.Once);
    }

    [Fact]
    public void SleepTimer_CommandAndEdgeCases_PreserveAndRestoreMoodBias()
    {
        using var harness = new SleepSceneHarness();

        // 1. 初始无氛围偏向（null）时开启并取消，应恢复回 null
        Assert.Null(harness.RecommendationService.CurrentMoodBias);
        harness.ViewModel.SetSleepTimerCommand.Execute(30).Subscribe();
        Assert.Equal(RadioSceneMode.Sleep, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("calm", harness.RecommendationService.CurrentMoodBias);
        harness.ViewModel.SetSleepTimerCommand.Execute(0).Subscribe();
        Assert.Equal(RadioSceneMode.Default, harness.ViewModel.CurrentSceneMode);
        Assert.Null(harness.RecommendationService.CurrentMoodBias);

        // 2. 伴眠期间重设时长不得覆写原氛围，且自然到期后完整复原
        harness.RecommendationService.SetMoodBias("energetic");
        harness.ViewModel.SetSleepTimerCommand.Execute(30).Subscribe();
        harness.ViewModel.SetSleepTimerCommand.Execute(45).Subscribe();
        Assert.Equal("calm", harness.RecommendationService.CurrentMoodBias);
        harness.TimeScheduler.AdvanceBy(TimeSpan.FromMinutes(45));
        Assert.Equal(RadioSceneMode.Default, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("energetic", harness.RecommendationService.CurrentMoodBias);

        // 3. 切换新氛围后再重入伴眠，退出应恢复最新进入前氛围而非历史旧值
        harness.RecommendationService.SetMoodBias("warm");
        harness.ViewModel.SetSleepTimerCommand.Execute(15).Subscribe();
        Assert.Equal("calm", harness.RecommendationService.CurrentMoodBias);
        harness.ViewModel.SetSleepTimerCommand.Execute(0).Subscribe();
        Assert.Equal("warm", harness.RecommendationService.CurrentMoodBias);
    }

    [Fact]
    public void EndOfTrackAndManualMode_DriveSleepSceneTransitions()
    {
        using var harness = new SleepSceneHarness();

        harness.ViewModel.SetSleepTimerCommand.Execute(-1).Subscribe();
        Assert.Equal(SleepTimerMode.EndOfTrack, harness.TimerService.Mode);
        Assert.Equal(RadioSceneMode.Sleep, harness.SceneService.CurrentMode);

        Assert.True(harness.TimerService.CheckAndConsumeEndOfTrack());
        Assert.Equal(RadioSceneMode.Default, harness.SceneService.CurrentMode);

        harness.SceneService.SetManualMode(RadioSceneMode.Sleep);
        Assert.Equal(RadioSceneMode.Sleep, harness.SceneService.CurrentMode);
        harness.SceneService.SetManualMode(RadioSceneMode.Default);
        Assert.Equal(RadioSceneMode.Default, harness.SceneService.CurrentMode);
        harness.SceneService.SetManualMode(null);
        Assert.Equal(RadioSceneMode.Default, harness.SceneService.CurrentMode);
    }

    [Fact]
    public void BuildTransitionPrompt_InjectsSleepContext_WhenSleepModeIsActive()
    {
        var profile = new DJProfile
        {
            Name = "Aster",
            Language = "zh",
            Description = "深夜温柔知性主播"
        };
        var current = MakeTrack("1", "上一首", "歌手A");
        var next = MakeTrack("2", "下一首", "歌手B");

        var prompt = DJService.BuildTransitionPrompt(
            profile, current, next, DateTime.Now, null, null,
            recentOnAirLines: null, listenerTopic: null,
            sceneMode: RadioSceneMode.Sleep);

        Assert.Contains("【伴眠电台情境】", prompt);
        Assert.Contains("极其温柔、轻柔、舒缓", prompt);
        Assert.Contains("[calm]", prompt);
    }

    [Fact]
    public void BuildTransitionPrompt_English_InjectsSleepContext()
    {
        var profile = new DJProfile
        {
            Name = "Lumen",
            Language = "en",
            Description = "Gentle night radio host"
        };
        var current = MakeTrack("1", "Track A", "Artist A");
        var next = MakeTrack("2", "Track B", "Artist B");

        var prompt = DJService.BuildTransitionPrompt(
            profile, current, next, DateTime.Now, null, null,
            recentOnAirLines: null, listenerTopic: null,
            sceneMode: RadioSceneMode.Sleep);

        Assert.Contains("[Sleep Radio Context]", prompt);
        Assert.Contains("bedside whisper", prompt);
        Assert.Contains("[calm]", prompt);
    }

    [Fact]
    public void BuildTransitionPrompt_DefaultMode_DoesNotContainSleepContext()
    {
        var profile = new DJProfile { Name = "小音", Language = "zh" };
        var current = MakeTrack("1", "A", "Artist A");
        var next = MakeTrack("2", "B", "Artist B");

        var prompt = DJService.BuildTransitionPrompt(
            profile, current, next, DateTime.Now, null, null,
            recentOnAirLines: null, listenerTopic: null,
            sceneMode: RadioSceneMode.Default);

        Assert.DoesNotContain("【伴眠电台情境】", prompt);
        Assert.DoesNotContain("[Sleep Radio Context]", prompt);
    }

    [Theory]
    [InlineData(30, 2, false, false)]   // 30分钟总长，剩余2分钟（<= 3分钟静默门限）-> 强制静默 false
    [InlineData(30, 3, true, false)]    // 相等边界（剩余3分钟 == 3分钟门限），即使开台也强制静默 false
    [InlineData(30, 10, false, true)]   // 30分钟总长，剩余10分钟（> 3分钟门限），满足间隔 -> 开口 true
    [InlineData(1, 0.1, false, false)]  // 1分钟总长（60秒），剩余6秒（<= 12秒自适应门限）-> 强制静默 false
    [InlineData(0, 0, false, true)]     // EndOfTrack 定时（目标为0）旁路时间门限，仅受 5 首间隔限制 -> 开口 true
    public void ShouldSpeakTransition_Enforces_Silence_WhenRemainingBelowThreshold(
        double targetMins, double remainingMins, bool stationOpening, bool expectedSpeak)
    {
        var target = TimeSpan.FromMinutes(targetMins);
        var remaining = TimeSpan.FromMinutes(remainingMins);

        var speak = MainWindowViewModel.ShouldSpeakTransition(
            stationOpening: stationOpening,
            transitionsSinceLastSpeech: 5,
            lastSpokenTimeBand: null,
            currentTimeBand: 0,
            isSleepMode: true,
            remainingTime: remaining,
            targetDuration: target);

        Assert.Equal(expectedSpeak, speak);
    }

    [Fact]
    public void ShouldSpeakTransition_SleepMode_Uses5TrackInterval()
    {
        var target = TimeSpan.FromMinutes(45);
        var remaining = TimeSpan.FromMinutes(30);

        // 伴眠模式下 4 首歌不够开口门槛（默认是 5）
        var speak4 = MainWindowViewModel.ShouldSpeakTransition(
            stationOpening: false,
            transitionsSinceLastSpeech: 4,
            lastSpokenTimeBand: 0,
            currentTimeBand: 0,
            isSleepMode: true,
            remainingTime: remaining,
            targetDuration: target);
        Assert.False(speak4);

        // 达到 5 首歌后方可开口
        var speak5 = MainWindowViewModel.ShouldSpeakTransition(
            stationOpening: false,
            transitionsSinceLastSpeech: 5,
            lastSpokenTimeBand: 0,
            currentTimeBand: 0,
            isSleepMode: true,
            remainingTime: remaining,
            targetDuration: target);
        Assert.True(speak5);
    }

    [Fact]
    public void FallbackIntroduction_Uses_CalmEmotion_UnderSleepMode()
    {
        var profile = new DJProfile { Name = "Aster", Language = "zh" };
        var next = MakeTrack("1", "晚安曲", "舒缓音乐家");

        var script = DJService.BuildFallbackIntroduction(
            profile, current: null, next: next, DateTime.Now, weather: null, variant: 0,
            sceneMode: RadioSceneMode.Sleep);

        Assert.Equal("calm", script.Emotion);
        Assert.Equal("calm", script.Expression);
        Assert.Equal("idle", script.Motion);
        Assert.Contains("愿好歌伴你安睡", script.Text);
    }

    [Theory]
    [InlineData("zh", "夜深爵士台", "为你连线网络电台《夜深爵士台》")]
    [InlineData("en", "Midnight Jazz FM", "live station \"Midnight Jazz FM\"")]
    public void FallbackIntroduction_LiveStreamUnderSleepMode_UsesDedicatedCalmCopy(
        string language, string stationName, string expectedSnippet)
    {
        var profile = new DJProfile { Name = "Aster", Language = language };
        var liveStation = MakeTrack("radio-1", stationName, "Jazz Broadcaster", isLive: true);

        var script = DJService.BuildFallbackIntroduction(
            profile, current: null, next: liveStation, DateTime.Now, weather: null, variant: 0,
            sceneMode: RadioSceneMode.Sleep);

        Assert.Equal("calm", script.Emotion);
        Assert.Equal("calm", script.Expression);
        Assert.Equal("idle", script.Motion);
        Assert.Contains(expectedSnippet, script.Text);
    }

    [Fact]
    public void EdgeTts_BuildSsml_Applies_WhisperProsody_AndClampsWithinBounds()
    {
        // 验证伴眠模式下的 SSML 参数
        var ssml = EdgeTtsService.BuildSsml("晚安，好梦", "zh-CN-XiaoxiaoNeural", emotion: "calm", sourceVoiceId: "female-yujie", isSleepMode: true);

        // 知性女声 baseline: (-3, -8)
        // calm emotion: (-1, -12, -5)
        // sleep mode delta: (-1, -6, -5)
        // finalPitch = -3 + (-1) + (-1) = -5Hz
        // finalRate = -8 + (-12) + (-6) = -26% -> Clamped to -25%
        // finalVolume = -5 + (-5) = -10%
        Assert.Contains("pitch='-5Hz'", ssml);
        Assert.Contains("rate='-25%'", ssml);
        Assert.Contains("volume='-10%'", ssml);
    }

    [Theory]
    [InlineData("夜店重低音DJ版", "网络歌手", true)]
    [InlineData("狂暴重金属摇滚", "摇滚乐队", true)]
    [InlineData("Lofi Hip Hop Chill Beats", "Lofi Girl", false)]
    [InlineData("月光奏鸣曲", "贝多芬", false)]
    public void CandidateRanker_Filters_AgitatedTracks_ForSleep(string title, string artist, bool expectedAgitated)
    {
        var candidate = new OnlineTrack
        {
            Title = title,
            Artist = artist,
            Source = "netease"
        };

        var isAgitated = CandidateRanker.IsAgitatedVersion(candidate);
        Assert.Equal(expectedAgitated, isAgitated);
    }

    [Fact]
    public void AppLanguage_SceneModeStrings_AreValidAndPaired()
    {
        var originalLanguage = AppLanguage.Current;
        try
        {
            Assert.NotEmpty(AppLanguage.Get("S_SceneModeSleep"));
            Assert.NotEmpty(AppLanguage.Get("S_SceneSleepStatus"));
            Assert.NotEmpty(AppLanguage.Get("S_SleepDjGoodnight"));

            AppLanguage.Apply("en");
            Assert.Equal("Sleep Radio", AppLanguage.Get("S_SceneModeSleep"));
            Assert.Equal("Sleep Radio · Goodnight", AppLanguage.Get("S_SceneSleepStatus"));

            AppLanguage.Apply("zh");
            Assert.Equal("伴眠电台", AppLanguage.Get("S_SceneModeSleep"));
            Assert.Equal("伴眠电台 · 晚安", AppLanguage.Get("S_SceneSleepStatus"));
            Assert.Equal("专注模式", AppLanguage.Get("S_SceneModeFocus"));
            Assert.Equal("专注电台 · 纯音免打扰", AppLanguage.Get("S_SceneFocusStatus"));
            Assert.NotEmpty(AppLanguage.Get("S_FocusModeTooltipOff"));
            Assert.NotEmpty(AppLanguage.Get("S_FocusModeTooltipOn"));

            AppLanguage.Apply("en");
            Assert.Equal("Focus Mode", AppLanguage.Get("S_SceneModeFocus"));
            Assert.Equal("Focus Radio · Do Not Disturb", AppLanguage.Get("S_SceneFocusStatus"));
            Assert.NotEmpty(AppLanguage.Get("S_FocusModeTooltipOff"));
            Assert.NotEmpty(AppLanguage.Get("S_FocusModeTooltipOn"));
        }
        finally
        {
            AppLanguage.Apply(originalLanguage);
        }
    }

    [Fact]
    public void FocusMode_ToggleCommand_ActivatesAndRestoresMoodBias()
    {
        using var harness = new SleepSceneHarness();

        harness.RecommendationService.SetMoodBias("energetic");
        Assert.Equal("energetic", harness.RecommendationService.CurrentMoodBias);
        Assert.False(harness.ViewModel.IsFocusRadioMode);
        Assert.False(harness.SceneService.IsFocusMode);

        // 开启专注模式 -> 切换为 Focus，锁定 focus 氛围，通知 DJ
        harness.ViewModel.ToggleFocusModeCommand.Execute().Subscribe();
        Assert.Equal(RadioSceneMode.Focus, harness.ViewModel.CurrentSceneMode);
        Assert.True(harness.ViewModel.IsFocusRadioMode);
        Assert.True(harness.SceneService.IsFocusMode);
        Assert.False(harness.ViewModel.IsSleepRadioMode);
        Assert.Equal("focus", harness.RecommendationService.CurrentMoodBias);
        harness.DjMock.Verify(x => x.UpdateSceneMode(RadioSceneMode.Focus), Times.Once);

        // 再次点击退出专注模式 -> 原子复原原氛围 energetic
        harness.ViewModel.ToggleFocusModeCommand.Execute().Subscribe();
        Assert.Equal(RadioSceneMode.Default, harness.ViewModel.CurrentSceneMode);
        Assert.False(harness.ViewModel.IsFocusRadioMode);
        Assert.False(harness.SceneService.IsFocusMode);
        Assert.Equal("energetic", harness.RecommendationService.CurrentMoodBias);
        harness.DjMock.Verify(x => x.UpdateSceneMode(RadioSceneMode.Default), Times.Once);
    }

    [Fact]
    public void FocusAndSleepMode_CrossTransitions_PreserveOriginalMoodBias()
    {
        using var harness = new SleepSceneHarness();

        // 1. Default("energetic") -> Focus("focus") -> 启动睡眠定时自动切 Sleep("calm") -> 取消回到 Default("energetic")
        harness.RecommendationService.SetMoodBias("energetic");
        harness.ViewModel.ToggleFocusModeCommand.Execute().Subscribe();
        Assert.Equal(RadioSceneMode.Focus, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("focus", harness.RecommendationService.CurrentMoodBias);

        harness.ViewModel.SetSleepTimerCommand.Execute(30).Subscribe();
        Assert.Equal(RadioSceneMode.Sleep, harness.ViewModel.CurrentSceneMode);
        Assert.True(harness.ViewModel.IsSleepRadioMode);
        Assert.False(harness.ViewModel.IsFocusRadioMode);
        Assert.Equal("calm", harness.RecommendationService.CurrentMoodBias);

        harness.ViewModel.SetSleepTimerCommand.Execute(0).Subscribe();
        Assert.Equal(RadioSceneMode.Default, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("energetic", harness.RecommendationService.CurrentMoodBias);

        // 2. Default("warm") -> Sleep("calm") -> 手动开启 Focus("focus") -> 取消睡眠定时后再关 Focus -> 回到 Default("warm")
        harness.RecommendationService.SetMoodBias("warm");
        harness.ViewModel.SetSleepTimerCommand.Execute(15).Subscribe();
        Assert.Equal(RadioSceneMode.Sleep, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("calm", harness.RecommendationService.CurrentMoodBias);

        harness.ViewModel.ToggleFocusModeCommand.Execute().Subscribe();
        Assert.Equal(RadioSceneMode.Focus, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("focus", harness.RecommendationService.CurrentMoodBias);

        harness.ViewModel.SetSleepTimerCommand.Execute(0).Subscribe();
        Assert.Equal(RadioSceneMode.Focus, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("focus", harness.RecommendationService.CurrentMoodBias);

        harness.ViewModel.ToggleFocusModeCommand.Execute().Subscribe();
        Assert.Equal(RadioSceneMode.Default, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("warm", harness.RecommendationService.CurrentMoodBias);

        // 3. Default("lofi") -> Sleep("calm") -> 手动切 Focus("focus") -> 定时仍激活时先关 Focus 应回到 Sleep("calm") -> 关定时恢复 "lofi"
        harness.RecommendationService.SetMoodBias("lofi");
        harness.ViewModel.SetSleepTimerCommand.Execute(20).Subscribe();
        Assert.Equal(RadioSceneMode.Sleep, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("calm", harness.RecommendationService.CurrentMoodBias);

        harness.ViewModel.ToggleFocusModeCommand.Execute().Subscribe();
        Assert.Equal(RadioSceneMode.Focus, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("focus", harness.RecommendationService.CurrentMoodBias);

        harness.ViewModel.ToggleFocusModeCommand.Execute().Subscribe();
        Assert.Equal(RadioSceneMode.Sleep, harness.ViewModel.CurrentSceneMode);
        Assert.True(harness.ViewModel.IsSleepRadioMode);
        Assert.False(harness.ViewModel.IsFocusRadioMode);
        Assert.Equal("calm", harness.RecommendationService.CurrentMoodBias);

        harness.ViewModel.SetSleepTimerCommand.Execute(0).Subscribe();
        Assert.Equal(RadioSceneMode.Default, harness.ViewModel.CurrentSceneMode);
        Assert.Equal("lofi", harness.RecommendationService.CurrentMoodBias);
    }

    [Theory]
    [InlineData(true, 0, null, 0, true)]    // 开台首曲允许简短致意
    [InlineData(false, 0, 0, 0, false)]     // 曲间静默
    [InlineData(false, 10, 0, 0, false)]    // 超过默认/伴眠间隔仍静默
    [InlineData(false, 0, 0, 1, false)]     // 跨时间带在专注模式下同样静默免打扰
    public void ShouldSpeakTransition_FocusMode_SilencesAllMidSessionTransitions_AllowsStationOpening(
        bool stationOpening,
        int transitionsSinceLastSpeech,
        int? lastSpokenTimeBand,
        int currentTimeBand,
        bool expectedSpeak)
    {
        var speak = MainWindowViewModel.ShouldSpeakTransition(
            stationOpening: stationOpening,
            transitionsSinceLastSpeech: transitionsSinceLastSpeech,
            lastSpokenTimeBand: lastSpokenTimeBand,
            currentTimeBand: currentTimeBand,
            isSleepMode: false,
            remainingTime: default,
            targetDuration: default,
            sceneMode: RadioSceneMode.Focus);

        Assert.Equal(expectedSpeak, speak);
    }

    [Fact]
    public void BuildTransitionPrompt_InjectsFocusContext_InChineseAndEnglish()
    {
        var zhProfile = new DJProfile { Name = "小音", Language = "zh" };
        var enProfile = new DJProfile { Name = "Lumen", Language = "en" };
        var current = MakeTrack("1", "Track A", "Artist A");
        var next = MakeTrack("2", "River Flows in You", "Yiruma");

        var zhPrompt = DJService.BuildTransitionPrompt(
            zhProfile, current, next, DateTime.Now, null, null,
            recentOnAirLines: null, listenerTopic: null,
            sceneMode: RadioSceneMode.Focus);
        Assert.Contains("【专注电台情境】", zhPrompt);
        Assert.Contains("严禁提问或寒暄", zhPrompt);
        Assert.Contains("[calm]", zhPrompt);

        var enPrompt = DJService.BuildTransitionPrompt(
            enProfile, current, next, DateTime.Now, null, null,
            recentOnAirLines: null, listenerTopic: null,
            sceneMode: RadioSceneMode.Focus);
        Assert.Contains("[Focus Radio Context]", enPrompt);
        Assert.Contains("minimal cognitive interruption", enPrompt);
        Assert.Contains("[calm]", enPrompt);
    }

    [Fact]
    public void FallbackIntroduction_Uses_FocusCopyAndCalmEmotion_UnderFocusMode()
    {
        var zhProfile = new DJProfile { Name = "小音", Language = "zh" };
        var enProfile = new DJProfile { Name = "Lumen", Language = "en" };
        var track = MakeTrack("1", "River Flows in You", "Yiruma");
        var liveStation = MakeTrack("radio-1", "Lofi Girl Radio", "Lofi Beats", isLive: true);

        var zhTrackScript = DJService.BuildFallbackIntroduction(
            zhProfile, current: null, next: track, DateTime.Now, weather: null, variant: 0,
            sceneMode: RadioSceneMode.Focus);
        Assert.Equal("calm", zhTrackScript.Emotion);
        Assert.Equal("calm", zhTrackScript.Expression);
        Assert.Equal("idle", zhTrackScript.Motion);
        Assert.Contains("专注电台", zhTrackScript.Text);

        var enTrackScript = DJService.BuildFallbackIntroduction(
            enProfile, current: null, next: track, DateTime.Now, weather: null, variant: 0,
            sceneMode: RadioSceneMode.Focus);
        Assert.Equal("calm", enTrackScript.Emotion);
        Assert.Equal("idle", enTrackScript.Motion);
        Assert.Contains("Focus Mode", enTrackScript.Text);

        var zhLiveScript = DJService.BuildFallbackIntroduction(
            zhProfile, current: null, next: liveStation, DateTime.Now, weather: null, variant: 0,
            sceneMode: RadioSceneMode.Focus);
        Assert.Equal("calm", zhLiveScript.Emotion);
        Assert.Equal("idle", zhLiveScript.Motion);
        Assert.Contains("专注电台", zhLiveScript.Text);
        Assert.Contains("Lofi Girl Radio", zhLiveScript.Text);

        var enLiveScript = DJService.BuildFallbackIntroduction(
            enProfile, current: null, next: liveStation, DateTime.Now, weather: null, variant: 0,
            sceneMode: RadioSceneMode.Focus);
        Assert.Equal("calm", enLiveScript.Emotion);
        Assert.Equal("idle", enLiveScript.Motion);
        Assert.Contains("Focus Mode", enLiveScript.Text);
        Assert.Contains("Lofi Girl Radio", enLiveScript.Text);
    }

    [Fact]
    public async Task CandidateRanker_AllowsPureInstrumental_InFocusMode_WhileBlockingKaraokeAndAgitated()
    {
        var pureZh = new OnlineTrack { Id = "1", Title = "夜的钢琴曲 (纯音乐)", Artist = "石进", Source = "netease" };
        var pureEn = new OnlineTrack { Id = "2", Title = "Gymnopédie No. 1 [Instrumental]", Artist = "Erik Satie", Source = "netease" };
        var karaoke = new OnlineTrack { Id = "3", Title = "晴天 (伴奏)", Artist = "周杰伦", Source = "netease" };
        var karaokeEn = new OnlineTrack { Id = "4", Title = "Yellow (Karaoke)", Artist = "Coldplay", Source = "netease" };
        var instBacking = new OnlineTrack { Id = "4b", Title = "晴天 (Inst.)", Artist = "周杰伦", Source = "netease" };
        var cover = new OnlineTrack { Id = "5", Title = "晴天 (Cover)", Artist = "翻唱", Source = "netease" };
        var rock = new OnlineTrack { Id = "6", Title = "狂暴重金属摇滚", Artist = "摇滚乐队", Source = "netease" };

        // 默认模式拦截纯音乐与伴奏；专注模式放行纯音乐/Instrumental，仍拦截卡拉OK伴奏、(Inst.)与翻唱
        Assert.True(CandidateRanker.IsUnwantedVersion(pureZh, "夜的钢琴曲", allowPureInstrumental: false));
        Assert.False(CandidateRanker.IsUnwantedVersion(pureZh, "夜的钢琴曲", allowPureInstrumental: true));
        Assert.False(CandidateRanker.IsUnwantedVersion(pureEn, "Gymnopédie", allowPureInstrumental: true));
        Assert.True(CandidateRanker.IsUnwantedVersion(karaoke, "晴天", allowPureInstrumental: true));
        Assert.True(CandidateRanker.IsUnwantedVersion(karaokeEn, "Yellow", allowPureInstrumental: true));
        Assert.True(CandidateRanker.IsUnwantedVersion(instBacking, "晴天", allowPureInstrumental: true));
        Assert.True(CandidateRanker.IsUnwantedVersion(cover, "晴天", allowPureInstrumental: true));
        Assert.True(CandidateRanker.IsAgitatedVersion(rock));

        // 真实 RecommendationService + Broker 端到端接线：验证 Prompt 注入专注选曲偏好、Broker 走纯音乐评分放行、
        // 且专注场景激活期间即使发生额外氛围偏好变更（如用户误点 FIRE），专注过滤约束依然保持生效
        var originalLanguage = AppLanguage.Current;
        AppLanguage.Apply("zh");
        try
        {
            string? capturedPrompt = null;
            var llmMock = new Mock<ILLMService>();
            llmMock.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
                .Callback<string, List<ChatMessage>>((p, _) => capturedPrompt = p)
                .ReturnsAsync("石进 夜的钢琴曲");
            var brokerMock = new Mock<Services.Music.IMusicSourceBroker>();
            var brokerSearchCalls = 0;
            brokerMock.Setup(x => x.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    MusicSearchIntent.Automatic,
                    true,
                    It.IsAny<System.Threading.CancellationToken>()))
                .Callback(() => brokerSearchCalls++)
                .ReturnsAsync(new List<OnlineTrack> { karaoke, instBacking, rock, pureZh, pureEn });
            brokerMock.Setup(x => x.ResolveTrackAsync(
                    It.IsAny<OnlineTrack>(),
                    false,
                    It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync((OnlineTrack t, bool _, System.Threading.CancellationToken _) =>
                    new Services.Music.ResolveTrackResult(
                        $"https://example.com/{t.Id}.mp3",
                        new ProviderTrackRef(t.Source, t.Id),
                        t.Source,
                        new Dictionary<string, string>()));

            var recService = new RecommendationService(llmMock.Object, brokerMock.Object);
            recService.UpdateSceneMode(RadioSceneMode.Focus);
            recService.SetMoodBias("energetic");
            var program = await recService.CreateProgramAsync(new RecommendationRequest { UserIntent = "继续当前电台" });

            Assert.NotNull(capturedPrompt);
            Assert.Contains("专注模式选曲偏好", capturedPrompt);
            Assert.Equal(1, brokerSearchCalls);
            Assert.Equal("focus", program.Context.Scene);
            var titles = program.Tracks.Select(t => t.Track.Title).ToList();
            Assert.Contains(pureZh.Title, titles);
            Assert.Contains(pureEn.Title, titles);
            Assert.DoesNotContain(karaoke.Title, titles);
            Assert.DoesNotContain(instBacking.Title, titles);
            Assert.DoesNotContain(rock.Title, titles);

            // 验证 LLM 异常回退时 BuildFallbackQueries 优先命中专注关键词（即使已有当前歌手）
            var queriedKeywords = new List<string>();
            llmMock.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
                .ThrowsAsync(new InvalidOperationException("offline"));
            brokerMock.Setup(x => x.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    MusicSearchIntent.Automatic,
                    true,
                    It.IsAny<System.Threading.CancellationToken>()))
                .Callback<string, int, MusicSearchIntent, bool, System.Threading.CancellationToken>(
                    (q, _, _, _, _) => queriedKeywords.Add(q))
                .ReturnsAsync(new List<OnlineTrack> { pureZh });
            await recService.CreateProgramAsync(new RecommendationRequest
            {
                UserIntent = "继续当前电台",
                CurrentTrack = MakeTrack("cur", "晴天", "周杰伦")
            });
            Assert.Contains(queriedKeywords, q => q.Contains("纯音乐") || q.Contains("instrumental", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            AppLanguage.Apply(originalLanguage);
        }
    }

    [AvaloniaFact]
    public void FocusModeButton_InMainWindow_TogglesFocusModeAndUpdatesTooltip()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        var originalLanguage = AppLanguage.Current;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
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

            var focusBtn = window.FindControl<Button>("FocusModeButton")
                ?? throw new InvalidOperationException("FocusModeButton not found");
            var buttonOnClick = typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Button.OnClick not found");
            Assert.Same(vm.ToggleFocusModeCommand, focusBtn.Command);
            Assert.DoesNotContain("active", focusBtn.Classes);
            Assert.Equal(AppLanguage.Get("S_FocusModeTooltipOff"), ToolTip.GetTip(focusBtn));

            buttonOnClick.Invoke(focusBtn, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsFocusRadioMode);
            Assert.Contains("active", focusBtn.Classes);
            Assert.Equal(AppLanguage.Get("S_FocusModeTooltipOn"), ToolTip.GetTip(focusBtn));

            AppLanguage.Apply("en");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AppLanguage.Get("S_FocusModeTooltipOn"), ToolTip.GetTip(focusBtn));

            buttonOnClick.Invoke(focusBtn, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsFocusRadioMode);
            Assert.DoesNotContain("active", focusBtn.Classes);
            Assert.Equal(AppLanguage.Get("S_FocusModeTooltipOff"), ToolTip.GetTip(focusBtn));
        }
        finally
        {
            window?.Close();
            Application.Current!.Styles.Remove(theme);
            vm.Dispose();
            AppLanguage.Apply(originalLanguage);
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
