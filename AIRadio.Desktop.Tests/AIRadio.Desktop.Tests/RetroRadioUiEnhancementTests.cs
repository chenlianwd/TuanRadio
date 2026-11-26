using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 复古 DJ 电台界面增强专项回归测试：
/// 覆盖 FM 确定性频点映射与优先级、VFD 频谱 Peak Hold 物理状态推进、
/// StatusBar 演播室 ON AIR 红灯样式覆写、ChatMessage 场记头部与语言刷新。
/// </summary>
public class RetroRadioUiEnhancementTests
{
    private static MainWindowViewModel CreateViewModel(
        Track? currentTrack,
        Subject<Track?>? trackChanged = null,
        Subject<bool>? ttsState = null)
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(trackChanged ?? new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(ttsState ?? new Subject<bool>());
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

    [Fact]
    public void ComputeFmDial_IsDeterministic_AndRespectsPriorityOrder()
    {
        // Idle 无曲目：停靠 98.0 MHz，HasTrack = false
        var idle = MainWindowViewModel.ComputeFmDial(null, RadioSceneMode.Default);
        Assert.Equal(98.0, idle.Frequency);
        Assert.Equal(string.Empty, idle.BandLabel);
        Assert.False(idle.HasTrack);

        // 常规单曲：90.0–106.8 MHz，重复计算严格一致
        var song = new Track { SourceId = "netease:123456", Title = "晴天" };
        var r1 = MainWindowViewModel.ComputeFmDial(song, RadioSceneMode.Default);
        var r2 = MainWindowViewModel.ComputeFmDial(song, RadioSceneMode.Default);
        Assert.Equal(r1, r2);
        Assert.InRange(r1.Frequency, 90.0, 106.8);
        Assert.Equal(string.Empty, r1.BandLabel);
        Assert.True(r1.HasTrack);

        // 场景专属频段：Focus 102.4 / Sleep 98.7
        var focus = MainWindowViewModel.ComputeFmDial(song, RadioSceneMode.Focus);
        Assert.Equal((102.4, "FOCUS", true), focus);
        var sleep = MainWindowViewModel.ComputeFmDial(song, RadioSceneMode.Sleep);
        Assert.Equal((98.7, "NIGHT", true), sleep);

        // 直播电台优先级高于场景模式：88.5–91.9 MHz · LIVE
        var live = new Track
        {
            SourceId = "radio:jazz-fm",
            Title = "Jazz FM",
            ProviderMetadata = new Dictionary<string, string> { ["is_live"] = "true" }
        };
        var liveRes = MainWindowViewModel.ComputeFmDial(live, RadioSceneMode.Focus);
        Assert.InRange(liveRes.Frequency, 88.5, 91.9);
        Assert.Equal("LIVE", liveRes.BandLabel);

        // 播客单集优先级高于场景模式：107.1–107.9 MHz · PODCAST
        var podcast = new Track
        {
            SourceId = "podcast:ep-42",
            Title = "Episode 42",
            ProviderMetadata = new Dictionary<string, string> { ["is_podcast"] = "true" }
        };
        var podRes = MainWindowViewModel.ComputeFmDial(podcast, RadioSceneMode.Sleep);
        Assert.InRange(podRes.Frequency, 107.1, 107.9);
        Assert.Equal("PODCAST", podRes.BandLabel);
    }

    [AvaloniaFact]
    public void SpectrumRenderer_VfdPeakHold_HoldsThenDecaysOnSetBands()
    {
        var renderer = new SpectrumRenderer { StyleName = "vfd" };
        renderer.SetBands([0.9f, 0.2f]);
        Assert.Equal(0.9f, renderer.Peaks[0], 3);

        // 掉回 0 后前 8 帧应保持峰值不动
        for (var i = 0; i < 8; i++)
        {
            renderer.SetBands([0.0f, 0.0f]);
            Assert.Equal(0.9f, renderer.Peaks[0], 3);
        }

        // 第 9 帧开始平滑慢落，不会直接归零
        renderer.SetBands([0.0f, 0.0f]);
        Assert.InRange(renderer.Peaks[0], 0.80f, 0.89f);
    }

    [AvaloniaFact]
    public void StatusBar_StudioLive_OverridesLampAndTextBrush_EvenWhenIdleListening()
    {
        var app = Application.Current!;
        app.Resources.MergedDictionaries.Add(
            (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://AIRadio/Themes/Colors.axaml")));

        var vm = CreateViewModel(null);
        Window? window = null;
        try
        {
            var bar = new StatusBar { DataContext = vm };
            window = new Window
            {
                Content = bar,
                Width = 500,
                Height = 40,
                RequestedThemeVariant = ThemeVariant.Dark
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var lampBox = bar.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "StateLampBox");
            var lampHalo = bar.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(e => e.Name == "StateLampHalo");
            var stateText = bar.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "StateText");
            Assert.DoesNotContain("studio-live", lampBox.Classes);
            Assert.False(lampHalo.IsVisible);

            // Idle 态按住录音：IsStudioLive 应激活 .studio-live 并点亮外圈红光晕、覆写文字为 StudioOnAirColor
            vm.ChatVM.IsListening = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsStudioLive);
            Assert.Contains("studio-live", lampBox.Classes);
            Assert.True(lampHalo.IsVisible);
            var fg = Assert.IsAssignableFrom<ISolidColorBrush>(stateText.Foreground);
            Assert.Equal(Color.Parse("#FFFF5A5F"), fg.Color);

            vm.ChatVM.IsListening = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsStudioLive);
            Assert.DoesNotContain("studio-live", lampBox.Classes);
            Assert.False(lampHalo.IsVisible);
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void ClockStage_FmDial_IsNotHitTestable_AndHidesInLyricsMode()
    {
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        var trackChanged = new Subject<Track?>();
        var vm = CreateViewModel(track, trackChanged);
        Window? window = null;
        try
        {
            var stage = new ClockStage { DataContext = vm };
            window = new Window { Content = stage, Width = 760, Height = 184 };
            window.Show();
            trackChanged.OnNext(track);
            Dispatcher.UIThread.RunJobs();

            var dial = stage.GetVisualDescendants().OfType<FmDialView>().Single(d => d.Name == "FmDial");
            Assert.False(dial.IsHitTestVisible);
            Assert.True(dial.IsVisible);
            Assert.True(dial.Bounds.Width >= 200, $"FmDial 应有有效渲染宽度，实际为 {dial.Bounds.Width}");
            Assert.StartsWith("TUANRADIO FM · ", vm.StationCallSign, StringComparison.Ordinal);

            vm.IsLyricsMode = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(dial.IsVisible);
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        }
    }

    [Fact]
    public void ChatMessage_HeaderText_IncludesSenderAndTime_AndRefreshesOnLanguageChange()
    {
        var prev = AppLanguage.Current;
        try
        {
            AppLanguage.Apply("zh");
            var msg = new ChatMessage
            {
                Role = MessageRole.User,
                Content = "放首夜曲",
                Timestamp = new DateTime(2026, 11, 26, 23, 14, 0)
            };
            Assert.True(msg.IsUser);
            Assert.False(msg.IsAssistant);
            Assert.Equal("我 · 23:14", msg.HeaderText);

            var changed = new List<string?>();
            msg.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            AppLanguage.Apply("en");
            msg.RefreshLocalization();

            Assert.Equal("Me · 23:14", msg.HeaderText);
            Assert.Contains(nameof(ChatMessage.SenderName), changed);
            Assert.Contains(nameof(ChatMessage.HeaderText), changed);
        }
        finally
        {
            AppLanguage.Apply(prev);
        }
    }
}
