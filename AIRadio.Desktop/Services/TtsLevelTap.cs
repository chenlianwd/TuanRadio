using System;
using NAudio.Wave;

namespace AIRadio.Desktop.Services;

// TTS 播放链音量探针：包在 MediaFoundationReader 与 WaveOutEvent 之间，透传音频
// 字节的同时计算每块的峰值（0..1）回调出去。Read 在 WaveOutEvent 播放线程调用，
// 回调只做轻量计算，不得触碰 UI。非 16-bit PCM 格式一律报 0——MediaFoundationReader
// 解码 mp3 恒为 16-bit PCM，该分支只是异常格式的安全降级（辉光落回基线）。
internal sealed class TtsLevelTap : IWaveProvider
{
    private readonly IWaveProvider _source;
    private readonly Action<float> _onPeak;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public TtsLevelTap(IWaveProvider source, Action<float> onPeak)
    {
        _source = source;
        _onPeak = onPeak;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var format = _source.WaveFormat;
        var peak = format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16
            ? ComputePeak(buffer, offset, read)
            : 0f;
        _onPeak(peak);
        return read;
    }

    // 峰值对交错声道与单声道等价（取全体样本绝对幅度最大值），无需按 blockAlign 拆帧
    internal static float ComputePeak(byte[] buffer, int offset, int bytes)
    {
        var samples = bytes / 2;
        float peak = 0;
        for (var i = 0; i < samples; i++)
        {
            var magnitude = Math.Abs(BitConverter.ToInt16(buffer, offset + i * 2)) / 32768f;
            if (magnitude > peak) peak = magnitude;
        }
        return peak;
    }
}
