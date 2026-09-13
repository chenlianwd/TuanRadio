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
}
