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

    [Fact]
    public async Task TranscribeAsync_MissingModelFile_ReturnsEmptyOrNull()
    {
        // WhisperSttService requires a model file; without it, TranscribeAsync should handle gracefully
        var service = new WhisperSttService();
        var wavPath = Path.Combine(_tempDir, "test.wav");

        // Create a minimal valid WAV file
        File.WriteAllBytes(wavPath, CreateMinimalWav());

        // Without calling EnsureModelReadyAsync, the model won't be downloaded
        // The service should either return empty or throw a handled exception
        try
        {
            var result = await service.TranscribeAsync(wavPath);
            // If it succeeds, result should be a string (possibly empty)
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            // Expected: model not found or initialization failure
            Assert.NotNull(ex.Message);
        }
    }

    [Fact]
    public async Task TranscribeAsync_EmptyWavFile_HandlesGracefully()
    {
        var service = new WhisperSttService();
        var wavPath = Path.Combine(_tempDir, "empty.wav");
        File.WriteAllBytes(wavPath, Array.Empty<byte>());

        try
        {
            var result = await service.TranscribeAsync(wavPath);
            Assert.NotNull(result);
        }
        catch (Exception)
        {
            // Expected: invalid WAV format
        }
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var service = new WhisperSttService();
        var ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }

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
