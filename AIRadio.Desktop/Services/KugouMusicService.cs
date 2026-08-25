using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 酷狗音乐：经本地 KuGouMusicApi 代理（server-kugou 目录，37251 端口）。
/// 酷狗的搜索与播放接口均已强制登录态（token+userid+dfid），
/// 未登录时本源直接报业务异常，登录后由设置页扫码写入 MusicAccountStore。
/// </summary>
public class KugouMusicService : IMusicSearchService
{
    private const string ProxyBase = "http://127.0.0.1:37251";
    private readonly HttpClient _httpClient;
    private readonly MusicAccountStore? _accounts;

    public string Name => "酷狗音乐";

    public KugouMusicService(HttpClient httpClient, MusicAccountStore? accounts = null)
    {
        _httpClient = httpClient;
        _accounts = accounts;
    }

    public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
        => SearchAsync(keyword, limit, CancellationToken.None);

    public async Task<List<OnlineTrack>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken)
    {
        var cookie = _accounts?.KugouCookie;
        if (string.IsNullOrEmpty(cookie))
        {
            // 播放接口同样要求登录，未登录时搜索结果必然不可播，直接透传原因避免误导
            throw new MusicSourceBusinessException("酷狗未登录，请在设置的音源账号中扫码登录");
        }

        try
        {
            var url = $"{ProxyBase}/search?keywords={Uri.EscapeDataString(keyword)}&pagesize={limit}";
            var response = await _httpClient.SendAsync(
                BuildRequest(url, cookie), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var statusEl) ||
                statusEl.ValueKind != JsonValueKind.Number ||
                statusEl.GetInt32() != 1)
            {
                var errorCode = root.TryGetProperty("error_code", out var errEl) &&
                                errEl.ValueKind == JsonValueKind.Number
                    ? errEl.GetInt32()
                    : -1;
                throw new MusicSourceBusinessException($"酷狗接口业务状态异常(status={statusEl.GetInt32()},error={errorCode})，登录态或本地代理可能失效");
            }

            var tracks = new List<OnlineTrack>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                // v3 移动端形状为 data.info[]（小写字段），v2 网页形状为 data.lists[]（大写字段）
                JsonElement list = default;
                var hasList = false;
                if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                {
                    list = info;
                    hasList = true;
                }
                else if (data.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Array)
                {
                    list = lists;
                    hasList = true;
                }

                if (hasList)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        try
                        {
                            var track = ParseTrack(item);
                            if (track != null)
                                tracks.Add(track);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Skipped malformed Kugou search item");
                        }
                    }
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
            Log.Warning(ex, "Kugou search failed");
            return new List<OnlineTrack>();
        }
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        var hash = trackId.Contains(':') ? trackId.Split(':')[1] : trackId;
        var cookie = _accounts?.KugouCookie;
        if (string.IsNullOrEmpty(cookie))
        {
            Log.Information("Kugou play url skipped: not logged in ({Hash})", hash);
            return null;
        }

        try
        {
            // timestamp 破缓存：播放地址可能带时效签名，AudioService 断流重刷时不能拿到 2 分钟内的旧缓存
            var url = $"{ProxyBase}/song/url?hash={Uri.EscapeDataString(hash)}" +
                      $"&quality=128&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var response = await _httpClient.SendAsync(
                BuildRequest(url, cookie), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var statusEl) ||
                statusEl.ValueKind != JsonValueKind.Number ||
                statusEl.GetInt32() != 1)
            {
                var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                Log.Information("Kugou play url rejected for {Hash}: status={Status} error={Error}",
                    hash, statusEl.ValueKind == JsonValueKind.Number ? statusEl.GetInt32() : -1, error);
                return null;
            }

            if (!root.TryGetProperty("data", out var data))
                return null;

            // v5/url 成功响应的 data 为数组（或单对象），条目内 url/url_backup 为 CDN 链接
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var playUrl = ExtractPlayUrl(item);
                    if (playUrl != null)
                        return playUrl;
                }
            }
            else if (data.ValueKind == JsonValueKind.Object)
            {
                return ExtractPlayUrl(data);
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kugou get play url failed for {Hash}", hash);
            return null;
        }
    }

    /// <summary>
    /// 酷狗登录态不进 URL：代理 server.js 会把 Authorization 头按 cookie 解析合并，
    /// Cookie 走 header 可避免进入日志、URL 缓存键与诊断输出。
    /// </summary>
    private static HttpRequestMessage BuildRequest(string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", cookie);
        return request;
    }

    private static OnlineTrack? ParseTrack(JsonElement item)
    {
        string? hash = GetString(item, "FileHash", "hash");
        if (string.IsNullOrEmpty(hash))
            return null;

        // 登录态 complexsearch v3 的歌名在 OriSongName（净歌名）；
        // FileName 是"歌手 - 歌名"展示格式，作为兜底并剥掉前缀与扩展名
        var title = GetString(item, "OriSongName", "SongName", "songname")
                    ?? ParseTitleFromFileName(GetString(item, "FileName"));
        if (string.IsNullOrEmpty(title))
            return null;

        var singer = GetString(item, "SingerName", "singername") ?? "";
        var album = GetString(item, "AlbumName", "album_name") ?? "";
        var durationSec = GetInt32(item, "Duration", "duration");

        return new OnlineTrack
        {
            Id = "kugou:" + hash,
            Title = title,
            Artist = singer,
            Album = album,
            DurationMs = durationSec * 1000L,
            Source = "酷狗"
        };
    }

    private static string? ParseTitleFromFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        var name = fileName;
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0)
            name = name[(dash + 3)..];
        var dot = name.LastIndexOf('.');
        if (dot > 0)
            name = name[..dot];
        name = name.Trim();
        return name.Length == 0 ? null : name;
    }

    private static string? GetString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var value = el.GetString();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }
        return null;
    }

    private static int GetInt32(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var el) &&
                el.ValueKind == JsonValueKind.Number &&
                el.TryGetInt32(out var value))
                return value;
        }
        return 0;
    }

    private static string? ExtractPlayUrl(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        if (item.TryGetProperty("url", out var urlEl) &&
            urlEl.ValueKind == JsonValueKind.String)
        {
            var url = urlEl.GetString();
            if (IsHttpUrl(url))
                return url;
        }

        if (item.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var url in urlsEl.EnumerateArray())
            {
                if (url.ValueKind == JsonValueKind.String && IsHttpUrl(url.GetString()))
                    return url.GetString();
            }
        }

        if (item.TryGetProperty("url_backup", out var backupEl) && backupEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var url in backupEl.EnumerateArray())
            {
                if (url.ValueKind == JsonValueKind.String && IsHttpUrl(url.GetString()))
                    return url.GetString();
            }
        }

        return null;
    }

    private static bool IsHttpUrl(string? url)
        => url is not null &&
           (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
