using System;
using NAudio.Wasapi;
using NAudio.Wave;

namespace AIRadio.Desktop.Services;

/// <summary>用 WasapiLoopbackCapture 抓系统输出 + FFT，产生 32 频段频谱（子项目 3，替换 EmitSimulatedSpectrum）。</summary>
public sealed class SpectrumAnalyzer : IDisposable
{
    private const int BandCount = 32;
    private const int FftSize = 1024;

    private readonly WasapiLoopbackCapture? _capture;
    private readonly float[] _buffer = new float[FftSize];
    private int _bufferCount;

    public event Action<float[]>? SpectrumReady;

    public SpectrumAnalyzer()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
        }
        catch
        {
            // 无音频设备（测试/headless 环境）—— SpectrumReady 不触发
            _capture = null;
        }
    }

    public void Start()
    {
        try { _capture?.StartRecording(); }
        catch { /* 设备占用或不可用 */ }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture == null) return;
        var ch = _capture.WaveFormat.Channels;
        if (ch < 1) ch = 1;
        var samples = e.BytesRecorded / 4; // IEEE float 32-bit

        for (int i = 0; i < samples; i += ch)
        {
            float mono = 0;
            int c = 0;
            for (; c < ch && i + c < samples; c++)
                mono += BitConverter.ToSingle(e.Buffer, (i + c) * 4);
            mono = mono / c;
            _buffer[_bufferCount++] = mono;
            if (_bufferCount >= FftSize)
            {
                ProcessFft();
                _bufferCount = 0;
            }
        }
    }

    private void ProcessFft()
    {
        var re = new double[FftSize];
        var im = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            var w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)); // Hann 窗
            re[i] = _buffer[i] * w;
        }
        Fft(re, im);

        var data = new float[BandCount];
        var half = FftSize / 2;
        for (int b = 0; b < BandCount; b++)
        {
            // 对数频率分组（低频窄、高频宽）
            var lo = (int)Math.Floor(Math.Pow(half, b / (double)BandCount));
            var hi = (int)Math.Floor(Math.Pow(half, (b + 1) / (double)BandCount));
            if (hi <= lo) hi = lo + 1;
            double sum = 0;
            for (int k = lo; k < hi && k < half; k++)
                sum += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            // 归一化 + 适度增益，落 0~1
            data[b] = (float)Math.Clamp(sum / (hi - lo) * 1.5, 0, 1);
        }
        SpectrumReady?.Invoke(data);
    }

    /// <summary>radix-2 Cooley-Tukey FFT（in-place，re/im 长度需 2 的幂）。</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    var tRe = curRe * re[i + k + len / 2] - curIm * im[i + k + len / 2];
                    var tIm = curRe * im[i + k + len / 2] + curIm * re[i + k + len / 2];
                    re[i + k + len / 2] = re[i + k] - tRe;
                    im[i + k + len / 2] = im[i + k] - tIm;
                    re[i + k] += tRe;
                    im[i + k] += tIm;
                    var nRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nRe;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.Dispose();
        }
    }
}
