using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// M3U / M3U8 / PLS 播放列表解析实现，内置编码容错、安全 URI 过滤、URL 去重与上限熔断保护。
/// </summary>
public sealed class PlaylistFileParser : IPlaylistFileParser
{
    public const int MaxImportStations = 500;

    static PlaylistFileParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<PlaylistParseResult> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new PlaylistParseResult([], 0, false);

        var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        var content = DecodeWithFallback(bytes);
        var defaultPrefix = Path.GetFileNameWithoutExtension(filePath);
        return ParseContent(content, defaultPrefix);
    }

    public PlaylistParseResult ParseContent(string content, string? defaultNamePrefix = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new PlaylistParseResult([], 0, false);

        var isPls = LooksLikePls(content);

        return isPls
            ? ParsePls(content, defaultNamePrefix)
            : ParseM3u(content, defaultNamePrefix);
    }

    private static bool LooksLikePls(string content)
    {
        using var reader = new StringReader(content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith(';'))
                continue;
            if (line.StartsWith('#'))
                return false;
            if (string.Equals(line, "[playlist]", StringComparison.OrdinalIgnoreCase))
                return true;

            var eq = line.IndexOf('=');
            if (eq > 4 && line[..eq].Trim().StartsWith("File", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[4..eq].Trim(), out _))
                return true;

            return false;
        }
        return false;
    }

    private static PlaylistParseResult ParseM3u(string content, string? defaultNamePrefix)
    {
        var stations = new List<RadioStation>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var truncated = false;
        string? pendingTitle = null;

        using var reader = new StringReader(content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pendingTitle = ExtractExtInfTitle(line);
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            if (!TryNormalizeHttpUrl(line, out var normalizedUri, out var dedupeKey))
            {
                skipped++;
                pendingTitle = null;
                continue;
            }

            if (!seenUrls.Add(dedupeKey))
            {
                skipped++;
                pendingTitle = null;
                continue;
            }

            if (stations.Count >= MaxImportStations)
            {
                truncated = true;
                break;
            }

            var name = !string.IsNullOrWhiteSpace(pendingTitle)
                ? pendingTitle
                : DeriveFallbackName(normalizedUri, defaultNamePrefix, stations.Count + 1);

            stations.Add(CreateStation(name, normalizedUri));
            pendingTitle = null;
        }

        return new PlaylistParseResult(stations, skipped, truncated);
    }

    private static PlaylistParseResult ParsePls(string content, string? defaultNamePrefix)
    {
        var entries = new SortedDictionary<int, (string? File, string? Title)>();
        var skipped = 0;

        using var reader = new StringReader(content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
                continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var key = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..].Trim();

            if (key.StartsWith("File", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(key[4..], out var fileIndex))
            {
                entries.TryGetValue(fileIndex, out var current);
                entries[fileIndex] = (value, current.Title);
            }
            else if (key.StartsWith("Title", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(key[5..], out var titleIndex))
            {
                entries.TryGetValue(titleIndex, out var current);
                entries[titleIndex] = (current.File, value);
            }
        }

        var stations = new List<RadioStation>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        foreach (var kv in entries)
        {
            var rawUrl = kv.Value.File;
            if (string.IsNullOrWhiteSpace(rawUrl) || !TryNormalizeHttpUrl(rawUrl, out var normalizedUri, out var dedupeKey))
            {
                if (!string.IsNullOrWhiteSpace(rawUrl))
                    skipped++;
                continue;
            }

            if (!seenUrls.Add(dedupeKey))
            {
                skipped++;
                continue;
            }

            if (stations.Count >= MaxImportStations)
            {
                truncated = true;
                break;
            }

            var title = !string.IsNullOrWhiteSpace(kv.Value.Title)
                ? kv.Value.Title.Trim()
                : DeriveFallbackName(normalizedUri, defaultNamePrefix, stations.Count + 1);

            stations.Add(CreateStation(title, normalizedUri));
        }

        return new PlaylistParseResult(stations, skipped, truncated);
    }

    private static string? ExtractExtInfTitle(string line)
    {
        var commaIndex = line.IndexOf(',');
        if (commaIndex < 0 || commaIndex == line.Length - 1)
            return null;

        var title = line[(commaIndex + 1)..].Trim();
        return title.Length > 0 ? title : null;
    }

    private static bool TryNormalizeHttpUrl(string raw, out Uri uri, out string dedupeKey)
    {
        uri = null!;
        dedupeKey = string.Empty;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;

        uri = parsed;
        dedupeKey = CustomRadioStorage.NormalizeStreamUrl(parsed.AbsoluteUri);
        return dedupeKey.Length > 0;
    }

    private static string DeriveFallbackName(Uri uri, string? prefix, int index)
    {
        var lastSeg = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : string.Empty;
        if (!string.IsNullOrWhiteSpace(lastSeg) && lastSeg.Length <= 40)
            return lastSeg;

        return string.IsNullOrWhiteSpace(prefix)
            ? $"{uri.Host} #{index}"
            : $"{prefix} #{index}";
    }

    private static RadioStation CreateStation(string name, Uri uri)
    {
        var codec = RadioStreamProbeService.InferCodecFromUrl(uri) ?? "MP3";
        return new RadioStation
        {
            StationUuid = RadioStation.CreateCustomUuid(),
            Name = name,
            StreamUrl = uri.AbsoluteUri,
            Tags = "Custom",
            Codec = codec,
            Bitrate = 0
        };
    }

    internal static string DecodeWithFallback(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 回退到系统默认 ANSI/GBK 编码（常见于 Windows 本地导出的 M3U）
            var fallback = Encoding.GetEncoding(0);
            return fallback.GetString(bytes);
        }
    }
}
