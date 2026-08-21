using System;
using System.Threading;
using NAudio.Wasapi;
using NAudio.Wave;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>用 WasapiLoopbackCapture 抓系统输出 + FFT，产生 32 频段频谱（子项目 3，替换 EmitSimulatedSpectrum）。</summary>
public sealed class SpectrumAnalyzer : IDisposable
{
    private const int BandCount = 32;
    private const int FftSize = 1024;
    private const int FallbackIntervalMs = 33;
    private const int RealSpectrumTimeoutMs = 180;
    // 低于此峰值时，真实回环数据通常只是设备底噪；让播放态视觉兜底接管，
    // 避免底噪不断刷新时间戳，导致频谱条永远停在最小高度。
    private const float RealSpectrumActivityThreshold = 0.08f;

    private readonly WasapiLoopbackCapture? _capture;
    private readonly Func<bool>? _isPlaybackActive;
    private readonly Func<long> _getTimestamp;
    private readonly Timer _fallbackTimer;
    private readonly object _publishGate = new();
    private readonly float[] _buffer = new float[FftSize];
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imaginary = new double[FftSize];

    // 对数频段边界只依赖 FftSize/BandCount，预计算避免每帧 64 次 Math.Pow
    private static readonly int[] BandEdges = BuildBandEdges();

    private static int[] BuildBandEdges()
    {
        var half = FftSize / 2;
        var edges = new int[BandCount + 1];
        for (int b = 0; b <= BandCount; b++)
            edges[b] = (int)Math.Floor(Math.Pow(half, b / (double)BandCount));
        return edges;
    }

    private int _bufferCount;
    private long _lastRealSpectrumAtMs;
    private int _fallbackWarningLogged;
    private int _disposed;

    public event Action<float[]>? SpectrumReady;

    public SpectrumAnalyzer(Func<bool>? isPlaybackActive = null)
        : this(isPlaybackActive, () => Environment.TickCount64, initializeCapture: true)
    {
    }

    internal SpectrumAnalyzer(
        Func<bool>? isPlaybackActive,
        Func<long> getTimestamp,
        bool initializeCapture)
    {
        _isPlaybackActive = isPlaybackActive;
        _getTimestamp = getTimestamp;
        _fallbackTimer = new Timer(_ => EmitFallbackIfNeeded(), null, Timeout.Infinite, Timeout.Infinite);

        if (!initializeCapture)
        {
            _capture = null;
            return;
        }

        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
        }
        catch (Exception ex)
        {
            // 无音频设备时仍允许播放态视觉兜底运行。
            Log.Warning(ex, "Spectrum loopback capture is unavailable; visual fallback remains available");
            _capture = null;
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _capture?.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Spectrum loopback capture could not start; visual fallback remains available");
        }

