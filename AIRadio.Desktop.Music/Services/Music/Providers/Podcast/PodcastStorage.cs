using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 播客订阅源与长音频断点进度本地 JSON 持久化（线程安全、原子写盘、节流防磨损、损坏自愈）。
/// </summary>
public sealed class PodcastStorage : IPodcastStorage, IDisposable
{
    internal const int MaxProgressEntries = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class PodcastStoreSnapshot
    {
        public int Version { get; set; } = 1;
        public bool InitializedCurated { get; set; }
        public List<PodcastFeed> Feeds { get; set; } = new();
        public Dictionary<string, PodcastEpisodeProgress> Progress { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;
    private PodcastStoreSnapshot? _cachedSnapshot;
    private bool _dirty;
    private DateTimeOffset _lastFlushUtc = DateTimeOffset.MinValue;

    public static string DefaultStorageFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIRadio",
        "podcasts.json");

    public PodcastStorage(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? DefaultStorageFile : filePath;
    }

    public async Task<IReadOnlyList<PodcastFeed>> LoadFeedsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);
            return store.Feeds.Select(CloneFeed).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> AddOrUpdateFeedAsync(PodcastFeed feed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var normalizedUrl = NormalizeRssUrl(feed.RssUrl);
        if (string.IsNullOrEmpty(normalizedUrl))
            return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);
            var existingIdx = store.Feeds.FindIndex(f =>
                string.Equals(NormalizeRssUrl(f.RssUrl), normalizedUrl, StringComparison.OrdinalIgnoreCase));

            var sanitized = new PodcastFeed
            {
                FeedId = string.IsNullOrWhiteSpace(feed.FeedId)
                    ? PodcastFeed.CustomFeedPrefix + PodcastEpisode.BuildEpisodeKey(normalizedUrl, null, normalizedUrl)
                    : feed.FeedId,
                Title = string.IsNullOrWhiteSpace(feed.Title) ? normalizedUrl : feed.Title.Trim(),
                Author = feed.Author?.Trim() ?? string.Empty,
                Description = feed.Description?.Trim() ?? string.Empty,
                RssUrl = normalizedUrl,
                ImageUrl = feed.ImageUrl?.Trim() ?? string.Empty,
                Category = feed.Category?.Trim() ?? string.Empty,
                Language = string.IsNullOrWhiteSpace(feed.Language) ? "zh" : feed.Language.Trim(),
                IsCurated = feed.IsCurated,
                LastUpdatedUtc = feed.LastUpdatedUtc ?? DateTimeOffset.UtcNow
            };

            var isNew = existingIdx < 0;
            if (isNew)
                store.Feeds.Insert(0, sanitized);
            else
                store.Feeds[existingIdx] = sanitized;

