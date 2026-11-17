using System;
using System.Collections.Generic;
using System.Linq;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 内置精选公开播客 RSS 源列表，用于首次启动初始化与零配置开箱收听。
/// </summary>
public static class CuratedPodcastRegistry
{
    private static readonly IReadOnlyList<PodcastFeed> StarterFeeds =
    [
        new PodcastFeed
        {
            FeedId = "curated-etw",
            Title = "声东击西",
            Author = "徐涛 / 声动活泼",
            Description = "通过跨界对话，探讨社会、科技与文化的连接。",
            RssUrl = "https://etw.fm/rss",
            Category = "文化 / 科技",
            Language = "zh",
            IsCurated = true
        },
        new PodcastFeed
        {
            FeedId = "curated-yitianshijie",
            Title = "一天世界",
            Author = "李如一",
            Description = "深入浅出的文化、设计与技术观察。",
            RssUrl = "https://yitianshijie.net/feed/audio.xml",
            Category = "人文 / 设计",
            Language = "zh",
            IsCurated = true
        },
        new PodcastFeed
        {
            FeedId = "curated-gcores",
            Title = "机核 GCORES",
            Author = "机核网",
            Description = "游戏、电影、设计与多元泛文化对谈节目。",
            RssUrl = "https://www.gcores.com/rss",
            Category = "游戏 / 文化",
            Language = "zh",
            IsCurated = true
        },
        new PodcastFeed
        {
            FeedId = "curated-syntax",
            Title = "Syntax",
            Author = "Wes Bos & Scott Tolinski",
            Description = "Tasty web development treats for full-stack engineers.",
            RssUrl = "https://feed.syntax.fm/rss",
            Category = "Technology",
            Language = "en",
            IsCurated = true
        },
        new PodcastFeed
        {
            FeedId = "curated-npr-news",
            Title = "NPR News Now",
            Author = "NPR",
            Description = "The latest news in five minutes, updated hourly.",
            RssUrl = "https://feeds.npr.org/500005/podcast.xml",
            Category = "News",
            Language = "en",
            IsCurated = true
        }
    ];

    public static IReadOnlyList<PodcastFeed> GetStarterFeeds()
        => StarterFeeds.Select(f => new PodcastFeed
        {
            FeedId = f.FeedId,
            Title = f.Title,
            Author = f.Author,
            Description = f.Description,
            RssUrl = f.RssUrl,
            ImageUrl = f.ImageUrl,
            Category = f.Category,
            Language = f.Language,
            IsCurated = f.IsCurated,
            LastUpdatedUtc = f.LastUpdatedUtc
        }).ToList();
}
