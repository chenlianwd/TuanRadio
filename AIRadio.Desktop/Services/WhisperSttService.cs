using System;
using System.IO;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Whisper.net;
using Whisper.net.Ggml;

namespace AIRadio.Desktop.Services;

public partial class WhisperSttService : ISttService, IDisposable
{
    private WhisperFactory? _factory;
    private readonly string _modelDir;
    private readonly string _modelPath;
    private readonly Func<CancellationToken, Task<Stream>>? _customModelDownloader;
    private readonly TimeSpan _modelDownloadTimeout;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Subject<SttProgress> _progressSubject = new();
    private int _disposed;

    /// <summary>模型下载（约 148MB）的总时限：网络停滞时快速失败而不是无限挂起持锁。</summary>
    private static readonly TimeSpan DefaultModelDownloadTimeout = TimeSpan.FromMinutes(5);
    private const long EstimatedBaseModelBytes = 148L * 1024 * 1024;
    /// <summary>有效 ggml 模型文件最小体积（真实 ggml-base 约 148MB，拦截 HTML 错误页或截断半份文件）。</summary>
    private const long MinValidModelBytes = 1024L * 1024;
    private const float DefaultNoSpeechThreshold = 0.6f;

    [GeneratedRegex(@"[\[\(（【]\s*(?:BLANK_AUDIO|NO_SPEECH|SILENCE|MUSIC|APPLAUSE|LAUGHTER|NOISE|INAUDIBLE|音乐|掌声|笑声|静音|空白|无声|背景音乐|字幕[^\)\]）】]*)\s*[\]\)）】]", RegexOptions.IgnoreCase)]
    private static partial Regex WhisperNoiseTagRegex();

    [GeneratedRegex(@"^[\s\p{P}\p{S}♪♫🎵🎶]+$")]
    private static partial Regex PunctuationOnlyRegex();

    /// <summary>
    /// Whisper 语言代码，如 "auto"、"zh"、"en"。默认 "auto" 自动识别中英文。
    /// </summary>
    public string Language { get; set; } = "auto";

    public IObservable<SttProgress> ProgressChanged => _progressSubject.AsObservable();

    public ApiFailureInfo? LastFailure { get; private set; }

