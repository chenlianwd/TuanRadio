using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 歌词 VM 状态机：取词触发、进度滚动与边界、旧词晚到丢弃、状态文案真值表与语言切换、Dispose 冻结。
/// </summary>
public class LyricsViewModelTests
{
    private static LyricResult LinesResult(params (int sec, string text)[] lines)
        => new()
        {
            Lines = new List<LyricLine>(
                lines.Select(l => new LyricLine(TimeSpan.FromSeconds(l.sec), l.text)))
        };

    private static (LyricsViewModel vm, Subject<Track?> tracks, Subject<TimeSpan> positions, Mock<ILyricService> lyrics, Action Dispose) Create(
        Track? currentTrack = null)
    {
        var tracks = new Subject<Track?>();
        var positions = new Subject<TimeSpan>();
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.TrackChanged).Returns(tracks);
        audio.SetupGet(x => x.PositionChanged).Returns(positions);
        audio.SetupGet(x => x.CurrentTrack).Returns(currentTrack);
        audio.SetupGet(x => x.CurrentPosition).Returns(TimeSpan.Zero);

        var lyricService = new Mock<ILyricService>();
        var vm = new LyricsViewModel(audio.Object, lyricService.Object);

        return (vm, tracks, positions, lyricService, vm.Dispose);
    }

    private static Track Track(string sourceId)
        => new() { SourceId = sourceId, Title = "歌", Artist = "手" };

    [Fact]
    public void TrackChanged_FetchesLyricsAndShowsCurrentLines()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("netease:1");
            var (vm, tracks, positions, lyrics, dispose) = Create(track);
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync(LinesResult((10, "第一句"), (20, "第二句"), (30, "第三句")));

            tracks.OnNext(track);

            Assert.True(vm.HasLyrics);
            Assert.Equal(string.Empty, vm.CurrentLineText); // CurrentPosition=0 且首句在 10s → 前奏态
            Assert.Equal("第一句", vm.NextLineText);
            Assert.Equal(string.Empty, vm.PreviousLineText);
            Assert.Empty(vm.LyricStatusText);
            Assert.False(vm.HasStatusText);

            positions.OnNext(TimeSpan.FromSeconds(25));
            Assert.Equal("第二句", vm.CurrentLineText);
            Assert.Equal("第一句", vm.PreviousLineText);
            Assert.Equal("第三句", vm.NextLineText);
            dispose();
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void PositionBeforeFirstLine_ShowsFirstLineAsNext()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("netease:1");
            var (vm, tracks, positions, lyrics, dispose) = Create(track);
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync(LinesResult((10, "第一句"), (20, "第二句")));

            tracks.OnNext(track);
            positions.OnNext(TimeSpan.FromSeconds(5));

            Assert.True(vm.HasLyrics);
            Assert.Equal(string.Empty, vm.CurrentLineText);
            Assert.Equal(string.Empty, vm.PreviousLineText);
            Assert.Equal("第一句", vm.NextLineText);
            dispose();
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void PositionAfterLastLine_LeavesNextEmpty()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("netease:1");
            var (vm, tracks, positions, lyrics, dispose) = Create(track);
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync(LinesResult((10, "第一句"), (20, "第二句")));

            tracks.OnNext(track);
            positions.OnNext(TimeSpan.FromSeconds(100));

            Assert.Equal("第二句", vm.CurrentLineText);
            Assert.Equal("第一句", vm.PreviousLineText);
            Assert.Equal(string.Empty, vm.NextLineText);
            dispose();
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void TrackChangedNull_ClearsStateWithoutFetching()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("netease:1");
            var (vm, tracks, _, lyrics, dispose) = Create(track);
            lyrics.Setup(x => x.GetLyricsAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(LinesResult((10, "句")));

            tracks.OnNext(track);
            tracks.OnNext(null);

            Assert.False(vm.HasLyrics);
            Assert.Equal(string.Empty, vm.CurrentLineText);
            Assert.Equal(string.Empty, vm.LyricStatusText);
            lyrics.Verify(x => x.GetLyricsAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()), Times.Once);
            dispose();
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void StaleResponseAfterTrackSwitch_IsDiscarded()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var first = Track("netease:1");
            var second = Track("netease:2");
            var current = first;

            var tracks = new Subject<Track?>();
            var audio = new Mock<IAudioService>();
            audio.SetupGet(x => x.TrackChanged).Returns(tracks);
            audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
            audio.SetupGet(x => x.CurrentTrack).Returns(() => current);
            audio.SetupGet(x => x.CurrentPosition).Returns(TimeSpan.Zero);

            var lyrics = new Mock<ILyricService>();
            var pending = new TaskCompletionSource<LyricResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lyrics.Setup(x => x.GetLyricsAsync(first, It.IsAny<CancellationToken>())).Returns(pending.Task);
            lyrics.Setup(x => x.GetLyricsAsync(second, It.IsAny<CancellationToken>()))
                .ReturnsAsync(LinesResult((0, "第二首的词")));

            using var vm = new LyricsViewModel(audio.Object, lyrics.Object);
            tracks.OnNext(first);
            current = second; // AudioService.CurrentTrack 跟随播放切换
            tracks.OnNext(second); // 第二首取词内联完成
            Assert.Equal("第二首的词", vm.CurrentLineText);

            pending.SetResult(LinesResult((0, "第一首的旧词"))); // 第一首的迟到响应

            Assert.Equal("第二首的词", vm.CurrentLineText); // 未被旧词覆盖
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void NoLyrics_ShowsNoLyricsStatus()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("youtube:x");
            var (vm, tracks, _, lyrics, dispose) = Create(track);
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync((LyricResult?)null);

            tracks.OnNext(track);

            Assert.False(vm.HasLyrics);
            Assert.Equal("暂无歌词", vm.LyricStatusText);
            Assert.True(vm.HasStatusText);
            dispose();
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void Instrumental_ShowsInstrumentalStatus()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("netease:9");
            var (vm, tracks, _, lyrics, dispose) = Create(track);
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LyricResult { IsInstrumental = true });

            tracks.OnNext(track);

            Assert.False(vm.HasLyrics);
            Assert.Equal("纯音乐，请欣赏", vm.LyricStatusText);
            dispose();
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void LanguageChange_RecalculatesStatusText()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("youtube:x");
            var (vm, tracks, _, lyrics, dispose) = Create(track);
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync((LyricResult?)null);

            tracks.OnNext(track);
            Assert.Equal("暂无歌词", vm.LyricStatusText);

            AppLanguage.Apply("en");
            Assert.Equal("No lyrics available", vm.LyricStatusText);

            AppLanguage.Apply("zh");
            Assert.Equal("暂无歌词", vm.LyricStatusText);
            dispose();
        }
        finally
        {
            AppLanguage.Apply("zh");
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task Dispose_PendingFetchDoesNotUpdateState()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("netease:1");
            var (vm, tracks, _, lyrics, dispose) = Create(track);
            var pending = new TaskCompletionSource<LyricResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .Returns(pending.Task);

            tracks.OnNext(track);
            Assert.Equal("正在获取歌词…", vm.LyricStatusText);

            var propertyChanges = 0;
            vm.PropertyChanged += (_, _) => propertyChanges++;

            dispose();
            pending.SetResult(LinesResult((0, "迟到的词")));
            await Task.Delay(150); // 让异步续体有机会跑完；冻结 = 零属性变更

            Assert.False(vm.HasLyrics);
            Assert.Equal("正在获取歌词…", vm.LyricStatusText); // Dispose 后冻结
            Assert.Equal(0, propertyChanges);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void HasCurrentLine_ReflectsPresenceOfCurrentLineText()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("netease:1");
            var (vm, tracks, positions, lyrics, dispose) = Create(track);
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync(LinesResult((5, "五秒开始唱"), (15, "十五秒第二句")));

            tracks.OnNext(track);

            Assert.True(vm.HasLyrics);
            Assert.False(vm.HasCurrentLine); // 0s 前奏：尚未有当前句

            positions.OnNext(TimeSpan.FromSeconds(6));
            Assert.True(vm.HasCurrentLine);
            Assert.Equal("五秒开始唱", vm.CurrentLineText);

            // 切歌清理
            tracks.OnNext(null);
            Assert.False(vm.HasLyrics);
            Assert.False(vm.HasCurrentLine);
            Assert.Empty(vm.CurrentLineText);

            dispose();
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 取词失败（null）后延迟重试一次：启动首播落 Node 代理冷启动窗口的恢复路径。
    // 确认无词的重试命中服务层缓存零开销，失败的重试是真请求——重试策略由 VM 统一承载
    [Fact]
    public async Task FailedFetch_RetriesAfterDelay_AndAppliesRecoveredLyrics()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("kugou:H1");
            var tracks = new Subject<Track?>();
            var audio = new Mock<IAudioService>();
            audio.SetupGet(x => x.TrackChanged).Returns(tracks);
            audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
            audio.SetupGet(x => x.CurrentTrack).Returns(track);
            audio.SetupGet(x => x.CurrentPosition).Returns(TimeSpan.Zero);

            var lyrics = new Mock<ILyricService>();
            var calls = 0;
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++calls == 1
                    ? null
                    : LinesResult((0, "重试拿到的词")));

            using var vm = new LyricsViewModel(audio.Object, lyrics.Object, TimeSpan.FromMilliseconds(60));
            tracks.OnNext(track);
            Assert.False(vm.HasLyrics); // 首次失败：暂无歌词
            Assert.Equal("暂无歌词", vm.LyricStatusText);

            var deadline = Environment.TickCount64 + 5000;
            while (!vm.HasLyrics && Environment.TickCount64 < deadline)
                await Task.Delay(50);

            Assert.True(vm.HasLyrics);
            Assert.Equal("重试拿到的词", vm.CurrentLineText);
            lyrics.Verify(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 确认无词也走一次重试（命中服务层缓存零请求）但绝不超过一次：等待多个重试周期
    // 后调用数恒为 2，防重试风暴
    [Fact]
    public async Task ConfirmedNoLyrics_RetriesExactlyOnce()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var track = Track("kugou:H2");
            var tracks = new Subject<Track?>();
            var audio = new Mock<IAudioService>();
            audio.SetupGet(x => x.TrackChanged).Returns(tracks);
            audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
            audio.SetupGet(x => x.CurrentTrack).Returns(track);
            audio.SetupGet(x => x.CurrentPosition).Returns(TimeSpan.Zero);

            var lyrics = new Mock<ILyricService>();
            var calls = 0;
            lyrics.Setup(x => x.GetLyricsAsync(track, It.IsAny<CancellationToken>()))
                .ReturnsAsync((LyricResult?)null)
                .Callback(() => Interlocked.Increment(ref calls));

            using var vm = new LyricsViewModel(audio.Object, lyrics.Object, TimeSpan.FromMilliseconds(60));
            tracks.OnNext(track);

            // 轮询等重试发生（正向信号），慢机上不依赖固定睡眠
            var deadline = Environment.TickCount64 + 5000;
            while (Volatile.Read(ref calls) < 2 && Environment.TickCount64 < deadline)
                await Task.Delay(50);
            Assert.True(Volatile.Read(ref calls) >= 2);

            await Task.Delay(250); // 负向窗口：确认绝无第三次调用
            Assert.False(vm.HasLyrics);
            Assert.Equal("暂无歌词", vm.LyricStatusText);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 重试触发前已切歌：旧曲目的重试作废（generation 卫兵），不再为旧曲烧请求
    [Fact]
    public async Task TrackSwitchBeforeRetry_DiscardsOldTrackRetry()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var first = Track("kugou:H1");
            var second = Track("netease:2");
            var current = first;
            var tracks = new Subject<Track?>();
            var audio = new Mock<IAudioService>();
            audio.SetupGet(x => x.TrackChanged).Returns(tracks);
            audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
            audio.SetupGet(x => x.CurrentTrack).Returns(() => current);
            audio.SetupGet(x => x.CurrentPosition).Returns(TimeSpan.Zero);

            var lyrics = new Mock<ILyricService>();
            lyrics.Setup(x => x.GetLyricsAsync(first, It.IsAny<CancellationToken>()))
                .ReturnsAsync((LyricResult?)null);
            lyrics.Setup(x => x.GetLyricsAsync(second, It.IsAny<CancellationToken>()))
                .ReturnsAsync(LinesResult((0, "第二首的词")));

            using var vm = new LyricsViewModel(audio.Object, lyrics.Object, TimeSpan.FromMilliseconds(60));
            tracks.OnNext(first);   // 失败：安排 60ms 后重试
            current = second;
            tracks.OnNext(second);  // 切歌：generation 推进，第二首取词内联完成
            Assert.Equal("第二首的词", vm.CurrentLineText);

            await Task.Delay(300); // 第一首的重试到期后必须被 generation 卫兵拦下

            lyrics.Verify(x => x.GetLyricsAsync(first, It.IsAny<CancellationToken>()), Times.Once);
            lyrics.Verify(x => x.GetLyricsAsync(second, It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal("第二首的词", vm.CurrentLineText); // 未被旧曲目的重试扰动
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
