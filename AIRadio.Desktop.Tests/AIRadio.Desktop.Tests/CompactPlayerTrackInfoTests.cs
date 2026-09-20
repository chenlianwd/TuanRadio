using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 回归：Avalonia 的 Tapped/DoubleTapped 手势经 RouteFinished 在整条按下路由完成后才触发，
/// 且双击要求两次按下命中同一元素（Gestures 的同源校验）。单击切换歌词会交换
/// TrackInfoArea 的可见子元素——若子元素参与命中测试，第二次按下的命中源改变，
/// DoubleTapped 根本不触发；按下处理器若清 _lastInfoTappedToggle，撤销分支永远不可达。
/// </summary>
public class CompactPlayerTrackInfoTests
{
    private static (MainWindowViewModel vm, Subject<Track?> tracks, Subject<TimeSpan> positions, Track track) CreateViewModel()
    {
        var tracks = new Subject<Track?>();
        var positions = new Subject<TimeSpan>();
        var track = new Track { SourceId = "netease:1", Title = "歌", Artist = "手" };
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(tracks);
        audio.SetupGet(x => x.CurrentTrack).Returns(track);
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(positions);
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => new List<Track>().AsReadOnly());

        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var lyrics = new Mock<ILyricService>();
        lyrics.Setup(x => x.GetLyricsAsync(It.IsAny<Track>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new LyricResult
            {
                Lines = new List<LyricLine>
                {
                    new(TimeSpan.FromSeconds(5), "第一句歌词"),
                    new(TimeSpan.FromSeconds(15), "第二句歌词")
                }
            });
        var vm = new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            settingsFile: Path.Combine(dir, "settings.json"),
            lyricService: lyrics.Object);
        return (vm, tracks, positions, track);
    }

    private static void Press(InputElement element, Pointer pointer, int clickCount)
        => element.RaiseEvent(new PointerPressedEventArgs(
            element, pointer, element, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, clickCount));

    private static void Release(InputElement element, Pointer pointer)
        => element.RaiseEvent(new PointerReleasedEventArgs(
            element, pointer, element, new Point(5, 5), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));

    [AvaloniaFact]
    public void TrackInfo_DoubleTapWhileLyricsVisible_RestoresToggleAndExpands()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, tracks, positions, track) = CreateViewModel();
        Window? window = null;
        try
        {
            var view = new CompactPlayer { DataContext = vm };
            window = new Window { Content = view };
            window.Show();

            var infoArea = view.FindNameScope()?.Find<Panel>("TrackInfoArea")
                ?? throw new InvalidOperationException("TrackInfoArea not found in CompactPlayer");

            vm.IsCompactMode = true;
            tracks.OnNext(track);
            positions.OnNext(TimeSpan.FromSeconds(6));
            Assert.True(vm.IsCompactLyricsVisible); // 单击切换会交换可见子元素：双击同源校验的最严场景

            var pointer = new Pointer(1, PointerType.Mouse, true);
            Press(infoArea, pointer, 1);
            Release(infoArea, pointer);
            Assert.False(vm.SettingsVM.CompactShowLyrics); // 第 1 次单击：切走歌词

            Press(infoArea, pointer, 2);
            Release(infoArea, pointer);

            Assert.False(vm.IsCompactMode); // 双击还原窗口
            Assert.True(vm.SettingsVM.CompactShowLyrics); // 撤销第 1 次单击；游离 release 不得再次翻转
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void TrackInfo_DoubleTapWithoutCurrentLine_DoesNotFlipSettingSilently()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, _, _, _) = CreateViewModel();
        Window? window = null;
        try
        {
            var view = new CompactPlayer { DataContext = vm };
            window = new Window { Content = view };
            window.Show();

            var infoArea = view.FindNameScope()?.Find<Panel>("TrackInfoArea")
                ?? throw new InvalidOperationException("TrackInfoArea not found in CompactPlayer");

            // 无当前歌词行：单击切换不产生任何可见变化，双击展开后设置必须回到原值
            vm.IsCompactMode = true;
            Assert.True(vm.SettingsVM.CompactShowLyrics);

            var pointer = new Pointer(2, PointerType.Mouse, true);
            Press(infoArea, pointer, 1);
            Release(infoArea, pointer);
            Assert.False(vm.SettingsVM.CompactShowLyrics);

            Press(infoArea, pointer, 2);
            Release(infoArea, pointer);

            Assert.False(vm.IsCompactMode);
            Assert.True(vm.SettingsVM.CompactShowLyrics);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void RowBackground_DoubleTap_DoesNotUndoEarlierTrackInfoClick()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, tracks, positions, track) = CreateViewModel();
        Window? window = null;
        try
        {
            var view = new CompactPlayer { DataContext = vm };
            window = new Window { Content = view };
            window.Show();

            var infoArea = view.FindNameScope()?.Find<Panel>("TrackInfoArea")
                ?? throw new InvalidOperationException("TrackInfoArea not found in CompactPlayer");
            var row = view.FindNameScope()?.Find<Grid>("InfoRow")
                ?? throw new InvalidOperationException("InfoRow not found in CompactPlayer");

            vm.IsCompactMode = true;
            tracks.OnNext(track);
            positions.OnNext(TimeSpan.FromSeconds(6));

            var pointer = new Pointer(3, PointerType.Mouse, true);
            Press(infoArea, pointer, 1);
            Release(infoArea, pointer);
            Assert.False(vm.SettingsVM.CompactShowLyrics); // 用户刻意单击切换

            // 随后在行内空白处双击（两次按下都命中 InfoRow）：仍可展开，但不得误撤销早前的单击
            Press(row, pointer, 1);
            Release(row, pointer);
            Press(row, pointer, 2);
            Release(row, pointer);

            Assert.False(vm.IsCompactMode);
            Assert.False(vm.SettingsVM.CompactShowLyrics);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