    public WhisperSttService()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIRadio", "models"),
            null,
            null)
    {
    }

    internal WhisperSttService(
        string modelDir,
        Func<CancellationToken, Task<Stream>>? modelDownloader = null,
        TimeSpan? downloadTimeout = null)
    {
        _modelDir = modelDir;
        _modelPath = Path.Combine(_modelDir, "ggml-base.bin");
        _customModelDownloader = modelDownloader;
        _modelDownloadTimeout = downloadTimeout ?? DefaultModelDownloadTimeout;
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
            LastFailure = null;
            await EnsureModelReadyCoreAsync(token);
        }
        finally
        {
            EmitProgress(SttStage.Idle);
            _operationGate.Release();
        }
    }

    public Task<string> TranscribeAsync(string wavFilePath)
        => TranscribeAsync(wavFilePath, null, CancellationToken.None);

    public Task<string> TranscribeAsync(string wavFilePath, CancellationToken cancellationToken)
        => TranscribeAsync(wavFilePath, null, cancellationToken);

    public async Task<string> TranscribeAsync(string wavFilePath, string? languageHint, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var token = linkedCts.Token;
        token.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(token);

        var phase = SttStage.LoadingModel;
        try
        {
            ThrowIfDisposed();
            LastFailure = null;

            phase = File.Exists(_modelPath) ? SttStage.LoadingModel : SttStage.DownloadingModel;
            await EnsureModelReadyCoreAsync(token);
            if (_factory == null)
                return string.Empty;

            phase = SttStage.Transcribing;
            EmitProgress(SttStage.Transcribing);

            var effectiveLanguage = string.IsNullOrWhiteSpace(languageHint) ? Language : languageHint.Trim();
            if (string.IsNullOrWhiteSpace(effectiveLanguage))
                effectiveLanguage = "auto";

            var builder = _factory.CreateBuilder()
                .WithLanguage(effectiveLanguage)
                .WithNoSpeechThreshold(DefaultNoSpeechThreshold);

            if (effectiveLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase))
                builder = builder.WithPrompt("以下是普通话的句子。");

            WhisperProcessor processor;
            try
            {
                processor = builder.Build();
            }
            catch
            {
                // Whisper.net 可能延迟到 Build() 才加载/校验 ggml 权重：
                // 失败时必须先释放 factory 句柄再清理坏模型，否则后续永远卡在加载坏文件
                phase = SttStage.LoadingModel;
                _factory?.Dispose();
                _factory = null;
                IsolateCorruptModelFile();
                throw;
            }

            using (processor)
            {
                await using var fileStream = File.OpenRead(wavFilePath);
                var segments = processor.ProcessAsync(fileStream);

                var sb = new System.Text.StringBuilder();
                await foreach (var segment in segments.WithCancellation(token).ConfigureAwait(false))
                {
                    sb.Append(segment.Text);
                }

                var result = CleanTranscript(sb.ToString());
                Log.Information("Whisper result: {Length} chars (lang={Language})", result.Length, effectiveLanguage);
                return result;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastFailure = BuildFailureInfo(ex, phase);
            Log.Warning(ex, "Whisper transcription failed at phase {Phase}", phase);
            return string.Empty;
        }
        finally
        {
            EmitProgress(SttStage.Idle);
            _operationGate.Release();
        }
    }

    internal static string CleanTranscript(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var cleaned = WhisperNoiseTagRegex().Replace(raw, " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        if (string.IsNullOrWhiteSpace(cleaned) || PunctuationOnlyRegex().IsMatch(cleaned))
            return string.Empty;

        return cleaned;
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

            EmitProgress(SttStage.LoadingModel);
            try
            {
                var fileInfo = new FileInfo(_modelPath);
                if (!fileInfo.Exists || fileInfo.Length < MinValidModelBytes)
                    throw new InvalidDataException($"Whisper model file is incomplete ({fileInfo.Length} bytes).");

                _factory = WhisperFactory.FromPath(_modelPath);
            }
            catch
            {
                _factory?.Dispose();
                _factory = null;
                IsolateCorruptModelFile();
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
        EmitProgress(SttStage.DownloadingModel, 0);
        Directory.CreateDirectory(_modelDir);
        var tempPath = _modelPath + ".tmp";
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(_modelDownloadTimeout);

            Stream modelStream;
            if (_customModelDownloader != null)
            {
                try
                {
                    modelStream = await _customModelDownloader(downloadCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.Warning("Whisper model download timed out after {Seconds}s", _modelDownloadTimeout.TotalSeconds);
                    throw new TimeoutException($"Whisper model download timed out after {_modelDownloadTimeout.TotalSeconds}s");
                }
            }
            else
            {
                // GetGgmlModelAsync 不接受取消令牌：握手阶段（连接/响应头）用 WaitAsync 兜底，
                // 取消后孤儿任务的响应流随即释放，避免应用关闭被网络握手拖延
                var modelStreamTask = WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.Base);
                try
                {
                    modelStream = await modelStreamTask.WaitAsync(downloadCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _ = modelStreamTask.ContinueWith(
                        t =>
                        {
                            // 握手若随后失败（faulted），t.Result 会抛——只清理成功完成的流
                            if (t.Status == TaskStatus.RanToCompletion)
                                t.Result?.Dispose();
                        },
                        TaskScheduler.Default);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("Whisper model download timed out after {Seconds}s", _modelDownloadTimeout.TotalSeconds);
                        throw new TimeoutException($"Whisper model download timed out after {_modelDownloadTimeout.TotalSeconds}s");
                    }
                    throw;
                }
            }

            await using (modelStream)
            await using (var fileStream = File.Create(tempPath))
            {
                long totalBytes = EstimatedBaseModelBytes;
                if (modelStream.CanSeek)
                {
                    try
                    {
                        if (modelStream.Length > 0)
                            totalBytes = modelStream.Length;
                    }
                    catch { }
                }

                var buffer = new byte[81920];
                long receivedBytes = 0;
                double lastReported = 0;
                var lastReportTick = Environment.TickCount64;

                try
                {
                    int bytesRead;
                    while ((bytesRead = await modelStream.ReadAsync(buffer, downloadCts.Token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), downloadCts.Token);
                        receivedBytes += bytesRead;

                        var ratio = Math.Clamp((double)receivedBytes / Math.Max(1, totalBytes), 0, 0.99);
                        var now = Environment.TickCount64;
                        if (ratio - lastReported >= 0.01 || now - lastReportTick >= 250)
                        {
                            lastReported = ratio;
                            lastReportTick = now;
                            EmitProgress(SttStage.DownloadingModel, ratio);
                        }
                    }

                    await fileStream.FlushAsync(downloadCts.Token);
                    EmitProgress(SttStage.DownloadingModel, 1.0);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.Warning("Whisper model download timed out after {Seconds}s", _modelDownloadTimeout.TotalSeconds);
                    throw new TimeoutException($"Whisper model download timed out after {_modelDownloadTimeout.TotalSeconds}s");
                }
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

    private void EmitProgress(SttStage stage, double? downloadProgress = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _progressSubject.OnNext(new SttProgress(stage, downloadProgress));
        }
        catch (ObjectDisposedException) { }
    }

    private static ApiFailureInfo BuildFailureInfo(Exception ex, SttStage phase)
    {
        if (ex is TimeoutException)
        {
            return new ApiFailureInfo(
                ApiFailureKind.Timeout,
                AppLanguage.T("语音模型下载超时", "Speech model download timed out"),
                AppLanguage.T("首次使用按住说话需要下载约 148MB 离线语音模型，当前网络连接超时。",
                    "First-time voice input downloads an offline speech model (~148 MB), and the connection timed out."),
                AppLanguage.T("请检查网络或代理设置后重试。", "Check your network or proxy settings and try again."));
        }

        if (phase == SttStage.DownloadingModel || ex is HttpRequestException)
        {
            return new ApiFailureInfo(
                ApiFailureKind.Network,
                AppLanguage.T("语音模型下载失败", "Speech model download failed"),
                AppLanguage.T("无法下载离线语音识别模型（约 148MB），可能是网络中断或无法访问模型源。",
                    "Failed to download the offline speech model (~148 MB). The network may be offline or blocked."),
                AppLanguage.T("检查网络连接或代理配置后重新按住说话即可继续下载。",
                    "Check your network or proxy settings, then hold to talk again to retry."));
        }

        if (phase == SttStage.LoadingModel)
        {
            return new ApiFailureInfo(
                ApiFailureKind.Unknown,
                AppLanguage.T("语音模型加载失败", "Speech model failed to load"),
                AppLanguage.T("本地语音识别模型初始化失败，可能文件不完整或运行环境受限。",
                    "Failed to initialize the local speech model. The file may be incomplete or the runtime failed to load."),
                AppLanguage.T("系统已自动清理异常模型文件，重新按住说话将尝试重新下载。",
                    "Corrupted model files have been cleared automatically. Hold to talk again to re-download."));
        }

        return new ApiFailureInfo(
            ApiFailureKind.Unknown,
            AppLanguage.T("语音识别失败", "Speech recognition failed"),
            string.IsNullOrWhiteSpace(ex.Message)
                ? AppLanguage.T("识别音频时发生异常。", "An error occurred while transcribing audio.")
                : ex.Message,
            AppLanguage.T("请重试，或直接打字输入。", "Please try again, or type your message instead."));
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
                _progressSubject.OnCompleted();
                _progressSubject.Dispose();
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

    private void IsolateCorruptModelFile()
    {
        // 兼容半份/损坏模型；删除失败（文件句柄未释放等）时改名隔离：
        // 否则 File.Exists 仍为真，下次跳过下载再次加载坏文件，STT 永久失效
        try { File.Delete(_modelPath); } catch { }
        if (File.Exists(_modelPath))
        {
            try { File.Move(_modelPath, _modelPath + ".bad", overwrite: true); } catch { }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WhisperSttService));
    }
}
