using System;
using System.IO;
using System.Runtime.CompilerServices;
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
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;

    /// <summary>
    /// Whisper 语言代码，如 "zh"、"en"。默认 "zh"。
    /// </summary>
    public string Language { get; set; } = "zh";

    public WhisperSttService()
    {
        _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIRadio", "models");
        _modelPath = Path.Combine(_modelDir, "ggml-base.bin");
    }

    public Task EnsureModelReadyAsync()
        => EnsureModelReadyAsync(CancellationToken.None);

    public async Task EnsureModelReadyAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var token = linkedCts.Token;
        token.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            await EnsureModelReadyCoreAsync(token);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<string> TranscribeAsync(string wavFilePath)
        => TranscribeAsync(wavFilePath, CancellationToken.None);

    public async Task<string> TranscribeAsync(string wavFilePath, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var token = linkedCts.Token;
        token.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(token);

        try
        {
            ThrowIfDisposed();
            await EnsureModelReadyCoreAsync(token);
            if (_factory == null)
                return string.Empty;

            using var processor = _factory.CreateBuilder()
                .WithLanguage(Language)
                .WithNoSpeechThreshold(0.3f)
                .Build();

            await using var fileStream = File.OpenRead(wavFilePath);
            var segments = processor.ProcessAsync(fileStream);

            var sb = new System.Text.StringBuilder();
            await foreach (var segment in segments.WithCancellation(token).ConfigureAwait(false))
            {
                sb.Append(segment.Text);
            }

            var result = sb.ToString().Trim();
            Log.Information("Whisper result: {Length} chars", result.Length);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Whisper transcription failed");
            return string.Empty;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        _ = Task.Run(DisposeWhenIdleAsync);
    }

    private async Task EnsureModelReadyCoreAsync(CancellationToken cancellationToken)
    {
        if (_factory != null)
            return;

        try
        {
            if (!File.Exists(_modelPath))
                await DownloadModelAsync(cancellationToken);

            try
            {
                _factory = WhisperFactory.FromPath(_modelPath);
            }
            catch
            {
                // 兼容旧版本可能遗留的半份模型；下次使用时会重新下载。
                try { File.Delete(_modelPath); } catch { }
                throw;
            }

            Log.Information("Whisper STT ready");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize Whisper");
            throw;
        }
    }

    private async Task DownloadModelAsync(CancellationToken cancellationToken)
    {
        Log.Information("Downloading Whisper base model...");
        Directory.CreateDirectory(_modelDir);
        var tempPath = _modelPath + ".tmp";
        try
        {
            await using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.Base);
            await using (var fileStream = File.Create(tempPath))
            {
                await modelStream.CopyToAsync(fileStream, cancellationToken);
                await fileStream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _modelPath, overwrite: true);
            Log.Information("Whisper model saved to {Path}", _modelPath);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private async Task DisposeWhenIdleAsync()
    {
        try
        {
            await _operationGate.WaitAsync();
            try
            {
                _factory?.Dispose();
                _factory = null;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Whisper cleanup failed");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WhisperSttService));
    }
}
