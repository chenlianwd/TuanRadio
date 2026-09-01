using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>酷狗云端歌单的只读摘要。</summary>
public sealed class KugouPlaylistInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int TrackCount { get; init; }
}

public interface IKugouPlaylistService
{
    bool IsLoggedIn { get; }
    Task<IReadOnlyList<KugouPlaylistInfo>> GetUserPlaylistsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksAsync(
        string playlistId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 可利用远端歌单声明的歌曲总数优化分页的实现。单纯实现
/// <see cref="IKugouPlaylistService"/> 的插件仍走原有串行兼容路径。
/// </summary>
public interface IKugouPlaylistTrackPageLoader
{
    Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksAsync(
        string playlistId,
        int expectedTrackCount,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 读取已登录账号的酷狗歌单。Cookie 只通过 Authorization 头交给本地代理，
/// 不进入 URL、普通日志或本地播放列表文件。
/// </summary>
public sealed class KugouPlaylistService : IKugouPlaylistService, IKugouPlaylistTrackPageLoader
{
    private const int PlaylistPageSize = 30;
    private const int TrackPageSize = 30;
    private const int MaxPages = 200;
    private const int TrackPageConcurrency = 4;
    private const int SequentialPageThreshold = 4;
    private const int MaxTransientRetries = 10;
    /// <summary>歌单同步整体预算：分页 × 重试退避最坏可放大到数十分钟，必须有全局上限；
    /// 超限以取消形式抛出，调用方（用户取消/应用退出）语义不变。</summary>
    private static readonly TimeSpan SyncTotalBudget = TimeSpan.FromMinutes(3);
    private readonly HttpClient _httpClient;
    private readonly MusicAccountStore _accounts;
    private readonly string _baseUrl;

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(_accounts.KugouCookie);

    public KugouPlaylistService(
        HttpClient httpClient,
        MusicAccountStore accounts,
        string baseUrl = "http://127.0.0.1:37251")
    {
        _httpClient = httpClient;
        _accounts = accounts;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<KugouPlaylistInfo>> GetUserPlaylistsAsync(
        CancellationToken cancellationToken = default)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(SyncTotalBudget);
        var token = budgetCts.Token;

        var cookie = RequireCookie();
        var results = new List<KugouPlaylistInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long fetchedItems = 0;

        for (var page = 1; page <= MaxPages; page++)
        {
            var root = await GetRootAsync(
                $"/user/playlist?page={page}&pagesize={PlaylistPageSize}&timestamp={ClockStamp()}",
                cookie,
                token);
            var items = FindItems(root, "info", "lists", "list", "records");
            fetchedItems += items.Count;

            foreach (var item in items)
            {
                var id = GetFlexibleString(item,
                    "listid", "list_id", "global_collection_id", "collection_id", "id");
                var name = GetString(item,
                    "name", "listname", "list_name", "specialname", "title");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !seen.Add(id))
                    continue;

                results.Add(new KugouPlaylistInfo
                {
                    Id = id,
                    Name = name,
                    TrackCount = GetInt32(item,
                        "count", "song_count", "track_count", "total", "file_count")
                });
            }

            // 上游可能把 pagesize 限制为较小值，不能用“短页”直接判断结束。
            // 有 total 时按实际累计响应数判断；没有 total 时回退到标准短页判断。
            var hasTotal = TryGetTotal(root, out var total);
            if (items.Count == 0 ||
                (hasTotal && fetchedItems >= total) ||
                (!hasTotal && items.Count < PlaylistPageSize))
                break;
        }

        return results;
    }

    public async Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(SyncTotalBudget);
        return await GetPlaylistTracksCoreAsync(playlistId, expectedTrackCount: 0, budgetCts.Token);
    }

    public async Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksAsync(
        string playlistId,
        int expectedTrackCount,
        CancellationToken cancellationToken = default)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(SyncTotalBudget);
        return await GetPlaylistTracksCoreAsync(playlistId, expectedTrackCount, budgetCts.Token);
    }

    private async Task<IReadOnlyList<OnlineTrack>> GetPlaylistTracksCoreAsync(
        string playlistId,
        int expectedTrackCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist id is required.", nameof(playlistId));

        var cookie = RequireCookie();
        var firstRoot = await GetTrackPageAsync(playlistId, 1, cookie, cancellationToken);
        var firstItems = FindItems(firstRoot, "info", "songs", "files", "list", "records");
        var pageRoots = new List<(int Page, JsonElement Root)> { (1, firstRoot) };

        // 优先使用接口返回的 total；打开歌单时再用摘要中的 TrackCount 作为兜底。
        // 首页实际返回条数可以识别上游对 pagesize 的限制（例如 30 被压成 20）。
        var total = TryGetTotal(firstRoot, out var reportedTotal)
            ? reportedTotal
            : Math.Max(0, expectedTrackCount);
        if (expectedTrackCount > total)
            total = expectedTrackCount;

        if (firstItems.Count > 0 && total > firstItems.Count)
        {
            // 以首页实际条数为准，而不是盲信响应里的 pagesize：上游可能把
            // 请求的 30 条压成 20 条，按声明值计算会漏掉最后一页。
            var effectivePageSize = firstItems.Count;
            if (effectivePageSize <= 0)
                effectivePageSize = TrackPageSize;

            var pageCount = (int)Math.Min(
                MaxPages,
                (total - 1) / effectivePageSize + 1);
            if (pageCount > 1)
            {
                var remaining = await FetchTrackPagesAsync(
                    playlistId,
                    cookie,
                    firstPage: 2,
                    lastPage: pageCount,
                    cancellationToken);
                pageRoots.AddRange(remaining);
            }
        }
        else if (total <= 0 && firstItems.Count > 0)
        {
            // 没有 total 且没有摘要提示时用短页终止规则，
            // 但以首页实际条数为基准：上游可能把 pagesize 压小（如 30→20），
            // 按固定 30 判定会把首页当成末页或直接漏掉后续页，静默截断歌单。
            // 上游对越界页重复返回满页（而不是空页/短页）时，按"无新增曲目"终止兜底。
            var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectTrackHashes(firstRoot, seenHashes);
            for (var page = 2; page <= MaxPages; page++)
            {
                var root = await GetTrackPageAsync(playlistId, page, cookie, cancellationToken);
                var items = FindItems(root, "info", "songs", "files", "list", "records");
                pageRoots.Add((page, root));
                if (items.Count == 0)
                    break;
                var before = seenHashes.Count;
                CollectTrackHashes(root, seenHashes);
                if (seenHashes.Count == before || items.Count < firstItems.Count)
                    break;
            }
        }

        var results = new List<OnlineTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, root) in pageRoots.OrderBy(item => item.Page))
            AppendTracks(root, results, seen);

