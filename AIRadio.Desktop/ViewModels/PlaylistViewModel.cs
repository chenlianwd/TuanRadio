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
    private readonly IKugouPlaylistService? _kugouPlaylistService;
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
    private bool _isSearching;
    private Func<string>? _searchStatusFactory;
    private Func<string>? _kugouStatusFactory;
    private readonly IDisposable _selectedTrackSub;
    private readonly IDisposable _selectedKugouPlaylistSub;
    private readonly IDisposable _kugouFilterSub;
    private readonly IDisposable _selectedSyncedPlaylistSub;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _kugouGate = new(1, 1);
    private readonly HashSet<string> _favoriteIds = new();
    // 读取到旧版歌单时记录原版本：首次成功写入当前格式前保留一代备份，之后清零保证幂等
    private int? _pendingLegacyVersion;
    // 读取到未来版本歌单时置位：拒绝本会话内任何回写，防止未知字段被旧版格式静默删除
    private bool _futureFormatSkipped;
    private static readonly string DefaultPlaylistDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");

    /// <summary>应用真实使用的播放列表路径；仅生产组合根允许落在这里，测试必须显式传临时路径。</summary>
    public static readonly string DefaultPlaylistFile = Path.Combine(DefaultPlaylistDir, "playlist.json");

    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<Track> Favorites { get; } = new();
    public ObservableCollection<OnlineTrack> SearchResults { get; } = new();
    public ObservableCollection<KugouPlaylistInfo> KugouPlaylists { get; } = new();
    public ObservableCollection<OnlineTrack> KugouPlaylistTracks { get; } = new();
    public ObservableCollection<OnlineTrack> FilteredKugouPlaylistTracks { get; } = new();
    public ObservableCollection<SyncedPlaylistInfo> SyncedPlaylists { get; } = new();
    public ObservableCollection<Track> VisibleLibraryTracks { get; } = new();

    [Reactive] public Track? SelectedTrack { get; set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool IsSearching { get; set; }
    [Reactive] public bool HasSearchStatus { get; set; }
    [Reactive] public string SearchStatusMessage { get; set; } = string.Empty;
    [Reactive] public string SearchButtonText { get; set; } = AppLanguage.T("搜索", "Search");
    [Reactive] public KugouPlaylistInfo? SelectedKugouPlaylist { get; set; }
    [Reactive] public bool IsKugouLoading { get; set; }
    [Reactive] public bool HasKugouPlaylists { get; set; }
    [Reactive] public bool HasKugouPlaylistTracks { get; set; }
    [Reactive] public bool HasKugouStatus { get; set; }
    [Reactive] public string KugouStatusMessage { get; set; } = string.Empty;
    [Reactive] public OnlineTrack? SelectedKugouTrack { get; set; }
    [Reactive] public int KugouImportedCount { get; set; }
    [Reactive] public string KugouFilterText { get; set; } = string.Empty;
    [Reactive] public SyncedPlaylistInfo? SelectedSyncedPlaylist { get; set; }
    [Reactive] public bool HasSyncedPlaylists { get; set; }
    [Reactive] public int TabIndex { get; set; } // 0=列表, 1=收藏, 2=搜索, 3=节目单, 4=酷狗歌单

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
    public ReactiveCommand<Unit, Unit> RefreshKugouPlaylistsCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportKugouPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayKugouPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleKugouPlaylistCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> PlayKugouTrackCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> PlayKugouNextCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> AddKugouToQueueCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowAllLibraryTracksCommand { get; }

    public PlaylistViewModel(
        IAudioService audioService,
        IMusicSearchService musicSearchService,
        string? playlistFile = null,
        Func<string, string, Task>? writeAllTextAsync = null,
        IKugouPlaylistService? kugouPlaylistService = null)
    {
        _audioService = audioService;
        _musicSearchService = musicSearchService;
        _kugouPlaylistService = kugouPlaylistService;
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
            RemoveTrackFromSyncedPlaylists(track);
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        ClearPlaylistCommand = ReactiveCommand.Create(() =>
        {
            _audioService.ClearPlaylist();
            Tracks.Clear();
            // 收藏从属于播放列表：清空后重载时收藏本来就会随之消失，这里同步清理避免幽灵条目和脏持久化
            Favorites.Clear();
            _favoriteIds.Clear();
            SyncedPlaylists.Clear();
            SelectedSyncedPlaylist = null;
            HasSyncedPlaylists = false;
            ApplyLibraryPlaylistFilter();
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
        RefreshKugouPlaylistsCommand = ReactiveCommand.CreateFromTask(
            () => LoadKugouPlaylistsAsync(force: true));
        ImportKugouPlaylistCommand = ReactiveCommand.CreateFromTask(ImportSelectedKugouPlaylistAsync);
        PlayKugouPlaylistCommand = ReactiveCommand.Create(() => StartKugouPlayback(0, shuffle: false));
        ShuffleKugouPlaylistCommand = ReactiveCommand.Create(() => StartKugouPlayback(0, shuffle: true));
        PlayKugouTrackCommand = ReactiveCommand.Create<OnlineTrack>(track =>
        {
            var index = KugouPlaylistTracks.IndexOf(track);
            if (index >= 0) StartKugouPlayback(index, shuffle: false);
        });
        PlayKugouNextCommand = ReactiveCommand.Create<OnlineTrack>(track =>
        {
            _audioService.PlayNextInQueue(track.ToTrack(string.Empty));
            SetKugouStatus(() => AppLanguage.T($"《{track.Title}》将在下一首播放。", $"\"{track.Title}\" will play next."));
        });
        AddKugouToQueueCommand = ReactiveCommand.Create<OnlineTrack>(track =>
        {
            _audioService.AddToQueue(track.ToTrack(string.Empty));
            SetKugouStatus(() => AppLanguage.T($"已将《{track.Title}》加入队列末尾。", $"Added \"{track.Title}\" to the end of the queue."));
        });
        ShowAllLibraryTracksCommand = ReactiveCommand.Create(() => { SelectedSyncedPlaylist = null; });

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
            if (Tracks.Contains(track))
                _audioService.PlayTrack(track);
        });

        _selectedTrackSub = this.WhenAnyValue(x => x.SelectedTrack)
            .WhereNotNull()
            .Subscribe(track =>
            {
                if (Tracks.Contains(track))
                    _audioService.PlayTrack(track);
            });

        // 选择云端歌单后按需读取曲目；Switch 会取消仍在进行的上一个选择，避免旧响应覆盖新选择。
        _selectedKugouPlaylistSub = this.WhenAnyValue(x => x.SelectedKugouPlaylist)
            .WhereNotNull()
            .Select(playlist => Observable.FromAsync(cancellationToken =>
                LoadKugouPlaylistTracksAsync(playlist, cancellationToken)))
            .Switch()
            .Subscribe(_ => { }, ex => Log.Warning(ex, "Kugou playlist selection stream failed"));

        _kugouFilterSub = this.WhenAnyValue(x => x.KugouFilterText)
            .Throttle(TimeSpan.FromMilliseconds(120), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyKugouFilter());
        _selectedSyncedPlaylistSub = this.WhenAnyValue(x => x.SelectedSyncedPlaylist)
            .Subscribe(_ => ApplyLibraryPlaylistFilter());

        // Auto-save when tracks change (skip during initial load)
        Tracks.CollectionChanged += OnTracksChanged;

        _onLanguageChanged = () =>
        {
            foreach (var track in Tracks)
                track.RefreshLocalization();
            // OnlineTrack 无 INPC：搜索结果与酷狗曲目列表按新语言重绑（语言切换是低频操作）
            RebindOnlineTracks(SearchResults);
            RebindOnlineTracks(FilteredKugouPlaylistTracks);
            // 搜索进行中保留"搜索中..."文案，结束后由 finally 按新语言复位
            if (!IsSearching)
                SearchButtonText = AppLanguage.T("搜索", "Search");
            if (_searchStatusFactory != null)
                SearchStatusMessage = _searchStatusFactory();
            if (_kugouStatusFactory != null)
                KugouStatusMessage = _kugouStatusFactory();
        };
        AppLanguage.Changed += _onLanguageChanged;
    }

    /// <summary>清空并重添同一批 OnlineTrack：触发集合通知让行模板重新读取计算型显示属性。</summary>
    private static void RebindOnlineTracks(System.Collections.ObjectModel.ObservableCollection<OnlineTrack> collection)
    {
        if (collection.Count == 0)
            return;

        var snapshot = collection.ToList();
        collection.Clear();
        foreach (var item in snapshot)
            collection.Add(item);
    }

    private void OnTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        ApplyLibraryPlaylistFilter();
        if (Volatile.Read(ref _disposed) == 0 && !_isLoading)
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _isLoading = true;
        var loadCompleted = false;
        _futureFormatSkipped = false;
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
                // 本会话内随后的任何自动保存也必须拒绝，否则首个列表操作就会覆盖未来格式文件。
                Log.Warning(
                    "Playlist version {Version} is newer than supported version {CurrentVersion}; loading skipped and saves disabled for this session",
                    data.Version,
                    PlaylistData.CurrentVersion);
                _futureFormatSkipped = true;
                return;
            }

            // 旧格式在内存中一次性迁移为当前版本；首次成功回写前由 SaveAsync 留一代对应版本的备份。
            // 无 Version 字段的历史文件视为 v1。
            if (data.Version < PlaylistData.CurrentVersion)
                _pendingLegacyVersion = Math.Max(1, data.Version);

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
            SyncedPlaylists.Clear();

            foreach (var playlist in data.SyncedPlaylists ?? new List<SyncedPlaylistData>())
            {
                if (string.IsNullOrWhiteSpace(playlist.ProviderId) ||
                    string.IsNullOrWhiteSpace(playlist.RemoteId))
                    continue;
                SyncedPlaylists.Add(new SyncedPlaylistInfo
                {
                    Id = playlist.Id,
                    ProviderId = playlist.ProviderId,
                    RemoteId = playlist.RemoteId,
                    Name = playlist.Name,
                    TrackSourceIds = (playlist.TrackSourceIds ?? new List<string>())
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    LastSyncedAt = playlist.LastSyncedAt
                });
            }

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
                        ProviderMetadata = item.Provider?.Metadata == null
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(item.Provider.Metadata, StringComparer.OrdinalIgnoreCase),
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

            HasSyncedPlaylists = SyncedPlaylists.Count > 0;
            ApplyLibraryPlaylistFilter();

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
        await TrySaveAsync();
    }

    /// <summary>保存当前播放列表并返回是否真正落盘，供需要向用户反馈结果的操作使用。</summary>
    private async Task<bool> TrySaveAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var gateHeld = false;
        try
        {
            await _saveGate.WaitAsync(_lifetimeCts.Token);
            gateHeld = true;
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            // 加载到未来版本歌单的会话：任何回写都会把未知字段静默删掉，必须整会话拒绝保存
            if (_futureFormatSkipped)
            {
                Log.Warning("Playlist save skipped: file uses a newer format than this app supports");
                return false;
            }

            // 快照必须在保存 gate 内生成：SemaphoreSlim 不保证 FIFO 唤醒，
            // gate 外生成的快照可能以"旧数据后写"覆盖新一次保存
            var data = new PlaylistData
            {
                Version = PlaylistData.CurrentVersion,
                Tracks = Tracks.Select(ToPlaylistTrack).ToList(),
                FavoriteIds = _favoriteIds.ToList(),
                SyncedPlaylists = SyncedPlaylists.Select(playlist => new SyncedPlaylistData
                {
                    Id = playlist.Id,
                    ProviderId = playlist.ProviderId,
                    RemoteId = playlist.RemoteId,
                    Name = playlist.Name,
                    TrackSourceIds = playlist.TrackSourceIds.ToList(),
                    LastSyncedAt = playlist.LastSyncedAt
                }).ToList()
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
            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 关闭时取消排队保存，避免 Dispose 后继续写盘。
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save playlist");
            return false;
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
                TrackId = parsed?.TrackId ?? t.SourceId!,
                Metadata = new Dictionary<string, string>(t.ProviderMetadata, StringComparer.OrdinalIgnoreCase)
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
    /// 旧格式首次回写前保留一代原版本备份（供手动降级旧版本时恢复）。
    /// 只有备份成功后才清零标记；首选路径不可写时回退到带时间戳的备份名，
    /// 避免首选备份路径持续失败导致整个会话的保存被静默阻断。
    /// </summary>
    private void KeepLegacyBackupOrThrow()
    {
        if (_pendingLegacyVersion is not { } legacyVersion)
            return;

        if (File.Exists(_playlistFile))
        {
            var versionLabel = $"v{legacyVersion}";
            try
            {
                File.Copy(_playlistFile, $"{_playlistFile}.{versionLabel}.bak", overwrite: true);
            }
            catch (Exception ex)
            {
                var fallbackPath = $"{_playlistFile}.{versionLabel}.{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Copy(_playlistFile, fallbackPath, overwrite: true);
                Log.Warning(
                    ex,
                    "Primary {Version} backup path failed; wrote timestamped backup {Path}",
                    versionLabel,
                    fallbackPath);
            }
        }

        _pendingLegacyVersion = null;
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
            var existing = Tracks.FirstOrDefault(t => MatchesOnlineTrack(t, track));
            if (existing != null)
            {
                _audioService.PlayTrack(existing);
                TabIndex = 0;
                SetSearchStatus(() => AppLanguage.T($"正在播放《{track.Title}》。", $"Now playing \"{track.Title}\"."));
                return;
            }

            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
            _audioService.PlayTrack(t);
            TabIndex = 0;
            await SaveAsync();
            SetSearchStatus(() => AppLanguage.T($"正在播放《{track.Title}》。", $"Now playing \"{track.Title}\"."));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 应用关闭/生命周期取消不算播放失败，不弹失败文案
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

    private async Task<(List<OnlineTrack> Tracks, IReadOnlyList<Services.SourceSearchStatus> Report)> SearchMusicAsync(
        string keyword,
        int limit)
    {
        if (_musicSearchService is Services.MultiSourceMusicService multi)
        {
            // 用作用域报告：并发搜索（电台推荐/DJ 点歌）不会覆盖本次搜索的逐源状态
            var outcome = await multi.SearchWithReportAsync(keyword, limit, _lifetimeCts.Token);
            return (outcome.Tracks, outcome.Report);
        }

        var tracks = await _musicSearchService.SearchAsync(keyword, limit);
        return (tracks, Array.Empty<Services.SourceSearchStatus>());
    }

    private Task<string?> ResolvePlayUrlAsync(OnlineTrack track)
        => _musicSearchService is Services.MultiSourceMusicService multi
            ? multi.GetPlayUrlAsync(track, _lifetimeCts.Token)
            : _musicSearchService.GetPlayUrlAsync(track.Id);

    public async Task LoadKugouPlaylistsAsync(bool force = false)
    {
        if (_kugouPlaylistService == null)
        {
            SetKugouStatus(() => AppLanguage.T(
                "酷狗歌单服务未启用。",
                "Kugou playlists are not available."));
            return;
        }

        if (!_kugouPlaylistService.IsLoggedIn)
        {
            KugouPlaylists.Clear();
            KugouPlaylistTracks.Clear();
            FilteredKugouPlaylistTracks.Clear();
            SelectedKugouPlaylist = null;
            HasKugouPlaylists = false;
            HasKugouPlaylistTracks = false;
            SetKugouStatus(() => AppLanguage.T(
                "请先在设置的音源账号中登录酷狗。",
                "Sign in to Kugou under Music accounts in Settings first."));
            return;
        }

        if (!force && KugouPlaylists.Count > 0)
            return;

        var gateHeld = false;
        try
        {
            await _kugouGate.WaitAsync(_lifetimeCts.Token);
            gateHeld = true;
            IsKugouLoading = true;
            SetKugouStatus(() => AppLanguage.T("正在同步酷狗歌单...", "Syncing Kugou playlists..."));

            var playlists = await _kugouPlaylistService.GetUserPlaylistsAsync(_lifetimeCts.Token);
            SelectedKugouPlaylist = null;
            KugouPlaylistTracks.Clear();
            FilteredKugouPlaylistTracks.Clear();
            KugouPlaylists.Clear();
            foreach (var playlist in playlists)
                KugouPlaylists.Add(playlist);

            HasKugouPlaylists = KugouPlaylists.Count > 0;
            HasKugouPlaylistTracks = false;
            if (KugouPlaylists.Count == 0)
            {
                SetKugouStatus(() => AppLanguage.T(
                    "该酷狗账号暂时没有可同步的歌单。",
                    "This Kugou account has no playlists to sync."));
                return;
            }

            SetKugouStatus(() => AppLanguage.T(
                $"已同步 {KugouPlaylists.Count} 个酷狗歌单。",
                $"Synced {KugouPlaylists.Count} Kugou playlist(s)."));
            SelectedKugouPlaylist = KugouPlaylists[0];
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            // 服务内部 3 分钟总预算到点（非用户取消）：给出超时专属提示而不是通用失败
            SetKugouStatus(() => AppLanguage.T(
                "同步超时（超过 3 分钟总预算），请稍后重试。",
                "Sync timed out (3-minute budget). Please retry later."));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load Kugou playlists");
            SetKugouStatus(() => AppLanguage.T(
                "同步酷狗歌单失败，请检查登录状态和网络后重试。",
                "Couldn't sync Kugou playlists. Check the sign-in and network, then retry."));
        }
        finally
        {
            IsKugouLoading = false;
            if (gateHeld)
                _kugouGate.Release();
        }
    }

    private async Task LoadKugouPlaylistTracksAsync(
        KugouPlaylistInfo playlist,
        CancellationToken selectionCancellationToken)
    {
        if (_kugouPlaylistService == null)
            return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token,
            selectionCancellationToken);
        var cancellationToken = linkedCts.Token;
        var gateHeld = false;
        try
        {
            await _kugouGate.WaitAsync(cancellationToken);
            gateHeld = true;
            IsKugouLoading = true;
            KugouPlaylistTracks.Clear();
            FilteredKugouPlaylistTracks.Clear();
            HasKugouPlaylistTracks = false;
            SetKugouStatus(() => AppLanguage.T(
                $"正在读取《{playlist.Name}》...",
                $"Loading \"{playlist.Name}\"..."));

            var tracks = _kugouPlaylistService is IKugouPlaylistTrackPageLoader pageLoader
                ? await pageLoader.GetPlaylistTracksAsync(playlist.Id, playlist.TrackCount, cancellationToken)
                : await _kugouPlaylistService.GetPlaylistTracksAsync(playlist.Id, cancellationToken);
            if (!string.Equals(SelectedKugouPlaylist?.Id, playlist.Id, StringComparison.Ordinal))
                return;

            KugouPlaylistTracks.Clear();
            foreach (var track in tracks)
                KugouPlaylistTracks.Add(track);
            ApplyKugouFilter();
            RefreshKugouImportedCount();
            HasKugouPlaylistTracks = KugouPlaylistTracks.Count > 0;
            var unavailable = Math.Max(0, playlist.TrackCount - KugouPlaylistTracks.Count);
            SetKugouStatus(() => KugouPlaylistTracks.Count == 0
                ? AppLanguage.T("这个歌单里没有可导入的歌曲。", "This playlist has no importable tracks.")
                : AppLanguage.T(
                    unavailable == 0
                        ? $"《{playlist.Name}》共读取 {KugouPlaylistTracks.Count} 首，其中 {KugouImportedCount} 首已导入。"
                        : $"《{playlist.Name}》读取 {KugouPlaylistTracks.Count}/{playlist.TrackCount} 首，{unavailable} 首暂不可用，其中 {KugouImportedCount} 首已导入。",
                    unavailable == 0
                        ? $"Loaded {KugouPlaylistTracks.Count} track(s) from \"{playlist.Name}\"; {KugouImportedCount} already imported."
                        : $"Loaded {KugouPlaylistTracks.Count}/{playlist.TrackCount} from \"{playlist.Name}\"; {unavailable} unavailable, {KugouImportedCount} already imported."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            // 服务内部 3 分钟总预算到点（非用户切换/取消）：给出超时专属提示
            SetKugouStatus(() => AppLanguage.T(
                $"读取《{playlist.Name}》超时（超过 3 分钟总预算），请稍后重试。",
                $"Timed out loading \"{playlist.Name}\" (3-minute budget). Try again later."));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load Kugou playlist tracks for {PlaylistId}", playlist.Id);
            SetKugouStatus(() => AppLanguage.T(
                $"读取《{playlist.Name}》失败，请稍后重试。",
                $"Couldn't load \"{playlist.Name}\"; try again later."));
        }
        finally
        {
            IsKugouLoading = false;
            if (gateHeld)
                _kugouGate.Release();
        }
    }

    private async Task ImportSelectedKugouPlaylistAsync()
    {
        var playlist = SelectedKugouPlaylist;
        if (playlist == null || KugouPlaylistTracks.Count == 0)
        {
            SetKugouStatus(() => AppLanguage.T(
                "请先选择一个包含歌曲的酷狗歌单。",
                "Choose a Kugou playlist that contains tracks first."));
            return;
        }

        var gateHeld = false;
        try
        {
            await _kugouGate.WaitAsync(_lifetimeCts.Token);
            gateHeld = true;
            IsKugouLoading = true;
            var imported = new List<Track>();
            foreach (var online in KugouPlaylistTracks)
            {
                // 重新同步必须补齐修复前已导入曲目的音源参数；否则重复过滤会让存量曲目
                // 永远停留在只有 hash、没有 album_id/备用 hash 的不可播状态。
                var existingProviderTrack = Tracks.FirstOrDefault(track =>
                    !string.IsNullOrWhiteSpace(track.SourceId) &&
                    MusicIdentity.IsSameSource(track.SourceId, online.Id));
                if (existingProviderTrack != null)
                {
                    MergeProviderMetadata(existingProviderTrack, online);
                    continue;
                }

                if (!Tracks.Any(track => MatchesOnlineTrack(track, online)))
                    imported.Add(online.ToTrack(string.Empty));
            }
            var skipped = KugouPlaylistTracks.Count - imported.Count;

            if (imported.Count > 0)
            {
                // 批量导入只保存一次；在线地址保持为空，首次播放时由现有解析器按 kugou:hash 获取。
                Tracks.CollectionChanged -= OnTracksChanged;
                try
                {
                    foreach (var track in imported)
                        Tracks.Add(track);
                }
                finally
                {
                    Tracks.CollectionChanged += OnTracksChanged;
                }

                _audioService.AddTracks(imported);
            }

            UpsertSyncedKugouPlaylist(playlist, KugouPlaylistTracks);
            ApplyLibraryPlaylistFilter();

            // 即使本轮全部判为重复也要重试落盘：上一轮可能已经加入内存，但写盘失败。
            var saveSucceeded = await TrySaveAsync();
            if (!saveSucceeded)
            {
                if (_lifetimeCts.IsCancellationRequested)
                    return;

                SetKugouStatus(() => AppLanguage.T(
                    $"已导入 {imported.Count} 首到当前会话，但保存失败；请重试导入。",
                    $"Imported {imported.Count} track(s) for this session, but saving failed; retry the import."));
                return;
            }

            RefreshKugouImportedCount();

            SetKugouStatus(() => skipped == 0
                ? AppLanguage.T(
                    $"已将《{playlist.Name}》同步为本地歌单，新增 {imported.Count} 首。",
                    $"Synced \"{playlist.Name}\" as a local playlist; added {imported.Count} track(s).")
                : AppLanguage.T(
                    $"已同步本地歌单《{playlist.Name}》：新增 {imported.Count} 首，保留 {skipped} 首已有歌曲。",
                    $"Synced local playlist \"{playlist.Name}\": added {imported.Count}, kept {skipped} existing track(s)."));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to import Kugou playlist {PlaylistId}", playlist.Id);
            SetKugouStatus(() => AppLanguage.T(
                $"导入《{playlist.Name}》失败，请稍后重试。",
                $"Couldn't import \"{playlist.Name}\"; try again later."));
        }
        finally
        {
            IsKugouLoading = false;
            if (gateHeld)
                _kugouGate.Release();
        }
    }

    private void StartKugouPlayback(int startIndex, bool shuffle)
    {
        var playlist = SelectedKugouPlaylist;
        if (playlist == null || KugouPlaylistTracks.Count == 0) return;

        var tracks = KugouPlaylistTracks.Select(track => track.ToTrack(string.Empty)).ToList();
        _audioService.StartPlaybackContext(tracks, startIndex, shuffle, $"酷狗 · {playlist.Name}");
        var current = shuffle ? null : KugouPlaylistTracks[Math.Clamp(startIndex, 0, KugouPlaylistTracks.Count - 1)];
        SetKugouStatus(() => shuffle
            ? AppLanguage.T($"正在随机播放《{playlist.Name}》。", $"Shuffling \"{playlist.Name}\".")
            : AppLanguage.T($"正在播放《{current!.Title}》，后续按歌单顺序播放。", $"Playing \"{current!.Title}\" and continuing in playlist order."));
    }

    private void RefreshKugouImportedCount()
    {
        KugouImportedCount = KugouPlaylistTracks.Count(online =>
            Tracks.Any(track => MatchesOnlineTrack(track, online)));
    }

    private void UpsertSyncedKugouPlaylist(
        KugouPlaylistInfo remote,
        IEnumerable<OnlineTrack> remoteTracks)
    {
        var mapping = SyncedPlaylists.FirstOrDefault(item =>
            string.Equals(item.ProviderId, "kugou", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.RemoteId, remote.Id, StringComparison.Ordinal));
        if (mapping == null)
        {
            mapping = new SyncedPlaylistInfo
            {
                Id = $"kugou-playlist:{remote.Id}",
                ProviderId = "kugou",
                RemoteId = remote.Id
            };
            SyncedPlaylists.Add(mapping);
        }

        mapping.Name = remote.Name;
        var known = new HashSet<string>(mapping.TrackSourceIds, StringComparer.OrdinalIgnoreCase);
        foreach (var sourceId in remoteTracks.Select(track => track.Id))
        {
            if (known.Add(sourceId))
                mapping.TrackSourceIds.Add(sourceId);
        }
        mapping.LastSyncedAt = DateTimeOffset.UtcNow;
        mapping.RefreshTrackCount();
        HasSyncedPlaylists = SyncedPlaylists.Count > 0;
    }

    private static void MergeProviderMetadata(Track existing, OnlineTrack remote)
    {
        foreach (var (key, value) in remote.ProviderMetadata)
        {
            if (!string.IsNullOrWhiteSpace(value))
                existing.ProviderMetadata[key] = value;
        }
    }

    private void RemoveTrackFromSyncedPlaylists(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.SourceId)) return;
        foreach (var playlist in SyncedPlaylists)
        {
            if (playlist.TrackSourceIds.RemoveAll(id =>
                    string.Equals(id, track.SourceId, StringComparison.OrdinalIgnoreCase)) > 0)
                playlist.RefreshTrackCount();
        }
    }

    private void ApplyLibraryPlaylistFilter()
    {
        var sourceIds = SelectedSyncedPlaylist == null
            ? null
            : new HashSet<string>(SelectedSyncedPlaylist.TrackSourceIds, StringComparer.OrdinalIgnoreCase);
        VisibleLibraryTracks.Clear();
        foreach (var track in Tracks)
        {
            if (sourceIds == null ||
                (!string.IsNullOrWhiteSpace(track.SourceId) && sourceIds.Contains(track.SourceId)))
                VisibleLibraryTracks.Add(track);
        }
    }

    private void ApplyKugouFilter()
    {
        var keyword = KugouFilterText.Trim();
        FilteredKugouPlaylistTracks.Clear();
        foreach (var track in KugouPlaylistTracks)
        {
            if (keyword.Length == 0 ||
                track.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                track.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                track.Album.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                FilteredKugouPlaylistTracks.Add(track);
        }
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        // 防重入：双击并发搜索时后完成者会互相覆盖结果与状态
        if (_isSearching) return;

        _isSearching = true;
        IsSearching = true;
        SearchButtonText = AppLanguage.T("搜索中...", "Searching...");
        SetSearchStatus(() => AppLanguage.T($"正在搜索“{SearchText}”...", $"Searching \"{SearchText}\"..."));
        try
        {
            var (results, report) = await SearchMusicAsync(SearchText, 20);
            SearchResults.Clear();
            foreach (var track in results)
            {
                SearchResults.Add(track);
            }
            TabIndex = 2; // auto-switch to search results
            SetSearchStatus(() => BuildSearchStatusMessage(SearchResults.Count, report));
            Log.Information("Search '{Query}' returned {Count} results", SearchText, results.Count);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 应用关闭/生命周期取消不是业务失败，不弹误导性失败文案
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search failed");
            SetSearchStatus(() => AppLanguage.T("搜索失败，可能是网络异常或音乐 API 服务不可用。", "Search failed; check the network or the music API service."));
        }
        finally
        {
            _isSearching = false;
            IsSearching = false;
            SearchButtonText = AppLanguage.T("搜索", "Search");
        }
    }

    /// <summary>构造搜索状态消息：透传各音源成功/超时/失败（子项目 5）。</summary>
    private string BuildSearchStatusMessage(int totalCount, IReadOnlyList<Services.SourceSearchStatus> report)
    {
        if (report.Count == 0)
            return totalCount == 0
                ? AppLanguage.T(
                    "没有找到可用结果。可以换个关键词，或检查网络/音乐 API 服务。",
                    "No results found. Try another keyword or check the network / music API service.")
                : AppLanguage.T($"找到 {totalCount} 个结果。", $"Found {totalCount} result(s).");

        var perSource = string.Join(AppLanguage.T("；", "; "), report.Select(FormatSourceStatus));
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

    private void SetKugouStatus(Func<string> messageFactory)
    {
        _kugouStatusFactory = messageFactory;
        KugouStatusMessage = messageFactory();
        HasKugouStatus = !string.IsNullOrWhiteSpace(KugouStatusMessage);
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
        _selectedKugouPlaylistSub.Dispose();
        _kugouFilterSub.Dispose();
        _selectedSyncedPlaylistSub.Dispose();
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
    public const int CurrentVersion = 3;

    /// <summary>歌单格式版本；v1 文件没有该字段，反序列化后为 0。</summary>
    public int Version { get; set; }
    public List<PlaylistTrack> Tracks { get; set; } = new();
    public List<string> FavoriteIds { get; set; } = new();
    public List<SyncedPlaylistData> SyncedPlaylists { get; set; } = new();
}

public sealed class SyncedPlaylistInfo : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
            _name = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public List<string> TrackSourceIds { get; set; } = new();
    public DateTimeOffset LastSyncedAt { get; set; }
    public int TrackCount => TrackSourceIds.Count;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshTrackCount()
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TrackCount)));
}

internal sealed class SyncedPlaylistData
{
    public string Id { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> TrackSourceIds { get; set; } = new();
    public DateTimeOffset LastSyncedAt { get; set; }
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
    /// <summary>v1 读取兼容字段；v2 起写入只使用 Provider。</summary>
    public string? SourceId { get; set; }
    public bool IsOnline { get; set; }
    public bool IsFavorite { get; set; }
}

internal class PlaylistProviderRef
{
    public string ProviderId { get; set; } = "";
    public string TrackId { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
