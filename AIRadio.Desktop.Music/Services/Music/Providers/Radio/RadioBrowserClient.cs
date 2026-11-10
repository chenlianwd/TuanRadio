using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// Radio-Browser 开放网络电台客户端。
/// 具备官方集群多镜像故障感知、请求级自动容灾轮转、15 分钟内存缓存与公开电台兜底库。
/// </summary>
public sealed class RadioBrowserClient : IRadioBrowserService, IDisposable
{
    private const string UserAgent = "TuanRadio/1.0.1 (+https://github.com/chenlianwd/TuanRadio)";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private const int MaxCacheEntries = 100;

    private static readonly string[] DefaultMirrors =
    [
        "https://de1.api.radio-browser.info",
        "https://nl1.api.radio-browser.info",
        "https://at1.api.radio-browser.info"
    ];

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<string> _mirrors;
    private readonly object _mirrorGate = new();
    private int _mirrorIndex;
    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, IReadOnlyList<RadioStation> Stations)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public RadioBrowserClient(HttpClient? httpClient = null, IReadOnlyList<string>? mirrors = null)
    {
        _mirrors = mirrors is { Count: > 0 }
            ? mirrors.Select(m => m.TrimEnd('/')).ToArray()
            : DefaultMirrors;

        if (httpClient != null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = DefaultTimeout
            };
            _ownsHttpClient = true;
        }
    }

    public async Task<IReadOnlyList<RadioStation>> GetStationsByCategoryAsync(
        string category,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(category) ? "top" : category.Trim().ToLowerInvariant();

        if (!forceRefresh && _cache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt <= CacheDuration)
        {
            return cached.Stations;
        }

        var path = MapCategoryPath(key);
        var stations = await FetchStationsWithMirrorFailoverAsync(path, cancellationToken).ConfigureAwait(false);

        if (stations != null && stations.Count > 0)
        {
            SetCache(key, stations);
            return stations;
        }

        // 所有镜像均不可达或返回空：降级至内置精选电台库
        Log.Warning("Radio-Browser mirrors unavailable; falling back to curated stations for category '{Category}'", key);
        return CuratedStationRegistry.GetCuratedStations(key);
    }

    public async Task<IReadOnlyList<RadioStation>> SearchStationsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetStationsByCategoryAsync("top", forceRefresh: false, cancellationToken).ConfigureAwait(false);

        var clean = query.Trim();
        var cacheKey = "search:" + clean.ToLowerInvariant();

        if (_cache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt <= CacheDuration)
        {
            return cached.Stations;
        }

        var path = $"/json/stations/search?name={Uri.EscapeDataString(clean)}&limit=40&order=clickcount&reverse=true&hidebroken=true";
        var stations = await FetchStationsWithMirrorFailoverAsync(path, cancellationToken).ConfigureAwait(false);

        if (stations != null && stations.Count > 0)
        {
            SetCache(cacheKey, stations);
            return stations;
        }

        // 搜索失败降级至内置精选库匹配
        return CuratedStationRegistry.SearchCuratedStations(clean);
    }

    public async Task<string?> ResolveStationUrlAsync(
        string stationUuid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stationUuid))
            return null;

        var cleanUuid = stationUuid.Trim();

        // 离线精选库优先直接命中
        var curated = CuratedStationRegistry.FindByUuid(cleanUuid);
        if (curated != null && !string.IsNullOrWhiteSpace(curated.StreamUrl))
            return curated.StreamUrl;

        var path = $"/json/stations/byuuid?uuids={Uri.EscapeDataString(cleanUuid)}";
        var stations = await FetchStationsWithMirrorFailoverAsync(path, cancellationToken).ConfigureAwait(false);

        return stations?.FirstOrDefault()?.StreamUrl;
    }

    private static string MapCategoryPath(string category) => category switch
    {
        "chinese" => "/json/stations/bycountrycodeexact/CN?limit=40&order=clickcount&reverse=true&hidebroken=true",
        "top" => "/json/stations/topclick/40?hidebroken=true",
        _ => $"/json/stations/bytag/{Uri.EscapeDataString(category)}?limit=40&order=clickcount&reverse=true&hidebroken=true"
    };

    private async Task<IReadOnlyList<RadioStation>?> FetchStationsWithMirrorFailoverAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var mirrorCount = _mirrors.Count;
        for (var attempt = 0; attempt < mirrorCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string mirror;
            lock (_mirrorGate)
            {
                mirror = _mirrors[_mirrorIndex];
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(DefaultTimeout);
                var attemptToken = timeoutCts.Token;

                var uri = new Uri(mirror + relativePath);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptToken).ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400)
                    throw new HttpRequestException($"Redirect rejected for mirror {mirror}");

                response.EnsureSuccessStatusCode();

                await using var contentStream = await response.Content.ReadAsStreamAsync(attemptToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(contentStream, cancellationToken: attemptToken).ConfigureAwait(false);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Radio-Browser response root is not an array");

                var list = new List<RadioStation>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var station = ParseStationElement(item);
                    if (station != null)
                        list.Add(station);
                }

                return list;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Radio-Browser mirror '{Mirror}' failed on '{Path}'", mirror, relativePath);
                RotateMirror();
            }
        }

        return null;
    }

    private void SetCache(string key, IReadOnlyList<RadioStation> stations)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in _cache)
            {
                if (now - kvp.Value.CachedAt > CacheDuration)
                    _cache.TryRemove(kvp.Key, out _);
            }
        }
        _cache[key] = (DateTimeOffset.UtcNow, stations);
    }

    private void RotateMirror()
    {
        lock (_mirrorGate)
        {
            _mirrorIndex = (_mirrorIndex + 1) % _mirrors.Count;
        }
    }

    private static RadioStation? ParseStationElement(JsonElement item)
    {
        var uuid = GetString(item, "stationuuid") ?? GetString(item, "changeuuid");
        var name = GetString(item, "name");
        var urlResolved = GetString(item, "url_resolved");
        var url = GetString(item, "url");

        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(name))
            return null;

        // 优先使用已解析重定向直链，否则回退原始流地址
        var rawUrl = !string.IsNullOrWhiteSpace(urlResolved) ? urlResolved : url;
        if (string.IsNullOrWhiteSpace(rawUrl))
            return null;

        rawUrl = rawUrl.Trim();
        // 协议清洗：Shoutcast icy:// 规范化为 http://
        if (rawUrl.StartsWith("icy://", StringComparison.OrdinalIgnoreCase))
            rawUrl = "http://" + rawUrl[6..];

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        // 过滤已知失效电台
        if (item.TryGetProperty("lastcheckok", out var checkEl) &&
            checkEl.TryGetInt32(out var check) && check == 0)
        {
            return null;
        }

        var bitrate = 0;
        if (item.TryGetProperty("bitrate", out var bitrateEl) && bitrateEl.TryGetInt32(out var br))
            bitrate = Math.Max(0, br);

        var votes = 0;
        if (item.TryGetProperty("votes", out var votesEl) && votesEl.TryGetInt32(out var vt))
            votes = Math.Max(0, vt);

        var clickCount = 0;
        if (item.TryGetProperty("clickcount", out var clickEl) && clickEl.TryGetInt32(out var cc))
            clickCount = Math.Max(0, cc);

        return new RadioStation
        {
            StationUuid = uuid.Trim(),
            Name = WebUtility.HtmlDecode(name).Trim(),
            StreamUrl = rawUrl,
            Tags = WebUtility.HtmlDecode(GetString(item, "tags") ?? string.Empty).Trim(),
            Country = WebUtility.HtmlDecode(GetString(item, "country") ?? string.Empty).Trim(),
            CountryCode = (GetString(item, "countrycode") ?? string.Empty).Trim(),
            Codec = (GetString(item, "codec") ?? "MP3").Trim(),
            Bitrate = bitrate,
            Votes = votes,
            ClickCount = clickCount
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_ownsHttpClient)
            _http.Dispose();
    }
}
