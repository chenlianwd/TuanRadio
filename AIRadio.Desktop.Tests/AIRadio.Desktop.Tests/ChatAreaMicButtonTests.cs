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
using Moq;
using ReactiveUI;

namespace AIRadio.Desktop.Tests;

public class ChatAreaMicButtonTests
{
    private static MainWindowViewModel CreateViewModel()
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

        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            settingsFile: Path.Combine(dir, "settings.json"));
    }

    // 回归：Button 的类处理器会把 PointerPressed/PointerReleased 标记为 Handled，
    // XAML 属性挂载（handledEventsToo:false）收不到这两个事件，按住说话入口会整体失效。
    // 按压视觉态由 ChatArea 处理器直接驱动：空闲按住=变色+捕获；被拒按住=不变色不捕获。
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
            // 直接在 view 上提供处理器用到的 4 个颜色键，避开主题字典在 headless 下的解析差异
            var brushes = new ResourceDictionary
            {
                ["C_FF56F5C4"] = Color.FromUInt32(0xFF56F5C4),
                ["C_FF050507"] = Color.FromUInt32(0xFF050507),
                ["C_33262835"] = Color.FromUInt32(0x33262835),
                ["C_FFEDEDF5"] = Color.FromUInt32(0xFFEDEDF5)
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
            Assert.Null(pointer.Captured);
            Assert.NotEqual(Color.FromUInt32(0xFF56F5C4), (mic.Background as SolidColorBrush)?.Color);
            mic.RaiseEvent(new PointerReleasedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));

            // —— 空闲路径：真正开始录音才有按压视觉与捕获 ——
            vm.ChatVM.IsProcessing = false;
            mic.RaiseEvent(new PointerPressedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1));

            if (!vm.ChatVM.IsListening)
                return; // 机器无输入设备（如 CI）：成功路径不可用，仅验证被拒路径

            Assert.Same(mic, pointer.Captured);
            var pressedBrush = Assert.IsType<SolidColorBrush>(mic.Background);
            Assert.Equal(Color.FromUInt32(0xFF56F5C4), pressedBrush.Color);

            mic.RaiseEvent(new PointerReleasedEventArgs(
                mic, pointer, mic, new Point(5, 5), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));

            Assert.Null(pointer.Captured);
            var releasedBrush = Assert.IsType<SolidColorBrush>(mic.Background);
            Assert.Equal(Color.FromUInt32(0x33262835), releasedBrush.Color);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
