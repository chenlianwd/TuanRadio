using System;

namespace AIRadio.Desktop.Models;

/// <summary>
/// 播客 RSS 订阅源实体。
/// </summary>
public sealed class PodcastFeed
{
    public const string SourcePrefix = "podcast:";
    public const string CustomFeedPrefix = "custom-";

    public string FeedId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string RssUrl { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Language { get; init; } = "zh";
    public bool IsCurated { get; init; }
    public DateTimeOffset? LastUpdatedUtc { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplaySubTitle =>
        string.IsNullOrWhiteSpace(Author)
            ? Category
            : string.IsNullOrWhiteSpace(Category) ? Author : $"{Author} · {Category}";
}
