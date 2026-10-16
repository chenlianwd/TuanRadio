using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading;
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
/// 回归锚定：ClockStage 点击分区域交互（2026-10-14 起，标题栏歌词按钮移除）。
/// 锚定四件事：
/// 1. 歌词图层自带两侧 SpectrumView 实例（不再与频谱互斥），环境指示器两种模式常显；
///    时钟图层与歌词图层按 IsLyricsMode 互斥。
/// 2. ToggleLyricsModeCommand 翻转 IsLyricsMode 并持久化 ShowLyricsInStage，
///    HasCurrentTrack 守门与 _audioService.CurrentTrack 一致（中栏点击用）。
/// 3. 点击分区域路由：中栏（时钟/歌词面板）切歌词（无曲目静默），频谱条循环频谱样式
///    （与设置页下拉同源），空白背景与环境指示器无动作。
/// 4. 光标诚实：中栏手型光标仅在有在播曲目时提示；频谱条随时可点（切样式）恒手型。
/// 注：headless 下无法投递真实指针（见 PlayerDeckFeedbackButtonTests 注），
/// 点击链路以"区域接线断言 + RouteStageTap 直驱"覆盖。
/// </summary>
public class ClockStageLyricsCoexistenceTests
{
    private static MainWindowViewModel CreateViewModel(Track? currentTrack, out string settingsFile, Subject<Track?>? trackChanged = null)
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(trackChanged ?? new Subject<Track?>());
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

