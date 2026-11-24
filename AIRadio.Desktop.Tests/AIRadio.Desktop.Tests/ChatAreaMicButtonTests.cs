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
using Avalonia.Media;
using Avalonia.Threading;
using Moq;
using ReactiveUI;

namespace AIRadio.Desktop.Tests;

public class ChatAreaMicButtonTests
{
    internal static MainWindowViewModel CreateViewModel() => CreateViewModel(out _);

    /// <summary>带 audio mock 的重载：供需要 Raise 服务事件的端到端测试使用（如辉光事件链）。</summary>
    internal static MainWindowViewModel CreateViewModel(out Mock<IAudioService> audio)
        => CreateViewModel(out audio, null);

    /// <summary>带天气服务注入的重载：供设置页天气回显等需要驱动 WeatherVM 的端到端测试使用。</summary>
    internal static MainWindowViewModel CreateViewModel(out Mock<IAudioService> audio, IWeatherService? weather)
        => CreateViewModel(out audio, weather, null);

    /// <summary>带音源注入的重载：供需要真实 Broker 物化音源列表的端到端测试使用（如设置页优先级按钮）。</summary>
    internal static MainWindowViewModel CreateViewModel(out Mock<IAudioService> audio, IWeatherService? weather, IMusicSearchService? musicSearch)
    {
        audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => new List<Track>().AsReadOnly());

        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dj = new Mock<IDJService>();
        // 空闲路径（假设备）录音会走完识别管线：纠错未 Setup 时 Moq 返回结果为 null 的已完成 task
        dj.Setup(x => x.CorrectTranscriptionAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((string transcript, System.Threading.CancellationToken _) => transcript);
        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            musicSearch ?? new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            settingsFile: Path.Combine(dir, "settings.json"),
            weatherService: weather);
        // 按住说话回归走假录音设备：真实 WaveInEvent 会打开系统麦克风（会话级开到测试进程退出）
        vm.ChatVM.RecordingDeviceFactory = () => new FakeWaveIn();
        return vm;
    }

    // 回归：Button 的类处理器会把 PointerPressed/PointerReleased 标记为 Handled，
    // XAML 属性挂载（handledEventsToo:false）收不到这两个事件，按住说话入口会整体失效。
    // 按压视觉态由样式类 Classes.listening 驱动，保留 DynamicResource 绑定以跟随主题切换。
    [AvaloniaFact]
    public void MicButton_PointerPressAndRelease_ReachesHoldToTalkHandlers()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = CreateViewModel();
        Window? window = null;
        try
        {
            var view = new ChatArea { DataContext = vm };
            // 直接在 view 上提供样式用到的颜色键，避开主题字典在 headless 下的解析差异
            var brushes = new ResourceDictionary
            {
                ["C_FF56F5C4"] = new SolidColorBrush(Color.FromUInt32(0xFF56F5C4)),
                ["C_FF050507"] = new SolidColorBrush(Color.FromUInt32(0xFF050507)),
                ["C_33262835"] = new SolidColorBrush(Color.FromUInt32(0x33262835)),
                ["C_FFEDEDF5"] = new SolidColorBrush(Color.FromUInt32(0xFFEDEDF5)),
                ["C_FFFFC86B"] = new SolidColorBrush(Color.FromUInt32(0xFFFFC86B))
            };
            view.Resources.MergedDictionaries.Add(brushes);
            window = new Window { Content = view };
            window.Show();

            var mic = view.FindNameScope()?.Find<Button>("MicButton")
                ?? throw new InvalidOperationException("MicButton not found in ChatArea");

            var pointer = new Pointer(1, PointerType.Mouse, true);

            // —— 被拒路径：AI 处理中按住不得出现按压视觉/捕获，否则用户误以为录音已开始 ——
            vm.ChatVM.IsProcessing = true;
            mic.RaiseEvent(new PointerPressedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(pointer.Captured);
            Assert.DoesNotContain("listening", mic.Classes);
            mic.RaiseEvent(new PointerReleasedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));

            // —— 右键不触发录音 ——
            vm.ChatVM.IsProcessing = false;
            mic.RaiseEvent(new PointerPressedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
                KeyModifiers.None, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.ChatVM.IsListening);
            Assert.Null(pointer.Captured);

            // —— 空闲左键路径：真正开始录音才有 listening 样式类与指针捕获 ——
            mic.RaiseEvent(new PointerPressedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1));
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.ChatVM.IsListening);
            Assert.Same(mic, pointer.Captured);
            Assert.Contains("listening", mic.Classes);
            var pressedBrush = Assert.IsType<SolidColorBrush>(mic.Background);
            Assert.Equal(Color.FromUInt32(0xFF56F5C4), pressedBrush.Color);

            mic.RaiseEvent(new PointerReleasedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));
            Dispatcher.UIThread.RunJobs();

            Assert.Null(pointer.Captured);
            Assert.DoesNotContain("listening", mic.Classes);
            var releasedBrush = Assert.IsType<SolidColorBrush>(mic.Background);
            Assert.Equal(Color.FromUInt32(0x33262835), releasedBrush.Color);

            // —— 回归：使用一次按住说话后切换主题资源，按钮背景仍应动态更新 ——
            brushes["C_33262835"] = new SolidColorBrush(Color.FromUInt32(0xFFCCCCCC));
            Dispatcher.UIThread.RunJobs();
            var themedBrush = Assert.IsType<SolidColorBrush>(mic.Background);
            Assert.Equal(Color.FromUInt32(0xFFCCCCCC), themedBrush.Color);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void MicButton_DragOutsideAndRelease_CancelsWithoutRecognizing()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = CreateViewModel();
        vm.ChatVM.MinRecordingDuration = TimeSpan.Zero;
        vm.ChatVM.RecordingDeviceFactory = () => new FakeWaveIn { InitialAudioBytes = FakeWaveIn.CreateTonePcm16(500) };
        Window? window = null;
        try
        {
            var view = new ChatArea { DataContext = vm };
            window = new Window { Content = view, Width = 600, Height = 400 };
            window.Show();

            var mic = view.FindNameScope()?.Find<Button>("MicButton")
                ?? throw new InvalidOperationException("MicButton not found in ChatArea");

            var pointer = new Pointer(1, PointerType.Mouse, true);
            mic.RaiseEvent(new PointerPressedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ChatVM.IsListening);

            // 拖到按钮上方很远处：进入取消态
            mic.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, mic, pointer, mic, new Point(5, -120), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
                KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ChatVM.IsCancellingVoice);
            Assert.Contains("cancel", mic.Classes);

            // 在区域外松开：取消录音，不进入识别态
            mic.RaiseEvent(new PointerReleasedEventArgs(
                mic, pointer, mic, new Point(5, -120), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.ChatVM.IsListening);
            Assert.False(vm.ChatVM.IsCancellingVoice);
            Assert.False(vm.ChatVM.IsRecognizing);

            // Esc 取消：同样取消录音并释放指针捕获
            mic.RaiseEvent(new PointerPressedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ChatVM.IsListening);
            Assert.Same(mic, pointer.Captured);

            mic.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape,
                Source = mic
            });
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.ChatVM.IsListening);
            Assert.False(vm.ChatVM.IsRecognizing);
            Assert.Null(pointer.Captured);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 回复等待徽标随 IsProcessing 翻转：本地模型生成窗口可达十余秒，没有可见反馈时
    // 用户会以为消息没发出去而重发（2026-10-27 实测撞死在禁用的发送按钮上）
    [AvaloniaFact]
    public void ProcessingBadge_FollowsChatProcessingFlag()
    {
        var vm = CreateViewModel();
        Window? window = null;
        try
        {
            var view = new ChatArea { DataContext = vm };
            window = new Window { Content = view };
            window.Show();

            var badge = view.FindNameScope()?.Find<Control>("ProcessingBadge")
                ?? throw new InvalidOperationException("ProcessingBadge not found in ChatArea");

            Assert.False(badge.IsVisible);
            vm.ChatVM.IsProcessing = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(badge.IsVisible);
            vm.ChatVM.IsProcessing = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(badge.IsVisible);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
        }
    }
}
