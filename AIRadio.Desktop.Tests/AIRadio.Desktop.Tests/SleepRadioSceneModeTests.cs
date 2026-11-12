using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
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
            IsLiveStream = isLive
        };

    [Fact]
    public void SleepTimer_Activates_SleepRadioSceneMode_AndCancels()
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.TrackChanged).Returns(Observable.Never<Track?>());

        var timerService = new SleepTimerService(audioMock.Object, ImmediateScheduler.Instance, ImmediateScheduler.Instance);
        var sceneService = new SceneModeService(timerService, ImmediateScheduler.Instance);

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

    [Fact]
    public void SleepTimer_Restores_OriginalMoodBias_OnCancel()
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.TrackChanged).Returns(Observable.Never<Track?>());

        var timerService = new SleepTimerService(audioMock.Object, ImmediateScheduler.Instance, ImmediateScheduler.Instance);
        var sceneService = new SceneModeService(timerService, ImmediateScheduler.Instance);

        var llmMock = new Mock<ILLMService>();
        var searchMock = new Mock<IMusicSearchService>();
        var recService = new RecommendationService(llmMock.Object, searchMock.Object);

        // 初始设置氛围为 energetic
        recService.SetMoodBias("energetic");
        Assert.Equal("energetic", recService.CurrentMoodBias);

        string? preSleepMoodBias = null;
        var modeSub = sceneService.ModeChanged.Subscribe(mode =>
        {
            if (mode == RadioSceneMode.Sleep)
            {
                preSleepMoodBias = recService.CurrentMoodBias;
                recService.SetMoodBias("calm");
            }
            else
            {
                recService.SetMoodBias(preSleepMoodBias);
                preSleepMoodBias = null;
            }
        });

        // 激活伴眠模式
        timerService.StartDurationTimer(TimeSpan.FromMinutes(30));
        Assert.Equal("calm", recService.CurrentMoodBias);

        // 取消伴眠模式 -> 自动恢复为 energetic
        timerService.CancelTimer();
        Assert.Equal("energetic", recService.CurrentMoodBias);

        modeSub.Dispose();
        sceneService.Dispose();
        timerService.Dispose();
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
    [InlineData(30, 2, false)]  // 30分钟总长，剩余2分钟（<= 3分钟静默门限）-> 强制静默 false
    [InlineData(30, 10, true)]  // 30分钟总长，剩余10分钟（> 3分钟门限），满足间隔 -> 开口 true
    [InlineData(1, 0.1, false)] // 1分钟总长（60秒），剩余6秒（<= 12秒自适应门限）-> 强制静默 false
    public void ShouldSpeakTransition_Enforces_Silence_WhenRemainingBelowThreshold(
        double targetMins, double remainingMins, bool expectedSpeak)
    {
        var target = TimeSpan.FromMinutes(targetMins);
        var remaining = TimeSpan.FromMinutes(remainingMins);

        var speak = MainWindowViewModel.ShouldSpeakTransition(
            stationOpening: false,
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
        Assert.NotEmpty(AppLanguage.Get("S_SceneModeSleep"));
        Assert.NotEmpty(AppLanguage.Get("S_SceneSleepStatus"));
        Assert.NotEmpty(AppLanguage.Get("S_SleepDjGoodnight"));

        AppLanguage.Apply("en");
        Assert.Equal("Sleep Radio", AppLanguage.Get("S_SceneModeSleep"));
        Assert.Equal("Sleep Radio · Goodnight", AppLanguage.Get("S_SceneSleepStatus"));

        AppLanguage.Apply("zh");
        Assert.Equal("伴眠电台", AppLanguage.Get("S_SceneModeSleep"));
        Assert.Equal("伴眠电台 · 晚安", AppLanguage.Get("S_SceneSleepStatus"));
    }
}
