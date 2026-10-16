using System;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 录音链路的注入替身：StartRecording 即回灌空数据、StopRecording 同步回执
/// RecordingStopped（真实 WaveInEvent 是异步回执），全程不触碰真实麦克风。
/// NAudio 类型用全限定名：using NAudio.Wave 会与 Models.PlaybackState 冲突。
/// </summary>
internal sealed class FakeWaveIn : NAudio.Wave.IWaveIn
{
    public NAudio.Wave.WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);

    public event EventHandler<NAudio.Wave.WaveInEventArgs>? DataAvailable;
    public event EventHandler<NAudio.Wave.StoppedEventArgs>? RecordingStopped;

    public void StartRecording() =>
        DataAvailable?.Invoke(this, new NAudio.Wave.WaveInEventArgs(Array.Empty<byte>(), 0));

    public void StopRecording() =>
        RecordingStopped?.Invoke(this, new NAudio.Wave.StoppedEventArgs());

    public void Dispose() { }
}
