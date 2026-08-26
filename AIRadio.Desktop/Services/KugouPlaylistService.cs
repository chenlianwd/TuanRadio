using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
/// 读取已登录账号的酷狗歌单。Cookie 只通过 Authorization 头交给本地代理，
/// 不进入 URL、普通日志或本地播放列表文件。
/// </summary>
public sealed class KugouPlaylistService : IKugouPlaylistService
{
    private const int PlaylistPageSize = 30;
    private const int TrackPageSize = 30;
    private const int MaxPages = 200;
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
        var cookie = RequireCookie();
        var results = new List<KugouPlaylistInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long fetchedItems = 0;

        for (var page = 1; page <= MaxPages; page++)
        {
            var root = await GetRootAsync(
                $"/user/playlist?page={page}&pagesize={PlaylistPageSize}&timestamp={ClockStamp()}",
                cookie,
                cancellationToken);
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
        if (string.IsNullOrWhiteSpace(playlistId))
            throw new ArgumentException("Playlist id is required.", nameof(playlistId));

        var cookie = RequireCookie();
        var results = new List<OnlineTrack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long fetchedItems = 0;

        for (var page = 1; page <= MaxPages; page++)
        {
            var root = await GetRootAsync(
                $"/playlist/track/all/new?listid={Uri.EscapeDataString(playlistId)}" +
                $"&page={page}&pagesize={TrackPageSize}&timestamp={ClockStamp()}",
                cookie,
                cancellationToken);
            var items = FindItems(root, "info", "songs", "files", "list", "records");
            fetchedItems += items.Count;

            foreach (var item in items)
            {
                var hash = GetNestedFlexibleString(item,
                    "hash", "FileHash", "filehash", "hash_128", "hash_std");
                if (string.IsNullOrWhiteSpace(hash) || !seen.Add(hash))
                    continue;

                var fileName = GetNestedString(item, "filename", "FileName", "file_name");
                var title = NormalizeTrackTitle(GetNestedString(item,
                                "songname", "SongName", "OriSongName", "audio_name", "name", "title"))
                            ?? ParseTitleFromFileName(fileName);
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var durationMs = GetDurationMilliseconds(item);
                results.Add(new OnlineTrack
                {
                    Id = "kugou:" + hash,
                    Title = title,
                    Artist = GetNestedString(item,
                        "singername", "SingerName", "singer_name", "author_name", "artist") ?? string.Empty,
                    Album = GetNestedString(item,
                        "album_name", "AlbumName", "albumname", "album") ?? string.Empty,
                    DurationMs = durationMs,
                    Source = "酷狗"
                });
            }

            var hasTotal = TryGetTotal(root, out var total);
            if (items.Count == 0 ||
                (hasTotal && fetchedItems >= total) ||
                (!hasTotal && items.Count < TrackPageSize))
                break;
        }

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
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + relativeUrl);
        request.Headers.TryAddWithoutValidation("Authorization", cookie);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.Number &&
            status.TryGetInt32(out var statusValue) &&
            statusValue != 1)
        {
            var error = GetString(root, "error", "message", "msg") ?? statusValue.ToString();
            throw new MusicSourceBusinessException(AppLanguage.T(
                $"酷狗歌单接口返回异常：{error}",
                $"Kugou playlist request failed: {error}"));
        }

        return root.Clone();
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

    private static long ClockStamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
