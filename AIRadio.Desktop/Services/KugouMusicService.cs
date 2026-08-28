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
    private const int MaxTransientRetries = 10;
    private readonly SemaphoreSlim _dfidGate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly MusicAccountStore? _accounts;
    private readonly KugouAccountService _accountService;
    private readonly KugouVerificationService? _verification;

    public string Name => "酷狗音乐";

    public KugouMusicService(HttpClient httpClient, MusicAccountStore? accounts = null,
        KugouVerificationService? verification = null)
    {
        _httpClient = httpClient;
        _accounts = accounts;
        _verification = verification;
        _accountService = new KugouAccountService(httpClient, ProxyBase);
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
            throw new MusicSourceBusinessException(AppLanguage.T(
                "酷狗未登录，请在设置的音源账号中扫码登录",
                "Kugou is not signed in. Scan the QR code under Music accounts in Settings."));
        }

        try
        {
            cookie = await EnsureDfidCookieAsync(cookie, cancellationToken);
            var url = $"{ProxyBase}/search?keywords={Uri.EscapeDataString(keyword)}&pagesize={limit}";
            using var response = await SendWithTransientRetryAsync(
                () => BuildRequest(url, cookie), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = TryGetInt32(root, "status") ?? -1;

            if (status != 1)
            {
                var errorCode = TryGetInt32(root, "error_code", "code") ?? -1;
                var error = GetFlexibleText(root, "error", "error_msg", "message", "msg");
                var safeError = SensitiveDataSanitizer.Sanitize(error) ?? error;
                throw new MusicSourceBusinessException(AppLanguage.T(
                    $"酷狗接口业务状态异常(status={status},error={errorCode})：{safeError ?? "未知错误"}，登录态或本地代理可能失效",
                    $"Kugou returned an unexpected status (status={status}, error={errorCode}): {safeError ?? "unknown error"}; the sign-in or local proxy may be invalid"));
            }

            // HTTP 失败即使响应体看起来像成功，也不能当作有效搜索结果；
            // 让 HttpRequestException 进入现有的“空结果”降级路径。
            if (!response.IsSuccessStatusCode)
                response.EnsureSuccessStatusCode();

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
        var separator = trackId.IndexOf(':');
        var hash = separator >= 0 ? trackId[(separator + 1)..] : trackId;
        var storedCookie = _accounts?.KugouCookie;
        if (string.IsNullOrEmpty(storedCookie))
        {
            Log.Information("Kugou play url skipped: not logged in ({Hash})", hash);
            return null;
        }

        try
        {
            // 旧版本保存的登录态可能没有 dfid。播放前惰性补齐并持久化，
            // 避免用户必须重新扫码才能恢复历史登录态。
            var cookie = await EnsureDfidCookieAsync(storedCookie, cancellationToken);

            // timestamp 破缓存：播放地址可能带时效签名，AudioService 断流重刷时不能拿到 2 分钟内的旧缓存
            var url = $"{ProxyBase}/song/url?hash={Uri.EscapeDataString(hash)}" +
                      $"&quality=128&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var response = await SendWithTransientRetryAsync(
                () => BuildRequest(url, cookie), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = TryGetInt32(root, "status") ?? -1;
            var errorCode = TryGetInt32(root, "error_code", "errcode", "code");
            var error = GetFlexibleText(root, "error", "error_msg", "message", "msg");

            // 20028 风控挑战：代理在上游返回 ssa-code 头时会在响应体附加 ssaCode/sid/edt。
            // 命中即上报验证服务（触发自动/手动滑块流程）；裸 status=2 等疑似形状没有
            // 事件 ID 也同样上报——探测拿到事件 ID 才会真正进入滑块流程，拿不到则快速
            // 失败并靠自动触发冷却防刷，否则整张歌单只会被无限跳过
            var shape = KugouVerificationService.ClassifyPlayUrlResponse(root, out var challengeEventId, out _);
            if (shape == KugouVerificationService.KugouPlayUrlShape.Challenge)
            {
                if (challengeEventId == null)
                    Log.Information(
                        "Kugou play url suspected risk-control challenge for {Hash}: errcode={Errcode} error={Error}",
                        hash, errorCode ?? -1, SensitiveDataSanitizer.Sanitize(error) ?? error);
                _verification?.RecordChallenge(new KugouChallenge(challengeEventId, hash));
            }

            if (status != 1 || !response.IsSuccessStatusCode)
            {
                Log.Information(
                    "Kugou play url rejected for {Hash}: http={HttpStatus} status={Status} errorCode={ErrorCode} error={Error} data={Data}",
                    hash,
                    (int)response.StatusCode,
                    status,
                    errorCode ?? -1,
                    SensitiveDataSanitizer.Sanitize(error) ?? error,
                    DescribeData(root));
                return null;
            }

            if (!root.TryGetProperty("data", out var data))
            {
                Log.Information("Kugou play url returned no data for {Hash}", hash);
                return null;
            }

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
                var playUrl = ExtractPlayUrl(data);
                if (playUrl != null)
                    return playUrl;
            }

            Log.Information("Kugou play url response contains no usable URL for {Hash}: data={Data}",
                hash, DescribeData(root));
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

    private async Task<string> EnsureDfidCookieAsync(
        string storedCookie,
        CancellationToken cancellationToken)
    {
        if (HasUsableDfid(storedCookie) || _accounts == null)
            return storedCookie;

        await _dfidGate.WaitAsync(cancellationToken);
        try
        {
            // 可能已有另一个播放请求完成了补齐。
            var baseline = _accounts.KugouCookie;
            var current = baseline ?? storedCookie;
            if (HasUsableDfid(current))
                return current;

            var enriched = await _accountService.EnsureDfidCookieAsync(current, cancellationToken);
            if (string.IsNullOrWhiteSpace(enriched))
                return current;

            // 网络补齐期间用户可能重新扫码登录：store 值一旦变化就不再回写旧 token+dfid，
            // 否则会把刚写入的新登录态整个覆盖掉。
            var latest = _accounts.KugouCookie;
            if (!string.Equals(latest, baseline, StringComparison.Ordinal))
            {
                Log.Information("Kugou credential changed during dfid enrichment; keeping the newer stored value");
                return latest ?? current;
            }

            try
            {
                await _accounts.SetKugouCookieAsync(enriched);
            }
            catch (Exception ex)
            {
                // 播放不应因凭据持久化失败而被阻断；本次请求仍使用已补齐的 Cookie。
                Log.Warning(ex, "Failed to persist refreshed Kugou device credential");
            }

            return enriched;
        }
        finally
        {
            _dfidGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendWithTransientRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = requestFactory();
                return await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (
                attempt < MaxTransientRetries &&
                ex.StatusCode == null)
            {
                // 本地 Node 代理启动期间可能暂时拒绝连接；等待后重建请求。
            }

            await Task.Delay(GetRetryDelay(attempt), cancellationToken);
        }
    }

    private static TimeSpan GetRetryDelay(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(3000, 500 * (attempt + 1)));

    private static bool HasUsableDfid(string cookie)
    {
        var dfid = ExtractCookieValue(cookie, "dfid");
        return !string.IsNullOrWhiteSpace(dfid) &&
               !string.Equals(dfid, "-", StringComparison.Ordinal) &&
               !string.Equals(dfid, "0", StringComparison.Ordinal);
    }

    private static string? ExtractCookieValue(string cookie, string name)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair[1].Trim();
        }

        return null;
    }

    private static int? TryGetInt32(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number))
                return number;
        }

        return null;
    }

    private static string? GetFlexibleText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }

        return null;
    }

    private static string DescribeData(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return "missing";
        if (data.ValueKind == JsonValueKind.Array)
            return $"array[{data.GetArrayLength()}]";
        return data.ValueKind.ToString().ToLowerInvariant();
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

    internal static string? ExtractPlayUrl(JsonElement item)
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
