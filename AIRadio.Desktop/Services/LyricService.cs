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

        LyricResult? result = null;
        // 酷狗链路故障（上游异常形状/传输失败）与网易兜底故障分别记账：
        // 任一链路故障且兜底无果 → 整体按失败处理不入缓存；两条链路都给出确认答案
        // （有词或明确无词）才入缓存
        var kugouFailed = false;
        var fallbackFailed = false;
        try
        {
            if (prefix == "netease")
            {
                result = await FetchNeteaseAsync(sourceId[(separator + 1)..], timeoutCts.Token);
            }
            else
            {
                try
                {
                    result = await FetchKugouAsync(sourceId[(separator + 1)..], track, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 酷狗 v1 歌词端点 2026-09-23 上游升级后持续返回异常形状（status:1
                    // 歌曲搜索形状），原生链路按失败处理，降级网易词库兜底
                    Log.Debug(ex, "Kugou lyrics fetch failed for {SourceId}; trying netease fallback", sourceId);
                    kugouFailed = true;
                }

                if (result == null && !string.IsNullOrWhiteSpace(track.Title))
                {
                    try
                    {
                        result = await FetchNeteaseByTitleAsync(track, timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Netease lyric fallback failed for {SourceId}", sourceId);
                        fallbackFailed = true;
                    }
                }

                if (result == null && (kugouFailed || fallbackFailed))
                    return null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 调用方主动取消（如应用关闭）必须透传；内部超时走下方兜底
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Lyrics fetch failed for {SourceId}", sourceId);
            return null;
        }

        // 失败（内部超时/代理冷启动未就绪/HTTP 错误/任一链路故障）已提前 return，
        // 走到这里是确认结果：有词，或酷狗与网易兜底双源确认无词
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
        ThrowIfAnomalousKugouStatus(root, "kugou lyric download");

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

    /// <summary>
    /// 酷狗曲目的网易词库兜底：按"标题 歌手"搜网易拿歌曲 ID，再走 /lyric 单步取词。
    /// 候选需标题归一化匹配且时长 ±3s（候选无时长放行），防同名异曲；无可命中候选
    /// 视为确认无词。播放的是酷狗版本、歌词取自网易版本，时长过滤已缓解时间轴偏差。
    /// </summary>
    private async Task<LyricResult?> FetchNeteaseByTitleAsync(Track track, CancellationToken cancellationToken)
    {
        var keyword = string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title.Trim()
            : $"{track.Title.Trim()} {track.Artist.Trim()}";
        var url = $"{_neteaseBase}/search?keywords={Uri.EscapeDataString(keyword)}&limit=8";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.Number &&
            code.GetInt32() != 200)
            throw new InvalidOperationException($"netease lyric fallback search: unexpected code {code.GetInt32()}");
        // 业务错误（鉴权失效/敏感词拦截等）是"没有答案"而非"确认无词"：抛出让上层按
        // 兜底失败处理不入负缓存，否则与酷狗侧确认叠加会把该曲整会话钉死为无词

        if (!root.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
            return null; // 合法无结果形状（网易无匹配时只有 songCount:0 无 songs 字段）

        var durationMs = (long)track.Duration.TotalMilliseconds;
        var targetTitle = MusicIdentity.NormalizeMusicText(track.Title);

        foreach (var song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                continue;
            if (!song.TryGetProperty("name", out var nameEl) ||
                nameEl.GetString() is not { } name ||
                string.IsNullOrWhiteSpace(name))
                continue;

            var normalized = MusicIdentity.NormalizeMusicText(name);
            if (normalized != targetTitle)
                continue;

            if (song.TryGetProperty("duration", out var durEl) &&
                durEl.ValueKind == JsonValueKind.Number &&
                durEl.TryGetInt64(out var candidateMs) &&
                candidateMs > 0 &&
                durationMs > 0 &&
                Math.Abs(candidateMs - durationMs) > CandidateDurationToleranceMs)
                continue;

            return await FetchNeteaseAsync(idEl.GetInt64().ToString(), cancellationToken);
        }

        return null;
    }

    private string BuildSearchUrl(string paramKind, string value, long durationMs)
    {
        var query = paramKind == "hash"
            ? $"hash={Uri.EscapeDataString(value)}"
            : $"keywords={Uri.EscapeDataString(value)}";
        return $"{_kugouBase}/search/lyric?{query}&duration={durationMs}";
    }

    /// <summary>status==200 且有候选 → 返回按 ±3s 时长过滤后的候选 id/accesskey；status==404/其余无候选 → null（允许上层继续关键词兜底）。
    /// 非 200/404 的 status 是上游异常形状（实机观测：网关抖动窗口对歌词搜索返回 status:1 的
    /// 歌曲搜索形状响应）——按获取失败上抛处理，否则会被当"确认无词"写入无 TTL 负缓存，
    /// 该曲目整会话恒无词。
    /// </summary>
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
        ThrowIfAnomalousKugouStatus(root, "kugou lyric search");

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

    /// <summary>
    /// 酷狗歌词接口的异常形状防线：status 缺失或非 200/404 时抛出，让上层按"获取失败"
    /// 处理（不入负缓存、下次重试）。合法无匹配的口径是 404（含空 candidates），
    /// 其余 status 是网关错路由/风控降级等异常（实机观测 status:1 + 歌曲搜索形状）。
    /// </summary>
    private static void ThrowIfAnomalousKugouStatus(JsonElement root, string source)
    {
        if (!root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException($"{source}: response missing numeric status");
        var value = status.GetInt32();
        if (value is not (200 or 404))
            throw new InvalidOperationException($"{source}: anomalous upstream status {value}");
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
