using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 电台模式与播放上下文的优先级语义：切入 radio 即由 DJ 接管后续选曲，
/// 残留的歌单上下文不得在曲目结束时继续旁路电台推荐串场链。
/// </summary>
public class RadioModePlaybackContextTests
{
    private static Track NewTrack(string title) => new()
    {
        Title = title,
        Artist = "测试",
        // 惯例：TEST-NET-3 假 URL，仅作身份占位不期望真正出声
        FilePath = $"https://203.0.113.{10 + title.Length % 80}/{title}.mp3"
    };

    [Fact]
    public void SetRepeatMode_ToRadio_ClearsPlaybackContext()
    {
        var svc = new AudioService();
        try
        {
            svc.SetRepeatMode("list");
            svc.StartPlaybackContext(new[] { NewTrack("甲"), NewTrack("乙") }, 0, contextName: "歌单 · 测试");
            Assert.Equal(2, svc.PlaybackQueue.Count);

            svc.SetRepeatMode("radio");

            Assert.Empty(svc.PlaybackQueue);
            Assert.Equal(string.Empty, svc.PlaybackContextName);
            Assert.Equal("radio", svc.RepeatMode);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void SetRepeatMode_BetweenNonRadioModes_KeepsPlaybackContext()
    {
        var svc = new AudioService();
        try
        {
            svc.SetRepeatMode("list");
            svc.StartPlaybackContext(new[] { NewTrack("甲"), NewTrack("乙") }, 0, contextName: "歌单 · 测试");

            svc.SetRepeatMode("single");

            Assert.Equal(2, svc.PlaybackQueue.Count);
            Assert.Equal("歌单 · 测试", svc.PlaybackContextName);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void SetRepeatMode_SameValue_DoesNotClearAgain()
    {
        var svc = new AudioService();
        try
        {
            svc.SetRepeatMode("list");
            svc.StartPlaybackContext(new[] { NewTrack("甲"), NewTrack("乙") }, 0, contextName: "歌单 · 测试");

            // 同值重设：不触发清理路径（幂等）
            svc.SetRepeatMode("list");

            Assert.Equal(2, svc.PlaybackQueue.Count);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void SetRepeatMode_DefaultToRadio_AlsoClearsPreexistingContext()
    {
        var svc = new AudioService();
        try
        {
            // 默认模式即 radio（AudioService 初始 _repeatMode="radio"），
            // 但上下文可能由其他路径建立；显式再设一次 radio 也应解除劫持
            svc.SetRepeatMode("single");
            svc.StartPlaybackContext(new[] { NewTrack("甲"), NewTrack("乙") }, 0, contextName: "歌单 · 测试");

            svc.SetRepeatMode("radio");

            Assert.Empty(svc.PlaybackQueue);
        }
        finally
        {
            svc.Dispose();
        }
    }

    // 推荐回调在途（NextAsync 最长约 60s 预算）时用户按停止：迟到的推荐不得开播。
    // Next() 的同步前导（信号量即得→锁内快照意图版本→挂起等回调）保证 Stop()
    // 必然发生在快照之后，时序确定
    [Fact]
    public async Task StopDuringInFlightRadioRecommendation_DiscardsLateTrack()
    {
        var svc = new AudioService();
        try
        {
            svc.SetRepeatMode("radio");
            var recommendation = new TaskCompletionSource<Track?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            svc.SetNextCallback(() => recommendation.Task);

            var played = new TaskCompletionSource<Track?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = svc.TrackChanged.Subscribe(track => played.TrySetResult(track));

            svc.Next();
            svc.Stop();
            recommendation.SetResult(NewTrack("迟到"));

            // 负向断言的宽限窗：丢弃路径为纯同步的锁+版本比较，无 IO，
            // 500ms 远超实际耗时；若缺陷存在，开播会在回调完成的毫秒级内发生
            var winner = await Task.WhenAny(played.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(played.Task, winner);
            Assert.Null(svc.CurrentTrack);
            Assert.Empty(svc.Playlist);
        }
        finally
        {
            svc.Dispose();
        }
    }
}
