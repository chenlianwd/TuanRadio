using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public class NeteaseMusicService : IMusicSearchService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly MusicAccountStore? _accounts;

    public string Name => "网易云音乐";

    public NeteaseMusicService(
        HttpClient httpClient,
        MusicAccountStore? accounts = null,
        string baseUrl = "http://127.0.0.1:37250")
    {
        _httpClient = httpClient;
        _accounts = accounts;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
        => SearchAsync(keyword, limit, CancellationToken.None);

    public async Task<List<OnlineTrack>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_baseUrl}/search?keywords={Uri.EscapeDataString(keyword)}&limit={limit}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 本地代理按 URL 缓存 2 分钟，而登录态走的是请求头：登录前后同一关键词的
            // 结果集不同（VIP 可见性/排序），带 cookie 时必须绕过缓存
            if (!string.IsNullOrEmpty(_accounts?.NeteaseCookie))
                request.Headers.TryAddWithoutValidation("x-apicache-bypass", "true");
            ApplyLoginCookie(request);
            using var searchResponse = await _httpClient.SendAsync(request, cancellationToken);
            searchResponse.EnsureSuccessStatusCode();
            var response = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var codeEl) ||
                codeEl.ValueKind != JsonValueKind.Number ||
                codeEl.GetInt32() != 200)
                throw new MusicSourceBusinessException(AppLanguage.T(
                    $"网易接口业务码异常({(codeEl.ValueKind == JsonValueKind.Number ? codeEl.GetInt32() : -1)})，本地代理或鉴权可能失效",
                    $"NetEase returned an unexpected business code ({(codeEl.ValueKind == JsonValueKind.Number ? codeEl.GetInt32() : -1)}); the local proxy or authentication may be invalid"));

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("songs", out var songs))
                return new List<OnlineTrack>();

            var tracks = new List<OnlineTrack>();

            foreach (var song in songs.EnumerateArray())
            {
                try
                {
                    var artistName = AppLanguage.T("未知", "Unknown");
                    if (song.TryGetProperty("artists", out var artists) &&
                        artists.GetArrayLength() > 0 &&
                        artists[0].TryGetProperty("name", out var nameEl))
                    {
                        artistName = nameEl.GetString() ?? AppLanguage.T("未知", "Unknown");
                    }

                    var albumName = "";
                    if (song.TryGetProperty("album", out var album) &&
                        album.TryGetProperty("name", out var albumEl))
                    {
                        albumName = albumEl.GetString() ?? "";
                    }

                    tracks.Add(new OnlineTrack
                    {
                        Id = "netease:" + (song.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "0"),
                        Title = song.TryGetProperty("name", out var titleEl) ? titleEl.GetString() ?? "" : "",
                        Artist = artistName,
                        Album = albumName,
                        DurationMs = song.TryGetProperty("duration", out var durEl) ? durEl.GetInt64() : 0,
                        Source = "网易"
                    });
                }
                catch (Exception ex)
                {
                    // 非官方接口字段变类型是常态：单条畸形条目只跳过自身，不让整源结果作废
                    Log.Debug(ex, "Skipped malformed Netease search item");
                }
            }

            return tracks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not MusicSourceBusinessException)
        {
            Log.Warning(ex, "Netease search failed");
            return new List<OnlineTrack>();
        }
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_baseUrl}/song/url/v1?id={Uri.EscapeDataString(trackId)}&level=exhigh";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 播放地址是带签名的临时 CDN 链接，重试时不能复用本地 API 的 2 分钟缓存。
            request.Headers.TryAddWithoutValidation("x-apicache-bypass", "true");
            // 登录后带上账号 cookie：VIP 账号可解锁 fee=1 歌曲的完整播放地址
            ApplyLoginCookie(request);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 200)
                return null;

            if (root.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (IsTrialOrRestricted(first))
                {
                    Log.Information("Netease returned a trial-only stream for {Id}; trying another source", trackId);
                    return null;
                }

                if (first.TryGetProperty("url", out var urlEl))
                {
                    var playUrl = urlEl.GetString();
                    return string.IsNullOrEmpty(playUrl) ? null : playUrl;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease get play url failed for {Id}", trackId);
            return null;
        }
    }

    private void ApplyLoginCookie(HttpRequestMessage request)
    {
        // 本地代理会解析请求头 Cookie 传给上游接口；HttpURLConnection 式 CookieContainer
        // 会因 Set-Cookie 的 Secure 标记在 http://127.0.0.1 上拒收，因此手动透传
        var cookie = _accounts?.NeteaseCookie;
        if (!string.IsNullOrEmpty(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    private static bool IsTrialOrRestricted(JsonElement item)
    {
        if (item.TryGetProperty("freeTrialInfo", out var trialInfo) &&
            trialInfo.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (!item.TryGetProperty("freeTrialPrivilege", out var privilege) ||
            privilege.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryGetPositiveInt32(privilege, "listenType") ||
               TryGetPositiveInt32(privilege, "cannotListenReason");
    }

    private static bool TryGetPositiveInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number) &&
           number > 0;
}
