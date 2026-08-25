using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class PlaylistViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;
    private readonly string _playlistDir;
    private readonly string _playlistFile;
    private readonly Func<string, string, Task> _writeAllTextAsync;
    private readonly bool _customWriter;
    private readonly CancellationTokenSource _lifetimeCts = new();
    // 常驻按钮文案随语言切换重置；静态事件必须持委托在 Dispose 退订
    private readonly Action _onLanguageChanged;
    private int _disposed;
    private bool _isPlayingOnline;
    private bool _isLoading;
    private Func<string>? _searchStatusFactory;
    private readonly IDisposable _selectedTrackSub;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly HashSet<string> _favoriteIds = new();
    // 读取到 v1 歌单时置位：首次成功写入 v2 前保留一代旧格式备份，之后清零保证幂等
    private bool _pendingLegacyBackup;
    private static readonly string DefaultPlaylistDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");

    /// <summary>应用真实使用的播放列表路径；仅生产组合根允许落在这里，测试必须显式传临时路径。</summary>
    public static readonly string DefaultPlaylistFile = Path.Combine(DefaultPlaylistDir, "playlist.json");

    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<Track> Favorites { get; } = new();
    public ObservableCollection<OnlineTrack> SearchResults { get; } = new();

    [Reactive] public Track? SelectedTrack { get; set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool IsSearching { get; set; }
    [Reactive] public bool HasSearchStatus { get; set; }
    [Reactive] public string SearchStatusMessage { get; set; } = string.Empty;
    [Reactive] public string SearchButtonText { get; set; } = AppLanguage.T("搜索", "Search");
    [Reactive] public int TabIndex { get; set; } // 0=列表, 1=收藏, 2=搜索, 3=节目单

    public ReactiveCommand<Track, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFavoritesCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSearchCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> PlayOnlineCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> AddOnlineCommand { get; }
    public ReactiveCommand<Track, Unit> ToggleFavoriteCommand { get; }
    public ReactiveCommand<Track, Unit> PlayFavoriteCommand { get; }

    public PlaylistViewModel(
        IAudioService audioService,
        IMusicSearchService musicSearchService,
        string? playlistFile = null,
        Func<string, string, Task>? writeAllTextAsync = null)
    {
        _audioService = audioService;
        _musicSearchService = musicSearchService;
        _playlistFile = playlistFile ?? DefaultPlaylistFile;
        _playlistDir = Path.GetDirectoryName(_playlistFile) ?? DefaultPlaylistDir;
        _customWriter = writeAllTextAsync != null;
        _writeAllTextAsync = writeAllTextAsync ?? ((path, contents) => File.WriteAllTextAsync(path, contents));

        RemoveTrackCommand = ReactiveCommand.Create<Track>(track =>
        {
            _audioService.RemoveTrack(track);
            Tracks.Remove(track);
            // 同步收藏视图：删除的曲目留在 Favorites 里会成为不可播的幽灵条目
            if (Favorites.Contains(track))
                Favorites.Remove(track);
            _favoriteIds.Remove(track.Id);
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        ClearPlaylistCommand = ReactiveCommand.Create(() =>
        {
            _audioService.ClearPlaylist();
            Tracks.Clear();
            // 收藏从属于播放列表：清空后重载时收藏本来就会随之消失，这里同步清理避免幽灵条目和脏持久化
            Favorites.Clear();
            _favoriteIds.Clear();
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        ShowPlaylistCommand = ReactiveCommand.Create(() =>
        {
            TabIndex = 0;
        });

        ShowFavoritesCommand = ReactiveCommand.Create(() =>
        {
            TabIndex = 1;
        });

        ShowSearchCommand = ReactiveCommand.Create(() =>
        {
            TabIndex = 2;
        });

        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);

        PlayOnlineCommand = ReactiveCommand.CreateFromTask<OnlineTrack>(PlayOnlineAsync);

        AddOnlineCommand = ReactiveCommand.CreateFromTask<OnlineTrack>(async track =>
        {
            try
            {
                SetSearchStatus(() => AppLanguage.T($"正在添加《{track.Title}》...", $"Adding \"{track.Title}\"..."));
                var url = await ResolvePlayUrlAsync(track);
                if (url == null)
                {
                    SetSearchStatus(() => AppLanguage.T("这首歌暂时无法获取播放地址，换一个结果试试。", "Couldn't get a playable URL for this track; try another result."));
                    return;
                }

                // Check if already in playlist (stable identity, not the temporary URL)
                var existing = Tracks.FirstOrDefault(t => MatchesOnlineTrack(t, track));
                if (existing != null)
                {
                    SetSearchStatus(() => AppLanguage.T("这首歌已经在播放列表里了。", "Already in the playlist."));
                    return;
                }

                var t = track.ToTrack(url);
                Tracks.Add(t);
                _audioService.AddTracks(new[] { t });
                TabIndex = 0;
                await SaveAsync();
                SetSearchStatus(() => AppLanguage.T($"已添加《{track.Title}》。", $"Added \"{track.Title}\"."));
            }
            catch (OperationCanceledException)
            {
                // 关闭窗口等场景的取消，不需要用户可见的错误提示
            }
            catch (Exception ex)
            {
                // ReactiveCommand 异常若无订阅者会落入 RxApp.DefaultExceptionHandler 直接抛出
                Log.Warning(ex, "Failed to add online track {Title}", track.Title);
                SetSearchStatus(() => AppLanguage.T($"添加《{track.Title}》失败，请稍后重试。", $"Failed to add \"{track.Title}\"; try again later."));
            }
        });

        ToggleFavoriteCommand = ReactiveCommand.Create<Track>(track =>
        {
            var playlistTrack = FindMatchingTrack(track) ?? track;
            if (!Tracks.Contains(playlistTrack))
                Tracks.Add(playlistTrack);

            if (_favoriteIds.Contains(playlistTrack.Id))
            {
                _favoriteIds.Remove(playlistTrack.Id);
                playlistTrack.IsFavorite = false;
                Favorites.Remove(playlistTrack);
            }
            else
            {
                _favoriteIds.Add(playlistTrack.Id);
                playlistTrack.IsFavorite = true;
                if (!Favorites.Contains(playlistTrack))
                    Favorites.Add(playlistTrack);
            }
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        PlayFavoriteCommand = ReactiveCommand.Create<Track>(track =>
        {
            var index = Tracks.IndexOf(track);
            if (index >= 0)
                _audioService.PlayAtIndex(index);
        });

        _selectedTrackSub = this.WhenAnyValue(x => x.SelectedTrack)
            .WhereNotNull()
            .Subscribe(track =>
            {
                var index = Tracks.IndexOf(track);
                if (index >= 0)
                {
                    _audioService.PlayAtIndex(index);
                }
            });

        // Auto-save when tracks change (skip during initial load)
        Tracks.CollectionChanged += OnTracksChanged;

        _onLanguageChanged = () =>
        {
            foreach (var track in Tracks)
                track.RefreshLocalization();
            // 搜索进行中保留"搜索中..."文案，结束后由 finally 按新语言复位
            if (!IsSearching)
                SearchButtonText = AppLanguage.T("搜索", "Search");
            if (_searchStatusFactory != null)
                SearchStatusMessage = _searchStatusFactory();
        };
        AppLanguage.Changed += _onLanguageChanged;
    }

    private void OnTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_isLoading)
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _isLoading = true;
        var loadCompleted = false;
        Tracks.CollectionChanged -= OnTracksChanged;
        try
        {
            if (!File.Exists(_playlistFile))
            {
                loadCompleted = true;
                return;
            }

            var json = await File.ReadAllTextAsync(_playlistFile, cancellationToken);
            var data = JsonSerializer.Deserialize<PlaylistData>(json);
            if (data == null || data.Tracks == null)
                return;

            if (data.Version > PlaylistData.CurrentVersion)
            {
                // 未来格式不能由旧版应用降级回写，否则未知字段会被静默删除。
                Log.Warning(
                    "Playlist version {Version} is newer than supported version {CurrentVersion}; loading skipped without modifying the file",
                    data.Version,
                    PlaylistData.CurrentVersion);
                return;
            }

            // v1（无 Version 字段）在内存中一次性迁移为 v2；首次成功回写前由 SaveAsync 留一代备份
            if (data.Version < PlaylistData.CurrentVersion)
                _pendingLegacyBackup = true;

            _favoriteIds.Clear();
            if (data.FavoriteIds != null && data.FavoriteIds.Count > 0)
            {
                foreach (var id in data.FavoriteIds)
                    _favoriteIds.Add(id);
            }
            else
            {
                // Backward compat: load from legacy IsFavorite field
                foreach (var item in data.Tracks)
                    if (item.IsFavorite && !string.IsNullOrEmpty(item.Id))
                        _favoriteIds.Add(item.Id);
            }

            Tracks.Clear();
            Favorites.Clear();

            foreach (var item in data.Tracks)
            {
                var sourceId = ResolvePersistedSourceId(item);
                if (!string.IsNullOrEmpty(sourceId))
                {
                    // 在线曲目：磁盘上的 FilePath 可能是过期签名直链，读取时即丢弃，播放前懒解析
                    var track = new Track
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Artist = item.Artist,
                        Album = item.Album,
                        Duration = TimeSpan.FromMilliseconds(item.DurationMs),
                        FilePath = string.Empty,
                        SourceId = sourceId,
                        IsFavorite = _favoriteIds.Contains(item.Id) || item.IsFavorite
                    };
                    Tracks.Add(track);
                    if (track.IsFavorite)
                        Favorites.Add(track);
                }
                else if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    var track = new Track
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Artist = item.Artist,
                        Album = item.Album,
                        Duration = TimeSpan.FromMilliseconds(item.DurationMs),
                        FilePath = item.FilePath,
                        IsFavorite = _favoriteIds.Contains(item.Id) || item.IsFavorite
                    };
                    Tracks.Add(track);
                    if (track.IsFavorite)
                        Favorites.Add(track);
                }
            }

            if (Tracks.Count > 0)
            {
                _audioService.LoadTracks(Tracks);
                Log.Information("Loaded {Count} tracks from playlist", Tracks.Count);
            }
            loadCompleted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load playlist");
        }
        finally
        {
            _isLoading = false;
            Tracks.CollectionChanged += OnTracksChanged;
            if (loadCompleted && Volatile.Read(ref _disposed) == 0)
                _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted); // save once after load completes
        }
    }

    /// <summary>还原持久化的音源身份：v2 优先 Provider，v1 回退 SourceId 兼容字段。</summary>
    private static string? ResolvePersistedSourceId(PlaylistTrack item)
    {
        if (item.Provider != null && !string.IsNullOrEmpty(item.Provider.TrackId))
        {
            return string.IsNullOrEmpty(item.Provider.ProviderId)
                ? item.Provider.TrackId
                : $"{item.Provider.ProviderId}:{item.Provider.TrackId}";
        }

        return string.IsNullOrWhiteSpace(item.SourceId) ? null : item.SourceId;
    }

    internal async Task SaveAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var gateHeld = false;
        try
        {
            await _saveGate.WaitAsync(_lifetimeCts.Token);
            gateHeld = true;
            if (Volatile.Read(ref _disposed) != 0)
                return;

            // 快照必须在保存 gate 内生成：SemaphoreSlim 不保证 FIFO 唤醒，
            // gate 外生成的快照可能以"旧数据后写"覆盖新一次保存
            var data = new PlaylistData
            {
                Version = PlaylistData.CurrentVersion,
                Tracks = Tracks.Select(ToPlaylistTrack).ToList(),
                FavoriteIds = _favoriteIds.ToList()
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            Directory.CreateDirectory(_playlistDir);
            KeepLegacyBackupOrThrow();
            if (_customWriter)
            {
                await _writeAllTextAsync(_playlistFile, json);
            }
            else
            {
                // 先写同目录临时文件，再替换正式文件，避免应用退出/磁盘异常时留下半份 JSON。
                var tempPath = _playlistFile + ".tmp";
                await File.WriteAllTextAsync(tempPath, json, _lifetimeCts.Token);
                File.Move(tempPath, _playlistFile, overwrite: true);
            }

            Log.Debug("Playlist saved: {Count} tracks, {FavCount} favorites", data.Tracks.Count, data.FavoriteIds.Count);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 关闭时取消排队保存，避免 Dispose 后继续写盘。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save playlist");
        }
        finally
        {
            if (gateHeld)
                _saveGate.Release();
        }
    }

    private PlaylistTrack ToPlaylistTrack(Track t)
    {
        var isOnline = !string.IsNullOrWhiteSpace(t.SourceId);
        PlaylistProviderRef? provider = null;
        if (isOnline)
        {
            var parsed = ProviderTrackRef.FromSourceId(t.SourceId);
            provider = new PlaylistProviderRef
            {
                ProviderId = parsed?.ProviderId ?? string.Empty,
                TrackId = parsed?.TrackId ?? t.SourceId!
            };
        }

        return new PlaylistTrack
        {
            Id = t.Id,
            Provider = provider,
            Title = t.Title,
            Artist = t.Artist,
            Album = t.Album,
            DurationMs = (long)t.Duration.TotalMilliseconds,
            // 在线曲目不落盘临时直链：签名 URL 会过期且可能泄露凭据；本地曲目保存稳定路径
            FilePath = isOnline ? string.Empty : t.FilePath,
            IsOnline = isOnline,
            IsFavorite = _favoriteIds.Contains(t.Id)
        };
    }

    /// <summary>
    /// v1 → v2 首次回写前保留一代旧格式备份（供手动降级旧版本时恢复）。
    /// 只有备份成功后才清零标记；备份失败必须中止本次 v2 写入，避免失去降级恢复点。
    /// </summary>
    private void KeepLegacyBackupOrThrow()
    {
        if (!_pendingLegacyBackup)
            return;

        if (File.Exists(_playlistFile))
            File.Copy(_playlistFile, _playlistFile + ".v1.bak", overwrite: true);

        _pendingLegacyBackup = false;
    }

    private async Task PlayOnlineAsync(OnlineTrack track)
    {
        if (_isPlayingOnline) return;
        _isPlayingOnline = true;
        try
        {
            SetSearchStatus(() => AppLanguage.T($"正在播放《{track.Title}》...", $"Playing \"{track.Title}\"..."));
            var url = await ResolvePlayUrlAsync(track);
            if (url == null)
            {
                Log.Warning("No play URL for track {Id}", track.Id);
                SetSearchStatus(() => AppLanguage.T("这首歌暂时无法获取播放地址，换一个结果试试。", "Couldn't get a playable URL for this track; try another result."));
                return;
            }

            // Check if track already in playlist (stable identity, not the temporary URL)
            var existingIndex = Tracks.FindIndex(t => MatchesOnlineTrack(t, track));
            if (existingIndex >= 0)
            {
                _audioService.PlayAtIndex(existingIndex);
                TabIndex = 0;
                SetSearchStatus(() => AppLanguage.T($"正在播放《{track.Title}》。", $"Now playing \"{track.Title}\"."));
                return;
            }

            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
            var index = Tracks.Count - 1;
            _audioService.PlayAtIndex(index);
            TabIndex = 0;
            await SaveAsync();
            SetSearchStatus(() => AppLanguage.T($"正在播放《{track.Title}》。", $"Now playing \"{track.Title}\"."));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Play online failed for {Track}", track.Title);
            SetSearchStatus(() => AppLanguage.T("播放失败，可能是音源不可用或网络超时。", "Playback failed; the source may be unavailable or the network timed out."));
        }
        finally
        {
            _isPlayingOnline = false;
        }
    }

    /// <summary>
    /// 在线曲目重复判定：同源稳定身份（SourceId）或标准化同曲（跨源换源后仍视为同一首）。
    /// 临时 URL 刷新后会变化，不参与判定。
    /// </summary>
    internal static bool MatchesOnlineTrack(Track track, OnlineTrack online)
    {
        if (string.IsNullOrWhiteSpace(track.SourceId))
            return false;

        return MusicIdentity.IsSameSource(track.SourceId, online.Id) ||
               (MusicIdentity.IsSameSongLoose(track.Title, track.Artist, online.Title, online.Artist) &&
                AreDurationsCompatible(track.Duration, TimeSpan.FromMilliseconds(online.DurationMs)));
    }

    private Task<List<OnlineTrack>> SearchMusicAsync(string keyword, int limit)
        => _musicSearchService is Services.MultiSourceMusicService multi
            ? multi.SearchAsync(keyword, limit, _lifetimeCts.Token)
            : _musicSearchService.SearchAsync(keyword, limit);

    private Task<string?> ResolvePlayUrlAsync(OnlineTrack track)
        => _musicSearchService is Services.MultiSourceMusicService multi
            ? multi.GetPlayUrlAsync(track, _lifetimeCts.Token)
            : _musicSearchService.GetPlayUrlAsync(track.Id);

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;

        IsSearching = true;
        SearchButtonText = AppLanguage.T("搜索中...", "Searching...");
        SetSearchStatus(() => AppLanguage.T($"正在搜索“{SearchText}”...", $"Searching \"{SearchText}\"..."));
        try
        {
            var results = await SearchMusicAsync(SearchText, 20);
            SearchResults.Clear();
            foreach (var track in results)
            {
                SearchResults.Add(track);
            }
            TabIndex = 2; // auto-switch to search results
            SetSearchStatus(() => BuildSearchStatusMessage(SearchResults.Count));
            Log.Information("Search '{Query}' returned {Count} results", SearchText, results.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search failed");
            SetSearchStatus(() => AppLanguage.T("搜索失败，可能是网络异常或音乐 API 服务不可用。", "Search failed; check the network or the music API service."));
        }
        finally
        {
            IsSearching = false;
            SearchButtonText = AppLanguage.T("搜索", "Search");
        }
    }

    /// <summary>构造搜索状态消息：透传各音源成功/超时/失败（子项目 5）。</summary>
    private string BuildSearchStatusMessage(int totalCount)
    {
        if (_musicSearchService is not Services.MultiSourceMusicService multi || multi.LastSearchReport.Count == 0)
            return totalCount == 0
                ? AppLanguage.T(
                    "没有找到可用结果。可以换个关键词，或检查网络/音乐 API 服务。",
                    "No results found. Try another keyword or check the network / music API service.")
                : AppLanguage.T($"找到 {totalCount} 个结果。", $"Found {totalCount} result(s).");

        var perSource = string.Join(AppLanguage.T("；", "; "), multi.LastSearchReport.Select(FormatSourceStatus));
        return totalCount == 0
            ? AppLanguage.T($"未找到结果。{perSource}", $"No results. {perSource}")
            : AppLanguage.T($"找到 {totalCount} 个结果。{perSource}", $"Found {totalCount} result(s). {perSource}");
    }

    internal static string FormatSourceStatus(Services.SourceSearchStatus status) => status.Status switch
    {
        "ok" => string.IsNullOrEmpty(status.Note)
            ? AppLanguage.T($"{AppLanguage.MusicSourceName(status.Name)}成功{status.Count}条", $"{AppLanguage.MusicSourceName(status.Name)}: {status.Count} result(s)")
            : AppLanguage.T($"{AppLanguage.MusicSourceName(status.Name)}搜到{status.Count}条({status.Note})", $"{AppLanguage.MusicSourceName(status.Name)}: {status.Count} results ({status.Note})"),
        "timeout" => AppLanguage.T($"{AppLanguage.MusicSourceName(status.Name)}超时", $"{AppLanguage.MusicSourceName(status.Name)} timed out"),
        _ => AppLanguage.T($"{AppLanguage.MusicSourceName(status.Name)}失败:{status.Error}", $"{AppLanguage.MusicSourceName(status.Name)} failed: {status.Error}")
    };

    private void SetSearchStatus(Func<string> messageFactory)
    {
        _searchStatusFactory = messageFactory;
        SearchStatusMessage = messageFactory();
        HasSearchStatus = !string.IsNullOrWhiteSpace(SearchStatusMessage);
    }

    public void AddExternalTrack(Track track)
    {
        var existing = FindMatchingTrack(track);
        if (existing != null)
        {
            if (track.IsFavorite || _favoriteIds.Contains(existing.Id))
            {
                existing.IsFavorite = true;
                _favoriteIds.Add(existing.Id);
                if (!Favorites.Contains(existing))
                    Favorites.Add(existing);
                _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            }
            return;
        }

        Tracks.Add(track);
        _audioService.AddTracks(new[] { track });
        if (_favoriteIds.Contains(track.Id) && !Favorites.Contains(track))
            Favorites.Add(track);
        TabIndex = 0;
        _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    internal Track? FindMatchingTrack(Track track)
    {
        return Tracks.FirstOrDefault(t =>
            MusicIdentity.IsSameSource(t.SourceId, track.SourceId) ||
            (!string.IsNullOrWhiteSpace(track.FilePath) && t.FilePath == track.FilePath) ||
            (!string.IsNullOrWhiteSpace(track.Id) && t.Id == track.Id) ||
            (!string.IsNullOrWhiteSpace(t.SourceId) &&
             !string.IsNullOrWhiteSpace(track.SourceId) &&
             MusicIdentity.IsSameSongLoose(t.Title, t.Artist, track.Title, track.Artist) &&
             AreDurationsCompatible(t.Duration, track.Duration)));
    }

    private static bool AreDurationsCompatible(TimeSpan left, TimeSpan right)
    {
        if (left <= TimeSpan.Zero || right <= TimeSpan.Zero)
            return true;

        return Math.Abs((left - right).TotalSeconds) <= 8;
    }

    public void AddFiles(string[] filePaths)
    {
        var added = new List<Track>();
        foreach (var path in filePaths)
        {
            var track = Track.FromFile(path);
            if (Tracks.Any(t => t.FilePath == track.FilePath))
                continue;

            Tracks.Add(track);
            added.Add(track);
        }

        if (added.Count > 0)
            _audioService.AddTracks(added);

        TabIndex = 0;
        _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        _selectedTrackSub.Dispose();
        Tracks.CollectionChanged -= OnTracksChanged;
        AppLanguage.Changed -= _onLanguageChanged;
    }
}

