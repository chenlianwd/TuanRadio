using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Whisper.net;
using Whisper.net.Ggml;

namespace AIRadio.Desktop.Services;

public class WhisperSttService : ISttService, IDisposable
{
    private WhisperFactory? _factory;
    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WhisperSttService()
    {
        _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIRadio", "models");
        _modelPath = Path.Combine(_modelDir, "ggml-base.bin");
    }

    public async Task EnsureModelReadyAsync()
    {
        if (_factory != null) return;
        await _lock.WaitAsync();
        try
        {
            if (_factory != null) return;

            if (!File.Exists(_modelPath))
            {
                Log.Information("Downloading Whisper base model...");
                Directory.CreateDirectory(_modelDir);
                var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.Base);
                await using var fileStream = File.Create(_modelPath);
                await modelStream.CopyToAsync(fileStream);
                Log.Information("Whisper model saved to {Path}", _modelPath);
            }

            _factory = WhisperFactory.FromPath(_modelPath);
            Log.Information("Whisper STT ready");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize Whisper");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> TranscribeAsync(string wavFilePath)
    {
        await EnsureModelReadyAsync();
        if (_factory == null) return string.Empty;

        try
        {
            using var processor = _factory.CreateBuilder()
                .WithLanguage("zh")
                .WithNoSpeechThreshold(0.3f)
                .Build();

            await using var fileStream = File.OpenRead(wavFilePath);
            var segments = processor.ProcessAsync(fileStream);

            var sb = new System.Text.StringBuilder();
            await foreach (var segment in segments)
            {
                sb.Append(segment.Text);
            }

            var result = sb.ToString().Trim();
            Log.Information("Whisper result: {Text}", result);
            return result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Whisper transcription failed");
            return string.Empty;
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _lock.Dispose();
    }
}
