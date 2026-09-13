using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 歌词获取：网易（/lyric 单步）与酷狗（/search/lyric 找候选 + /lyric 取词）经本地代理。
/// 歌词是展示增强：任何失败/超时返回 null，不抛异常、不进音源熔断与健康统计。
/// </summary>
public class LyricService : ILyricService
{
    private const int CacheCapacity = 128;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    // 候选与曲目时长差的容忍窗口（ms）；title-only 关键词兜底可能命中同名异曲
    private const long CandidateDurationToleranceMs = 3000;

    private readonly HttpClient _httpClient;
    private readonly string _neteaseBase;
    private readonly string _kugouBase;
    private readonly ConcurrentDictionary<string, LyricResult?> _cache = new();

    public LyricService(
        HttpClient httpClient,
        string neteaseBaseUrl = "http://127.0.0.1:37250",
        string kugouBaseUrl = "http://127.0.0.1:37251")
    {
        _httpClient = httpClient;
        _neteaseBase = neteaseBaseUrl.TrimEnd('/');
        _kugouBase = kugouBaseUrl.TrimEnd('/');
    }

    public async Task<LyricResult?> GetLyricsAsync(Track track, CancellationToken cancellationToken)
    {
        var sourceId = track.SourceId;
        if (string.IsNullOrEmpty(sourceId))
            return null;

        var separator = sourceId.IndexOf(':');
        if (separator <= 0)
            return null;

        var prefix = sourceId[..separator];
        if (prefix is not ("netease" or "kugou"))
            return null; // kuwo/migu/youtube 等前缀与本地文件：无歌词链路，不入缓存

        if (_cache.TryGetValue(sourceId, out var cached))
            return cached;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FetchTimeout);

        LyricResult? result;
        try
        {
            result = prefix == "netease"
                ? await FetchNeteaseAsync(sourceId[(separator + 1)..], timeoutCts.Token)
                : await FetchKugouAsync(sourceId[(separator + 1)..], track, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 调用方主动取消（如应用关闭）必须透传；内部超时走下方兜底
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Lyrics fetch failed for {SourceId}", sourceId);
            result = null;
        }

        if (_cache.Count >= CacheCapacity)
            _cache.Clear(); // 电台模式无限续播防泄漏，粗粒度淘汰足够
        _cache[sourceId] = result;
        return result;
    }

    private async Task<LyricResult?> FetchNeteaseAsync(string songId, CancellationToken cancellationToken)
    {
        var url = $"{_neteaseBase}/lyric?id={Uri.EscapeDataString(songId)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.Number &&
            code.GetInt32() != 200)
            return null;

        var lrcText = GetString(root, "lrc", "lyric");
        var lines = LrcParser.Parse(lrcText);

        var isInstrumental = GetBool(root, "nolyric") || GetBool(root, "pureMusic");
        if (lines.Count == 0)
            return isInstrumental ? new LyricResult { IsInstrumental = true } : null;

        if (IsPlaceholderOnly(lines))
            return null; // 未收录歌曲返回 "[00:00.00]暂无歌词" 占位

        return new LyricResult { Lines = lines };
    }

    private async Task<LyricResult?> FetchKugouAsync(string hash, Track track, CancellationToken cancellationToken)
    {
        var durationMs = (long)track.Duration.TotalMilliseconds;

        (string Id, string Accesskey)? candidate = null;

        // 第一步按 hash；404 且无候选时先 title-only、再 title+artist（中文曲库组合词必 404）
        candidate = await SearchCandidateAsync(BuildSearchUrl("hash", hash, durationMs), durationMs, cancellationToken);
        if (candidate == null && !string.IsNullOrWhiteSpace(track.Title))
        {
            candidate = await SearchCandidateAsync(BuildSearchUrl("keywords", track.Title.Trim(), durationMs), durationMs, cancellationToken)
                        ?? (!string.IsNullOrWhiteSpace(track.Artist)
                            ? await SearchCandidateAsync(
                                BuildSearchUrl("keywords", $"{track.Title.Trim()} {track.Artist.Trim()}", durationMs),
                                durationMs, cancellationToken)
                            : null);
        }

        if (candidate is not { } picked)
            return null;

        var url = $"{_kugouBase}/lyric?id={Uri.EscapeDataString(picked.Id)}&accesskey={Uri.EscapeDataString(picked.Accesskey)}&fmt=lrc&decode=1";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.Number ||
            status.GetInt32() != 200)
            return null;

        var decodeContent = GetString(root, "decodeContent");
        var lines = LrcParser.Parse(decodeContent);
        if (lines.Count == 0 || IsPlaceholderOnly(lines))
            return null;

        return new LyricResult { Lines = lines };
    }

    private string BuildSearchUrl(string paramKind, string value, long durationMs)
    {
        var query = paramKind == "hash"
            ? $"hash={Uri.EscapeDataString(value)}"
            : $"keywords={Uri.EscapeDataString(value)}";
        return $"{_kugouBase}/search/lyric?{query}&duration={durationMs}";
    }

    /// <summary>status==200 且有候选 → 返回按 ±3s 时长过滤后的候选 id/accesskey；status==404/其余无候选 → null（允许上层继续关键词兜底）。</summary>
    private async Task<(string Id, string Accesskey)?> SearchCandidateAsync(
        string url,
        long durationMs,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.Number ||
            status.GetInt32() != 200)
            return null;

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
            return null;

        foreach (var item in candidates.EnumerateArray())
        {
            if (item.TryGetProperty("duration", out var duration) &&
                duration.ValueKind == JsonValueKind.Number &&
                duration.TryGetInt64(out var candidateMs) &&
                durationMs > 0 &&
                Math.Abs(candidateMs - durationMs) <= CandidateDurationToleranceMs)
            {
                // 时长命中的候选也可能缺 id/accesskey：提取失败继续扫，全部不可用回退首候选
                var matched = ExtractCandidate(item);
                if (matched != null)
                    return matched;
            }
        }

        return ExtractCandidate(candidates[0]);
    }

    // JsonElement 是 JsonDocument 的视图，文档释放后不可再读：候选信息必须在 doc 存活期内提取成字符串
    private static (string Id, string Accesskey)? ExtractCandidate(JsonElement item)
    {
        var id = GetString(item, "id");
        var accesskey = GetString(item, "accesskey");
        return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accesskey)
            ? null
            : (id, accesskey);
    }

    /// <summary>占位判定：解析结果全部行 Trim 后均为"暂无歌词"（未收录歌曲的占位 lrc）。</summary>
    private static bool IsPlaceholderOnly(IReadOnlyList<LyricLine> lines)
        => lines.All(line => line.Text.Trim() == "暂无歌词");

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString();
                if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return value.ToString();
            }
        }
        return null;
    }

    private static string? GetString(JsonElement element, string objectName, string propertyName)
        => element.TryGetProperty(objectName, out var obj) &&
           obj.ValueKind == JsonValueKind.Object
            ? GetString(obj, propertyName)
            : null;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) &&
           (value.ValueKind == JsonValueKind.True ||
            (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0));
}