// ObservableCollection lacks FindIndex; used by MainWindowViewModel and ChatViewModel
public static class ObservableCollectionExtensions
{
    public static int FindIndex<T>(this ObservableCollection<T> collection, Func<T, bool> predicate)
    {
        for (int i = 0; i < collection.Count; i++)
        {
            if (predicate(collection[i])) return i;
        }
        return -1;
    }
}

// DTOs for JSON deserialization — mutable setters required by JsonSerializer
internal class PlaylistData
{
    public const int CurrentVersion = 2;

    /// <summary>歌单格式版本；v1 文件没有该字段，反序列化后为 0。</summary>
    public int Version { get; set; }
    public List<PlaylistTrack> Tracks { get; set; } = new();
    public List<string> FavoriteIds { get; set; } = new();
}

internal class PlaylistTrack
{
    public string Id { get; set; } = "";
    public PlaylistProviderRef? Provider { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public long DurationMs { get; set; }
    /// <summary>本地曲目保存稳定文件路径；在线曲目恒为空（临时直链不落盘）。</summary>
    public string FilePath { get; set; } = "";
    /// <summary>v1 读取兼容字段；v2 写入只使用 Provider。</summary>
    public string? SourceId { get; set; }
    public bool IsOnline { get; set; }
    public bool IsFavorite { get; set; }
}

internal class PlaylistProviderRef
{
    public string ProviderId { get; set; } = "";
    public string TrackId { get; set; } = "";
}