            await SaveStoreCoreAsync(store, ct).ConfigureAwait(false);
            return isNew;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveFeedAsync(string rssUrl, CancellationToken ct = default)
    {
        var normalizedUrl = NormalizeRssUrl(rssUrl);
        if (string.IsNullOrEmpty(normalizedUrl))
            return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);
            var removed = store.Feeds.RemoveAll(f =>
                string.Equals(NormalizeRssUrl(f.RssUrl), normalizedUrl, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return false;

            await SaveStoreCoreAsync(store, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PodcastEpisodeProgress?> GetEpisodeProgressAsync(string episodeKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(episodeKey))
            return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);
            return store.Progress.TryGetValue(episodeKey, out var progress) ? progress : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateEpisodeProgressAsync(
        string episodeKey,
        long positionMs,
        long durationMs,
        bool isCompleted,
        bool flushImmediately = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(episodeKey))
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var store = await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);
            var effectivePos = isCompleted ? 0L : Math.Max(0, positionMs);
            store.Progress[episodeKey] = new PodcastEpisodeProgress(
                effectivePos,
                Math.Max(0, durationMs),
                isCompleted,
                DateTimeOffset.UtcNow);

            TrimProgressIfNeeded(store);
            _dirty = true;

            var now = DateTimeOffset.UtcNow;
            if (flushImmediately || now - _lastFlushUtc >= FlushInterval)
            {
                await SaveStoreCoreAsync(store, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_dirty || _cachedSnapshot == null)
                return;
            await SaveStoreCoreAsync(_cachedSnapshot, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void FlushBlocking()
    {
        if (!_dirty || _cachedSnapshot == null)
            return;

        if (!_gate.Wait(TimeSpan.FromSeconds(2)))
            return;
        try
        {
            if (!_dirty || _cachedSnapshot == null)
                return;
            SaveStoreBlocking(_cachedSnapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SaveStoreBlocking(PodcastStoreSnapshot store)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(store, JsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true);
            _cachedSnapshot = store;
            _dirty = false;
            _lastFlushUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to synchronously persist podcast storage to {FilePath}", _filePath);
        }
    }

    private async Task<PodcastStoreSnapshot> EnsureLoadedCoreAsync(CancellationToken ct)
    {
        if (_cachedSnapshot != null)
            return _cachedSnapshot;

        if (!File.Exists(_filePath))
        {
            var initial = new PodcastStoreSnapshot
            {
                InitializedCurated = true,
                Feeds = CuratedPodcastRegistry.GetStarterFeeds().ToList()
            };
            await SaveStoreCoreAsync(initial, ct).ConfigureAwait(false);
            _cachedSnapshot = initial;
            return initial;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var loaded = JsonSerializer.Deserialize<PodcastStoreSnapshot>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.Feeds ??= new List<PodcastFeed>();
                    loaded.Progress = new Dictionary<string, PodcastEpisodeProgress>(
                        loaded.Progress ?? new Dictionary<string, PodcastEpisodeProgress>(),
                        StringComparer.OrdinalIgnoreCase);

                    if (!loaded.InitializedCurated && loaded.Feeds.Count == 0)
                    {
                        loaded.InitializedCurated = true;
                        loaded.Feeds = CuratedPodcastRegistry.GetStarterFeeds().ToList();
                        await SaveStoreCoreAsync(loaded, ct).ConfigureAwait(false);
                    }

                    _cachedSnapshot = loaded;
                    return loaded;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Corrupt podcast storage at {FilePath}; recovering defaults", _filePath);
        }

        var recovered = new PodcastStoreSnapshot
        {
            InitializedCurated = true,
            Feeds = CuratedPodcastRegistry.GetStarterFeeds().ToList()
        };
        await SaveStoreCoreAsync(recovered, ct).ConfigureAwait(false);
        _cachedSnapshot = recovered;
        return recovered;
    }

    private async Task SaveStoreCoreAsync(PodcastStoreSnapshot store, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(store, JsonOptions);
            await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
            File.Move(tmpPath, _filePath, overwrite: true);
            _cachedSnapshot = store;
            _dirty = false;
            _lastFlushUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist podcast storage to {FilePath}", _filePath);
        }
    }

    private static void TrimProgressIfNeeded(PodcastStoreSnapshot store)
    {
        if (store.Progress.Count <= MaxProgressEntries)
            return;

        var keysToRemove = store.Progress
            .OrderByDescending(kv => kv.Value.UpdatedAtUtc)
            .Skip(MaxProgressEntries)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in keysToRemove)
            store.Progress.Remove(key);
    }

    private static PodcastFeed CloneFeed(PodcastFeed feed) => new()
    {
        FeedId = feed.FeedId,
        Title = feed.Title,
        Author = feed.Author,
        Description = feed.Description,
        RssUrl = feed.RssUrl,
        ImageUrl = feed.ImageUrl,
        Category = feed.Category,
        Language = feed.Language,
        IsCurated = feed.IsCurated,
        LastUpdatedUtc = feed.LastUpdatedUtc
    };

    private static string NormalizeRssUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri
            : url.Trim();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            FlushBlocking();
        }
        catch
        {
            // 关闭阶段尽力落盘，不抛出阻止退出
        }
        _gate.Dispose();
    }
}
