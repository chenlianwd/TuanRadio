using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using Serilog;

namespace AIRadio.Desktop.Services;

public enum RadioFxKind
{
    /// <summary>FM 调频扫频：快速频率滑动与带通白噪声脉冲，用于 DJ 串场播报前过渡。</summary>
    TuningSweep,

    /// <summary>台呼微鸣音：双音清脆复古提示音，用于换台/节目单刷新。</summary>
    StationChime
}

public interface IRadioSoundFxService : IDisposable
{
    bool IsEnabled { get; set; }
    void PlayFx(RadioFxKind kind);
    byte[] GetSynthesizedPcm(RadioFxKind kind);
}

/// <summary>
/// 复古电台声音质感引擎：纯程序化合成（NAudio 44.1kHz 16-bit PCM），零外部音频大资产依赖。
/// </summary>
public class RadioSoundFxService : IRadioSoundFxService
{
    private const int SampleRate = 44100;
    private static readonly byte[] TuningSweepPcm;
    private static readonly byte[] StationChimePcm;

    private readonly ConcurrentDictionary<WaveOutEvent, byte> _activeOutputs = new();
    private int _disposed;

    public bool IsEnabled { get; set; } = true;

    static RadioSoundFxService()
    {
        TuningSweepPcm = SynthesizeTuningSweep();
        StationChimePcm = SynthesizeStationChime();
    }

    public byte[] GetSynthesizedPcm(RadioFxKind kind) => kind switch
    {
        RadioFxKind.StationChime => StationChimePcm,
        _ => TuningSweepPcm
    };

    public void PlayFx(RadioFxKind kind)
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
            return;

        var pcm = GetSynthesizedPcm(kind);
        if (pcm.Length == 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            MemoryStream? ms = null;
            RawSourceWaveStream? raw = null;
            WaveOutEvent? waveOut = null;

            try
            {
                ms = new MemoryStream(pcm);
                raw = new RawSourceWaveStream(ms, new WaveFormat(SampleRate, 16, 1));
                waveOut = new WaveOutEvent { DesiredLatency = 80 };
                waveOut.Init(raw);

                _activeOutputs.TryAdd(waveOut, 0);

                waveOut.PlaybackStopped += (s, e) =>
                {
                    _activeOutputs.TryRemove(waveOut, out byte _);
                    try
                    {
                        waveOut.Dispose();
                        raw.Dispose();
                        ms.Dispose();
                    }
                    catch { }
                };

                waveOut.Play();
            }
            catch (Exception ex)
            {
                if (waveOut != null)
                    _activeOutputs.TryRemove(waveOut, out byte _);

                // 无音频设备/驱动不可用时降级静默，不影响主流程
                Log.Debug(ex, "RadioSoundFx playback skipped or failed on current device");
                try { waveOut?.Dispose(); } catch { }
                try { raw?.Dispose(); } catch { }
                try { ms?.Dispose(); } catch { }
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var output in _activeOutputs.Keys)
        {
            try
            {
                output.Stop();
                output.Dispose();
            }
            catch { }
        }
        _activeOutputs.Clear();
    }

    /// <summary>
    /// 程序化合成调频扫频：正弦滑频（250Hz -> 1400Hz）+ Biquad 带通滤波白噪声（1600Hz, Q=1.2），
    /// 320ms，Hann 包络，峰值 -18dB（幅值约 4000）。
    /// </summary>
    private static byte[] SynthesizeTuningSweep()
    {
        const double durationSeconds = 0.320;
        var sampleCount = (int)(durationSeconds * SampleRate);
        var bytes = new byte[sampleCount * 2];

        // Biquad 带通滤波器系数 (f0 = 1600Hz, Q = 1.2)
        var omega0 = 2.0 * Math.PI * 1600.0 / SampleRate;
        var alpha = Math.Sin(omega0) / (2.0 * 1.2);
        var a0 = 1.0 + alpha;
        var b0 = alpha / a0;
        var b1 = 0.0;
        var b2 = -alpha / a0;
        var a1 = (-2.0 * Math.Cos(omega0)) / a0;
        var a2 = (1.0 - alpha) / a0;

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        var rng = new Random(42); // 固定种子保证波形确定性
        const double f0 = 250.0;
        const double f1 = 1400.0;

        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / SampleRate;
            var p = (double)i / sampleCount;

            // 频率二次方滑动积分相位
            var phase = 2.0 * Math.PI * (f0 * t + ((f1 - f0) / 3.0) * (t * p * p));
            var tone = Math.Sin(phase);

            // 均匀白噪声通过带通滤波
            var white = rng.NextDouble() * 2.0 - 1.0;
            var noise = b0 * white + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = white;
            y2 = y1;
            y1 = noise;

            // 混合：35% 音调 + 65% 带通沙沙杂音
            var mixed = 0.35 * tone + 0.65 * noise;

            // 平滑 Hann 包络：首 15ms 淡入，180ms 后淡出至 0
            double env;
            if (t < 0.015)
            {
                env = 0.5 * (1.0 - Math.Cos(Math.PI * (t / 0.015)));
            }
            else if (t > 0.180)
            {
                var fadeProgress = (t - 0.180) / (durationSeconds - 0.180);
                env = 0.5 * (1.0 + Math.Cos(Math.PI * Math.Clamp(fadeProgress, 0.0, 1.0)));
            }
            else
            {
                env = 1.0;
            }

            // 峰值约 -18dB (4000 / 32767)
            var sample = mixed * env * 3900.0;
            var sampleShort = (short)Math.Clamp(Math.Round(sample), short.MinValue, short.MaxValue);

            bytes[2 * i] = (byte)(sampleShort & 0xFF);
            bytes[2 * i + 1] = (byte)((sampleShort >> 8) & 0xFF);
        }

        return bytes;
    }

    /// <summary>
    /// 程序化合成台呼微鸣音：双音复古电台提示（880Hz + 1320Hz），280ms，钟声衰减，峰值 -20dB（幅值约 3200）。
    /// </summary>
    private static byte[] SynthesizeStationChime()
    {
        const double durationSeconds = 0.280;
        var sampleCount = (int)(durationSeconds * SampleRate);
        var bytes = new byte[sampleCount * 2];

        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / SampleRate;

            // 双音频复古提示音 (880Hz + 1320Hz)
            var tone = 0.55 * Math.Sin(2.0 * Math.PI * 880.0 * t) +
                       0.45 * Math.Sin(2.0 * Math.PI * 1320.0 * t);

            // 钟声包络：5ms 极速微淡入防爆音，随后指数衰减
            var attack = t < 0.005 ? 0.5 * (1.0 - Math.Cos(Math.PI * (t / 0.005))) : 1.0;
            var decay = Math.Exp(-12.0 * t);
            var env = attack * decay;

            var sample = tone * env * 3200.0;
            var sampleShort = (short)Math.Clamp(Math.Round(sample), short.MinValue, short.MaxValue);

            bytes[2 * i] = (byte)(sampleShort & 0xFF);
            bytes[2 * i + 1] = (byte)((sampleShort >> 8) & 0xFF);
        }

        return bytes;
    }
}
