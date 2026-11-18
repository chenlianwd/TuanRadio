using System.Collections.Generic;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 从 OPML 文档解析出的播客订阅条目前置结构。
/// </summary>
public sealed record OpmlFeedOutline(
    string Title,
    string RssUrl,
    string Category = "",
    string HtmlUrl = "");

/// <summary>
/// OPML 解析结果。
/// </summary>
public sealed record OpmlParseResult(
    bool IsSuccess,
    IReadOnlyList<OpmlFeedOutline> Feeds,
    int SkippedInvalidCount = 0,
    string? ErrorMessage = null);

/// <summary>
/// 播客 OPML 1.0/2.0 导入解析与导出序列化服务（禁 DTD 防 XXE）。
/// </summary>
public interface IPodcastOpmlService
{
    OpmlParseResult Parse(string opmlXml);

    string Export(IEnumerable<PodcastFeed> feeds, string? documentTitle = null);
}
