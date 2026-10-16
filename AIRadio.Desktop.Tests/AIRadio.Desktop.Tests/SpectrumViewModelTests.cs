using System.Reactive.Linq;
using System.Reactive.Subjects;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 频谱 VM 只保留 SpectrumReceived 事件通道（全部渲染经此直连）；
/// 曾有的 Bands ObservableCollection 零消费者，已按 2026-10-15 审查裁决移除。
/// </summary>
public class SpectrumViewModelTests
{
    [Fact]
    public void SpectrumData_FiresSpectrumReceivedEvent()
    {
        var spectrum = new Subject<float[]>();
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.SpectrumData).Returns(spectrum);

        using var vm = new SpectrumViewModel(audioMock.Object);

        float[]? received = null;
        vm.SpectrumReceived += data => received = data;

        var data = new float[] { 0.1f, 0.2f };
        spectrum.OnNext(data);

        Assert.NotNull(received);
        Assert.Same(data, received);
    }

    [Fact]
    public void Dispose_UnsubscribesFromSpectrumData()
    {
        var spectrum = new Subject<float[]>();
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.SpectrumData).Returns(spectrum);

        var vm = new SpectrumViewModel(audioMock.Object);
        float[]? received = null;
        vm.SpectrumReceived += data => received = data;
        vm.Dispose();

        // Dispose 后不再向下游转发
        spectrum.OnNext(new float[32]);
        Assert.Null(received);
    }
}
