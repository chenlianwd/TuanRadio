using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 真实 LibVLC 链路的实际轨道信息采集回归：播放本地静音 WAV 后应发布
/// TrackMediaInfo（编码/采样率/声道）。在线流（ParseNetwork，含 Audius 302、
/// 网易签名 CDN）无法自动化，属人工验收清单项。
/// </summary>
public class AudioServiceMediaInfoTests
{
    [Fact]
    public async Task LocalFilePlayback_EmitsTrackMediaInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var audio = new AudioService();
        try
        {
            var wav = WriteSilenceWav(Path.Combine(dir, "silence.wav"), 2000);
            var track = Track.FromFile(wav);

            var infoReady = new TaskCompletionSource<TrackMediaInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = audio.TrackMediaInfo.FirstAsync().Subscribe(
                info => infoReady.TrySetResult(info),
                ex => infoReady.TrySetException(ex));

            audio.LoadTracks(new[] { track });
            audio.PlayAtIndex(0);

            var info = await infoReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(track.Id, info.TrackId);
            Assert.Equal("PCM", info.Codec);
            Assert.Equal(8000, info.SampleRateHz);
            Assert.Equal(1, info.Channels);
            // 16bit/8kHz 单声道 = 128 kbps；demuxer 不估算时为 null，不硬性要求
            if (info.BitrateKbps is { } kbps)
                Assert.InRange(kbps, 100, 160);
        }
        finally
        {
            audio.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>生成静音 WAV（16bit 单声道 8kHz），与 DurabilityTests 同规格。</summary>
    private static string WriteSilenceWav(string path, int durationMs)
    {
        const int rate = 8000;
        var dataLen = (int)(rate * durationMs / 1000.0) * 2;
        using var stream = new FileStream(path, FileMode.Create);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLen);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLen);
        writer.Write(new byte[dataLen]);
        return path;
    }
}
