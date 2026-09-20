using System;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class RadioSoundFxServiceTests
{
    [Fact]
    public void SynthesizeTuningSweep_ProducesValidPcmData()
    {
        using var service = new RadioSoundFxService();
        var pcm = service.GetSynthesizedPcm(RadioFxKind.TuningSweep);

        Assert.NotNull(pcm);
        // 320ms * 44100 samples/s * 2 bytes/sample = 28224 bytes
        Assert.Equal(28224, pcm.Length);

        // 检查首部淡入：第 1 个样本应接近 0（防爆音）
        var firstSample = BitConverter.ToInt16(pcm, 0);
        Assert.InRange(Math.Abs(firstSample), 0, 50);

        // 检查尾部淡出：最后 1 个样本应接近 0
        var lastSample = BitConverter.ToInt16(pcm, pcm.Length - 2);
        Assert.InRange(Math.Abs(lastSample), 0, 100);

        // 检查整体幅值：非静音且不爆音（在 -18dB 目标幅值约 4000 左右）
        short maxAmp = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(pcm, i));
            if (sample > maxAmp) maxAmp = sample;
        }

        Assert.True(maxAmp > 1000, $"Tuning sweep should not be silence, maxAmp was {maxAmp}");
        Assert.True(maxAmp <= 6000, $"Tuning sweep should stay around -18dB, maxAmp was {maxAmp}");
    }

    [Fact]
    public void SynthesizeStationChime_ProducesValidPcmData()
    {
        using var service = new RadioSoundFxService();
        var pcm = service.GetSynthesizedPcm(RadioFxKind.StationChime);

        Assert.NotNull(pcm);
        // 280ms * 44100 samples/s * 2 bytes/sample = 24696 bytes
        Assert.Equal(24696, pcm.Length);

        // 检查首部微淡入
        var firstSample = BitConverter.ToInt16(pcm, 0);
        Assert.InRange(Math.Abs(firstSample), 0, 50);

        // 检查钟声衰减：尾部振幅应显著小于头部峰值
        var tailSample = Math.Abs(BitConverter.ToInt16(pcm, pcm.Length - 200));

        short maxAmp = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(pcm, i));
            if (sample > maxAmp) maxAmp = sample;
        }

        Assert.True(maxAmp > 1000, $"Chime should not be silence, maxAmp was {maxAmp}");
        Assert.True(maxAmp <= 5000, $"Chime should not clip, maxAmp was {maxAmp}");
        Assert.True(tailSample < maxAmp / 10, $"Chime should decay exponentially, tail was {tailSample} vs max {maxAmp}");
    }

    [Fact]
    public void PlayFx_WhenDisabled_DoesNotThrow()
    {
        using var service = new RadioSoundFxService { IsEnabled = false };
        // 禁用状态下零操作且绝不抛出异常
        service.PlayFx(RadioFxKind.TuningSweep);
        service.PlayFx(RadioFxKind.StationChime);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimesSafely()
    {
        var service = new RadioSoundFxService();
        service.Dispose();
        service.Dispose(); // 幂等释放不应抛异常
    }
}