        settingsFile = Path.Combine(dir, "settings.json");
        return new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            settingsFile: settingsFile,
            recommendationService: rec.Object);
    }

    private static MainWindowViewModel CreateViewModel(Track? currentTrack, Subject<Track?>? trackChanged = null)
        => CreateViewModel(currentTrack, out _, trackChanged);

    private static T FindNamed<T>(ClockStage stage, string name) where T : Control
        => stage.GetVisualDescendants().OfType<T>()
               .Single(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private static SpectrumView FindSpectrum(ClockStage stage, string name)
        => stage.GetVisualDescendants().OfType<SpectrumView>()
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
    public void StageCursor_CenterPanelHonest_SpectrumAlwaysHand()
    {
        // 光标诚实：中栏（时钟/歌词面板）手型仅在有在播曲目时提示（无曲目点击静默）；
        // 频谱条随时可点（切样式），不受在播状态影响恒手型
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        var trackChanged = new Subject<Track?>();
        var vm = CreateViewModel(track, trackChanged);

        Window? window = null;
        try
        {
            var stage = new ClockStage { DataContext = vm };
            window = new Window { Content = stage };
            window.Show();
            var clockPanel = FindNamed<StackPanel>(stage, "ClockPanel");
            var lyricsPanel = FindNamed<StackPanel>(stage, "LyricsPanel");
            var spectrums = new[] { "ClockSpectrumLeft", "ClockSpectrumRight", "LyricsSpectrumLeft", "LyricsSpectrumRight" }
                .Select(name => FindSpectrum(stage, name)).ToArray();

            Assert.False(vm.HasCurrentTrack); // VM 构造后未播任何事件：不可点、不提示
            Assert.DoesNotContain("clickable", clockPanel.Classes);
            Assert.Null(clockPanel.Cursor);
            Assert.All(spectrums, s => Assert.NotNull(s.Cursor)); // 频谱条（两图层全部实例）无曲目也恒手型

            trackChanged.OnNext(track);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasCurrentTrack);
            Assert.Contains("clickable", clockPanel.Classes);
            Assert.NotNull(clockPanel.Cursor); // clickable 样式置 Hand
            Assert.Contains("clickable", lyricsPanel.Classes);
            Assert.NotNull(lyricsPanel.Cursor);

            trackChanged.OnNext(null); // 曲目清空（TrackChanged 也承载 null）
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.HasCurrentTrack);
            Assert.DoesNotContain("clickable", clockPanel.Classes);
            Assert.Null(clockPanel.Cursor); // 样式脱附，回落默认箭头
            Assert.DoesNotContain("clickable", lyricsPanel.Classes);
            Assert.Null(lyricsPanel.Cursor);
            Assert.All(spectrums, s => Assert.NotNull(s.Cursor));
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
        Assert.False(vm.HasCurrentTrack); // 中栏点击守门：无曲目不进空歌词层
        Assert.False(vm.IsLyricsMode);    // 全新 settings.json 默认关

        Execute(vm.ToggleLyricsModeCommand);
        Assert.True(vm.IsLyricsMode);
        Assert.True(vm.SettingsVM.ShowLyricsInStage);

        Execute(vm.ToggleLyricsModeCommand);
        Assert.False(vm.IsLyricsMode);
        Assert.False(vm.SettingsVM.ShowLyricsInStage);
    }

    [AvaloniaFact]
    public void RouteStageTap_CenterPanel_TogglesLyrics_OnlyWithTrack()
    {
        // 无在播曲目：中栏点击静默
        var vm = CreateViewModel(null);
        Window? window = null;
        try
        {
            var stage = new ClockStage { DataContext = vm };
            window = new Window { Content = stage };
            window.Show();
            stage.RouteStageTap(FindNamed<StackPanel>(stage, "ClockPanel"), vm);
            Assert.False(vm.IsLyricsMode);
        }
        finally
        {
            window?.Close();
        }

        // 有在播曲目：面板本体与其子元素（经祖先链）都命中切换
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        Window? window2 = null;
        try
        {
            var trackChanged = new Subject<Track?>();
            var vmWithTrack = CreateViewModel(track, out _, trackChanged);
            trackChanged.OnNext(track);

            var stage2 = new ClockStage { DataContext = vmWithTrack };
            window2 = new Window { Content = stage2 };
            window2.Show();
            var clockPanel = FindNamed<StackPanel>(stage2, "ClockPanel");
            var clockText = clockPanel.GetVisualDescendants().OfType<TextBlock>().First();

            stage2.RouteStageTap(clockText, vmWithTrack); // 点在时钟文本上
            Assert.True(vmWithTrack.IsLyricsMode);
            stage2.RouteStageTap(FindNamed<StackPanel>(stage2, "LyricsPanel"), vmWithTrack); // 点歌词面板切回
            Assert.False(vmWithTrack.IsLyricsMode);
            stage2.RouteStageTap(clockPanel, vmWithTrack); // 面板本体亦可
            Assert.True(vmWithTrack.IsLyricsMode);
        }
        finally
        {
            window2?.Close();
        }
    }

    [AvaloniaFact]
    public void RouteStageTap_Spectrum_CyclesStyle_IgnoresBackgroundAndIndicators()
    {
        var vm = CreateViewModel(null); // 频谱切样式随时可用，无曲目也生效
        Window? window = null;
        try
        {
            var stage = new ClockStage { DataContext = vm };
            window = new Window { Content = stage };
            window.Show();

            Assert.Equal("bars", vm.SettingsVM.SelectedSpectrumStyle);
            stage.RouteStageTap(FindSpectrum(stage, "ClockSpectrumLeft"), vm);
            Assert.Equal("mirror", vm.SettingsVM.SelectedSpectrumStyle);
            Assert.Equal("mirror", vm.SpectrumVM.SelectedStyle); // 既有订阅链路同步渲染层

            vm.IsLyricsMode = true; // 歌词图层的频谱同样命中
            Dispatcher.UIThread.RunJobs();
            stage.RouteStageTap(FindSpectrum(stage, "LyricsSpectrumRight"), vm);
            Assert.Equal("wave", vm.SettingsVM.SelectedSpectrumStyle);

            // 空白背景与环境指示器不属于任何区域：无动作（歌词模式也不被误切）
            stage.RouteStageTap(FindNamed<Grid>(stage, "StageRoot"), vm);
            stage.RouteStageTap(FindNamed<StackPanel>(stage, "EnvironmentIndicators"), vm);
            Assert.Equal("wave", vm.SettingsVM.SelectedSpectrumStyle);
            Assert.True(vm.IsLyricsMode);
        }
        finally
        {
            window?.Close();
        }
    }

    [AvaloniaFact]
    public void CycleSpectrumStyleCommand_CyclesThroughAllStyles_Persists()
    {
        var vm = CreateViewModel(null, out var settingsFile);
        Assert.Equal("bars", vm.SettingsVM.SelectedSpectrumStyle);

        foreach (var expected in new[] { "mirror", "wave", "particles", "bars" })
        {
            Execute(vm.CycleSpectrumStyleCommand);
            Assert.Equal(expected, vm.SettingsVM.SelectedSpectrumStyle);
            Assert.Equal(expected, vm.SpectrumVM.SelectedStyle);
        }

        // 持久化走 SaveUiStateCommand 异步落盘：轮询 settings.json 出现终值
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var persisted = false;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (File.Exists(settingsFile) && File.ReadAllText(settingsFile).Contains("\"bars\""))
            {
                persisted = true;
                break;
            }
            Thread.Sleep(20);
        }
        Assert.True(persisted, "频谱样式应在超时内持久化到 settings.json");
    }

    [AvaloniaFact]
    public void LyricsPanel_AdaptiveWidth_GivesSpectrumRoom_CapsAtMax_StableAcrossLyricChanges()
    {
        // 中栏自适应：小窗口按可用宽 55% 收窄给频谱让位（旧固定 540 时每侧频谱仅 ~68px
        // 成细条）；大窗口 540 封顶；歌词内容变化（状态行出现）不带动面板宽度（防抖动）
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "netease:2035455652" };
        var trackChanged = new Subject<Track?>();
        var vm = CreateViewModel(track, trackChanged);

        Window? window = null;
        try
        {
            var stage = new ClockStage { DataContext = vm };
            window = new Window { Content = stage, Width = 760, Height = 760 };
            window.Show();
            vm.IsLyricsMode = true;
            Dispatcher.UIThread.RunJobs();

            var panel = FindNamed<StackPanel>(stage, "LyricsPanel");
            var leftSpectrum = FindSpectrum(stage, "LyricsSpectrumLeft");
            var expectedNarrow = Math.Clamp((stage.Bounds.Width - 48) * 0.55, 220, 540);

            Assert.Equal(expectedNarrow, panel.Width);
            Assert.True(panel.Width < 540, $"760 宽窗口下中栏应收窄，实际 {panel.Width}");
            Assert.True(leftSpectrum.Bounds.Width > 100,
                $"频谱每侧应 >100px（旧固定 540 时 ~68px），实际 {leftSpectrum.Bounds.Width}");

            // 防抖动锚点：曲目变化驱动歌词状态行出现/翻转（内容尺寸需求变化），面板宽度不动
            var widthBeforeLyricContent = panel.Width;
            trackChanged.OnNext(track);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.LyricsVM.HasStatusText); // 取词失败/加载态的状态行已出现
            var statusDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < statusDeadline &&
                   (vm.LyricsVM.LyricStatusText.Contains("获取") || vm.LyricsVM.LyricStatusText.Contains("Loading")))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }
            Assert.Equal(widthBeforeLyricContent, panel.Width);

            // 大窗口 540 封顶（歌词行宽仍给足）
            window.Width = 1500;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(540, panel.Width);
        }
        finally
        {
            window?.Close();
        }
    }
}
