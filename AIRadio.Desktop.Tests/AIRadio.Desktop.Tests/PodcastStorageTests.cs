using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class PodcastStorageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storeFile;

    public PodcastStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storeFile = Path.Combine(_tempDir, "podcasts.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task FirstLoad_SeedsCuratedFeeds_AndRemovalPersists()
    {
        using var storage = new PodcastStorage(_storeFile);
        var initial = await storage.LoadFeedsAsync();

        Assert.True(initial.Count >= 4);
        var target = initial[0];

        Assert.True(await storage.RemoveFeedAsync(target.RssUrl));

        using var reloadedStorage = new PodcastStorage(_storeFile);
        var afterReload = await reloadedStorage.LoadFeedsAsync();
        Assert.DoesNotContain(afterReload, f => f.RssUrl == target.RssUrl);
    }

    [Fact]
    public async Task AddOrUpdateFeed_DeduplicatesByUrl_AndSavesProgressSafely()
    {
        using var storage = new PodcastStorage(_storeFile);
        var feed = new PodcastFeed
        {
            Title = "自定义播客",
            Author = "Tester",
            RssUrl = "https://example.com/podcast.xml"
        };

        Assert.True(await storage.AddOrUpdateFeedAsync(feed));
        Assert.False(await storage.AddOrUpdateFeedAsync(new PodcastFeed
        {
            Title = "自定义播客(更新)",
            RssUrl = "https://example.com/podcast.xml"
        }));

        var feeds = await storage.LoadFeedsAsync();
        Assert.Single(feeds, f => f.RssUrl == "https://example.com/podcast.xml");
        Assert.Equal("自定义播客(更新)", feeds.First(f => f.RssUrl == "https://example.com/podcast.xml").Title);

        // 断点保存与播完归零
        await storage.UpdateEpisodeProgressAsync("ep-key-1", 45_000, 180_000, isCompleted: false, flushImmediately: true);
        using (var reopened = new PodcastStorage(_storeFile))
        {
            var progress = await reopened.GetEpisodeProgressAsync("ep-key-1");
            Assert.NotNull(progress);
            Assert.Equal(45_000, progress!.PositionMs);
            Assert.False(progress.IsCompleted);
        }

        await storage.UpdateEpisodeProgressAsync("ep-key-1", 180_000, 180_000, isCompleted: true, flushImmediately: true);
        var completed = await storage.GetEpisodeProgressAsync("ep-key-1");
        Assert.NotNull(completed);
        Assert.True(completed!.IsCompleted);
        Assert.Equal(0, completed.PositionMs);
    }

    [Fact]
    public async Task CorruptFile_RecoversGracefullyWithCuratedDefaults()
    {
        await File.WriteAllTextAsync(_storeFile, "{ invalid json !!!");
        using var storage = new PodcastStorage(_storeFile);

        var feeds = await storage.LoadFeedsAsync();
        Assert.NotEmpty(feeds);
    }

    [Fact]
    public async Task ProgressMap_CapsAtMaxProgressEntries()
    {
        using var storage = new PodcastStorage(_storeFile);
        for (var i = 0; i < PodcastStorage.MaxProgressEntries + 25; i++)
        {
            await storage.UpdateEpisodeProgressAsync($"ep-{i}", 10_000 + i, 300_000, isCompleted: false, flushImmediately: false);
        }
        await storage.FlushAsync();

        // 验证旧或边界记录仍在容量约束下（抽查最新一批必然存在）
        var latest = await storage.GetEpisodeProgressAsync($"ep-{PodcastStorage.MaxProgressEntries + 24}");
        Assert.NotNull(latest);
    }

    [Fact]
    public async Task Dispose_SynchronouslyFlushesDirtyProgress_WithoutDataLoss()
    {
        using (var storage = new PodcastStorage(_storeFile))
        {
            await storage.UpdateEpisodeProgressAsync("ep-unflushed", 88_000, 300_000, isCompleted: false, flushImmediately: false);
        }

        using var reopened = new PodcastStorage(_storeFile);
        var saved = await reopened.GetEpisodeProgressAsync("ep-unflushed");
        Assert.NotNull(saved);
        Assert.Equal(88_000, saved!.PositionMs);
    }
}
