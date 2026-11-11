using System;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class SleepTimerServiceTests
{
    private readonly Mock<IAudioService> _audioMock = new();
    private readonly Subject<Track?> _trackEndedSubject = new();
    private readonly Subject<Track?> _trackChangedSubject = new();
    private readonly HistoricalScheduler _scheduler = new();
    private float _currentVolume = 0.8f;

    public SleepTimerServiceTests()
    {
        _audioMock.SetupGet(a => a.TrackEnded).Returns(_trackEndedSubject);
        _audioMock.SetupGet(a => a.TrackChanged).Returns(_trackChangedSubject);
        _audioMock.SetupProperty(a => a.Volume, _currentVolume);
        _audioMock.Setup(a => a.Volume).Returns(() => _currentVolume);
        _audioMock.SetupSet(a => a.Volume = It.IsAny<float>())
            .Callback<float>(v => _currentVolume = v);
    }

    [Fact]
    public void DurationTimer_CountsDownAndPausesAudio()
    {
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        service.StartDurationTimer(TimeSpan.FromSeconds(30));

        Assert.True(service.IsActive);
        Assert.Equal(SleepTimerMode.Duration, service.Mode);
        Assert.Equal(TimeSpan.FromSeconds(30), service.RemainingTime);

        // 推进 10 秒
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(20), service.RemainingTime);
        _audioMock.Verify(a => a.Pause(), Times.Never);

        // 推进 20 秒直到到期
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.Zero, service.RemainingTime);
        Assert.False(service.IsActive);
        Assert.Equal(SleepTimerMode.Disabled, service.Mode);
        _audioMock.Verify(a => a.Pause(), Times.Once);
    }

    [Fact]
    public void FadeOut_LinearlyReducesVolume_AndRestoresAfterPause()
    {
        _currentVolume = 0.8f;
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        service.StartDurationTimer(TimeSpan.FromSeconds(30));

        // 推进到最后 10 秒前
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(20));
        Assert.True(service.IsFadingOut);

        // 推进 5 秒（剩余 5 秒，淡出过半，音量约 0.4）
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(5));
        Assert.True(service.IsFadingOut);
        Assert.True(_currentVolume < 0.8f && _currentVolume > 0.1f, $"Expected faded volume around 0.4, but was {_currentVolume}");

        // 推进到到期
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(5));
        _audioMock.Verify(a => a.Pause(), Times.Once);
        // Pause 之后音量必须原子恢复为原始设定值 0.8
        Assert.Equal(0.8f, _currentVolume, 2);
        Assert.False(service.IsFadingOut);
    }

    [Fact]
    public void ManualVolumeChange_InterruptsFadeOut()
    {
        _currentVolume = 0.8f;
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        service.StartDurationTimer(TimeSpan.FromSeconds(30));

        // 推进到淡出开始
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(20));
        Assert.True(service.IsFadingOut);

        // 用户手动调整音量为 0.95f（与当前淡出算法计算值偏差 > 0.05）
        _currentVolume = 0.95f;

        // 继续推进 3 秒
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(3));

        // 音量不应被算法压制，仍保持用户手动设定的 0.95f
        Assert.Equal(0.95f, _currentVolume, 2);
    }

    [Fact]
    public void EndOfTrack_PausesOnNaturalTrackEnd()
    {
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        var track = new Track { Title = "Test Song", SourceId = "netease" };
        _audioMock.SetupGet(a => a.CurrentTrack).Returns(track);

        service.StartEndOfTrackTimer();
        Assert.True(service.IsActive);
        Assert.Equal(SleepTimerMode.EndOfTrack, service.Mode);

        _trackEndedSubject.OnNext(track);
        _scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100));

        Assert.False(service.IsActive);
        Assert.Equal(SleepTimerMode.Disabled, service.Mode);
        _audioMock.Verify(a => a.Pause(), Times.Once);
    }

    [Fact]
    public void EndOfTrack_CancelledWhenSwitchingToLiveStream()
    {
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        var track = new Track { Title = "Test Song", SourceId = "netease" };
        _audioMock.SetupGet(a => a.CurrentTrack).Returns(track);

        service.StartEndOfTrackTimer();
        Assert.True(service.IsActive);

        // 切歌到直播电台流
        var liveTrack = new Track { Title = "Live Radio", SourceId = "radio-browser:station-1" };
        _trackChangedSubject.OnNext(liveTrack);
        _scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100));

        Assert.False(service.IsActive);
        Assert.Equal(SleepTimerMode.Disabled, service.Mode);
    }

    [Fact]
    public void StartEndOfTrackTimer_IgnoredIfCurrentlyLiveStream()
    {
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        var liveTrack = new Track { Title = "Live Radio", SourceId = "radio-browser:station-1" };
        _audioMock.SetupGet(a => a.CurrentTrack).Returns(liveTrack);

        service.StartEndOfTrackTimer();
        Assert.False(service.IsActive);
        Assert.Equal(SleepTimerMode.Disabled, service.Mode);
    }

    [Fact]
    public void CancelTimer_StopsCountdownAndRestoresVolumeIfFading()
    {
        _currentVolume = 0.8f;
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        service.StartDurationTimer(TimeSpan.FromSeconds(30));

        // 推进到淡出中
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(25));
        Assert.True(service.IsFadingOut);
        Assert.True(_currentVolume < 0.8f);

        service.CancelTimer();

        Assert.False(service.IsActive);
        Assert.Equal(SleepTimerMode.Disabled, service.Mode);
        Assert.Equal(0.8f, _currentVolume, 2);

        // 之后即使时间继续推进，也不会再触发 Pause
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(10));
        _audioMock.Verify(a => a.Pause(), Times.Never);
    }

    [Fact]
    public void CloseAppOnExpiry_DispatchesShutdown_ForDuration()
    {
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        var shutdownTriggered = false;
        service.RequestAppShutdown += () => shutdownTriggered = true;
        service.CloseAppOnExpiry = true;

        service.StartDurationTimer(TimeSpan.FromSeconds(5));
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(5));

        Assert.True(shutdownTriggered);
        _audioMock.Verify(a => a.Pause(), Times.Once);
    }

    [Fact]
    public void CloseAppOnExpiry_DispatchesShutdown_ForEndOfTrack()
    {
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        var shutdownTriggered = false;
        service.RequestAppShutdown += () => shutdownTriggered = true;
        service.CloseAppOnExpiry = true;

        service.StartEndOfTrackTimer();
        var consumed = service.CheckAndConsumeEndOfTrack();

        Assert.True(consumed);
        Assert.True(shutdownTriggered);
        _audioMock.Verify(a => a.Pause(), Times.Once);
    }

    [Fact]
    public void CheckAndConsumeEndOfTrack_IsIdempotent()
    {
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        service.StartEndOfTrackTimer();

        Assert.True(service.CheckAndConsumeEndOfTrack());
        Assert.False(service.CheckAndConsumeEndOfTrack());
        _audioMock.Verify(a => a.Pause(), Times.Once);
    }

    [Fact]
    public void Dispose_DuringFadeOut_RestoresBaseVolume()
    {
        _currentVolume = 0.8f;
        var service = new SleepTimerService(_audioMock.Object, _scheduler);
        service.StartDurationTimer(TimeSpan.FromSeconds(30));

        // 推进到淡出中
        _scheduler.AdvanceBy(TimeSpan.FromSeconds(25));
        Assert.True(service.IsFadingOut);
        Assert.True(_currentVolume < 0.8f);

        // 在淡出中途释放服务
        service.Dispose();

        // 音量必须立即恢复为基准 0.8f
        Assert.Equal(0.8f, _currentVolume, 2);
        Assert.False(service.IsActive);
    }

    [Fact]
    public void DisplayStatus_UpdatesOnLanguageChange_ForEndOfTrack()
    {
        using var service = new SleepTimerService(_audioMock.Object, _scheduler);
        AppLanguage.Apply("zh");
        service.StartEndOfTrackTimer();

        Assert.Equal("播完停播", service.DisplayStatus);

        AppLanguage.Apply("en");
        Assert.Equal("Stop at end", service.DisplayStatus);

        AppLanguage.Apply("zh");
        Assert.Equal("播完停播", service.DisplayStatus);
    }
}
