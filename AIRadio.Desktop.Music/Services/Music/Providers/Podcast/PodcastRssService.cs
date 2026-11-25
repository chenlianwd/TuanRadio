using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 播客 RSS / Atom 安全抓取与解析实现：
/// 禁止 DTD/XXE、限制 5MB 响应体积、逐跳校验重定向防 SSRF、支持 10 分钟内存缓存。
/// </summary>
public sealed class PodcastRssService : IPodcastRssService, IDisposable
{
    internal const int MaxFeedBytes = 5 * 1024 * 1024;
    private const int MaxRedirectHops = 5;
    private const int MaxEpisodesPerFeed = 50;
    private const string UserAgent = "TuanRadio/1.0 (+https://github.com/chenlianwd/TuanRadio)";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly XNamespace ItunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace ContentNs = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";

    private static readonly Regex HtmlTagRegex = new(@"<[a-zA-Z/!?][^>]*>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac"];

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Func<Uri, CancellationToken, Task<bool>> _uriValidator;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(PodcastFeedParseResult Result, DateTimeOffset ExpiresAtUtc);

    public PodcastRssService(
        HttpClient? httpClient = null,
        Func<Uri, CancellationToken, Task<bool>>? uriValidator = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            _httpClient = new HttpClient(handler);
            _ownsClient = true;
        }

