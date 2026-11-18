using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public sealed class PodcastOpmlServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalLanguage;

    public PodcastOpmlServiceTests()
    {
        _originalLanguage = AppLanguage.Current;
        AppLanguage.Apply("zh");
        _tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-opml-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        AppLanguage.Apply(_originalLanguage);
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Parse_ValidOpmlWithCategoriesAndDuplicates_ExtractsDeduplicatedFeeds()
    {
        const string opml = """
            <?xml version="1.0" encoding="utf-8"?>
            <opml version="2.0">
              <head><title>My Subscriptions</title></head>
              <body>
                <outline type="rss" text="Flat Feed" xmlUrl="https://example.com/flat.xml" />
                <outline text="Tech" title="Tech">
                  <outline type="rss" text="Tech Cast" title="Tech Cast Title" xmlUrl="https://example.com/tech.xml" htmlUrl="https://example.com" />
                  <outline type="rss" text="Duplicate Tech" xmlUrl="https://example.com/tech.xml" />
                  <outline type="rss" text="Unsafe Scheme" xmlUrl="ftp://example.com/bad.xml" />
                </outline>
              </body>
            </opml>
            """;

        var svc = new PodcastOpmlService();
        var result = svc.Parse(opml);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.SkippedInvalidCount);
        Assert.Equal(2, result.Feeds.Count);
        Assert.Equal("Flat Feed", result.Feeds[0].Title);
        Assert.Equal(string.Empty, result.Feeds[0].Category);
        Assert.Equal("Tech Cast Title", result.Feeds[1].Title);
        Assert.Equal("Tech", result.Feeds[1].Category);
        Assert.Equal("https://example.com", result.Feeds[1].HtmlUrl);
    }

    [Fact]
    public void Parse_DtdXxePayload_IsRejectedSafely()
    {
        const string xxeOpml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE opml [
              <!ENTITY secret SYSTEM "file:///etc/passwd">
            ]>
            <opml version="2.0">
              <body><outline type="rss" text="&secret;" xmlUrl="https://example.com/rss.xml" /></body>
            </opml>
            """;

        var svc = new PodcastOpmlService();
        var result = svc.Parse(xxeOpml);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Feeds);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public void Export_ThenParse_RoundTripsFeedsAndCategories()
    {
        var svc = new PodcastOpmlService();
        var feeds = new[]
        {
            new PodcastFeed { Title = "中文科技 & 杂谈", RssUrl = "https://example.com/zh.xml", Category = "科技" },
            new PodcastFeed { Title = "Uncategorized Cast", RssUrl = "https://example.com/plain.xml", Category = "" }
        };

        var exported = svc.Export(feeds, "TuanRadio Export");
        var parsed = svc.Parse(exported);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(2, parsed.Feeds.Count);
        Assert.Contains(parsed.Feeds, f => f.RssUrl == "https://example.com/plain.xml" && f.Category == string.Empty);
        Assert.Contains(parsed.Feeds, f => f.RssUrl == "https://example.com/zh.xml" && f.Title == "中文科技 & 杂谈" && f.Category == "科技");
    }

    [Fact]
    public async Task PodcastStorage_AddFeedsIfMissingAsync_InsertsOnlyMissingFeedsAtomically()
    {
        var storePath = Path.Combine(_tempDir, "podcasts.json");
        using var storage = new PodcastStorage(storePath);

        var initial = await storage.LoadFeedsAsync();
        var existing = initial[0];

        var added = await storage.AddFeedsIfMissingAsync(new[]
        {
            new PodcastFeed { Title = "Dup", RssUrl = existing.RssUrl },
            new PodcastFeed { Title = "New A", RssUrl = "https://example.com/a.xml", Category = "News" },
            new PodcastFeed { Title = "New A Dup", RssUrl = "https://example.com/a.xml" },
            new PodcastFeed { Title = "New B", RssUrl = "https://example.com/b.xml" }
        });

        Assert.Equal(2, added);
        var updated = await storage.LoadFeedsAsync();
        Assert.Equal(initial.Count + 2, updated.Count);
        Assert.Contains(updated, f => f.RssUrl == "https://example.com/a.xml" && f.Category == "News");
        Assert.Contains(updated, f => f.RssUrl == "https://example.com/b.xml");
    }

    [Fact]
    public async Task PlaylistViewModel_ImportAndExportOpml_FiltersSsrfAndPersistsValidFeeds()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var audioMock = new Mock<IAudioService>();
            audioMock.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
            audioMock.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
            audioMock.SetupGet(x => x.Playlist).Returns(Array.Empty<Track>());

            var rssMock = new Mock<IPodcastRssService>();
            rssMock.Setup(x => x.FetchFeedAsync("https://93.184.216.34/imported.xml", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PodcastFeedParseResult(
                    true,
                    new PodcastFeed { Title = "Imported Show", RssUrl = "https://93.184.216.34/imported.xml" },
                    Array.Empty<PodcastEpisode>()));

            using var storage = new PodcastStorage(Path.Combine(_tempDir, "podcasts.json"));
            var existing = (await storage.LoadFeedsAsync()).First();

            using var playlistVm = new PlaylistViewModel(
                audioMock.Object,
                new Mock<IMusicSearchService>().Object,
                playlistFile: Path.Combine(_tempDir, "playlist.json"),
                podcastRssService: rssMock.Object,
                podcastStorage: storage);

            var opmlPath = Path.Combine(_tempDir, "import.opml");
            var opmlXml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <opml version="2.0">
                  <body>
                    <outline type="rss" text="Existing" xmlUrl="{existing.RssUrl}" />
                    <outline type="rss" text="SSRF Loopback" xmlUrl="http://127.0.0.1:8080/internal.xml" />
                    <outline type="rss" text="Imported Show" xmlUrl="https://93.184.216.34/imported.xml" />
                  </body>
                </opml>
                """;
            await File.WriteAllTextAsync(opmlPath, opmlXml);

            await playlistVm.ImportOpmlFileAsync(opmlPath);

            Assert.False(playlistVm.PodcastOpmlIsWarning);
            Assert.Contains("1", playlistVm.PodcastOpmlStatusText);
            Assert.Contains(playlistVm.PodcastFeeds, f => f.RssUrl == "https://93.184.216.34/imported.xml");
            Assert.DoesNotContain(playlistVm.PodcastFeeds, f => f.RssUrl.Contains("127.0.0.1", StringComparison.Ordinal));

            var exportPath = Path.Combine(_tempDir, "export.opml");
            await playlistVm.ExportOpmlFileAsync(exportPath);

            Assert.True(File.Exists(exportPath));
            var exportedXml = await File.ReadAllTextAsync(exportPath);
            Assert.Contains("https://93.184.216.34/imported.xml", exportedXml);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
