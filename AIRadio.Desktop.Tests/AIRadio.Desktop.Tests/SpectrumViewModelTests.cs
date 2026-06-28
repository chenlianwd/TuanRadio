using System.Reactive.Linq;
using System.Reactive.Subjects;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class SpectrumViewModelTests
{
    [Fact]
    public void Constructor_Initializes32BandsAtZero()
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.SpectrumData).Returns(Observable.Empty<float[]>());

        using var vm = new SpectrumViewModel(audioMock.Object);

        Assert.Equal(32, vm.Bands.Count);
        Assert.All(vm.Bands, b => Assert.Equal(0f, b));
    }

    [Fact]
    public void SpectrumData_UpdatesBands()
    {
        var spectrum = new Subject<float[]>();
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.SpectrumData).Returns(spectrum);

        using var vm = new SpectrumViewModel(audioMock.Object);

        var data = new float[32];
        data[0] = 0.5f;
        data[1] = 0.8f;
        spectrum.OnNext(data);

        Assert.Equal(0.5f, vm.Bands[0]);
        Assert.Equal(0.8f, vm.Bands[1]);
        Assert.Equal(0f, vm.Bands[2]);
    }

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
        vm.Dispose();

        // Should not throw or update after dispose
        spectrum.OnNext(new float[32]);
        Assert.Equal(0f, vm.Bands[0]);
    }
}