        _fallbackTimer.Change(100, FallbackIntervalMs);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture == null || Volatile.Read(ref _disposed) != 0) return;

        try
        {
            var format = _capture.WaveFormat;
            var channels = Math.Max(1, format.Channels);
            var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
            var blockAlign = format.BlockAlign > 0
                ? format.BlockAlign
                : channels * bytesPerSample;
            var frameCount = e.BytesRecorded / blockAlign;

            for (int frame = 0; frame < frameCount; frame++)
            {
                var frameOffset = frame * blockAlign;
                float mono = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    var sampleOffset = frameOffset + channel * bytesPerSample;
                    mono += ReadSample(e.Buffer, sampleOffset, bytesPerSample, format.Encoding);
                }

                _buffer[_bufferCount++] = mono / channels;
                if (_bufferCount >= FftSize)
                {
                    ProcessFft();
                    _bufferCount = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Spectrum loopback data could not be processed");
            _bufferCount = 0;
        }
    }

    private void ProcessFft()
    {
        var re = _real;
        var im = _imaginary;
        Array.Clear(im);
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
            // 对数频率分组（低频窄、高频宽），边界来自预计算表
            var lo = BandEdges[b];
            var hi = BandEdges[b + 1];
            if (hi <= lo) hi = lo + 1;
            double sum = 0;
            for (int k = lo; k < hi && k < half; k++)
                sum += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);

            // FFT 幅度先除以采样点数，再映射到 -60dB..0dB，避免不同音量下
            // 频谱条全部挤在最小高度或全部直接顶满。
            var averageMagnitude = sum / (hi - lo) * 2 / FftSize;
            var decibels = 20 * Math.Log10(Math.Max(averageMagnitude, 1e-6));
            data[b] = (float)Math.Clamp((decibels + 60) / 60, 0, 1);
        }

        HandleCapturedSpectrum(data);
    }

    private void EmitFallbackIfNeeded()
    {
        if (_isPlaybackActive == null || Volatile.Read(ref _disposed) != 0)
            return;

        bool isPlaybackActive;
        try
        {
            isPlaybackActive = _isPlaybackActive();
        }
        catch
        {
            return;
        }

        if (!isPlaybackActive)
            return;

        var now = _getTimestamp();
        var lastRealSpectrumAtMs = Interlocked.Read(ref _lastRealSpectrumAtMs);
        if (lastRealSpectrumAtMs > 0 && now - lastRealSpectrumAtMs < RealSpectrumTimeoutMs)
            return;

        if (Interlocked.Exchange(ref _fallbackWarningLogged, 1) == 0)
        {
            Log.Warning("Spectrum loopback capture produced no recent data; using playback visual fallback");
        }

        PublishSpectrum(CreateFallbackSpectrum(now / 1000.0));
    }

    private void HandleCapturedSpectrum(float[] data)
    {
        var now = _getTimestamp();
        var hasMeaningfulSignal = false;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] >= RealSpectrumActivityThreshold)
            {
                hasMeaningfulSignal = true;
                break;
            }
        }

        if (hasMeaningfulSignal)
        {
            Interlocked.Exchange(ref _lastRealSpectrumAtMs, now);
            Interlocked.Exchange(ref _fallbackWarningLogged, 0);
            PublishSpectrum(data);
            return;
        }

        // 短暂静音沿用真实频谱；持续静音不更新时间戳，让播放态兜底接管。
        var lastRealSpectrumAtMs = Interlocked.Read(ref _lastRealSpectrumAtMs);
        if (lastRealSpectrumAtMs > 0 && now - lastRealSpectrumAtMs < RealSpectrumTimeoutMs)
            PublishSpectrum(data);
    }

    private void PublishSpectrum(float[] data)
    {
        lock (_publishGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            SpectrumReady?.Invoke(data);
        }
    }

    internal void RunFallbackTickForTesting() => EmitFallbackIfNeeded();

    internal void PushCapturedSpectrumForTesting(float[] data) => HandleCapturedSpectrum(data);

    internal static float[] CreateFallbackSpectrum(double time)
    {
        var beat = 0.5 + 0.5 * Math.Sin(time * 3.2);
        var bassPulse = Math.Pow(Math.Max(0, Math.Sin(time * 5.8)), 2);
        var midPulse = 0.5 + 0.5 * Math.Sin(time * 2.1 + Math.Sin(time * 0.7));
        var treblePulse = 0.2 + 0.2 * (0.5 + 0.5 * Math.Sin(time * 8.7));
        var data = new float[BandCount];

        for (int i = 0; i < data.Length; i++)
        {
            var band = i / (double)(data.Length - 1);
            var envelope = band < 0.25
                ? 0.55 * bassPulse
                : band < 0.7
                    ? 0.38 * midPulse
                    : 0.24 * treblePulse;
            var wave = 0.24 * Math.Sin(time * (7 + band * 12) + i * 0.55);
            data[i] = (float)Math.Clamp(0.12 + envelope + beat * 0.18 + wave, 0.04, 1);
        }

        return data;
    }

    private static float ReadSample(byte[] buffer, int offset, int bytesPerSample, WaveFormatEncoding encoding)
    {
        if (offset < 0 || offset + bytesPerSample > buffer.Length)
            return 0;

        if (encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample >= 4)
            return BitConverter.ToSingle(buffer, offset);

        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => Read24BitSample(buffer, offset),
            4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0
        };
    }

    private static float Read24BitSample(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        if ((value & 0x800000) != 0)
            value |= unchecked((int)0xFF000000);
        return value / 8388608f;
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // DisposeAsync 会等待已排队的 Timer 回调退出，避免回调在下游 Subject
        // 已释放后继续发布。发布锁同时等待可能正在执行的采集回调完成。
        _fallbackTimer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
        }

        lock (_publishGate)
        {
            SpectrumReady = null;
        }
    }
}
