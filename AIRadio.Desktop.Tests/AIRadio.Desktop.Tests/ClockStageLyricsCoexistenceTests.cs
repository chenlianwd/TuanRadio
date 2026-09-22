using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 回归锚定：ClockStage 歌词模式与频谱共存（2026-09-22 起）。
/// 原实现歌词开启时整个三栏图层（含两侧频谱与环境指示器）隐藏，用户反馈空间浪费。
/// 锚定三件事：
/// 1. 歌词图层自带两侧 SpectrumView 实例（不再与频谱互斥），环境指示器两种模式常显；
/// 2. 时钟图层与歌词图层按 IsLyricsMode 互斥；
/// 3. ToggleLyricsModeCommand 翻转 IsLyricsMode 并持久化 ShowLyricsInStage，
///    HasCurrentTrack 守门与 _audioService.CurrentTrack 一致（舞台点击用）。
/// 注：headless 下无法投递真实指针（见 PlayerDeckFeedbackButtonTests 注），
/// 点击链路以"图层接线断言 + 命令直驱"覆盖。
/// </summary>
public class ClockStageLyricsCoexistenceTests
{
    private static MainWindowViewModel CreateViewModel(Track? currentTrack)
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => new List<Track>().AsReadOnly());
        audio.Setup(x => x.CurrentTrack).Returns(currentTrack);

        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rec = new Mock<IRecommendationService>();
        rec.Setup(x => x.FeedbackHistory).Returns(new List<UserMusicFeedback>());
        rec.Setup(x => x.RecentlyPlayed).Returns(new List<Track>());

        return new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            settingsFile: Path.Combine(dir, "settings.json"),
            recommendationService: rec.Object);
    }

    private static T FindNamed<T>(ClockStage stage, string name) where T : Control
        => stage.GetVisualDescendants().OfType<T>()
               .Single(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private static void Execute(ReactiveCommand<Unit, Unit> command)
        => command.Execute().Subscribe();

    [AvaloniaFact]
    public void LyricsMode_SideSpectrumsStayVisible_ClockLayerSwapsOut()
    {
        var vm = CreateViewModel(new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" });
        Window? window = null;
        try
        {
            var stage = new ClockStage { DataContext = vm };
            window = new Window { Content = stage };
            window.Show();

            var clockLayer = FindNamed<Grid>(stage, "ClockLayer");
            var lyricsLayer = FindNamed<Grid>(stage, "LyricsLayer");
            var indicators = FindNamed<StackPanel>(stage, "EnvironmentIndicators");

            // 共存锚点：歌词图层必须自带两侧频谱实例（不再与频谱互斥）
            var lyricsSpectrums = stage.GetVisualDescendants().OfType<SpectrumView>()
                .Where(s => s.Name is "LyricsSpectrumLeft" or "LyricsSpectrumRight")
                .ToList();
            Assert.Equal(2, lyricsSpectrums.Count);
            Assert.All(lyricsSpectrums, s => Assert.Same(lyricsLayer, s.GetVisualParent()));

            vm.IsLyricsMode = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(clockLayer.IsVisible);
            Assert.True(lyricsLayer.IsVisible);
            Assert.True(indicators.IsVisible); // 环境指示器两种模式常显

            vm.IsLyricsMode = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(clockLayer.IsVisible);
            Assert.False(lyricsLayer.IsVisible);
            Assert.True(indicators.IsVisible);
        }
        finally
        {
            window?.Close();
        }
    }

    [AvaloniaFact]
    public void ToggleLyricsModeCommand_FlipsMode_PersistsSetting()
    {
        var vm = CreateViewModel(null);
        Assert.False(vm.HasCurrentTrack); // 舞台点击守门：无曲目不进空歌词层
        Assert.False(vm.IsLyricsMode);    // 全新 settings.json 默认关

        Execute(vm.ToggleLyricsModeCommand);
        Assert.True(vm.IsLyricsMode);
        Assert.True(vm.SettingsVM.ShowLyricsInStage);

        Execute(vm.ToggleLyricsModeCommand);
        Assert.False(vm.IsLyricsMode);
        Assert.False(vm.SettingsVM.ShowLyricsInStage);
    }
}
