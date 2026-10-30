using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 音色试听：合成走与 DJ 开口同链的风格 Id（female-yujie 等，映射真实 Edge 音色+
// 基线韵律在 SynthesizeAsync 内部），播放走真实 TTS 通道。锚定合成参数、播放链、
// 失败文案可见性、防重入与 XAML 接线。
public class SettingsVoicePreviewTests
{
    private readonly Mock<ILLMService> _mockLlm = new();
    private readonly Mock<ISecureStorage> _mockStorage = new();

    private static string CreateTempSettingsFile()
        => Path.Combine(Path.GetTempPath(), $"airadio-voice-preview-{Guid.NewGuid():N}.json");

    private SettingsViewModel CreateViewModel(ITtsService? tts = null, IAudioService? audio = null)
    {
        _mockStorage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        return new SettingsViewModel(
            _mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            ttsService: tts, audioService: audio);
    }

    // 真实 AudioService 的 TtsError 恒为可订阅流；宽松 mock 默认返回 null 会让
    // 播放窗口订阅抛 NRE 落进通用 catch，mock 须反映契约
    private static Mock<IAudioService> CreateAudioMock(IObservable<string>? ttsError = null)
    {
        var mock = new Mock<IAudioService>();
        mock.SetupGet(x => x.TtsError).Returns(ttsError ?? Observable.Never<string>());
        return mock;
    }

