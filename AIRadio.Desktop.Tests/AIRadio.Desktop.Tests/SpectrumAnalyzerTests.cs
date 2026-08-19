using AIRadio.Desktop.Services;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class SpectrumAnalyzerTests
{
    [Fact]
    public void FallbackSpectrum_ProducesMovingValuesWithinDisplayRange()
    {
        var first = SpectrumAnalyzer.CreateFallbackSpectrum(0);
        var second = SpectrumAnalyzer.CreateFallbackSpectrum(0.5);

        Assert.Equal(32, first.Length);
        Assert.Equal(32, second.Length);
        Assert.All(first, value => Assert.InRange(value, 0.04f, 1f));
        Assert.All(second, value => Assert.InRange(value, 0.04f, 1f));
        Assert.NotEqual(first[0], second[0]);
    }

    [Fact]
    public void FallbackTick_DoesNotEmitWhenPlaybackIsInactive()
    {
        long now = 1_000;
        using var analyzer = new SpectrumAnalyzer(() => false, () => now, initializeCapture: false);
        var received = new List<float[]>();
        analyzer.SpectrumReady += received.Add;

        analyzer.RunFallbackTickForTesting();

        Assert.Empty(received);
    }

    [Fact]
    public void SilentCapturedFrame_DoesNotPreventFallback()
    {
        long now = 1_000;
        using var analyzer = new SpectrumAnalyzer(() => true, () => now, initializeCapture: false);
        var received = new List<float[]>();
        analyzer.SpectrumReady += received.Add;

        analyzer.PushCapturedSpectrumForTesting(new float[32]);
        analyzer.RunFallbackTickForTesting();

        Assert.Single(received);
        Assert.Contains(received[0], value => value > 0.04f);
    }

    [Fact]
    public void LowLevelCapturedFrame_DoesNotPreventFallback()
    {
        long now = 1_000;
        using var analyzer = new SpectrumAnalyzer(() => true, () => now, initializeCapture: false);
        var received = new List<float[]>();
        analyzer.SpectrumReady += received.Add;
        var lowLevelSpectrum = new float[32];
        Array.Fill(lowLevelSpectrum, 0.02f);

        analyzer.PushCapturedSpectrumForTesting(lowLevelSpectrum);
        analyzer.RunFallbackTickForTesting();

        Assert.Single(received);
        Assert.NotSame(lowLevelSpectrum, received[0]);
        Assert.Contains(received[0], value => value > 0.04f);
    }

    [Fact]
    public void MeaningfulCapturedFrame_SuppressesFallbackUntilTimeout()
    {
        long now = 1_000;
        using var analyzer = new SpectrumAnalyzer(() => true, () => now, initializeCapture: false);
        var received = new List<float[]>();
        analyzer.SpectrumReady += received.Add;
        var realSpectrum = new float[32];
        realSpectrum[4] = 0.5f;

        analyzer.PushCapturedSpectrumForTesting(realSpectrum);
        now += 100;
        analyzer.RunFallbackTickForTesting();

        Assert.Single(received);
        Assert.Same(realSpectrum, received[0]);

        now += 100;
        analyzer.RunFallbackTickForTesting();

        Assert.Equal(2, received.Count);
        Assert.NotSame(realSpectrum, received[1]);
    }

    [Fact]
    public void Start_EmitsFallbackWhenCaptureIsUnavailableAndPlaybackIsActive()
    {
        using var analyzer = new SpectrumAnalyzer(
            () => true,
            () => Environment.TickCount64,
            initializeCapture: false);
        using var received = new ManualResetEventSlim();
        analyzer.SpectrumReady += _ => received.Set();

        analyzer.Start();

        Assert.True(received.Wait(1_000));
    }

    [Fact]
    public void Dispose_PreventsFurtherFallbackPublication()
    {
        long now = 1_000;
        var analyzer = new SpectrumAnalyzer(() => true, () => now, initializeCapture: false);
        var received = new List<float[]>();
        analyzer.SpectrumReady += received.Add;

        analyzer.Dispose();
        analyzer.RunFallbackTickForTesting();

        Assert.Empty(received);
    }
}
