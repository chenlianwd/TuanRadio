using System;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly TimeSpan CredentialRefreshFailureCooldown = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private long _credentialRefreshNotBeforeMs;
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
        if (_accounts != null)
            _accounts.KugouCredentialChanged += OnKugouCredentialChanged;
    }

    private void OnKugouCredentialChanged(object? sender, EventArgs e)
    {
        // 扫码、退出或手动刷新都代表一次新的账号意图，旧会话的十分钟退避不能继续阻挡它。
        Volatile.Write(ref _credentialRefreshNotBeforeMs, 0);
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
            cookie = await EnsureCredentialAsync(cookie, cancellationToken);
            var url = $"{ProxyBase}/search?keywords={Uri.EscapeDataString(keyword)}&pagesize={limit}";
            using var response = await SendWithTransientRetryAsync(
                () => BuildRequest(url, cookie), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = TryGetInt32(root, "status") ?? -1;

            // HTTP 失败属于传输故障，即使响应体碰巧带有业务字段，也必须交给聚合层熔断。
            if (!response.IsSuccessStatusCode)
                response.EnsureSuccessStatusCode();

            if (status != 1)
            {
                var errorCode = TryGetInt32(root, "error_code", "code") ?? -1;
                var error = GetFlexibleText(root, "error", "error_msg", "message", "msg");
                var safeError = SensitiveDataSanitizer.Sanitize(error) ?? error;
                throw new MusicSourceBusinessException(AppLanguage.T(
                    $"酷狗接口业务状态异常(status={status},error={errorCode})：{safeError ?? "未知错误"}，登录态或本地代理可能失效",
                    $"Kugou returned an unexpected status (status={status}, error={errorCode}): {safeError ?? "unknown error"}; the sign-in or local proxy may be invalid"));
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
            // 传输/协议故障必须交给聚合层计入熔断；返回空集合会被误记为成功。
            throw;
        }
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
        => GetPlayUrlCoreAsync(
            trackId,
            providerMetadata: null,
            attemptAuth: true,
            attemptLegacy: true,
            cancellationToken);

    public async Task<string?> GetPlayUrlAsync(OnlineTrack track, CancellationToken cancellationToken)
    {
        var hashes = new[]
            {
                StripSourcePrefix(track.Id),
                GetMetadata(track.ProviderMetadata, "hash_std"),
                GetMetadata(track.ProviderMetadata, "hash_128")
            }
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 所有稳定 hash 先走当前 Auth 主链；只有整组失败后，才允许主 hash 进入一次旧接口。
        foreach (var hash in hashes)
        {
            var playUrl = await GetPlayUrlCoreAsync(
                hash!,
                track.ProviderMetadata,
                attemptAuth: true,
                attemptLegacy: false,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(playUrl))
                return playUrl;
        }

        return hashes.Length == 0
            ? null
            : await GetPlayUrlCoreAsync(
                hashes[0]!,
                track.ProviderMetadata,
                attemptAuth: false,
                attemptLegacy: true,
                cancellationToken);
    }

    private async Task<string?> GetPlayUrlCoreAsync(
        string trackId,
        IReadOnlyDictionary<string, string>? providerMetadata,
        bool attemptAuth,
        bool attemptLegacy,
        CancellationToken cancellationToken)
    {
        var hash = StripSourcePrefix(trackId);
        var storedCookie = _accounts?.KugouCookie;
        if (string.IsNullOrEmpty(storedCookie))
        {
            Log.Information("Kugou play url skipped: not logged in ({Hash})", hash);
            return null;
        }

        try
        {
            var cookie = await EnsureCredentialAsync(storedCookie, cancellationToken);

            if (attemptAuth)
            {
                var authUrl = BuildPlayUrl(hash, providerMetadata, useAuth: true);
                var playUrl = await TryGetPlayUrlAsync(authUrl, cookie, hash, "auth", cancellationToken);
                if (!string.IsNullOrWhiteSpace(playUrl))
                    return playUrl;
            }

            if (attemptLegacy)
            {
                var legacyUrl = BuildPlayUrl(hash, providerMetadata, useAuth: false);
                return await TryGetPlayUrlAsync(legacyUrl, cookie, hash, "legacy", cancellationToken);
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
            // null 仅表示正常业务下无可播地址，异常则交给聚合层计入音源健康状态。
            throw;
        }
    }

    private async Task<string?> TryGetPlayUrlAsync(
        string url,
        string cookie,
        string hash,
        string route,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithTransientRetryAsync(
            () => BuildRequest(url, cookie), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var status = TryGetInt32(root, "status") ?? -1;
        var errorCode = TryGetInt32(root, "error_code", "errcode", "code");
        var error = GetFlexibleText(root, "error", "error_msg", "message", "msg");

        var shape = KugouVerificationService.ClassifyPlayUrlResponse(
            root, out var challengeEventId, out _);
        if (shape == KugouVerificationService.KugouPlayUrlShape.Challenge)
        {
            if (challengeEventId == null)
                Log.Information(
                    "Kugou {Route} play URL suspected risk-control challenge for {Hash}: errcode={Errcode} error={Error}",
                    route, hash, errorCode ?? -1, SensitiveDataSanitizer.Sanitize(error) ?? error);
            _verification?.RecordChallenge(new KugouChallenge(challengeEventId, hash));
        }

        if (!response.IsSuccessStatusCode)
            response.EnsureSuccessStatusCode();

        if (status != 1)
        {
            Log.Information(
                "Kugou {Route} play URL rejected for {Hash}: http={HttpStatus} status={Status} errorCode={ErrorCode} error={Error} data={Data}",
                route,
                hash,
                (int)response.StatusCode,
                status,
                errorCode ?? -1,
                SensitiveDataSanitizer.Sanitize(error) ?? error,
                DescribeData(root));
            return null;
        }

        if (root.TryGetProperty("data", out var data))
        {
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
        }

        // Auth 链（/tracker/v5/url）成功时把直链放在根级 url[]/backupUrl[]，没有 data 包装。
        var rootPlayUrl = ExtractPlayUrl(root);
        if (rootPlayUrl != null)
            return rootPlayUrl;

        Log.Information("Kugou {Route} play URL response contains no usable URL for {Hash}: data={Data}",
            route, hash, DescribeData(root));
        return null;
    }

    private static string BuildPlayUrl(
        string hash,
        IReadOnlyDictionary<string, string>? providerMetadata,
        bool useAuth)
    {
        var route = useAuth ? "/song/url/auth/merge" : "/song/url";
        var url = $"{ProxyBase}{route}?hash={Uri.EscapeDataString(hash)}" +
                  $"&quality=128&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        url = AppendPositiveNumber(url, "album_id", GetMetadata(providerMetadata, "album_id"));
        url = AppendPositiveNumber(url, "album_audio_id", GetMetadata(providerMetadata, "album_audio_id"));
        return url;
    }

    private static string AppendPositiveNumber(string url, string name, string? value)
        => long.TryParse(value, out var number) && number > 0
            ? $"{url}&{name}={number}"
            : url;

    private static string? GetMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string key)
        => metadata != null && metadata.TryGetValue(key, out var value)
            ? value
            : null;

    private static string StripSourcePrefix(string trackId)
    {
        var separator = trackId.IndexOf(':');
        return separator >= 0 ? trackId[(separator + 1)..] : trackId;
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

    private async Task<string> EnsureCredentialAsync(
        string storedCookie,
        CancellationToken cancellationToken)
    {
        var needsFullSession = ShouldRefreshCompleteSession(storedCookie);
        if ((!needsFullSession && KugouCookieCodec.HasUsableDfid(storedCookie)) || _accounts == null)
            return storedCookie;

        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            // 可能已有另一个播放请求完成了补齐。
            var baseline = _accounts.KugouCookie;
            var current = baseline ?? storedCookie;
            needsFullSession = ShouldRefreshCompleteSession(current);
            if (!needsFullSession && KugouCookieCodec.HasUsableDfid(current))
                return current;

            var enriched = _accounts.IsLoaded
                ? await _accountService.RefreshCredentialAsync(current, cancellationToken: cancellationToken)
                : await _accountService.EnsureDfidCookieAsync(current, cancellationToken);
            if (string.IsNullOrWhiteSpace(enriched))
            {
                DeferCompleteSessionRefresh();
                return current;
            }

            if (_accounts.IsLoaded &&
                (KugouCookieCodec.NeedsSessionRefresh(enriched) ||
                 string.IsNullOrWhiteSpace(KugouCookieCodec.Get(enriched, "auth"))))
            {
                DeferCompleteSessionRefresh();
            }
            else
            {
                Volatile.Write(ref _credentialRefreshNotBeforeMs, 0);
            }

            // 网络补齐期间用户可能重新扫码登录：store 值一旦变化就不再回写旧 token+dfid，
            // 否则会把刚写入的新登录态整个覆盖掉。
            var latest = _accounts.KugouCookie;
            if (!string.Equals(latest, baseline, StringComparison.Ordinal))
            {
                Log.Information("Kugou credential changed during session enrichment; keeping the newer stored value");
                return latest ?? current;
            }

            try
            {
                await _accounts.SetKugouCookieAsync(enriched);
            }
            catch (Exception ex)
            {
                // 播放不应因凭据持久化失败而被阻断；本次请求仍使用已补齐的 Cookie。
                Log.Warning(ex, "Failed to persist refreshed Kugou session credential");
            }

            return enriched;
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    private bool ShouldRefreshCompleteSession(string cookie)
        => _accounts?.IsLoaded == true &&
           Environment.TickCount64 >= Volatile.Read(ref _credentialRefreshNotBeforeMs) &&
           (KugouCookieCodec.NeedsSessionRefresh(cookie) ||
            string.IsNullOrWhiteSpace(KugouCookieCodec.Get(cookie, "auth")));

    private void DeferCompleteSessionRefresh()
        => Volatile.Write(
            ref _credentialRefreshNotBeforeMs,
            Environment.TickCount64 + (long)CredentialRefreshFailureCooldown.TotalMilliseconds);

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

        if (item.TryGetProperty("url", out var urlEl))
        {
            if (urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString();
                if (IsHttpUrl(url))
                    return url;
            }
            else if (urlEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var url in urlEl.EnumerateArray())
                {
                    if (url.ValueKind == JsonValueKind.String && IsHttpUrl(url.GetString()))
                        return url.GetString();
                }
            }
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

        if (item.TryGetProperty("backupUrl", out var camelBackupEl) && camelBackupEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var url in camelBackupEl.EnumerateArray())
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