    [Fact]
    public void PreviewTexts_SixVoices_DistinctAndTimeNeutral()
    {
        string[] voiceIds =
        {
            "female-shaonv", "female-yujie", "female-chengshu",
            "male-qn-qingse", "male-qn-jingying", "male-qn-badao"
        };

        // 回落句与六档专属句一起参与互异（回落句退化成某专属句的复制也要红）
        var texts = voiceIds
            .Select(id => SettingsViewModel.GetVoicePreviewText(id))
            .Append(SettingsViewModel.GetVoicePreviewText("unknown-voice"))
            .ToArray();
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        // 每档专属介绍语互不相同
        Assert.Equal(texts.Length, texts.Distinct().Count());
        // 时间中立：不预设时段（中英文任一语言下都不得出现）
        Assert.All(texts, t =>
        {
            Assert.DoesNotContain("今晚", t, StringComparison.Ordinal);
            Assert.DoesNotContain("tonight", t, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task PreviewVoice_SynthesizesSelectedStyle_ThroughProductionChain()
    {
        var mockTts = new Mock<ITtsService>();
        var mockAudio = CreateAudioMock();
        var audio = new byte[] { 1, 2, 3 };
        mockTts.Setup(x => x.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(audio);

        using var vm = CreateViewModel(mockTts.Object, mockAudio.Object);
        vm.CharacterVoice = new VoiceOption { Id = "female-yujie" };
        var expectedText = SettingsViewModel.GetVoicePreviewText("female-yujie");

        await vm.PreviewVoiceCommand.Execute().FirstAsync();

        // 风格 Id 原样进合成（映射与基线韵律在服务内部=与 DJ 开口同链）；emotion 中性
        mockTts.Verify(x => x.SynthesizeAsync(
            expectedText, "female-yujie", "neutral", It.IsAny<CancellationToken>()), Times.Once);
        // 音频字节原样进真实 TTS 播放通道
        mockAudio.Verify(x => x.PlayTtsAudio(audio), Times.Once);
        Assert.False(vm.IsPreviewingVoice);
        Assert.Equal(string.Empty, vm.VoicePreviewStatus);
    }

    [Fact]
    public async Task PreviewVoice_SynthesisCanceled_ShowsTimeoutStatus()
    {
        var mockTts = new Mock<ITtsService>();
        var mockAudio = CreateAudioMock();
        mockTts.Setup(x => x.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        using var vm = CreateViewModel(mockTts.Object, mockAudio.Object);
        vm.CharacterVoice = new VoiceOption { Id = "female-shaonv" };

        await vm.PreviewVoiceCommand.Execute().FirstAsync();

        // OCE 走专属超时文案（语言中立匹配中英任一），删掉该 catch 分支会落
        // 通用"失败"文案使此断言红
        Assert.True(
            vm.VoicePreviewStatus.Contains("超时", StringComparison.Ordinal) ||
            vm.VoicePreviewStatus.Contains("timed out", StringComparison.OrdinalIgnoreCase),
            $"expected timeout wording, got: {vm.VoicePreviewStatus}");
        mockAudio.Verify(x => x.PlayTtsAudio(It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task PreviewVoice_PlaybackStartupFailure_SurfacesTtsErrorInStatus()
    {
        // 无输出设备等场景：PlayTtsAudio 不外抛、只经 TtsError 通道广播（常规展示 UI
        // 在设置弹层之下）——试听须在播放窗口内捕获并落回状态行
        var mockTts = new Mock<ITtsService>();
        var mockAudio = CreateAudioMock();
        var ttsError = new System.Reactive.Subjects.Subject<string>();
        mockTts.Setup(x => x.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 4, 5 });
        mockAudio.SetupGet(x => x.TtsError).Returns(ttsError);
        mockAudio.Setup(x => x.PlayTtsAudio(It.IsAny<byte[]>()))
            .Callback<byte[]>(_ => ttsError.OnNext("播放设备不可用"));

        using var vm = CreateViewModel(mockTts.Object, mockAudio.Object);
        vm.CharacterVoice = new VoiceOption { Id = "female-yujie" };

        await vm.PreviewVoiceCommand.Execute().FirstAsync();

        Assert.Equal("播放设备不可用", vm.VoicePreviewStatus);
    }

    [Fact]
    public async Task PreviewVoice_AfterFailure_RetryClearsStatus()
    {
        var mockTts = new Mock<ITtsService>();
        var mockAudio = CreateAudioMock();
        var audio = new byte[] { 8 };
        mockTts.SetupSequence(x => x.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"))
            .ReturnsAsync(audio);

        using var vm = CreateViewModel(mockTts.Object, mockAudio.Object);
        vm.CharacterVoice = new VoiceOption { Id = "male-qn-qingse" };

        await vm.PreviewVoiceCommand.Execute().FirstAsync();
        Assert.False(string.IsNullOrEmpty(vm.VoicePreviewStatus));

        // 失败后重试成功：状态清空、正常播放
        await vm.PreviewVoiceCommand.Execute().FirstAsync();
        Assert.Equal(string.Empty, vm.VoicePreviewStatus);
        mockAudio.Verify(x => x.PlayTtsAudio(audio), Times.Once);
    }

    [Fact]
    public async Task PreviewVoice_SynthesisFailure_ShowsStatusAndSkipsPlayback()
    {
        var mockTts = new Mock<ITtsService>();
        var mockAudio = CreateAudioMock();
        mockTts.Setup(x => x.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));

        using var vm = CreateViewModel(mockTts.Object, mockAudio.Object);
        vm.CharacterVoice = new VoiceOption { Id = "male-qn-badao" };

        await vm.PreviewVoiceCommand.Execute().FirstAsync();

        Assert.False(string.IsNullOrEmpty(vm.VoicePreviewStatus));
        mockAudio.Verify(x => x.PlayTtsAudio(It.IsAny<byte[]>()), Times.Never);
        Assert.False(vm.IsPreviewingVoice);
    }

    [Fact]
    public async Task PreviewVoice_EmptyAudio_ShowsStatusWithoutPlayback()
    {
        var mockTts = new Mock<ITtsService>();
        var mockAudio = CreateAudioMock();
        mockTts.Setup(x => x.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<byte>());

        using var vm = CreateViewModel(mockTts.Object, mockAudio.Object);
        vm.CharacterVoice = new VoiceOption { Id = "female-chengshu" };

        await vm.PreviewVoiceCommand.Execute().FirstAsync();

        Assert.False(string.IsNullOrEmpty(vm.VoicePreviewStatus));
        mockAudio.Verify(x => x.PlayTtsAudio(It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task PreviewVoice_WhileRunning_DisablesCommandUntilDone()
    {
        var mockTts = new Mock<ITtsService>();
        var mockAudio = CreateAudioMock();
        var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        mockTts.Setup(x => x.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        using var vm = CreateViewModel(mockTts.Object, mockAudio.Object);
        vm.CharacterVoice = new VoiceOption { Id = "male-qn-jingying" };
        // canExecute 是推送流（StartWith(false) 起始，随后初始可用 true）。
        // 有序断言"先 true 后 false"才有区分度：命令从未执行时序列止于 [false,true]。
        // 防重入由 ReactiveCommand 内建 isExecuting 与 IsPreviewingVoice 双保险，
        // 显式 canExecute 的价值是与 IsPreviewingVoice 同步的即时禁用
        var observed = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        using var sub = vm.PreviewVoiceCommand.CanExecute.Subscribe(observed.Enqueue);

        var execution = vm.PreviewVoiceCommand.Execute().FirstAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!vm.IsPreviewingVoice && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        Assert.True(vm.IsPreviewingVoice);

        gate.SetResult(new byte[] { 7 });
        await execution;

        Assert.False(vm.IsPreviewingVoice);
        bool[] sequence = Array.Empty<bool>();
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            sequence = observed.ToArray();
            var firstTrue = Array.IndexOf(sequence, true);
            if (firstTrue >= 0 && Array.LastIndexOf(sequence, false) > firstTrue)
                break;
            Thread.Sleep(20);
        }
        var firstAvailable = Array.IndexOf(sequence, true);
        var lastDisabled = Array.LastIndexOf(sequence, false);
        Assert.True(firstAvailable >= 0 && lastDisabled > firstAvailable,
            $"canExecute 应先可用(true)后禁用(false)，实际序列 [{string.Join(",", sequence)}]");
    }

    [Fact]
    public async Task PreviewVoice_NullServices_SilentNoOp()
    {
        // 旧构造签名（服务未注入）不抛异常、不写状态
        using var vm = CreateViewModel();
        vm.CharacterVoice = new VoiceOption { Id = "female-shaonv" };

        await vm.PreviewVoiceCommand.Execute().FirstAsync();

        Assert.False(vm.IsPreviewingVoice);
        Assert.Equal(string.Empty, vm.VoicePreviewStatus);
    }

    // XAML 接线：试听按钮 Command 绑定到 SettingsVM.PreviewVoiceCommand。
    // 弹层保持隐藏（绑定激活在逻辑树 attach 与 IsVisible 无关，同 SettingsNavTests 手法）
    [AvaloniaFact]
    public void VoicePreviewButton_BindsPreviewCommand()
    {
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();

            var view = window.FindDescendantOfType<SettingsView>()
                ?? throw new InvalidOperationException("SettingsView not found");
            var button = view.FindControl<Button>("VoicePreviewButton")
                ?? throw new InvalidOperationException("VoicePreviewButton not found");
            var statusText = view.FindControl<TextBlock>("VoicePreviewStatusText")
                ?? throw new InvalidOperationException("VoicePreviewStatusText not found");

            Assert.Same(vm.SettingsVM.PreviewVoiceCommand, button.Command);
            // 状态行绑定求值：初始为空串（绑定断链时 Text 为 null）
            Assert.Equal(string.Empty, statusText.Text);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
        }
    }
}
