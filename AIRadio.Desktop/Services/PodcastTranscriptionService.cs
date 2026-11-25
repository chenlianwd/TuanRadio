using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services.Music;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 播客音频前段转写服务：
/// 仅流式拉取前 3MB 音频片段（逐跳重定向 SSRF 校验），转码为 16kHz 单声道 WAV（上限 90 秒）后交由 Whisper 识别。
/// </summary>
public sealed class PodcastTranscriptionService : IPodcastTranscriptionService, IDisposable
{
    internal const int MaxAudioExcerptBytes = 3 * 1024 * 1024;
    internal const int MaxExcerptSeconds = 90;
    private const int MaxRedirectHops = 5;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(25);
    private const string UserAgent = "TuanRadio/1.0 (+https://github.com/chenlianwd/TuanRadio)";

    private readonly ISttService _sttService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Func<Uri, CancellationToken, Task<bool>> _uriValidator;
    private readonly Func<string, string, int, CancellationToken, Task<bool>> _audioToWavConverter;

    public PodcastTranscriptionService(
        ISttService sttService,
        HttpClient? httpClient = null,
        Func<Uri, CancellationToken, Task<bool>>? uriValidator = null)
        : this(sttService, httpClient, uriValidator, null)
    {
    }

    internal PodcastTranscriptionService(
        ISttService sttService,
        HttpClient? httpClient,
        Func<Uri, CancellationToken, Task<bool>>? uriValidator,
        Func<string, string, int, CancellationToken, Task<bool>>? audioToWavConverter)
    {
        _sttService = sttService ?? throw new ArgumentNullException(nameof(sttService));
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            _httpClient = new HttpClient(handler);
            _ownsClient = true;
        }

        _uriValidator = uriValidator ?? MediaUriPolicy.ValidateCustomRadioAsync;
        _audioToWavConverter = audioToWavConverter ?? ConvertToWhisperWavAsync;
    }

    public async Task<string> TranscribeExcerptAsync(
        PodcastEpisode episode,
        string? languageHint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        if (string.IsNullOrWhiteSpace(episode.AudioUrl) ||
            !Uri.TryCreate(episode.AudioUrl.Trim(), UriKind.Absolute, out var initialUri) ||
            (initialUri.Scheme != Uri.UriSchemeHttp && initialUri.Scheme != Uri.UriSchemeHttps))
        {
            return string.Empty;
        }

        if (!await _uriValidator(initialUri, cancellationToken).ConfigureAwait(false))
        {
            Log.Warning("Podcast transcription blocked unsafe audio URL for {Title}", episode.Title);
            return string.Empty;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "AIRadio", "podcast-stt");
        Directory.CreateDirectory(tempDir);
        var id = Guid.NewGuid().ToString("N");
        string? sourcePath = null;
        var wavPath = Path.Combine(tempDir, $"{id}.wav");

        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(DownloadTimeout);

            var downloaded = await DownloadExcerptAsync(initialUri, tempDir, id, downloadCts.Token).ConfigureAwait(false);
            if (downloaded == null)
                return string.Empty;
            sourcePath = downloaded;

            var converted = await _audioToWavConverter(sourcePath, wavPath, MaxExcerptSeconds, cancellationToken)
                .ConfigureAwait(false);
            if (!converted || !File.Exists(wavPath))
                return string.Empty;

            var resolvedLang = InferWhisperLanguage(episode, languageHint);
            var transcript = await _sttService.TranscribeAsync(wavPath, resolvedLang, cancellationToken)
                .ConfigureAwait(false);
            return transcript?.Trim() ?? string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Podcast excerpt transcription failed for {Title}", episode.Title);
            return string.Empty;
        }
        finally
        {
            TryDeleteFile(sourcePath);
            TryDeleteFile(wavPath);
        }
    }

    internal static string InferWhisperLanguage(PodcastEpisode episode, string? languageHint)
    {
        var titleSample = $"{episode.FeedTitle} {episode.Title}";
        var hasCjk = titleSample.Any(c => c >= '\u4E00' && c <= '\u9FFF');
        if (hasCjk)
            return "zh";

        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            var normalized = languageHint.Trim().Split('-', '_')[0].ToLowerInvariant();
            if (normalized is "en" or "ja" or "ko" or "fr" or "de" or "es")
                return normalized;
            if (normalized == "zh")
                return titleSample.Any(char.IsLetter) ? "en" : "zh";
        }

        return titleSample.Any(char.IsLetter) ? "en" : "auto";
    }

    private async Task<string?> DownloadExcerptAsync(
        Uri initialUri,
        string tempDir,
        string fileId,
        CancellationToken ct)
    {
        var current = initialUri;
        var useRange = true;
        for (var hop = 0; hop <= MaxRedirectHops; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (useRange)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, MaxAudioExcerptBytes - 1);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!await _uriValidator(next, ct).ConfigureAwait(false))
                {
                    Log.Warning("Podcast transcription redirect blocked for {Url}", SensitiveDataSanitizer.SanitizeUrlForLog(next));
                    return null;
                }
                current = next;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && useRange)
            {
                useRange = false;
                hop--;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Podcast transcription download returned status {Status} for {Url}", statusCode, SensitiveDataSanitizer.SanitizeUrlForLog(current));
                return null;
            }

            var ext = InferAudioExtension(current, response.Content.Headers.ContentType?.MediaType);
            var targetPath = Path.Combine(tempDir, $"{fileId}{ext}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[16 * 1024];
            var total = 0;

            while (total < MaxAudioExcerptBytes)
            {
                var toRead = Math.Min(buffer.Length, MaxAudioExcerptBytes - total);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
            return total > 0 ? targetPath : null;
        }

        Log.Warning("Podcast transcription exceeded max redirect hops for {Url}", SensitiveDataSanitizer.SanitizeUrlForLog(initialUri));
        return null;
    }

    private static Task<bool> ConvertToWhisperWavAsync(
        string sourcePath,
        string wavPath,
        int maxSeconds,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var reader = new MediaFoundationReader(sourcePath);
            ISampleProvider sampleProvider = reader.ToSampleProvider();
            if (sampleProvider.WaveFormat.Channels == 2)
                sampleProvider = sampleProvider.ToMono();
            else if (sampleProvider.WaveFormat.Channels > 2)
                sampleProvider = new MultiplexingSampleProvider(new[] { sampleProvider }, 1);
            if (sampleProvider.WaveFormat.SampleRate != 16000)
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);

            var maxSamples = 16000 * Math.Max(1, maxSeconds);
            var floatBuffer = new float[4096];
            var writtenSamples = 0;

            using var writer = new WaveFileWriter(wavPath, new WaveFormat(16000, 16, 1));
            while (writtenSamples < maxSamples)
            {
                ct.ThrowIfCancellationRequested();
                var toRead = Math.Min(floatBuffer.Length, maxSamples - writtenSamples);
                var read = sampleProvider.Read(floatBuffer, 0, toRead);
                if (read <= 0)
                    break;
                writer.WriteSamples(floatBuffer, 0, read);
                writtenSamples += read;
            }

            writer.Flush();
            return writtenSamples > 0;
        }, ct);
    }

    private static string InferAudioExtension(Uri uri, string? mediaType)
    {
        var pathExt = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (pathExt is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".wav" or ".wma")
            return pathExt;

        return mediaType?.ToLowerInvariant() switch
        {
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
            "audio/aac" => ".aac",
            "audio/ogg" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            _ => ".mp3"
        };
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }
}