        _uriValidator = uriValidator ?? MediaUriPolicy.ValidateCustomRadioAsync;
    }

    public async Task<PodcastFeedParseResult> FetchFeedAsync(
        string rssUrl,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rssUrl) ||
            !Uri.TryCreate(rssUrl.Trim(), UriKind.Absolute, out var initialUri) ||
            (initialUri.Scheme != Uri.UriSchemeHttp && initialUri.Scheme != Uri.UriSchemeHttps))
        {
            return new PodcastFeedParseResult(
                false, null, Array.Empty<PodcastEpisode>(),
                AppLanguage.Get("S_PodcastInvalidUrl"));
        }

        var normalizedKey = initialUri.AbsoluteUri;
        if (!forceRefresh &&
            _cache.TryGetValue(normalizedKey, out var cached) &&
            cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return cached.Result;
        }

        if (!await _uriValidator(initialUri, ct).ConfigureAwait(false))
        {
            return new PodcastFeedParseResult(
                false, null, Array.Empty<PodcastEpisode>(),
                AppLanguage.T("该 RSS 地址被安全策略拒绝", "RSS URL blocked by security policy"));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            var xml = await DownloadWithSafeRedirectsAsync(initialUri, timeoutCts.Token).ConfigureAwait(false);
            if (xml == null)
            {
                return new PodcastFeedParseResult(
                    false, null, Array.Empty<PodcastEpisode>(),
                    AppLanguage.T("RSS 下载失败或响应异常", "Failed to download RSS feed"));
            }

            var parsed = ParseFeedXml(xml, normalizedKey);
            if (parsed.IsSuccess)
            {
                _cache[normalizedKey] = new CacheEntry(parsed, DateTimeOffset.UtcNow + CacheTtl);
            }
            return parsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return new PodcastFeedParseResult(false, null, Array.Empty<PodcastEpisode>(), ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch podcast RSS feed {Url}", SensitiveDataSanitizer.SanitizeUrlForLog(normalizedKey));
            return new PodcastFeedParseResult(
                false, null, Array.Empty<PodcastEpisode>(),
                AppLanguage.T("播客源拉取失败，请检查网络或地址", "Failed to fetch podcast feed; check network or URL"));
        }
    }

    private async Task<string?> DownloadWithSafeRedirectsAsync(Uri initialUri, CancellationToken ct)
    {
        var current = initialUri;
        for (var hop = 0; hop <= MaxRedirectHops; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/rss+xml, application/atom+xml, application/xml, text/xml;q=0.9, */*;q=0.8");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!await _uriValidator(next, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        AppLanguage.T("重定向目标被安全策略拒绝", "Redirect target blocked by security policy"));
                }
                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    AppLanguage.T($"服务器返回状态 {statusCode}", $"Server returned status {statusCode}"));
            }

            if (response.Content.Headers.ContentLength is > MaxFeedBytes)
            {
                throw new InvalidOperationException(
                    AppLanguage.T("播客 RSS 文件超过 5 MB 上限", "Podcast RSS feed exceeds 5 MB limit"));
            }

            return await ReadBoundedStringAsync(response.Content, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            AppLanguage.T("播客 RSS 重定向次数过多", "Too many RSS redirects"));
    }

    private static async Task<string> ReadBoundedStringAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > MaxFeedBytes)
            {
                throw new InvalidOperationException(
                    AppLanguage.T("播客 RSS 文件超过 5 MB 上限", "Podcast RSS feed exceeds 5 MB limit"));
            }
            buffer.Write(chunk, 0, read);
        }

        var encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }
        return encoding.GetString(buffer.ToArray());
    }

    /// <summary>
    /// 安全解析 RSS 2.0 或 Atom XML 文档（禁用 DTD 防 XXE）。供测试直接驱动。
    /// </summary>
    internal static PodcastFeedParseResult ParseFeedXml(string xmlContent, string feedUrl)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return new PodcastFeedParseResult(
                false, null, Array.Empty<PodcastEpisode>(),
                AppLanguage.T("播客 RSS 内容为空", "Podcast RSS content is empty"));
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1024
            };

            using var stringReader = new StringReader(xmlContent.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var doc = XDocument.Load(xmlReader, LoadOptions.None);
            var root = doc.Root;
            if (root == null)
            {
                return new PodcastFeedParseResult(
                    false, null, Array.Empty<PodcastEpisode>(),
                    AppLanguage.T("无效的 XML 根节点", "Invalid XML root element"));
            }

            if (string.Equals(root.Name.LocalName, "feed", StringComparison.OrdinalIgnoreCase))
                return ParseAtomFeed(root, feedUrl);

            var channel = root.Element("channel") ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "channel");
            if (channel == null)
            {
                return new PodcastFeedParseResult(
                    false, null, Array.Empty<PodcastEpisode>(),
                    AppLanguage.T("未识别的播客 RSS 格式（缺少 channel 节点）", "Unrecognized podcast RSS format (missing channel element)"));
            }

            return ParseRssChannel(channel, feedUrl);
        }
        catch (XmlException ex)
        {
            Log.Warning(ex, "Rejected or malformed podcast XML for {Url}", SensitiveDataSanitizer.SanitizeUrlForLog(feedUrl));
            return new PodcastFeedParseResult(
                false, null, Array.Empty<PodcastEpisode>(),
                AppLanguage.T("RSS XML 解析失败或包含不安全声明", "Failed to parse RSS XML or unsafe declaration rejected"));
        }
    }

    private static PodcastFeedParseResult ParseRssChannel(XElement channel, string feedUrl)
    {
        var feedTitle = StripHtml(channel.Element("title")?.Value);
        if (string.IsNullOrWhiteSpace(feedTitle))
            feedTitle = feedUrl;

        var feedAuthor = StripHtml(
            channel.Element(ItunesNs + "author")?.Value
            ?? channel.Element("author")?.Value
            ?? channel.Element("managingEditor")?.Value);

        var feedDesc = StripHtml(
            channel.Element(ItunesNs + "summary")?.Value
            ?? channel.Element("description")?.Value);

        var imageUrl = channel.Element(ItunesNs + "image")?.Attribute("href")?.Value?.Trim()
                       ?? channel.Element("image")?.Element("url")?.Value?.Trim()
                       ?? string.Empty;

        var category = channel.Element(ItunesNs + "category")?.Attribute("text")?.Value?.Trim()
                       ?? channel.Element("category")?.Value?.Trim()
                       ?? string.Empty;

        var rawLang = channel.Element("language")?.Value?.Trim();
        var language = string.IsNullOrWhiteSpace(rawLang)
            ? "zh"
            : rawLang.Split('-', '_')[0].ToLowerInvariant();

        var feed = new PodcastFeed
        {
            FeedId = PodcastFeed.CustomFeedPrefix + PodcastEpisode.BuildEpisodeKey(feedUrl, null, feedUrl),
            Title = feedTitle,
            Author = feedAuthor,
            Description = feedDesc,
            RssUrl = feedUrl,
            ImageUrl = imageUrl,
            Category = category,
            Language = language,
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };

        var episodes = new List<PodcastEpisode>();
        foreach (var item in channel.Elements("item"))
        {
            if (episodes.Count >= MaxEpisodesPerFeed)
                break;

            var enclosure = item.Elements("enclosure").FirstOrDefault(e =>
                IsAudioEnclosure(e.Attribute("url")?.Value, e.Attribute("type")?.Value));
            var audioUrl = enclosure?.Attribute("url")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(audioUrl) || !IsSafeBasicAudioUrl(audioUrl))
                continue;

            var title = StripHtml(item.Element("title")?.Value);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var guid = item.Element("guid")?.Value?.Trim();
            var summaryRaw = item.Element(ContentNs + "encoded")?.Value
                             ?? item.Element(ItunesNs + "summary")?.Value
                             ?? item.Element("description")?.Value
                             ?? string.Empty;
            var summary = TruncateSummary(StripHtml(summaryRaw));
            var author = StripHtml(item.Element(ItunesNs + "author")?.Value ?? item.Element("author")?.Value);
            if (string.IsNullOrWhiteSpace(author))
                author = feedAuthor;

            var duration = ParseDuration(item.Element(ItunesNs + "duration")?.Value);
            var pubDate = ParseDate(item.Element("pubDate")?.Value);
            var codec = InferCodec(audioUrl, enclosure?.Attribute("type")?.Value);

            episodes.Add(new PodcastEpisode
            {
                EpisodeKey = PodcastEpisode.BuildEpisodeKey(feedUrl, guid, audioUrl),
                FeedUrl = feedUrl,
                FeedTitle = feedTitle,
                Title = title,
                Author = author,
                AudioUrl = audioUrl,
                PublishedAt = pubDate,
                Duration = duration,
                Summary = summary,
                Codec = codec
            });
        }

        return new PodcastFeedParseResult(true, feed, episodes);
    }

    private static PodcastFeedParseResult ParseAtomFeed(XElement root, string feedUrl)
    {
        var ns = root.Name.Namespace == XNamespace.None ? AtomNs : root.Name.Namespace;
        var feedTitle = StripHtml(root.Element(ns + "title")?.Value);
        if (string.IsNullOrWhiteSpace(feedTitle))
            feedTitle = feedUrl;

        var feedAuthor = StripHtml(root.Element(ns + "author")?.Element(ns + "name")?.Value);
        var feedDesc = StripHtml(root.Element(ns + "subtitle")?.Value ?? root.Element(ns + "tagline")?.Value);
        var imageUrl = root.Element(ns + "logo")?.Value?.Trim()
                       ?? root.Element(ns + "icon")?.Value?.Trim()
                       ?? string.Empty;
        var rawLang = root.Attribute(XNamespace.Xml + "lang")?.Value?.Trim()
                      ?? root.Element(ns + "language")?.Value?.Trim();
        var language = string.IsNullOrWhiteSpace(rawLang)
            ? "zh"
            : rawLang.Split('-', '_')[0].ToLowerInvariant();

        var feed = new PodcastFeed
        {
            FeedId = PodcastFeed.CustomFeedPrefix + PodcastEpisode.BuildEpisodeKey(feedUrl, null, feedUrl),
            Title = feedTitle,
            Author = feedAuthor,
            Description = feedDesc,
            RssUrl = feedUrl,
            ImageUrl = imageUrl,
            Language = language,
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };

        var episodes = new List<PodcastEpisode>();
        foreach (var entry in root.Elements(ns + "entry"))
        {
            if (episodes.Count >= MaxEpisodesPerFeed)
                break;

            var link = entry.Elements(ns + "link").FirstOrDefault(l =>
                string.Equals(l.Attribute("rel")?.Value, "enclosure", StringComparison.OrdinalIgnoreCase) &&
                IsAudioEnclosure(l.Attribute("href")?.Value, l.Attribute("type")?.Value));
            var audioUrl = link?.Attribute("href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(audioUrl) || !IsSafeBasicAudioUrl(audioUrl))
                continue;

            var title = StripHtml(entry.Element(ns + "title")?.Value);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var id = entry.Element(ns + "id")?.Value?.Trim();
            var summaryRaw = entry.Element(ns + "content")?.Value
                             ?? entry.Element(ns + "summary")?.Value
                             ?? string.Empty;
            var summary = TruncateSummary(StripHtml(summaryRaw));
            var author = StripHtml(entry.Element(ns + "author")?.Element(ns + "name")?.Value);
            if (string.IsNullOrWhiteSpace(author))
                author = feedAuthor;
            var pubDate = ParseDate(entry.Element(ns + "published")?.Value ?? entry.Element(ns + "updated")?.Value);
            var duration = ParseDuration(entry.Element(ItunesNs + "duration")?.Value);
            var codec = InferCodec(audioUrl, link?.Attribute("type")?.Value);

            episodes.Add(new PodcastEpisode
            {
                EpisodeKey = PodcastEpisode.BuildEpisodeKey(feedUrl, id, audioUrl),
                FeedUrl = feedUrl,
                FeedTitle = feedTitle,
                Title = title,
                Author = author,
                AudioUrl = audioUrl,
                PublishedAt = pubDate,
                Duration = duration,
                Summary = summary,
                Codec = codec
            });
        }

        return new PodcastFeedParseResult(true, feed, episodes);
    }

    internal static bool IsAudioEnclosure(string? url, string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            var type = mimeType.Trim();
            if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("application/ogg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        var path = uri.AbsolutePath;
        return AudioExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeBasicAudioUrl(string audioUrl)
    {
        if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IPAddress.TryParse(uri.Host, out var ip) && IsHighRiskIp(ip))
            return false;
        return true;
    }

    private static bool IsHighRiskIp(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 127 || bytes[0] == 0)
                return true;
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
            if (bytes[0] >= 224)
                return true;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal;
    }

    internal static TimeSpan ParseDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.Zero;

        var trimmed = raw.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);

        var parts = trimmed.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) &&
            mins >= 0 && secs >= 0)
        {
            return TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs);
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hrs) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hMins) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hSecs) &&
            hrs >= 0 && hMins >= 0 && hSecs >= 0)
        {
            return new TimeSpan(hrs, hMins, hSecs);
        }

        return TimeSpan.Zero;
    }

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed;
        return null;
    }

    internal static string StripHtml(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var decoded = WebUtility.HtmlDecode(raw);
        var withoutTags = HtmlTagRegex.Replace(decoded, " ");
        withoutTags = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(withoutTags, " ").Trim();
    }

    private static string TruncateSummary(string text)
        => text.Length <= 4000 ? text : text[..4000].TrimEnd() + "...";

    private static string InferCodec(string url, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            var m = mimeType.ToLowerInvariant();
            if (m.Contains("mpeg") || m.Contains("mp3")) return "MP3";
            if (m.Contains("mp4") || m.Contains("m4a") || m.Contains("aac")) return "AAC";
            if (m.Contains("ogg")) return "OGG";
            if (m.Contains("opus")) return "OPUS";
        }
        var lower = url.ToLowerInvariant();
        if (lower.Contains(".m4a") || lower.Contains(".aac")) return "AAC";
        if (lower.Contains(".ogg")) return "OGG";
        if (lower.Contains(".opus")) return "OPUS";
        if (lower.Contains(".wav")) return "WAV";
        return "MP3";
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }
}