        return results;
    }

    private string RequireCookie()
        => _accounts.KugouCookie ?? throw new MusicSourceBusinessException(AppLanguage.T(
            "酷狗未登录，请先在设置中扫码登录。",
            "Kugou is not signed in. Scan the QR code in Settings first."));

    private async Task<JsonElement> GetRootAsync(
        string relativeUrl,
        string cookie,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + relativeUrl);
                request.Headers.TryAddWithoutValidation("Authorization", cookie);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(body);
                    root = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // 非 JSON 的 5xx 按瞬时代理故障重试（Node 半启动时常返回 HTML 错误页）。
                    // catch 块内 EnsureSuccessStatusCode 抛出的新异常不会被同级
                    // catch (HttpRequestException) 捕获，必须在此显式走重试路径。
                    if (!response.IsSuccessStatusCode &&
                        attempt < MaxTransientRetries &&
                        IsTransientStatus(response.StatusCode))
                    {
                        Log.Debug(
                            "Kugou playlist request retry {Attempt}/{MaxAttempts} for {Path}: non-JSON HTTP {Status}",
                            attempt + 1,
                            MaxTransientRetries,
                            relativeUrl.Split('?', 2)[0],
                            (int)response.StatusCode);
                        await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                        continue;
                    }

                    // 重试耗尽或非瞬时状态：保留原始 HTTP 错误，便于上层诊断
                    response.EnsureSuccessStatusCode();
                    throw;
                }

                var statusValue = TryGetInt32(root, "status");
                if (statusValue.HasValue && statusValue.Value != 1)
                {
                    var errorCode = TryGetInt32(root, "error_code", "code");
                    var error = GetString(root, "error", "error_msg", "message", "msg")
                                ?? errorCode?.ToString()
                                ?? statusValue.Value.ToString();
                    error = SensitiveDataSanitizer.Sanitize(error) ?? error;
                    throw new MusicSourceBusinessException(AppLanguage.T(
                        $"酷狗歌单接口返回异常：{error}",
                        $"Kugou playlist request failed: {error}"));
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < MaxTransientRetries && IsTransientStatus(response.StatusCode))
                    {
                        Log.Debug(
                            "Kugou playlist request retry {Attempt}/{MaxAttempts} for {Path}: HTTP {Status}",
                            attempt + 1,
                            MaxTransientRetries,
                            relativeUrl.Split('?', 2)[0],
                            (int)response.StatusCode);
                        await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                }

                return root;
            }
            catch (MusicSourceBusinessException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (
                attempt < MaxTransientRetries &&
                (ex.StatusCode == null || IsTransientStatus(ex.StatusCode.Value)))
            {
                Log.Debug(
                    "Kugou playlist request retry {Attempt}/{MaxAttempts} for {Path}: {Message}",
                    attempt + 1,
                    MaxTransientRetries,
                    relativeUrl.Split('?', 2)[0],
                    ex.Message);
                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
        }
    }

    private async Task<JsonElement> GetTrackPageAsync(
        string playlistId,
        int page,
        string cookie,
        CancellationToken cancellationToken)
        => await GetRootAsync(
            $"/playlist/track/all/new?listid={Uri.EscapeDataString(playlistId)}" +
            $"&page={page}&pagesize={TrackPageSize}&timestamp={ClockStamp()}",
            cookie,
            cancellationToken);

    private async Task<IReadOnlyList<(int Page, JsonElement Root)>> FetchTrackPagesAsync(
        string playlistId,
        string cookie,
        int firstPage,
        int lastPage,
        CancellationToken cancellationToken)
    {
        if (lastPage < firstPage)
            return Array.Empty<(int Page, JsonElement Root)>();

        var pageCount = lastPage - firstPage + 1;
        if (pageCount <= SequentialPageThreshold)
        {
            var sequential = new List<(int Page, JsonElement Root)>(pageCount);
            for (var page = firstPage; page <= lastPage; page++)
                sequential.Add((page, await GetTrackPageAsync(playlistId, page, cookie, cancellationToken)));
            return sequential;
        }

        using var limiter = new SemaphoreSlim(TrackPageConcurrency, TrackPageConcurrency);
        var tasks = Enumerable.Range(firstPage, pageCount)
            .Select(async page =>
            {
                await limiter.WaitAsync(cancellationToken);
                try
                {
                    var root = await GetTrackPageAsync(playlistId, page, cookie, cancellationToken);
                    return (Page: page, Root: root);
                }
                finally
                {
                    limiter.Release();
                }
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);
        return results.OrderBy(item => item.Page).ToArray();
    }

    private static void AppendTracks(
        JsonElement root,
        ICollection<OnlineTrack> results,
        ISet<string> seen)
    {
        var items = FindItems(root, "info", "songs", "files", "list", "records");
        foreach (var item in items)
        {
            var hash = GetNestedFlexibleString(item,
                "hash", "FileHash", "filehash", "hash_128", "hash_std");
            if (string.IsNullOrWhiteSpace(hash) || !seen.Add(hash))
                continue;

            var fileName = GetNestedString(item, "filename", "FileName", "file_name");
            var rawTitle = GetNestedString(item,
                "songname", "SongName", "OriSongName", "audio_name", "name", "title");
            var title = NormalizeTrackTitle(rawTitle)
                        ?? ParseTitleFromFileName(fileName);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var artist = GetNestedString(item,
                "singername", "SingerName", "singer_name", "author_name", "artist") ?? string.Empty;
            NormalizeArtistAndTitle(ref artist, ref title, fileName);

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddMetadata(metadata, "album_id", GetNestedFlexibleString(item,
                "album_id", "albumid", "AlbumID", "AlbumId"));
            AddMetadata(metadata, "album_audio_id", GetNestedFlexibleString(item,
                "album_audio_id", "album_audioid", "mixsongid", "MixSongId", "MixSongID"));
            AddMetadata(metadata, "hash_std", GetNestedFlexibleString(item,
                "hash_std", "HashStd"));
            AddMetadata(metadata, "hash_128", GetNestedFlexibleString(item,
                "hash_128", "Hash128"));

            results.Add(new OnlineTrack
            {
                Id = "kugou:" + hash,
                Title = title,
                Artist = artist,
                Album = GetNestedString(item,
                    "album_name", "AlbumName", "albumname", "album") ?? string.Empty,
                DurationMs = GetDurationMilliseconds(item),
                Source = "酷狗",
                ProviderMetadata = metadata
            });
        }
    }

    /// <summary>收集一页曲目去重用的 hash 集合，与 AppendTracks 的身份提取保持同一口径。</summary>
    private static void CollectTrackHashes(JsonElement root, ISet<string> seen)
    {
        var items = FindItems(root, "info", "songs", "files", "list", "records");
        foreach (var item in items)
        {
            var hash = GetNestedFlexibleString(item,
                "hash", "FileHash", "filehash", "hash_128", "hash_std");
            if (!string.IsNullOrWhiteSpace(hash))
                seen.Add(hash);
        }
    }

    private static IReadOnlyList<JsonElement> FindItems(JsonElement root, params string[] names)
    {
        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : root;
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().ToArray();

        foreach (var name in names)
        {
            if (TryFindArray(data, name, 0, out var array))
                return array.EnumerateArray().ToArray();
        }

        return Array.Empty<JsonElement>();
    }

    private static bool TryFindArray(JsonElement element, string name, int depth, out JsonElement result)
    {
        result = default;
        if (depth > 3 || element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.Array)
        {
            result = direct;
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryFindArray(property.Value, name, depth + 1, out result))
                return true;
        }

        return false;
    }

    private static bool TryGetTotal(JsonElement root, out long total)
    {
        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : root;
        total = GetInt64(data, "total", "total_count");
        return total > 0;
    }

    private static int? TryGetInt32(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number;
        }

        return null;
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == (HttpStatusCode)429 ||
           (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(3000, 500 * (attempt + 1)));

    private static string? GetNestedString(JsonElement item, params string[] names)
    {
        var value = GetString(item, names);
        if (value != null)
            return value;

        foreach (var nestedName in new[] { "audio_info", "audio", "base", "file", "song" })
        {
            if (item.TryGetProperty(nestedName, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                value = GetString(nested, names);
                if (value != null)
                    return value;
            }
        }

        return null;
    }

    private static string? GetNestedFlexibleString(JsonElement item, params string[] names)
    {
        var value = GetFlexibleString(item, names);
        if (value != null)
            return value;

        foreach (var nestedName in new[] { "audio_info", "audio", "base", "file", "song" })
        {
            if (item.TryGetProperty(nestedName, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                value = GetFlexibleString(nested, names);
                if (value != null)
                    return value;
            }
        }

        return null;
    }

    private static long GetDurationMilliseconds(JsonElement item)
    {
        var milliseconds = GetNestedInt64(item, "timelen", "duration_ms", "durationMillis", "time_length");
        if (milliseconds > 0)
            return milliseconds;

        var duration = GetNestedInt64(item, "duration", "Duration", "time");
        if (duration <= 0)
            return 0;
        return duration > 10_000 ? duration : duration * 1000;
    }

    private static long GetNestedInt64(JsonElement item, params string[] names)
    {
        var value = GetInt64(item, names);
        if (value > 0)
            return value;

        foreach (var nestedName in new[] { "audio_info", "audio", "base", "file", "song" })
        {
            if (item.TryGetProperty(nestedName, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                value = GetInt64(nested, names);
                if (value > 0)
                    return value;
            }
        }

        return 0;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }

        return null;
    }

    private static string? GetFlexibleString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }

        return null;
    }

    private static int GetInt32(JsonElement element, params string[] names)
    {
        var value = GetInt64(element, names);
        return value is > 0 and <= int.MaxValue ? (int)value : 0;
    }

    private static long GetInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
                return number;
        }

        return 0;
    }

    private static string? ParseTitleFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var title = fileName;
        var separator = title.IndexOf(" - ", StringComparison.Ordinal);
        if (separator >= 0)
            title = title[(separator + 3)..];
        var extension = title.LastIndexOf('.');
        if (extension > 0)
            title = title[..extension];
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }

    private static string? NormalizeTrackTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var normalized = title.Trim();
        foreach (var extension in new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma" })
        {
            if (normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^extension.Length].Trim();
                break;
            }
        }
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// 酷狗歌单部分响应只有 audio_name/filename，值为“歌手 - 歌名”，author_name 为空。
    /// 在入库前拆开，避免跨源兜底把整段展示名当成歌曲标题而永远匹配失败。
    /// </summary>
    private static void NormalizeArtistAndTitle(ref string artist, ref string title, string? fileName)
    {
        if (TrySplitArtistAndTitle(title, out var titleArtist, out var cleanTitle))
        {
            if (string.IsNullOrWhiteSpace(artist))
                artist = titleArtist;

            if (string.IsNullOrWhiteSpace(artist) ||
                MusicIdentity.NormalizeMusicText(artist) == MusicIdentity.NormalizeMusicText(titleArtist))
                title = cleanTitle;
        }

        if (!string.IsNullOrWhiteSpace(artist) ||
            !TrySplitArtistAndTitle(NormalizeTrackTitle(fileName), out var fileArtist, out _))
            return;

        artist = fileArtist;
    }

    private static bool TrySplitArtistAndTitle(
        string? displayName,
        out string artist,
        out string title)
    {
        artist = string.Empty;
        title = string.Empty;
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        foreach (var separator in new[] { " - ", " – ", " — ", "－" })
        {
            var index = displayName.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= displayName.Length)
                continue;

            artist = displayName[..index].Trim();
            title = displayName[(index + separator.Length)..].Trim();
            return artist.Length > 0 && title.Length > 0;
        }

        return false;
    }

    private static void AddMetadata(
        IDictionary<string, string> metadata,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != "0")
            metadata[key] = value;
    }

    private static long ClockStamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
