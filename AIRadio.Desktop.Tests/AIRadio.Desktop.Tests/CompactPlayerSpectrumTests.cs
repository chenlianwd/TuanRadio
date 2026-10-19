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
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 回归锚定（2026-10-19 用户反馈：简洁模式频谱只有一种样式、无法切换、不与主界面同步）：
/// 迷你频谱复用主舞台 SpectrumRenderer——
/// 1. StyleName 绑 SpectrumVM.SelectedStyle，与设置页下拉/舞台点击同源实时同步；
/// 2. 自身透明命中层可点击循环样式（同主舞台频谱列手势），持久化走同一 SaveUiState 链路；
/// 3. 频谱数据经 SpectrumReceived 降采样 32→8 段喂 Renderer（46px 内 32 柱会挤成 2px 重叠）。
/// </summary>
public class CompactPlayerSpectrumTests
{
    private static (MainWindowViewModel vm, Subject<float[]> spectrum) CreateViewModel()
    {
        var spectrum = new Subject<float[]>();
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(spectrum);
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => new List<Track>().AsReadOnly());

        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rec = new Mock<IRecommendationService>();
        rec.Setup(x => x.FeedbackHistory).Returns(new List<UserMusicFeedback>());
        rec.Setup(x => x.RecentlyPlayed).Returns(new List<Track>());

        var vm = new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            settingsFile: Path.Combine(dir, "settings.json"),
            recommendationService: rec.Object);
        return (vm, spectrum);
    }

    [Fact]
    public void DownsampleBands_AveragesGroupsAndHandlesShortInput()
    {
        // 32 段 → 8 段：每段为连续 4 段的均值
        var data = new float[32];
        for (var i = 0; i < data.Length; i++)
            data[i] = i;
        var target = new float[8];
        CompactPlayer.DownsampleBands(data, target);
        Assert.Equal(new float[] { 1.5f, 5.5f, 9.5f, 13.5f, 17.5f, 21.5f, 25.5f, 29.5f }, target);

        // 短输入直接拷贝，余位补零
        var shortTarget = new float[8];
        CompactPlayer.DownsampleBands(new[] { 0.1f, 0.2f, 0.3f }, shortTarget);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f, 0, 0, 0, 0, 0 }, shortTarget);
    }

    [AvaloniaFact]
    public void MiniSpectrum_StyleFollowsViewModelSelection()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, _) = CreateViewModel();
        Window? window = null;
        try
        {
            var view = new CompactPlayer { DataContext = vm };
            window = new Window { Content = view, Width = 560, Height = 140 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var renderer = view.FindNameScope()?.Find<SpectrumRenderer>("CompactSpectrum")
                ?? throw new InvalidOperationException("CompactSpectrum not found in CompactPlayer");

            Assert.Equal("bars", renderer.StyleName); // 默认样式与主界面同源

            // 主界面链路（设置页下拉 / 舞台点击）改样式 → 简洁模式迷你频谱实时跟随
            vm.SettingsVM.SelectedSpectrumStyle = "wave";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("wave", vm.SpectrumVM.SelectedStyle);
            Assert.Equal("wave", renderer.StyleName);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void RealPointer_MiniSpectrumClick_CyclesStyle()
    {
        // 简洁模式频谱自身可切换：透明命中层整块可点（不依赖绘制几何），
        // 经真实指针管线（MouseMove/MouseDown/MouseUp → Tapped）锚定。
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, _) = CreateViewModel();
        Window? window = null;
        try
        {
            var view = new CompactPlayer { DataContext = vm };
            window = new Window { Content = view, Width = 560, Height = 140 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("bars", vm.SettingsVM.SelectedSpectrumStyle);

            var host = view.FindNameScope()?.Find<Grid>("CompactSpectrumHost")
                ?? throw new InvalidOperationException("CompactSpectrumHost not found in CompactPlayer");
            var renderer = view.FindNameScope()?.Find<SpectrumRenderer>("CompactSpectrum")
                ?? throw new InvalidOperationException("CompactSpectrum not found in CompactPlayer");

            var p = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), window)!.Value;
            window.MouseMove(p);
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("mirror", vm.SettingsVM.SelectedSpectrumStyle); // 循环进主界面同源设置
            Assert.Equal("mirror", vm.SpectrumVM.SelectedStyle);          // 主舞台频谱实例同源跟随
            Assert.Equal("mirror", renderer.StyleName);                   // 迷你频谱即时跟随

            // 行 1（曲目信息区）不属于频谱命中区：点击不得循环样式
            var infoRow = view.FindNameScope()?.Find<Grid>("InfoRow")
                ?? throw new InvalidOperationException("InfoRow not found in CompactPlayer");
            var p2 = infoRow.TranslatePoint(new Point(4, infoRow.Bounds.Height / 2), window)!.Value;
            window.MouseMove(p2);
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(p2, MouseButton.Left);
            window.MouseUp(p2, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("mirror", vm.SettingsVM.SelectedSpectrumStyle);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void SpectrumEvent_FeedsMiniRenderer_Downsampled()
    {
        // 事件接线端到端：AudioService.SpectrumData → SpectrumVM.SpectrumReceived → 降采样 → Renderer.SetBands
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, spectrum) = CreateViewModel();
        Window? window = null;
        try
        {
            var view = new CompactPlayer { DataContext = vm };
            window = new Window { Content = view, Width = 560, Height = 140 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var renderer = view.FindNameScope()?.Find<SpectrumRenderer>("CompactSpectrum")
                ?? throw new InvalidOperationException("CompactSpectrum not found in CompactPlayer");

            var data = new float[32];
            for (var i = 0; i < data.Length; i++)
                data[i] = i / 31f;
            spectrum.OnNext(data);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(8, renderer.Bands.Length); // 降采样落地（Renderer 内部钩子）
            Assert.Equal(1.5f / 31f, renderer.Bands[0], 5); // 首组均值 = (0+1+2+3)/4/31
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
