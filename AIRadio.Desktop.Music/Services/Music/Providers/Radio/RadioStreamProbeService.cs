using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 基于 HTTP ResponseHeadersRead 的轻量流媒体探测实现。
/// </summary>
public sealed class RadioStreamProbeService : IRadioStreamProbeService, IDisposable
{
    private const string UserAgent = "TuanRadio/1.0";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3.5);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public RadioStreamProbeService(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsClient = true;
        }
    }

    public async Task<StreamProbeResult> ProbeAsync(string streamUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl) ||
            !Uri.TryCreate(streamUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return new StreamProbeResult(false, null, null, null, null, false,
                AppLanguage.T("无效的 URL 格式", "Invalid URL format"));
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return new StreamProbeResult(false, null, null, null, null, false,
                AppLanguage.T("仅支持 http 或 https 协议", "Only http or https protocols are supported"));
        }

        var allowed = await MediaUriPolicy.ValidateCustomRadioAsync(uri, ct).ConfigureAwait(false);
        if (!allowed)
        {
            return new StreamProbeResult(false, null, null, null, null, false,
                AppLanguage.T("该地址被安全策略拒绝（禁止本机回环或受限地址）", "Address blocked by security policy"));
        }

        var fallbackCodec = InferCodecFromUrl(uri);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            // 仅读取响应头后立即释放 response，强行切断底层直播流，防止内存与连接泄漏
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new StreamProbeResult(false, null, fallbackCodec, null, null, false,
                    AppLanguage.T($"服务器返回状态 {(int)response.StatusCode}", $"Server returned status {(int)response.StatusCode}"));
            }

            var icyName = GetHeaderValue(response, "icy-name");
            var icyGenre = GetHeaderValue(response, "icy-genre");
            var icyBrRaw = GetHeaderValue(response, "icy-br");

            var bitrate = ParseBitrateKbps(icyBrRaw);

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var codec = MapContentTypeToCodec(contentType) ?? fallbackCodec;

            var hasIcyHeaders = !string.IsNullOrEmpty(icyName) || !string.IsNullOrEmpty(icyGenre) || bitrate.HasValue;
            var isChunked = response.Headers.TransferEncodingChunked == true;
            var contentLength = response.Content.Headers.ContentLength;
            var looksLikeStaticFile = !hasIcyHeaders &&
                                      !isChunked &&
                                      contentLength is > 0 &&
                                      !string.Equals(codec, "HLS", StringComparison.OrdinalIgnoreCase);

            return new StreamProbeResult(
                Success: true,
                StationName: string.IsNullOrWhiteSpace(icyName) ? null : WebUtility.HtmlDecode(icyName.Trim()),
                Codec: codec,
                BitrateKbps: bitrate,
                Tags: string.IsNullOrWhiteSpace(icyGenre) ? null : WebUtility.HtmlDecode(icyGenre.Trim()),
                LooksLikeStaticFile: looksLikeStaticFile,
                ErrorMessage: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new StreamProbeResult(false, null, fallbackCodec, null, null, false,
                AppLanguage.T("探测超时，仍可手动填写信息后保存", "Probe timed out; you can still save manually"));
        }
        catch (HttpRequestException ex)
        {
            Log.Debug(ex, "Stream probe failed for {Url}", streamUrl);
            return new StreamProbeResult(false, null, fallbackCodec, null, null, false,
                AppLanguage.T("无法探测元数据（可能为非标 ICY 协议或网络受限），仍可保存", "Could not probe metadata; you can still save manually"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Unexpected error probing {Url}", streamUrl);
            return new StreamProbeResult(false, null, fallbackCodec, null, null, false,
                AppLanguage.T("探测异常，仍可手动保存", "Probe error; you can still save manually"));
        }
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string headerName)
    {
        if (response.Headers.TryGetValues(headerName, out var values))
            return values.FirstOrDefault();
        if (response.Content.Headers.TryGetValues(headerName, out var contentValues))
            return contentValues.FirstOrDefault();
        return null;
    }

    internal static int? ParseBitrateKbps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var start = -1;
        var length = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (char.IsAsciiDigit(raw[i]))
            {
                if (start < 0) start = i;
                length++;
            }
            else if (start >= 0)
            {
                break;
            }
        }

        if (start >= 0 &&
            int.TryParse(raw.AsSpan(start, length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var br) &&
            br > 0)
        {
            return br;
        }
        return null;
    }

    internal static string? MapContentTypeToCodec(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return null;

        return mediaType.Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "MP3",
            "audio/aac" or "audio/aacp" or "audio/x-aac" => "AAC",
            "audio/ogg" or "application/ogg" => "OGG",
            "audio/flac" or "audio/x-flac" => "FLAC",
            "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/x-mpegurl" or "audio/mpegurl" => "HLS",
            _ => null
        };
    }

    public static string? InferCodecFromUrl(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".m3u8", StringComparison.Ordinal)) return "HLS";
        if (path.EndsWith(".aac", StringComparison.Ordinal) || path.EndsWith(".aacp", StringComparison.Ordinal)) return "AAC";
        if (path.EndsWith(".ogg", StringComparison.Ordinal) || path.EndsWith(".opus", StringComparison.Ordinal)) return "OGG";
        if (path.EndsWith(".flac", StringComparison.Ordinal)) return "FLAC";
        if (path.EndsWith(".mp3", StringComparison.Ordinal)) return "MP3";
        return null;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }
}
