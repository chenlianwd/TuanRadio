using System;
using AIRadio.Desktop.Services;
using NAudio.Wave;
using Xunit;

namespace AIRadio.Desktop.Tests;

// TTS 播放链音量探针：说话徽章"跟随真实音量"辉光的数据源。
// 包在 MediaFoundationReader 与 WaveOutEvent 之间，透传字节并按块回调峰值（0..1）。
public class TtsLevelTapTests
{
    private sealed class FakeSource : IWaveProvider
    {
        private readonly byte[] _data;
        public WaveFormat WaveFormat { get; }
        public FakeSource(WaveFormat format, byte[] data)
        {
            WaveFormat = format;
            _data = data;
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _data.Length);
            Array.Copy(_data, 0, buffer, offset, n);
            return n;
        }
    }

    [Fact]
    public void ComputePeak_TakesMaxAbsoluteSample_WithOffsetAndTrailingByteIgnored()
    {
        // 样本 0.5 / -0.75 / 0.25 → 峰值 0.75；offset=1 起读、长度 5（末尾半个样本被忽略）
        var data = new byte[7];
        Array.Copy(BitConverter.GetBytes((short)0), 0, data, 0, 2);      // 前置垫字节
        Array.Copy(BitConverter.GetBytes((short)16384), 0, data, 1, 2);  // 0.5
        Array.Copy(BitConverter.GetBytes((short)-24576), 0, data, 3, 2); // -0.75
        Array.Copy(BitConverter.GetBytes((short)8192), 0, data, 5, 2);   // 0.25（部分越过长度）
        Assert.Equal(0.75f, TtsLevelTap.ComputePeak(data, 1, 5), precision: 3);
    }

    [Fact]
    public void Read_PassesBytesThroughAndReportsPeak()
    {
        var data = new byte[4];
        Array.Copy(BitConverter.GetBytes((short)32767), 0, data, 0, 2);
        Array.Copy(BitConverter.GetBytes((short)0), 0, data, 2, 2);
        var source = new FakeSource(new WaveFormat(24000, 16, 1), data);
        float? reported = null;
        var tap = new TtsLevelTap(source, p => reported = p);

        var buffer = new byte[4];
        var read = tap.Read(buffer, 0, 4);

        Assert.Equal(4, read);
        Assert.Equal(data, buffer);
        Assert.NotNull(reported);
        Assert.Equal(1.0f, reported!.Value, precision: 3);
    }

    [Fact]
    public void Read_NonPcm16Format_ReportsZeroButStillPassesBytesThrough()
    {
        var data = new byte[] { 0xFF, 0x7F, 0x34 };
        var source = new FakeSource(new WaveFormat(8000, 8, 1), data);
        float? reported = null;
        var tap = new TtsLevelTap(source, p => reported = p);

        var buffer = new byte[3];
        var read = tap.Read(buffer, 0, 3);

        Assert.Equal(3, read);
        Assert.Equal(data, buffer);
        Assert.NotNull(reported);
        Assert.Equal(0f, reported!.Value, precision: 3);
    }
}
