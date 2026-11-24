using System;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 录音链路的注入替身：StartRecording 按配置回灌 PCM 数据、StopRecording 同步回执
/// RecordingStopped（真实 WaveInEvent 是异步回执），全程不触碰真实麦克风。
/// NAudio 类型用全限定名：using NAudio.Wave 会与 Models.PlaybackState 冲突。
/// </summary>
internal sealed class FakeWaveIn : NAudio.Wave.IWaveIn
{
    public NAudio.Wave.WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);

    /// <summary>StartRecording 时回灌的 PCM 数据；null 时回灌空包。</summary>
    public byte[]? InitialAudioBytes { get; set; }

    public event EventHandler<NAudio.Wave.WaveInEventArgs>? DataAvailable;
    public event EventHandler<NAudio.Wave.StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        var data = InitialAudioBytes ?? Array.Empty<byte>();
        DataAvailable?.Invoke(this, new NAudio.Wave.WaveInEventArgs(data, data.Length));
    }

    public void StopRecording() =>
        RecordingStopped?.Invoke(this, new NAudio.Wave.StoppedEventArgs());

    public void SimulateUnexpectedStop(Exception? exception = null) =>
        RecordingStopped?.Invoke(this, new NAudio.Wave.StoppedEventArgs(exception));

    public static byte[] CreateTonePcm16(int durationMs = 500, short amplitude = 4000)
    {
        var sampleCount = 16000 * durationMs / 1000;
        var bytes = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000.0) * amplitude);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return bytes;
    }

    public static byte[] CreateSilentPcm16(int durationMs = 500)
        => new byte[16000 * durationMs / 1000 * 2];

    public void Dispose() { }
}
