using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class WhisperSttServiceTests : IDisposable
{
    private readonly string _tempDir;

    public WhisperSttServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WhisperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [SkippableFact]
    public async Task TranscribeAsync_MinimalWav_CompletesAndReturnsText()
    {
        // 隔离防护：模型缺失时 TranscribeAsync 内部（EnsureModelReadyCoreAsync）会触发
        // ~148MB 真实下载，仅在模型已就位时执行；Skip.If 让干净机器上的跳过计入摘要
        Skip.If(!IsModelAlreadyDownloaded(), "Whisper model (~148MB) not present; skipping to avoid a real download");

        var service = new WhisperSttService();
        try
        {
            var wavPath = Path.Combine(_tempDir, "test.wav");
            File.WriteAllBytes(wavPath, CreateMinimalWav());

            try
            {
                var result = await service.TranscribeAsync(wavPath);
                Assert.NotNull(result);
            }
            catch (NullReferenceException)
            {
                // NRE 是内部缺陷不是"无效输入的预期失败"：本机无模型时该路径无法实证，
                // 有模型机器上 NRE 必须显形而非绿灯（原版连 ex.Message 非空都算断言）
                throw;
            }
            // 极小 WAV 对 whisper-net 属边界输入，明确的解码/格式异常可接受
        }
        finally
        {
            service.Dispose();
        }
    }

    [SkippableFact]
    public async Task TranscribeAsync_EmptyWavFile_HandlesGracefully()
    {
        Skip.If(!IsModelAlreadyDownloaded(), "Whisper model (~148MB) not present; skipping to avoid a real download");

        var service = new WhisperSttService();
        try
        {
            var wavPath = Path.Combine(_tempDir, "empty.wav");
            File.WriteAllBytes(wavPath, Array.Empty<byte>());

            try
            {
                var result = await service.TranscribeAsync(wavPath);
                Assert.NotNull(result);
            }
            catch (NullReferenceException)
            {
                // NRE 是内部缺陷不是"无效输入的预期失败"：原空 catch 连 NRE 都绿灯，
                // 这里必须让它显形
                throw;
            }
            // 其余异常（无效 WAV 的解码失败等）属可接受的明确失败
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var service = new WhisperSttService();
        var ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }

    private static bool IsModelAlreadyDownloaded()
        => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIRadio", "models", "ggml-base.bin"));

    private static byte[] CreateMinimalWav()
    {
        // Minimal WAV header (44 bytes) + 1 sample of silence
        var data = new byte[48];
        // RIFF header
        data[0] = (byte)'R'; data[1] = (byte)'I'; data[2] = (byte)'F'; data[3] = (byte)'F';
        BitConverter.GetBytes(36).CopyTo(data, 4); // file size - 8
        data[8] = (byte)'W'; data[9] = (byte)'A'; data[10] = (byte)'V'; data[11] = (byte)'E';
        // fmt chunk
        data[12] = (byte)'f'; data[13] = (byte)'m'; data[14] = (byte)'t'; data[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(data, 16); // chunk size
        BitConverter.GetBytes((short)1).CopyTo(data, 20); // PCM
        BitConverter.GetBytes((short)1).CopyTo(data, 22); // mono
        BitConverter.GetBytes(16000).CopyTo(data, 24); // sample rate
        BitConverter.GetBytes(32000).CopyTo(data, 28); // byte rate
        BitConverter.GetBytes((short)2).CopyTo(data, 32); // block align
        BitConverter.GetBytes((short)16).CopyTo(data, 34); // bits per sample
        // data chunk
        data[36] = (byte)'d'; data[37] = (byte)'a'; data[38] = (byte)'t'; data[39] = (byte)'a';
        BitConverter.GetBytes(4).CopyTo(data, 40); // data size
        return data;
    }
}
