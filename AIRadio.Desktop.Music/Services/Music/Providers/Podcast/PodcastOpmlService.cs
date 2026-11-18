using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 播客 OPML 导入解析与 OPML 2.0 导出实现（禁 DTD/外部实体解析，限制文件体积）。
/// </summary>
public sealed class PodcastOpmlService : IPodcastOpmlService
{
    public const int MaxOpmlChars = 5 * 1024 * 1024;
    private const int MaxOutlines = 1000;

    public OpmlParseResult Parse(string opmlXml)
    {
        if (string.IsNullOrWhiteSpace(opmlXml))
        {
            return new OpmlParseResult(
                false,
                Array.Empty<OpmlFeedOutline>(),
                0,
                AppLanguage.T("OPML 文件内容为空", "OPML content is empty"));
        }

        if (opmlXml.Length > MaxOpmlChars)
        {
            return new OpmlParseResult(
                false,
                Array.Empty<OpmlFeedOutline>(),
                0,
                AppLanguage.T("OPML 文件超过 5 MB 大小上限", "OPML file exceeds 5 MB limit"));
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

            using var stringReader = new StringReader(opmlXml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var doc = XDocument.Load(xmlReader, LoadOptions.None);
            var root = doc.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "opml", StringComparison.OrdinalIgnoreCase))
            {
                return new OpmlParseResult(
                    false,
                    Array.Empty<OpmlFeedOutline>(),
                    0,
                    AppLanguage.T("未识别的 OPML 文件格式（缺少 opml 根节点）", "Unrecognized OPML format (missing opml root)"));
            }

            var body = root.Elements().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, "body", StringComparison.OrdinalIgnoreCase));
            if (body == null)
            {
                return new OpmlParseResult(
                    false,
                    Array.Empty<OpmlFeedOutline>(),
                    0,
                    AppLanguage.T("未识别的 OPML 文件格式（缺少 body 节点）", "Unrecognized OPML format (missing body element)"));
            }

            var feeds = new List<OpmlFeedOutline>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedInvalid = 0;
            var visitedNodes = 0;

            CollectOutlines(body, string.Empty, feeds, seenUrls, ref skippedInvalid, ref visitedNodes);
            return new OpmlParseResult(true, feeds, skippedInvalid);
        }
        catch (XmlException)
        {
            return new OpmlParseResult(
                false,
                Array.Empty<OpmlFeedOutline>(),
                0,
                AppLanguage.T("OPML XML 解析失败或包含不安全声明", "Failed to parse OPML XML or unsafe declaration rejected"));
        }
    }

    public string Export(IEnumerable<PodcastFeed> feeds, string? documentTitle = null)
    {
        ArgumentNullException.ThrowIfNull(feeds);

        var title = string.IsNullOrWhiteSpace(documentTitle) ? "TuanRadio Podcasts" : documentTitle.Trim();
        var body = new XElement("body");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var validFeeds = new List<PodcastFeed>();
        foreach (var feed in feeds)
        {
            if (feed == null || string.IsNullOrWhiteSpace(feed.RssUrl))
                continue;
            if (!Uri.TryCreate(feed.RssUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                continue;
            if (seen.Add(uri.AbsoluteUri))
                validFeeds.Add(feed);
        }

        var uncategorized = validFeeds.Where(f => string.IsNullOrWhiteSpace(f.Category));
        foreach (var feed in uncategorized)
            body.Add(CreateFeedOutline(feed));

        var grouped = validFeeds
            .Where(f => !string.IsNullOrWhiteSpace(f.Category))
            .GroupBy(f => f.Category.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var groupOutline = new XElement("outline",
                new XAttribute("text", group.Key),
                new XAttribute("title", group.Key));
            foreach (var feed in group)
                groupOutline.Add(CreateFeedOutline(feed));
            body.Add(groupOutline);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("opml",
                new XAttribute("version", "2.0"),
                new XElement("head",
                    new XElement("title", title),
                    new XElement("dateCreated", DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture))),
                body));

        using var writer = new Utf8StringWriter();
        doc.Save(writer, SaveOptions.None);
        return writer.ToString();
    }

    private static XElement CreateFeedOutline(PodcastFeed feed)
    {
        var label = string.IsNullOrWhiteSpace(feed.Title) ? feed.RssUrl.Trim() : feed.Title.Trim();
        return new XElement("outline",
            new XAttribute("type", "rss"),
            new XAttribute("text", label),
            new XAttribute("title", label),
            new XAttribute("xmlUrl", feed.RssUrl.Trim()));
    }

    private static void CollectOutlines(
        XElement parent,
        string currentCategory,
        List<OpmlFeedOutline> feeds,
        HashSet<string> seenUrls,
        ref int skippedInvalid,
        ref int visitedNodes)
    {
        foreach (var outline in parent.Elements().Where(e =>
                     string.Equals(e.Name.LocalName, "outline", StringComparison.OrdinalIgnoreCase)))
        {
            if (++visitedNodes > MaxOutlines)
                return;

            var xmlUrl = GetAttributeIgnoreCase(outline, "xmlUrl");
            var outlineType = GetAttributeIgnoreCase(outline, "type");
            if (string.IsNullOrWhiteSpace(xmlUrl) &&
                (string.Equals(outlineType, "rss", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(outlineType, "atom", StringComparison.OrdinalIgnoreCase)))
            {
                xmlUrl = GetAttributeIgnoreCase(outline, "url");
            }

            var outlineTitle = GetAttributeIgnoreCase(outline, "title");
            if (string.IsNullOrWhiteSpace(outlineTitle))
                outlineTitle = GetAttributeIgnoreCase(outline, "text");

            if (!string.IsNullOrWhiteSpace(xmlUrl))
            {
                if (Uri.TryCreate(xmlUrl.Trim(), UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    if (seenUrls.Add(uri.AbsoluteUri))
                    {
                        var title = string.IsNullOrWhiteSpace(outlineTitle) ? uri.Host : outlineTitle.Trim();
                        var htmlUrl = GetAttributeIgnoreCase(outline, "htmlUrl")?.Trim() ?? string.Empty;
                        feeds.Add(new OpmlFeedOutline(title, uri.AbsoluteUri, currentCategory, htmlUrl));
                    }
                }
                else
                {
                    skippedInvalid++;
                }
            }

            if (outline.HasElements)
            {
                var nextCategory = string.IsNullOrWhiteSpace(xmlUrl) && !string.IsNullOrWhiteSpace(outlineTitle)
                    ? outlineTitle.Trim()
                    : currentCategory;
                CollectOutlines(outline, nextCategory, feeds, seenUrls, ref skippedInvalid, ref visitedNodes);
            }
        }
    }

    private static string? GetAttributeIgnoreCase(XElement element, string name)
        => element.Attributes().FirstOrDefault(a =>
            string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }
}
