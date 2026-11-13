using ReactiveUI;
using ReactiveUI.SourceGenerators;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
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

public partial class PlaylistViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;
    private readonly IKugouPlaylistService? _kugouPlaylistService;
    private readonly LocalLibraryProvider? _localLibrary;
    private readonly IRadioBrowserService? _radioBrowserService;
    private readonly ICustomRadioStorage? _customRadioStorage;
    private readonly IRadioStreamProbeService? _radioStreamProbeService;
    private readonly IPlaylistFileParser? _playlistFileParser;
    private IReadOnlyList<RadioStation> _customStationsCache = [];
    private bool _customStationsCacheLoaded;
    private string _currentCategoryKey = "lofi";
    private readonly string _playlistDir;
    private readonly string _playlistFile;
    private readonly Func<string, string, Task> _writeAllTextAsync;
    private readonly bool _customWriter;
    private readonly CancellationTokenSource _lifetimeCts = new();
    // 常驻按钮文案随语言切换重置；静态事件必须持委托在 Dispose 退订
    private readonly Action _onLanguageChanged;
    private readonly IDisposable _searchTextSub;
    private readonly IDisposable _libraryFilterSub;
    private readonly IDisposable _nowPlayingSub;
    private Track? _nowPlayingListTrack;
    private int _clearPlaylistArmed;
    private int _clearPlaylistArmVersion;
    private int _disposed;
    private bool _isLoading;
    private bool _isSearching;
    private CancellationTokenSource? _slowSearchCts;
    private long _searchGeneration;
    private Func<string>? _searchStatusFactory;
    private Func<string>? _kugouStatusFactory;
    private Func<string>? _localLibraryStatusFactory;
    private Func<string>? _radioStatusFactory;
    private readonly IDisposable _selectedKugouPlaylistSub;
    private readonly IDisposable _kugouFilterSub;
    private readonly IDisposable _selectedSyncedPlaylistSub;
    private readonly IDisposable _favoriteFilterSub;
    private readonly IDisposable _favoriteSortSub;
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
    public ObservableCollection<RadioCategoryItem> RadioCategories { get; } = new();
    public ObservableCollection<RadioStation> RadioStations { get; } = new();
    public ObservableCollection<Track> VisibleLibraryTracks { get; } = new();
    public ObservableCollection<Track> VisibleFavoriteTracks { get; } = new();
    public ObservableCollection<Track> FavoriteStations { get; } = new();
    public ObservableCollection<FavoriteSortOption> FavoriteSortOptions { get; } = new();
    /// <summary>主歌单多选结果（OneWayToSource 绑定 ListBox.SelectedItems，控件侧写入）。</summary>
    public ObservableCollection<Track> SelectedLibraryTracks { get; } = new();
    /// <summary>主列表内当前在播曲本体（宽松匹配落标）；列表无在播曲时为 null。定位按钮消费。</summary>
    public Track? NowPlayingListTrack => _nowPlayingListTrack;
    /// <summary>可见序与主序一致（未启用同步歌单视图且无过滤词）时才允许拖拽排序。</summary>
    public bool CanReorderLibrary
        => SelectedSyncedPlaylist == null && string.IsNullOrWhiteSpace(LibraryFilterText);

    [Reactive] public partial string SearchText { get; set; } = string.Empty;
    [Reactive] public partial string LibraryFilterText { get; set; } = string.Empty;
    [Reactive] public partial string ClearPlaylistButtonText { get; set; } = AppLanguage.T("清空歌单", "Clear playlist");
    [Reactive] public partial bool IsSearching { get; set; }
    [Reactive] public partial bool HasSearchStatus { get; set; }
    [Reactive] public partial string SearchStatusMessage { get; set; } = string.Empty;
    [Reactive] public partial string SearchButtonText { get; set; } = AppLanguage.T("搜索", "Search");
    [Reactive] public partial string LocalLibraryStatus { get; set; } = string.Empty;
    [Reactive] public partial bool IsLocalLibraryScanning { get; set; }
    [Reactive] public partial KugouPlaylistInfo? SelectedKugouPlaylist { get; set; }
    [Reactive] public partial bool IsKugouLoading { get; set; }
    [Reactive] public partial bool HasKugouPlaylists { get; set; }
    [Reactive] public partial bool HasKugouPlaylistTracks { get; set; }
    [Reactive] public partial bool HasKugouStatus { get; set; }
    [Reactive] public partial string KugouStatusMessage { get; set; } = string.Empty;
    [Reactive] public partial OnlineTrack? SelectedKugouTrack { get; set; }
    [Reactive] public partial int KugouImportedCount { get; set; }
    [Reactive] public partial string KugouFilterText { get; set; } = string.Empty;
    [Reactive] public partial SyncedPlaylistInfo? SelectedSyncedPlaylist { get; set; }
    [Reactive] public partial bool HasSyncedPlaylists { get; set; }
    [Reactive] public partial int TabIndex { get; set; } // 0=列表, 1=收藏, 2=搜索, 3=节目单, 4=酷狗歌单, 5=网络电台
    [Reactive] public partial RadioCategoryItem? SelectedRadioCategory { get; set; }
    [Reactive] public partial string RadioSearchText { get; set; } = string.Empty;
    [Reactive] public partial string RadioStatusMessage { get; set; } = string.Empty;
    [Reactive] public partial bool IsRadioLoading { get; set; }
    [Reactive] public partial bool HasRadioStations { get; set; }

    // 自定义电台内嵌添加/编辑/导入面板状态（独立生命周期，不受分类切换与语言重置影响）
    [Reactive] public partial bool IsCustomStationPanelOpen { get; set; }
    [Reactive] public partial bool IsCustomImportMode { get; set; }
    [Reactive] public partial bool IsEditingCustomStation { get; set; }
    [Reactive] public partial string? EditingCustomStationUuid { get; set; }
    [Reactive] public partial string CustomStationUrl { get; set; } = string.Empty;
    [Reactive] public partial string CustomStationName { get; set; } = string.Empty;
    [Reactive] public partial string CustomStationTags { get; set; } = string.Empty;
    [Reactive] public partial string CustomStationCodec { get; set; } = "MP3";
    [Reactive] public partial string CustomStationBitrateText { get; set; } = string.Empty;
    [Reactive] public partial bool IsProbingStream { get; set; }
    [Reactive] public partial string ProbeStatusText { get; set; } = string.Empty;
    [Reactive] public partial bool ProbeIsWarning { get; set; }
    [Reactive] public partial string ImportSummaryText { get; set; } = string.Empty;
    [Reactive] public partial bool HasImportPreview { get; set; }
    public ObservableCollection<RadioStation> ImportPreviewStations { get; } = new();
    [Reactive] public partial string FavoriteFilterText { get; set; } = string.Empty;
    [Reactive] public partial FavoriteSortOption? SelectedFavoriteSortOption { get; set; }
    [Reactive] public partial int FavoriteSubTabIndex { get; set; } // 0=歌曲, 1=电台
    [Reactive] public partial bool IsFavoriteSongsSubTab { get; set; } = true;
    [Reactive] public partial bool HasVisibleFavoriteTracks { get; set; }
    [Reactive] public partial bool HasFavoriteStations { get; set; }
    [Reactive] public partial string FavoriteSongStatusMessage { get; set; } = string.Empty;
    [Reactive] public partial string FavoriteStationStatusMessage { get; set; } = string.Empty;
    [Reactive] public partial string FavoriteSongsTabTitle { get; set; } = AppLanguage.T("歌曲 (0)", "Songs (0)");
    [Reactive] public partial string FavoriteStationsTabTitle { get; set; } = AppLanguage.T("电台 (0)", "Radio (0)");

    public ReactiveCommand<int, Unit> SetFavoriteSubTabCommand { get; }

    public ReactiveCommand<Unit, Unit> SearchRadioStationsCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshRadioStationsCommand { get; }
    public ReactiveCommand<RadioStation, Unit> PlayRadioStationCommand { get; }
    public ReactiveCommand<RadioStation, Unit> FavoriteRadioStationCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenAddCustomStationCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCustomStationPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToManualAddModeCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchToFileImportModeCommand { get; }
    public ReactiveCommand<Unit, Unit> ProbeCustomStreamCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCustomStationOnlyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAndPlayCustomStationCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmImportCustomStationsCommand { get; }
    public ReactiveCommand<RadioStation, Unit> EditCustomStationCommand { get; }
    public ReactiveCommand<RadioStation, Unit> DeleteCustomStationCommand { get; }

    public ReactiveCommand<Track, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveSelectedTracksCommand { get; }
    public ReactiveCommand<Unit, Unit> FavoriteSelectedTracksCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFavoritesCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSearchCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> PlayOnlineCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> AddOnlineCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> PlaySearchNextCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> AddSearchToQueueCommand { get; }
    public ReactiveCommand<Track, Unit> ToggleFavoriteCommand { get; }
    public ReactiveCommand<Track, Unit> PlayFavoriteCommand { get; }
    public ReactiveCommand<Track, Unit> PlayFromLibraryCommand { get; }
    public ReactiveCommand<Track, Unit> PlayTrackNextCommand { get; }
    public ReactiveCommand<Track, Unit> EnqueueTrackCommand { get; }
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
        IKugouPlaylistService? kugouPlaylistService = null,
        LocalLibraryProvider? localLibrary = null,
        IRadioBrowserService? radioBrowserService = null,
        ICustomRadioStorage? customRadioStorage = null,
        IRadioStreamProbeService? radioStreamProbeService = null,
        IPlaylistFileParser? playlistFileParser = null)
    {
        _audioService = audioService;
        _musicSearchService = musicSearchService;
        _kugouPlaylistService = kugouPlaylistService;
        _localLibrary = localLibrary;
        _radioBrowserService = radioBrowserService;
        _customRadioStorage = customRadioStorage;
        _radioStreamProbeService = radioStreamProbeService;
        _playlistFileParser = playlistFileParser;
        _playlistFile = playlistFile ?? DefaultPlaylistFile;
        _playlistDir = Path.GetDirectoryName(_playlistFile) ?? DefaultPlaylistDir;
        _customWriter = writeAllTextAsync != null;
        _writeAllTextAsync = writeAllTextAsync ?? ((path, contents) => File.WriteAllTextAsync(path, contents));

        RemoveTrackCommand = ReactiveCommand.Create<Track>(track =>
        {
            RemoveTrackCore(track);
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        RemoveSelectedTracksCommand = ReactiveCommand.Create(() =>
        {
            var selected = SelectedLibraryTracks.Where(Tracks.Contains).ToList();
            if (selected.Count == 0) return;
            foreach (var track in selected)
                RemoveTrackCore(track, refreshView: false);
            ApplyLibraryPlaylistFilter();
            SelectedLibraryTracks.Clear();
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            var removedCount = selected.Count;
        });

        FavoriteSelectedTracksCommand = ReactiveCommand.Create(() =>
        {
            var selected = SelectedLibraryTracks.Where(Tracks.Contains).ToList();
            var added = 0;
            foreach (var track in selected)
            {
                if (_favoriteIds.Contains(track.Id)) continue;
                _favoriteIds.Add(track.Id);
                track.IsFavorite = true;
                if (!Favorites.Contains(track))
                    Favorites.Add(track);
                ApplyFavoriteFilterAndSort();
                added++;
            }
            if (added > 0)
                _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            var addedCount = added;
            SetLocalLibraryStatus(addedCount > 0
                ? () => AppLanguage.T($"已收藏 {addedCount} 首。", $"Favorited {addedCount} track(s).")
                : () => AppLanguage.T("所选歌曲都已在收藏中。", "All selected tracks are already favorites."));
        });

        // 清空为破坏性操作（连带清收藏与同步歌单映射）：5 秒内再点一次确认，
        // 逐字复用 SettingsViewModel.ResetListenerProfile 的武装模式——命令体必须同步
        // （CreateFromTask 执行期间的再次 Execute 会被忽略，确认点击会失效）
        ClearPlaylistCommand = ReactiveCommand.Create(() =>
        {
            if (Interlocked.CompareExchange(ref _clearPlaylistArmed, 1, 0) != 0)
            {
                Interlocked.Increment(ref _clearPlaylistArmVersion);
                Interlocked.Exchange(ref _clearPlaylistArmed, 0);
                ClearPlaylistButtonText = AppLanguage.T("清空歌单", "Clear playlist");
                ClearPlaylistCore();
                return;
            }

            ClearPlaylistButtonText = AppLanguage.T("再次点击确认", "Click again to confirm");
            SetLocalLibraryStatus(() => AppLanguage.T(
                "将清空歌单、收藏与同步歌单，5 秒内再次点击确认。",
                "This clears the playlist, favorites and synced playlists. Click again within 5 seconds to confirm."));
            var version = Interlocked.Increment(ref _clearPlaylistArmVersion);
            // Timer 用默认调度器（测试环境 MainThreadScheduler 可能是 Immediate，dueTime 会被忽略），
            // 回调再切回 UI 线程；版本号防 stale 解除
            Observable.Timer(TimeSpan.FromSeconds(5))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    if (version != Volatile.Read(ref _clearPlaylistArmVersion)) return;
                    if (Interlocked.CompareExchange(ref _clearPlaylistArmed, 0, 1) == 1)
                        ClearPlaylistButtonText = AppLanguage.T("清空歌单", "Clear playlist");
                });
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

        // 同步命令：上下文化点播没有 await 窗口，无需异步重入守卫
        PlayOnlineCommand = ReactiveCommand.Create<OnlineTrack>(PlayOnline);

        PlaySearchNextCommand = ReactiveCommand.Create<OnlineTrack>(track =>
        {
            _audioService.PlayNextInQueue(track.ToTrack(string.Empty));
            SetSearchStatus(() => AppLanguage.T($"《{track.Title}》将在下一首播放。", $"\"{track.Title}\" will play next."));
        });
        AddSearchToQueueCommand = ReactiveCommand.Create<OnlineTrack>(track =>
        {
            _audioService.AddToQueue(track.ToTrack(string.Empty));
            SetSearchStatus(() => AppLanguage.T($"已将《{track.Title}》加入队列末尾。", $"Added \"{track.Title}\" to the end of the queue."));
        });

        AddOnlineCommand = ReactiveCommand.CreateFromTask<OnlineTrack>(async track =>
        {
            CancelSlowSearch();
            try
            {
                SetSearchStatus(() => AppLanguage.T($"正在添加《{track.Title}》...", $"Adding \"{track.Title}\"..."));
                var t = await ResolvePlayableTrackAsync(track);
                if (t == null)
                {
                    SetSearchStatus(() => AppLanguage.T("这首歌暂时无法获取播放地址，换一个结果试试。", "Couldn't get a playable URL for this track; try another result."));
                    return;
                }

                // Check if already in playlist (stable identity, not the temporary URL)
                var existing = Tracks.FirstOrDefault(existingTrack => MatchesOnlineTrack(existingTrack, track));
                if (existing != null)
                {
                    SetSearchStatus(() => AppLanguage.T("这首歌已经在播放列表里了。", "Already in the playlist."));
                    return;
                }

                Tracks.Add(t);
                _audioService.AddTracks(new[] { t });
                // 在播曲经此入库时 AddTracks 的合并不发 TrackChanged（仅歌单从空填充的
                // 首次通知例外，与订阅重算幂等），补发一次标记确保落标（同 AddExternalTrack）
                UpdateNowPlayingMarker(_audioService.CurrentTrack);
                TabIndex = 0;
                // 保存失败必须可见：与 ImportKugouPlaylistAsync 同口径，否则磁盘满/文件
                // 被占用时用户看到"已添加"，重启后静默丢失
                var saveSucceeded = await TrySaveAsync();
                SetSearchStatus(() => saveSucceeded
                    ? AppLanguage.T($"已添加《{track.Title}》。", $"Added \"{track.Title}\".")
                    : AppLanguage.T($"已添加《{track.Title}》到本会话，但保存失败；请重试添加。",
                        $"Added \"{track.Title}\" for this session, but saving failed; retry."));
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
            if (!playlistTrack.IsLiveStream && !Tracks.Contains(playlistTrack))
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
            ApplyFavoriteFilterAndSort();
            var stationInList = RadioStations.FirstOrDefault(s => (RadioStation.SourcePrefix + s.StationUuid) == playlistTrack.Id);
            if (stationInList != null)
                stationInList.IsFavorite = playlistTrack.IsFavorite;
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        PlayFavoriteCommand = ReactiveCommand.Create<Track>(track =>
        {
            if (track.IsLiveStream)
            {
                _audioService.PlayTrack(track);
                return;
            }

            var songsOnly = VisibleFavoriteTracks.Where(t => !t.IsLiveStream).ToList();
            if (songsOnly.Count == 0)
                songsOnly = Favorites.Where(t => !t.IsLiveStream).ToList();

            var startIndex = songsOnly.IndexOf(track);
            if (startIndex < 0)
                startIndex = 0;
            _audioService.StartPlaybackContext(songsOnly, startIndex, shuffle: false,
                AppLanguage.T("收藏", "Favorites"));
        });

        // 主歌单行内 ▶ 承接原"点行即播"（多选模式下点行=选中，不再触发播放）
        PlayFromLibraryCommand = ReactiveCommand.Create<Track>(track =>
        {
            if (Tracks.Contains(track))
                _audioService.PlayTrack(track);
        });

        // 下一首播/加队列：主歌单与收藏行共用（收藏曲必在主歌单内）
        PlayTrackNextCommand = ReactiveCommand.Create<Track>(track =>
        {
            _audioService.PlayNextInQueue(track);
            var nextTitle = track.Title;
            SetLocalLibraryStatus(() => AppLanguage.T($"《{nextTitle}》将在下一首播放。", $"\"{nextTitle}\" will play next."));
        });
        EnqueueTrackCommand = ReactiveCommand.Create<Track>(track =>
        {
            _audioService.AddToQueue(track);
            var queuedTitle = track.Title;
            SetLocalLibraryStatus(() => AppLanguage.T($"已将《{queuedTitle}》加入队列末尾。", $"Added \"{queuedTitle}\" to the end of the queue."));
        });

        // 在播标记：TrackChanged 可能从 LibVLC 事件线程触发，必须编组回 UI 线程再改 UI 态
        _nowPlayingSub = _audioService.TrackChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdateNowPlayingMarker);

        _libraryFilterSub = this.WhenAnyValue(x => x.LibraryFilterText)
            .Throttle(TimeSpan.FromMilliseconds(120), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyLibraryPlaylistFilter());

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

        SearchRadioStationsCommand = ReactiveCommand.CreateFromTask(SearchRadioStationsAsync);
        RefreshRadioStationsCommand = ReactiveCommand.CreateFromTask(() =>
        {
            var key = SelectedRadioCategory?.Key ?? "lofi";
            return LoadRadioStationsByCategoryAsync(key, force: true);
        });
        PlayRadioStationCommand = ReactiveCommand.Create<RadioStation>(PlayRadioStation);
        FavoriteRadioStationCommand = ReactiveCommand.Create<RadioStation>(FavoriteRadioStation);
        OpenAddCustomStationCommand = ReactiveCommand.Create(OpenAddCustomStationPanel);
        CloseCustomStationPanelCommand = ReactiveCommand.Create(CloseCustomStationPanel);
        SwitchToManualAddModeCommand = ReactiveCommand.Create(() => { IsCustomImportMode = false; });
        SwitchToFileImportModeCommand = ReactiveCommand.Create(() => { IsCustomImportMode = true; });
        ProbeCustomStreamCommand = ReactiveCommand.CreateFromTask(ProbeCustomStreamAsync);
        SaveCustomStationOnlyCommand = ReactiveCommand.CreateFromTask(() => SaveCustomStationAsync(playAfterSave: false));
        SaveAndPlayCustomStationCommand = ReactiveCommand.CreateFromTask(() => SaveCustomStationAsync(playAfterSave: true));
        ConfirmImportCustomStationsCommand = ReactiveCommand.CreateFromTask(ConfirmImportCustomStationsAsync);
        EditCustomStationCommand = ReactiveCommand.Create<RadioStation>(OpenEditCustomStationPanel);
        DeleteCustomStationCommand = ReactiveCommand.CreateFromTask<RadioStation>(DeleteCustomStationAsync);

        SetFavoriteSubTabCommand = ReactiveCommand.Create<int>(idx => FavoriteSubTabIndex = idx);
        this.WhenAnyValue(x => x.FavoriteSubTabIndex)
            .Subscribe(idx => IsFavoriteSongsSubTab = idx == 0);

        RebuildFavoriteSortOptions();

        _favoriteFilterSub = this.WhenAnyValue(x => x.FavoriteFilterText)
            .Throttle(TimeSpan.FromMilliseconds(120), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFavoriteFilterAndSort());

        _favoriteSortSub = this.WhenAnyValue(x => x.SelectedFavoriteSortOption)
            .Subscribe(_ => ApplyFavoriteFilterAndSort());

        PopulateRadioCategories();
        SelectedRadioCategory = RadioCategories.FirstOrDefault(c => c.Key == "lofi") ?? RadioCategories.FirstOrDefault();
        _currentCategoryKey = SelectedRadioCategory?.Key ?? "lofi";

        this.WhenAnyValue(x => x.SelectedRadioCategory)
            .WhereNotNull()
            .Skip(1)
            .Subscribe(category =>
            {
                if (category.Key == _currentCategoryKey)
                    return;
                _currentCategoryKey = category.Key;
                RadioSearchText = string.Empty;
                _ = LoadRadioStationsByCategoryAsync(category.Key);
            });

        // Auto-save when tracks change (skip during initial load)
        Tracks.CollectionChanged += OnTracksChanged;

        _onLanguageChanged = () =>
        {
            foreach (var track in Tracks)
                track.RefreshLocalization();
            foreach (var track in Favorites)
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
            ClearPlaylistButtonText = Volatile.Read(ref _clearPlaylistArmed) == 1
                ? AppLanguage.T("再次点击确认", "Click again to confirm")
                : AppLanguage.T("清空歌单", "Clear playlist");
            if (_localLibraryStatusFactory != null)
                LocalLibraryStatus = _localLibraryStatusFactory();
            if (_radioStatusFactory != null)
                RadioStatusMessage = _radioStatusFactory();
            PopulateRadioCategories();
            RebuildFavoriteSortOptions();
            ApplyFavoriteFilterAndSort();
        };
        AppLanguage.Changed += _onLanguageChanged;
        _searchTextSub = this.WhenAnyValue(x => x.SearchText).Skip(1)
            .Subscribe(_ =>
            {
                if (!_isSearching && Volatile.Read(ref _slowSearchCts) == null) return;
                CancelSlowSearch();
                SetSearchStatus(() => AppLanguage.T(
                    "搜索词已更改，请重新搜索。", "Query changed; search again."));
            });
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

    /// <summary>
    /// 多步状态清理期间抑制 Tracks 变更触发的自动保存，由调用方在清理完成后统一保存一次，
    /// 避免中间不一致快照（如已删曲目但收藏/同步歌单未清理）落盘。
    /// </summary>
    private void MutateWithoutAutosave(Action mutate)
    {
        // 加载期间 OnTracksChanged 已被 LoadAsync 摘除：这里不能再 += 回去，
        // 否则 LoadAsync 收尾再次 += 会造成双订阅（每次变更双倍过滤/保存）
        if (_isLoading)
        {
            mutate();
            return;
        }

        Tracks.CollectionChanged -= OnTracksChanged;
        try
        {
            mutate();
        }
        finally
        {
            Tracks.CollectionChanged += OnTracksChanged;
            ApplyFavoriteFilterAndSort();
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || _isLoading)
            return;

        _isLoading = true;
        var loadCompleted = false;
        _futureFormatSkipped = false;
        // 加载的 await 窗口内窗口已可见可交互：记录进入加载时的快照，
        // 应用文件数据后把窗口内新增的曲目/收藏合并回来，否则被 Clear 静默丢弃并固化
        var tracksAtLoadStart = Tracks.ToList();
        var favoriteIdsAtLoadStart = _favoriteIds.ToHashSet();
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

            // 必须在 Clear 之前捕获窗口内新增的收藏：Clear 之后差集恒为空，窗口内点收藏会被静默回滚
            var lateFavoriteIds = _favoriteIds.Where(id => !favoriteIdsAtLoadStart.Contains(id)).ToList();
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

            // 窗口内（读取/反序列化期间）新增的曲目：合并回放，避免丢更新（收藏差集已在 Clear 前捕获）
            var lateAddedTracks = Tracks.Where(t => !tracksAtLoadStart.Contains(t)).ToList();

            Tracks.Clear();
            Favorites.Clear();
        ApplyFavoriteFilterAndSort();
            SyncedPlaylists.Clear();
            // 旧实例全部换血：在播标记指向的实例即将失效，清除防幽灵高亮
            if (_nowPlayingListTrack != null)
            {
                _nowPlayingListTrack.IsNowPlaying = false;
                _nowPlayingListTrack = null;
            }

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
                    if (track.ProviderMetadata.TryGetValue("codec", out var codecVal) && !string.IsNullOrWhiteSpace(codecVal))
                        track.DeclaredCodec = codecVal.ToUpperInvariant();
                    if (track.ProviderMetadata.TryGetValue("bitrate", out var brVal) && int.TryParse(brVal, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var br) && br > 0)
                        track.DeclaredBitrateKbps = br;
                    if (track.IsLiveStream && track.ProviderMetadata.TryGetValue("stream_url", out var su) && !string.IsNullOrWhiteSpace(su))
                        track.FilePath = su;
                    // 仅存在于 legacy IsFavorite 字段的收藏要并入 _favoriteIds：保存侧只看它，
                    // 否则先显示为已收藏、首次保存即被静默清除（空 Id 与 legacy 回填路径同口径排除）
                    if (track.IsFavorite && !string.IsNullOrEmpty(track.Id))
                        _favoriteIds.Add(track.Id);
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
                    // 同上：legacy 收藏并入 _favoriteIds，保证显示与持久化同一数据源
                    if (track.IsFavorite && !string.IsNullOrEmpty(track.Id))
                        _favoriteIds.Add(track.Id);
                    Tracks.Add(track);
                    if (track.IsFavorite)
                        Favorites.Add(track);
                }
            }

            // 合并加载窗口内新增的曲目/收藏（按既有宽松口径查重，避免与文件数据重复入列）
            foreach (var late in lateAddedTracks)
            {
                if (FindMatchingTrack(late) != null)
                    continue;
                Tracks.Add(late);
                if (late.IsFavorite && !Favorites.Contains(late))
                    Favorites.Add(late);
            }
            foreach (var id in lateFavoriteIds)
                _favoriteIds.Add(id);

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
            ApplyFavoriteFilterAndSort();
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
            var json = await Task.Run(
                () => JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                _lifetimeCts.Token);

            // 快照已在 UI 线程生成（顺序性见上）；序列化与写盘移出 UI 线程——数千曲库
            // 或杀软扫描锁文件时，同步 IO 挂在 UI 线程上每次列表变更都会造成可感知卡顿
            var playlistDir = _playlistDir;
            var playlistFile = _playlistFile;
            if (_customWriter)
            {
                await _writeAllTextAsync(playlistFile, json);
            }
            else
            {
                // 先写同目录临时文件，再替换正式文件，避免应用退出/磁盘异常时留下半份 JSON。
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(playlistDir);
                    KeepLegacyBackupOrThrow();
                    var tempPath = playlistFile + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, playlistFile, overwrite: true);
                }, _lifetimeCts.Token);
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

    private void PlayOnline(OnlineTrack track)
    {
        CancelSlowSearch();

        // 搜索结果点播 = 会话级播放上下文（同酷狗歌单语义）：从点击项起连播整个结果列表，
        // 曲终按模式回落（列表循环在列表内循环、电台模式播完回归 DJ）。曲目以瞬态身份播放，
        // 不写主歌单不落盘；想留存用行内「+」或播放器 ❤️。空 FilePath 由播放核懒解析，
        // 不可播结果经自愈链自动跳到下一个结果。
        var startIndex = SearchResults.IndexOf(track);
        if (startIndex < 0)
        {
            Log.Debug("Play online track {Id} not found in current search results; starting from index 0", track.Id);
            startIndex = 0;
        }
        var tracks = SearchResults.Select(item => item.ToTrack(string.Empty)).ToList();
        if (tracks.Count == 0) return;

        _audioService.StartPlaybackContext(tracks, startIndex, shuffle: false,
            AppLanguage.T("搜索结果", "Search results"));
        SetSearchStatus(() => AppLanguage.T($"正在播放《{track.Title}》，后续按搜索结果顺序播放。",
            $"Now playing \"{track.Title}\"; continuing in search result order."));
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
        if (_musicSearchService is Services.Music.IMusicSourceBroker multi)
        {
            // 用作用域报告：并发搜索（电台推荐/DJ 点歌）不会覆盖本次搜索的逐源状态
            var outcome = await multi.SearchFastWithReportAsync(keyword, limit, _lifetimeCts.Token);
            return (outcome.Tracks, outcome.Report);
        }

        var tracks = await _musicSearchService.SearchAsync(keyword, limit, _lifetimeCts.Token);
        return (tracks, Array.Empty<Services.SourceSearchStatus>());
    }

    /// <summary>
    /// 解析并构造可播放 Track：broker 路径拿完整 ResolveTrackResult（首播即携带播放头/声明值，
    /// 非 broker 兜底只有 URL）。
    /// </summary>
    private async Task<Track?> ResolvePlayableTrackAsync(OnlineTrack track)
    {
        if (_musicSearchService is Services.Music.IMusicSourceBroker multi)
        {
            var resolution = await multi.ResolveTrackAsync(track, forceRefresh: false, _lifetimeCts.Token);
            return resolution == null ? null : track.ToTrack(resolution);
        }

        var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
        return string.IsNullOrWhiteSpace(url) ? null : track.ToTrack(url);
    }

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
            // 只有自己持门置位过才复位：取消路径（gateHeld=false）无条件复位会
            // 提前熄灭另一路仍在持门加载的指示器
            if (gateHeld)
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
            // 取消路径（gateHeld=false）不能无条件复位：会提前熄灭另一路仍在持门加载的指示器
            if (gateHeld)
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
            ApplyFavoriteFilterAndSort();
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
            // 取消路径（gateHeld=false）不能无条件复位：会提前熄灭另一路仍在持门加载的指示器
            if (gateHeld)
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
        var keyword = LibraryFilterText.Trim();
        VisibleLibraryTracks.Clear();
        foreach (var track in Tracks)
        {
            if (sourceIds != null &&
                (string.IsNullOrWhiteSpace(track.SourceId) || !sourceIds.Contains(track.SourceId)))
                continue;
            if (keyword.Length > 0 &&
                !track.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !track.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !track.Album.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;
            VisibleLibraryTracks.Add(track);
        }
    }

    /// <summary>移除单曲的完整清理（列表/收藏/同步歌单/收藏 id 与可见视图），不含落盘；单删/批删共用。</summary>
    private void RemoveTrackCore(Track track, bool refreshView = true)
    {
        _audioService.RemoveTrack(track);
        // 抑制 Tracks.Remove 触发的自动保存：此刻 _favoriteIds/SyncedPlaylists 尚未清理，
        // 中间快照若恰逢崩溃/第二次保存失败会残留孤儿数据；由调用方统一保存
        MutateWithoutAutosave(() =>
        {
            Tracks.Remove(track);
            // 同步收藏视图：删除的曲目留在 Favorites 里会成为不可播的幽灵条目
            if (Favorites.Contains(track))
                Favorites.Remove(track);
        });
        if (track.IsNowPlaying)
        {
            track.IsNowPlaying = false;
            if (ReferenceEquals(_nowPlayingListTrack, track))
                _nowPlayingListTrack = null;
        }
        _favoriteIds.Remove(track.Id);
        RemoveTrackFromSyncedPlaylists(track);
        // 批量路径逐曲重建可见列表是 O(n²)：由调用方收尾统一重建一次
        if (refreshView)
            ApplyLibraryPlaylistFilter();
    }

    private void ClearPlaylistCore()
    {
        _audioService.ClearPlaylist();
        MutateWithoutAutosave(Tracks.Clear);
        // 收藏从属于播放列表：清空后重载时收藏本来就会随之消失，这里同步清理避免幽灵条目和脏持久化
        Favorites.Clear();
        ApplyFavoriteFilterAndSort();
        _favoriteIds.Clear();
        SyncedPlaylists.Clear();
        SelectedSyncedPlaylist = null;
        HasSyncedPlaylists = false;
        SelectedLibraryTracks.Clear();
        ApplyLibraryPlaylistFilter();
        SetLocalLibraryStatus(() => AppLanguage.T("已清空歌单。", "Playlist cleared."));
        _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 在播标记维护：在播实例可能是瞬态副本/换源实例，经 FindMatchingTrack 宽松匹配
    /// 落到主歌单内的本体（与 MainWindowViewModel 心形推送同款手法）。
    /// </summary>
    private void UpdateNowPlayingMarker(Track? track)
    {
        if (_nowPlayingListTrack != null)
            _nowPlayingListTrack.IsNowPlaying = false;
        _nowPlayingListTrack = null;
        if (track == null) return;

        var match = FindMatchingTrack(track);
        if (match != null)
        {
            match.IsNowPlaying = true;
            _nowPlayingListTrack = match;
        }
    }

    /// <summary>
    /// 拖拽排序入口：索引为可见序坐标（仅在 CanReorderLibrary 时可见序=主序），
    /// newIndex 为移除拖拽项后的插入位（与 AudioService.MoveTrack 的最终位语义一致）。
    /// </summary>
    public void MoveVisibleTrack(int oldIndex, int newIndex)
    {
        if (!CanReorderLibrary) return;
        if (oldIndex < 0 || oldIndex >= Tracks.Count ||
            newIndex < 0 || newIndex >= Tracks.Count ||
            oldIndex == newIndex)
            return;

        MutateWithoutAutosave(() =>
        {
            var track = Tracks[oldIndex];
            Tracks.RemoveAt(oldIndex);
            Tracks.Insert(newIndex, track);
        });
        _audioService.MoveTrack(oldIndex, newIndex);
        ApplyLibraryPlaylistFilter();
        _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>复制到剪贴板的曲目信息文本（歌名 - 歌手；歌手缺省时仅歌名）。</summary>
    internal static string FormatTrackInfo(string title, string? artist)
        => string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";

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

        CancelSlowSearch();
        var generation = Interlocked.Read(ref _searchGeneration);
        var query = SearchText.Trim();

        _isSearching = true;
        IsSearching = true;
        SearchButtonText = AppLanguage.T("搜索中...", "Searching...");
        SetSearchStatus(() => AppLanguage.T($"正在搜索“{query}”...", $"Searching \"{query}\"..."));
        try
        {
            var (results, report) = await SearchMusicAsync(query, 20);
            if (generation != Interlocked.Read(ref _searchGeneration) || Volatile.Read(ref _disposed) != 0)
                return;
            SearchResults.Clear();
            foreach (var track in results)
            {
                SearchResults.Add(track);
            }
            TabIndex = 2; // auto-switch to search results
            if (results.Count == 0 && _musicSearchService is Services.Music.IMusicSourceBroker broker)
            {
                SetSearchStatus(() => AppLanguage.T(
                    $"快速音源未找到“{query}”，正在后台尝试慢源。",
                    $"No fast-source results for \"{query}\"; checking slow sources in the background."));
                var slowCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                Interlocked.Exchange(ref _slowSearchCts, slowCts);
                _ = SearchSlowAndPublishAsync(broker, query, 20, generation, report, slowCts);
            }
            else
                SetSearchStatus(() => BuildSearchStatusMessage(SearchResults.Count, report));
            Log.Information("Search '{Query}' returned {Count} fast result(s)", query, results.Count);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 应用关闭/生命周期取消不是业务失败，不弹误导性失败文案
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search failed");
            if (generation == Interlocked.Read(ref _searchGeneration) && Volatile.Read(ref _disposed) == 0)
                SetSearchStatus(() => AppLanguage.T("搜索失败，可能是网络异常或音乐 API 服务不可用。", "Search failed; check the network or the music API service."));
        }
        finally
        {
            _isSearching = false;
            IsSearching = false;
            SearchButtonText = AppLanguage.T("搜索", "Search");
        }
    }

    private async Task SearchSlowAndPublishAsync(Services.Music.IMusicSourceBroker broker,
        string query, int limit, long generation,
        IReadOnlyList<Services.SourceSearchStatus> fastReport, CancellationTokenSource source)
    {
        try
        {
            var outcome = await broker.SearchSlowWithReportAsync(query, limit, source.Token);
            if (source.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 ||
                generation != Interlocked.Read(ref _searchGeneration) ||
                !string.Equals(SearchText.Trim(), query, StringComparison.Ordinal))
                return;
            SearchResults.Clear();
            foreach (var track in outcome.Tracks)
                SearchResults.Add(track);
            var combinedReport = fastReport.Concat(outcome.Report).ToArray();
            SetSearchStatus(() => BuildSearchStatusMessage(outcome.Tracks.Count, combinedReport));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Slow music search failed");
            if (generation == Interlocked.Read(ref _searchGeneration) && Volatile.Read(ref _disposed) == 0)
                SetSearchStatus(() => AppLanguage.T(
                    "慢源搜索失败，请稍后重试。", "Slow-source search failed; try again later."));
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _slowSearchCts, null, source), source))
                source.Dispose();
        }
    }

    private void CancelSlowSearch()
    {
        Interlocked.Increment(ref _searchGeneration);
        var previous = Interlocked.Exchange(ref _slowSearchCts, null);
        if (previous == null) return;
        previous.Cancel();
        previous.Dispose();
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

    internal static string FormatSourceStatus(Services.SourceSearchStatus status)
    {
        var name = AppLanguage.MusicSourceName(status.Name);
        return status.Status switch
        {
            "ok" => LocalizeSourceDetail(status.Note) is { Length: > 0 } note
                ? AppLanguage.T($"{name}搜到{status.Count}条({note})", $"{name}: {status.Count} results ({note})")
                : AppLanguage.T($"{name}成功{status.Count}条", $"{name}: {status.Count} result(s)"),
            "timeout" => AppLanguage.T($"{name}超时", $"{name} timed out"),
            "disabled" => LocalizeSourceDetail(status.Note ?? status.Error) is { Length: > 0 } reason
                ? AppLanguage.T($"{name}已停用（{reason}）", $"{name} disabled ({reason})")
                : AppLanguage.T($"{name}已停用", $"{name} disabled"),
            _ => FormatFailedSourceStatus(status)
        };
    }

    private static string? LocalizeSourceDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return detail;
        return detail switch
        {
            "已在设置中停用" or "Disabled in settings"
                => AppLanguage.T("已在设置中停用", "Disabled in settings"),
            "尚未配置服务器" or "Server not configured"
                => AppLanguage.T("尚未配置服务器", "Server not configured"),
            "可播性检查超时，已跳过" or "playability check timed out, skipped"
                => AppLanguage.T("可播性检查超时，已跳过", "playability check timed out, skipped"),
            "试听或失效片段，已过滤" or "preview or stale clips only, filtered"
                => AppLanguage.T("试听或失效片段，已过滤", "preview or stale clips only, filtered"),
            _ when TryExtractCircuitSeconds(detail, out var seconds)
                => AppLanguage.T($"连续接口故障，暂停请求 {seconds} 秒", $"circuit open for {seconds}s after repeated transport failures"),
            _ => detail
        };
    }

    private static bool TryExtractCircuitSeconds(string detail, out string seconds)
    {
        const string zhPrefix = "连续接口故障，暂停请求 ";
        const string zhSuffix = " 秒";
        if (detail.StartsWith(zhPrefix, StringComparison.Ordinal) && detail.EndsWith(zhSuffix, StringComparison.Ordinal))
        {
            seconds = detail[zhPrefix.Length..^zhSuffix.Length];
            return seconds.Length > 0;
        }
        const string enPrefix = "circuit open for ";
        const string enSuffix = "s after repeated transport failures";
        if (detail.StartsWith(enPrefix, StringComparison.Ordinal) && detail.EndsWith(enSuffix, StringComparison.Ordinal))
        {
            seconds = detail[enPrefix.Length..^enSuffix.Length];
            return seconds.Length > 0;
        }
        seconds = string.Empty;
        return false;
    }

    /// <summary>
    /// failed 状态按结构化分类渲染用户可读文案与恢复建议；
    /// None（非业务失败）与 Unknown（未标注业务异常）回退到原始错误文本。
    /// AuthExpired 同时覆盖"酷狗登录态失效"（扫码可解）与"网易 code≠200"（常见为本地
    /// 代理未就绪，扫码无效），文案必须取两类场景皆不误导的措辞。
    /// </summary>
    private static string FormatFailedSourceStatus(Services.SourceSearchStatus status)
    {
        var name = AppLanguage.MusicSourceName(status.Name);
        if (string.Equals(status.Name, "OpenSubsonic", StringComparison.OrdinalIgnoreCase) &&
            status.FailureKind is MusicSourceFailureKind.NotSignedIn or MusicSourceFailureKind.AuthExpired)
            return AppLanguage.T(
                $"{name}认证失败，请到设置检查私有曲库地址和账号",
                $"{name} authentication failed; check the private library server and account in Settings");
        return status.FailureKind switch
        {
            MusicSourceFailureKind.NotSignedIn => AppLanguage.T(
                $"{name}未登录，请到设置的音源账号扫码登录",
                $"{name} is not signed in; scan the QR code under Music accounts in Settings"),
            MusicSourceFailureKind.AuthExpired => AppLanguage.T(
                $"{name}登录态或本地服务异常，请到设置检查音源账号后重试",
                $"{name} sign-in or local service issue; check the music account in Settings and retry"),
            MusicSourceFailureKind.RiskControl => AppLanguage.T(
                $"{name}触发风控验证，请到设置点击「滑块验证」；播放时会尝试自动弹出验证页（约 10 分钟内不重复弹）",
                $"{name} triggered a captcha check; use \"Captcha verify\" in Settings. Playback will try to open the verification page automatically (at most once every ~10 minutes)"),
            MusicSourceFailureKind.ApiBroken => AppLanguage.T(
                $"{name}接口暂时不可用，已跳过",
                $"{name} is temporarily unavailable; skipped"),
            _ => AppLanguage.T($"{name}失败:{status.Error}", $"{name} failed: {status.Error}")
        };
    }

    /// <summary>写搜索状态行并登记工厂委托（语言切换时按新语言重算，code-behind 复制反馈共用）。</summary>
    internal void SetSearchStatus(Func<string> messageFactory)
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
        // 瞬态在播曲经此转正：AddTracks 合并替换不发 TrackChanged，补发一次标记防整首播着却不高亮
        UpdateNowPlayingMarker(_audioService.CurrentTrack);
        if (_favoriteIds.Contains(track.Id) && !Favorites.Contains(track))
            Favorites.Add(track);
        TabIndex = 0;
        _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    internal Track? FindMatchingTrack(Track track)
    {
        // 引用优先（同 AudioService.FindTrackIndex 语义）：库内存在同曲多份时定位到
        // 用户实际点/在播的那一份，而非按 SourceId/宽松匹配撞上的第一份
        var byReference = Tracks.FirstOrDefault(t => ReferenceEquals(t, track));
        if (byReference != null)
            return byReference;

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
        var added = AddFilesCore(filePaths);
        _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        if (_localLibrary != null && added.Count > 0)
            _ = _localLibrary.AddFilesAsync(added.Select(track => track.FilePath))
                .ContinueWith(t => Log.Warning(t.Exception, "Local library index failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task AddFilesAndIndexAsync(string[] filePaths, CancellationToken cancellationToken = default)
    {
        var added = AddFilesCore(filePaths);
        if (added.Count == 0) return;
        IsLocalLibraryScanning = true;
        try
        {
            var save = SaveAsync();
            var index = _localLibrary == null
                ? Task.FromResult(0)
                : _localLibrary.AddFilesAsync(added.Select(track => track.FilePath), cancellationToken);
            await Task.WhenAll(save, index);
            if (_localLibrary != null)
            {
                var count = index.Result;
                SetLocalLibraryStatus(() => FormatLocalLibraryCount(count));
            }
        }
        finally { IsLocalLibraryScanning = false; }
    }

    private List<Track> AddFilesCore(string[] filePaths)
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
        return added;
    }

    public async Task AddLocalFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (_localLibrary == null) return;
        IsLocalLibraryScanning = true;
        try
        {
            var count = await _localLibrary.AddFolderAsync(folder, cancellationToken);
            SetLocalLibraryStatus(() => FormatLocalLibraryCount(count));
        }
        finally { IsLocalLibraryScanning = false; }
    }

    public async Task RescanLocalLibraryAsync(CancellationToken cancellationToken = default)
    {
        if (_localLibrary == null) return;
        IsLocalLibraryScanning = true;
        try
        {
            var count = await _localLibrary.RescanAsync(cancellationToken);
            SetLocalLibraryStatus(() => FormatLocalLibraryCount(count));
        }
        finally { IsLocalLibraryScanning = false; }
    }

    private void SetLocalLibraryStatus(Func<string> factory)
    {
        _localLibraryStatusFactory = factory;
        LocalLibraryStatus = factory();
    }

    private void SetRadioStatus(Func<string> factory)
    {
        _radioStatusFactory = factory;
        RadioStatusMessage = factory();
    }

    private void ClearRadioStatus()
    {
        _radioStatusFactory = null;
        RadioStatusMessage = string.Empty;
    }

    private static string FormatLocalLibraryCount(int count)
        => AppLanguage.T($"本地曲库已收录 {count} 首", $"Local library: {count} tracks");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        CancelSlowSearch();
        _libraryFilterSub.Dispose();
        _nowPlayingSub.Dispose();
        _selectedKugouPlaylistSub.Dispose();
        _kugouFilterSub.Dispose();
        _selectedSyncedPlaylistSub.Dispose();
        _searchTextSub.Dispose();
        _favoriteFilterSub?.Dispose();
        _favoriteSortSub?.Dispose();
        Tracks.CollectionChanged -= OnTracksChanged;
        AppLanguage.Changed -= _onLanguageChanged;
    }

    private void PopulateRadioCategories()
    {
        var selectedKey = SelectedRadioCategory?.Key ?? "lofi";
        RadioCategories.Clear();
        RadioCategories.Add(new RadioCategoryItem("favorites", AppLanguage.T("★ 我的收藏", "★ My Favorites")));
        RadioCategories.Add(new RadioCategoryItem("custom", AppLanguage.Get("S_CustomRadioCategory")));
        RadioCategories.Add(new RadioCategoryItem("lofi", AppLanguage.T("Lofi 低保真", "Lofi")));
        RadioCategories.Add(new RadioCategoryItem("jazz", AppLanguage.T("爵士", "Jazz")));
        RadioCategories.Add(new RadioCategoryItem("classical", AppLanguage.T("古典", "Classical")));
        RadioCategories.Add(new RadioCategoryItem("synthwave", AppLanguage.T("合成波", "Synthwave")));
        RadioCategories.Add(new RadioCategoryItem("pop", AppLanguage.T("流行", "Pop")));
        RadioCategories.Add(new RadioCategoryItem("news", AppLanguage.T("新闻资讯", "News")));
        RadioCategories.Add(new RadioCategoryItem("chinese", AppLanguage.T("中文电台", "Chinese")));
        RadioCategories.Add(new RadioCategoryItem("top", AppLanguage.T("热门榜单", "Top Hits")));
        SelectedRadioCategory = RadioCategories.FirstOrDefault(c => c.Key == selectedKey) ?? RadioCategories.FirstOrDefault();
    }

    public async Task LoadInitialRadioStationsAsync()
    {
        if (RadioStations.Count > 0)
            return;

        var key = SelectedRadioCategory?.Key ?? "lofi";
        await LoadRadioStationsByCategoryAsync(key);
    }

    public async Task LoadRadioStationsByCategoryAsync(string category, bool force = false)
    {
        _currentCategoryKey = category;
        if (string.Equals(category, "favorites", StringComparison.OrdinalIgnoreCase))
        {
            RadioStations.Clear();
            var favoriteStationTracks = Favorites.Where(t => t.IsLiveStream).ToList();
            foreach (var t in favoriteStationTracks)
            {
                var uuid = t.SourceId?.StartsWith(RadioStation.SourcePrefix, StringComparison.OrdinalIgnoreCase) == true
                    ? t.SourceId[RadioStation.SourcePrefix.Length..]
                    : t.Id;
                RadioStations.Add(new RadioStation
                {
                    StationUuid = uuid,
                    Name = t.Title,
                    StreamUrl = t.FilePath ?? string.Empty,
                    Tags = t.Artist ?? string.Empty,
                    Codec = t.DeclaredCodec ?? "MP3",
                    Bitrate = t.DeclaredBitrateKbps ?? 0,
                    IsFavorite = true
                });
            }
            HasRadioStations = RadioStations.Count > 0;
            if (HasRadioStations)
                ClearRadioStatus();
            else
                SetRadioStatus(() => AppLanguage.T("暂无收藏电台，可在其他流派中点击爱心收藏。", "No favorite radio stations yet. Favorite some stations first."));
            return;
        }

        if (string.Equals(category, "custom", StringComparison.OrdinalIgnoreCase))
        {
            await LoadCustomStationsIntoListAsync();
            return;
        }

        if (_radioBrowserService == null)
        {
            SetRadioStatus(() => AppLanguage.T("电台服务未初始化", "Radio service not initialized"));
            HasRadioStations = false;
            return;
        }

        IsRadioLoading = true;
        SetRadioStatus(() => AppLanguage.T("正在连接全球电台网络...", "Connecting to global radio network..."));
        try
        {
            var stations = await _radioBrowserService.GetStationsByCategoryAsync(category, forceRefresh: force, _lifetimeCts.Token);
            RadioStations.Clear();
            foreach (var s in stations)
            {
                s.IsFavorite = _favoriteIds.Contains(RadioStation.SourcePrefix + s.StationUuid);
                RadioStations.Add(s);
            }
            HasRadioStations = RadioStations.Count > 0;
            if (HasRadioStations)
                ClearRadioStatus();
            else
                SetRadioStatus(() => AppLanguage.T("暂无可用电台", "No stations available"));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load radio stations for category {Category}", category);
            SetRadioStatus(() => AppLanguage.T("电台加载失败，请检查网络后重试", "Failed to load stations; check network and retry"));
            HasRadioStations = false;
        }
        finally
        {
            IsRadioLoading = false;
        }
    }

    public async Task SearchRadioStationsAsync()
    {
        var query = RadioSearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            var key = SelectedRadioCategory?.Key ?? "lofi";
            await LoadRadioStationsByCategoryAsync(key);
            return;
        }

        // 确保自定义电台缓存已加载，便于搜索置顶聚合
        if (_customRadioStorage != null && !_customStationsCacheLoaded)
        {
            _customStationsCache = await _customRadioStorage.LoadStationsAsync(_lifetimeCts.Token);
            _customStationsCacheLoaded = true;
        }

        var matchedCustom = _customStationsCache
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        s.Tags.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (string.Equals(SelectedRadioCategory?.Key, "custom", StringComparison.OrdinalIgnoreCase) || _radioBrowserService == null)
        {
            RadioStations.Clear();
            foreach (var s in matchedCustom)
            {
                s.IsFavorite = _favoriteIds.Contains(RadioStation.SourcePrefix + s.StationUuid);
                RadioStations.Add(s);
            }
            HasRadioStations = RadioStations.Count > 0;
            if (HasRadioStations)
                ClearRadioStatus();
            else
                SetRadioStatus(() => AppLanguage.T($"未找到与“{query}”相关的自定义电台", $"No custom stations found for \"{query}\""));
            return;
        }

        IsRadioLoading = true;
        SetRadioStatus(() => AppLanguage.T($"正在搜索“{query}”...", $"Searching for \"{query}\"..."));
        try
        {
            var remoteStations = await _radioBrowserService.SearchStationsAsync(query, _lifetimeCts.Token);
            RadioStations.Clear();
            foreach (var s in matchedCustom.Concat(remoteStations))
            {
                s.IsFavorite = _favoriteIds.Contains(RadioStation.SourcePrefix + s.StationUuid);
                RadioStations.Add(s);
            }
            HasRadioStations = RadioStations.Count > 0;
            if (HasRadioStations)
                ClearRadioStatus();
            else
                SetRadioStatus(() => AppLanguage.T($"未找到与“{query}”相关的电台", $"No stations found for \"{query}\""));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to search radio stations for {Query}", query);
            SetRadioStatus(() => AppLanguage.T("搜索失败，请重试", "Search failed; try again"));
            HasRadioStations = false;
        }
        finally
        {
            IsRadioLoading = false;
        }
    }

    private async Task LoadCustomStationsIntoListAsync()
    {
        if (_customRadioStorage == null)
        {
            RadioStations.Clear();
            HasRadioStations = false;
            SetRadioStatus(() => AppLanguage.Get("S_NoCustomStations"));
            return;
        }

        IsRadioLoading = true;
        try
        {
            var stations = await _customRadioStorage.LoadStationsAsync(_lifetimeCts.Token);
            _customStationsCache = stations;
            _customStationsCacheLoaded = true;
            RadioStations.Clear();
            foreach (var s in stations)
            {
                s.IsFavorite = _favoriteIds.Contains(RadioStation.SourcePrefix + s.StationUuid);
                RadioStations.Add(s);
            }
            HasRadioStations = RadioStations.Count > 0;
            if (HasRadioStations)
                ClearRadioStatus();
            else
                SetRadioStatus(() => AppLanguage.Get("S_NoCustomStations"));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load custom radio stations");
            RadioStations.Clear();
            HasRadioStations = false;
            SetRadioStatus(() => AppLanguage.Get("S_NoCustomStations"));
        }
        finally
        {
            IsRadioLoading = false;
        }
    }

    private void OpenAddCustomStationPanel()
    {
        IsEditingCustomStation = false;
        EditingCustomStationUuid = null;
        IsCustomImportMode = false;
        CustomStationUrl = string.Empty;
        CustomStationName = string.Empty;
        CustomStationTags = string.Empty;
        CustomStationCodec = "MP3";
        CustomStationBitrateText = string.Empty;
        ProbeStatusText = string.Empty;
        ProbeIsWarning = false;
        ImportPreviewStations.Clear();
        HasImportPreview = false;
        ImportSummaryText = string.Empty;
        IsCustomStationPanelOpen = true;
    }

    private void OpenEditCustomStationPanel(RadioStation? station)
    {
        if (station == null || !station.IsCustom)
            return;

        IsEditingCustomStation = true;
        EditingCustomStationUuid = station.StationUuid;
        IsCustomImportMode = false;
        CustomStationUrl = station.StreamUrl;
        CustomStationName = station.Name;
        CustomStationTags = station.Tags;
        CustomStationCodec = string.IsNullOrWhiteSpace(station.Codec) ? "MP3" : station.Codec;
        CustomStationBitrateText = station.Bitrate > 0
            ? station.Bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        ProbeStatusText = string.Empty;
        ProbeIsWarning = false;
        IsCustomStationPanelOpen = true;
    }

    private void CloseCustomStationPanel()
    {
        IsCustomStationPanelOpen = false;
        IsEditingCustomStation = false;
        EditingCustomStationUuid = null;
        ProbeStatusText = string.Empty;
        ProbeIsWarning = false;
        ImportPreviewStations.Clear();
        HasImportPreview = false;
        ImportSummaryText = string.Empty;
    }

    public async Task ProbeCustomStreamAsync()
    {
        var url = CustomStationUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ProbeIsWarning = true;
            ProbeStatusText = AppLanguage.T("请输入流地址后再探测", "Enter a stream URL first");
            return;
        }

        if (_radioStreamProbeService == null)
        {
            ProbeIsWarning = true;
            ProbeStatusText = AppLanguage.T("探测服务不可用", "Probe service unavailable");
            return;
        }

        IsProbingStream = true;
        ProbeIsWarning = false;
        ProbeStatusText = AppLanguage.Get("S_Probing");
        try
        {
            var result = await _radioStreamProbeService.ProbeAsync(url, _lifetimeCts.Token);
            if (!string.IsNullOrWhiteSpace(result.Codec))
                CustomStationCodec = result.Codec;
            if (result.BitrateKbps is > 0)
                CustomStationBitrateText = result.BitrateKbps.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(CustomStationName) && !string.IsNullOrWhiteSpace(result.StationName))
                CustomStationName = result.StationName;
            if (string.IsNullOrWhiteSpace(CustomStationTags) && !string.IsNullOrWhiteSpace(result.Tags))
                CustomStationTags = result.Tags;

            if (result.Success)
            {
                if (result.LooksLikeStaticFile)
                {
                    ProbeIsWarning = true;
                    ProbeStatusText = AppLanguage.T(
                        "⚠ 探测到固定大小音频文件，可能非 24/7 直播流",
                        "⚠ Static audio file detected; may not be a 24/7 live stream");
                }
                else
                {
                    ProbeIsWarning = false;
                    var quality = result.BitrateKbps is > 0
                        ? $"{result.Codec ?? "STREAM"} {result.BitrateKbps}k"
                        : (result.Codec ?? "STREAM");
                    ProbeStatusText = AppLanguage.T($"✓ 探测成功 · {quality}", $"✓ Probe OK · {quality}");
                }
            }
            else
            {
                ProbeIsWarning = true;
                ProbeStatusText = $"⚠ {result.ErrorMessage ?? AppLanguage.T("探测失败，仍可手动保存", "Probe failed; you can still save manually")}";
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ProbeCustomStreamAsync error");
            ProbeIsWarning = true;
            ProbeStatusText = AppLanguage.T("⚠ 探测失败，仍可手动保存", "⚠ Probe failed; you can still save manually");
        }
        finally
        {
            IsProbingStream = false;
        }
    }

    public async Task SaveCustomStationAsync(bool playAfterSave)
    {
        var url = CustomStationUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ProbeIsWarning = true;
            ProbeStatusText = AppLanguage.T("请输入有效的 http:// 或 https:// 流地址", "Enter a valid http:// or https:// stream URL");
            return;
        }

        if (!await MediaUriPolicy.ValidateCustomRadioAsync(uri, _lifetimeCts.Token))
        {
            ProbeIsWarning = true;
            ProbeStatusText = AppLanguage.T("该地址被安全策略拒绝（禁止本机回环或受限地址）", "Address blocked by security policy");
            return;
        }

        var name = CustomStationName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = uri.Host;

        var tags = CustomStationTags.Trim();
        var codec = string.IsNullOrWhiteSpace(CustomStationCodec)
            ? (RadioStreamProbeService.InferCodecFromUrl(uri) ?? "MP3")
            : CustomStationCodec.Trim().ToUpperInvariant();
        var bitrate = int.TryParse(CustomStationBitrateText.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedBitrate) && parsedBitrate > 0
            ? Math.Min(parsedBitrate, 9999)
            : 0;

        if (_customRadioStorage == null)
            return;

        RadioStation savedStation;
        if (IsEditingCustomStation && !string.IsNullOrWhiteSpace(EditingCustomStationUuid))
        {
            var existing = _customStationsCache.FirstOrDefault(s =>
                string.Equals(s.StationUuid, EditingCustomStationUuid, StringComparison.OrdinalIgnoreCase));
            savedStation = new RadioStation
            {
                StationUuid = EditingCustomStationUuid,
                Name = name,
                StreamUrl = uri.AbsoluteUri,
                Tags = string.IsNullOrWhiteSpace(tags) ? "Custom" : tags,
                Codec = codec,
                Bitrate = bitrate,
                IsFavorite = existing?.IsFavorite ?? _favoriteIds.Contains(RadioStation.SourcePrefix + EditingCustomStationUuid)
            };

            await _customRadioStorage.UpdateStationAsync(savedStation, _lifetimeCts.Token);

            // 四级级联更新：同步收藏夹持久化实体
            var sourceId = RadioStation.SourcePrefix + savedStation.StationUuid;
            var favTrack = Favorites.FirstOrDefault(t => string.Equals(t.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (favTrack != null)
            {
                favTrack.Title = savedStation.Name;
                favTrack.Artist = savedStation.DisplayCountryAndTags;
                favTrack.FilePath = savedStation.StreamUrl;
                favTrack.DeclaredCodec = savedStation.Codec;
                favTrack.DeclaredBitrateKbps = savedStation.Bitrate > 0 ? savedStation.Bitrate : null;
                favTrack.ProviderMetadata["stream_url"] = savedStation.StreamUrl;
                favTrack.ProviderMetadata["tags"] = savedStation.Tags;
                favTrack.ProviderMetadata["codec"] = savedStation.Codec;
                favTrack.ProviderMetadata["bitrate"] = savedStation.Bitrate > 0
                    ? savedStation.Bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;
                ApplyFavoriteFilterAndSort();
                _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        else
        {
            savedStation = new RadioStation
            {
                StationUuid = RadioStation.CreateCustomUuid(),
                Name = name,
                StreamUrl = uri.AbsoluteUri,
                Tags = string.IsNullOrWhiteSpace(tags) ? "Custom" : tags,
                Codec = codec,
                Bitrate = bitrate
            };

            var added = await _customRadioStorage.AddStationAsync(savedStation, _lifetimeCts.Token);
            if (!added)
            {
                ProbeIsWarning = true;
                ProbeStatusText = AppLanguage.T("该电台地址已存在于自定义列表中", "This stream URL already exists");
                return;
            }
        }

        CloseCustomStationPanel();

        var customCategory = RadioCategories.FirstOrDefault(c => string.Equals(c.Key, "custom", StringComparison.OrdinalIgnoreCase));
        if (customCategory != null && !string.Equals(SelectedRadioCategory?.Key, "custom", StringComparison.OrdinalIgnoreCase))
        {
            _currentCategoryKey = "custom";
            RadioSearchText = string.Empty;
            SelectedRadioCategory = customCategory;
        }
        await LoadCustomStationsIntoListAsync();

        if (playAfterSave)
        {
            PlayRadioStation(savedStation);
        }
    }

    public async Task DeleteCustomStationAsync(RadioStation? station)
    {
        if (station == null || !station.IsCustom || _customRadioStorage == null)
            return;

        await _customRadioStorage.RemoveStationAsync(station.StationUuid, _lifetimeCts.Token);

        // 级联删除收藏夹关联项
        var sourceId = RadioStation.SourcePrefix + station.StationUuid;
        if (_favoriteIds.Remove(sourceId))
        {
            var favTrack = Favorites.FirstOrDefault(t => string.Equals(t.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (favTrack != null)
                Favorites.Remove(favTrack);
            ApplyFavoriteFilterAndSort();
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        }

        // 若曾意外残留于主歌单，一并移除
        var libraryTrack = Tracks.FirstOrDefault(t => string.Equals(t.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (libraryTrack != null)
        {
            RemoveTrackCore(libraryTrack);
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        }

        RadioStations.Remove(station);
        _customStationsCache = _customStationsCache
            .Where(s => !string.Equals(s.StationUuid, station.StationUuid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        HasRadioStations = RadioStations.Count > 0;
        if (!HasRadioStations && string.Equals(SelectedRadioCategory?.Key, "custom", StringComparison.OrdinalIgnoreCase))
            SetRadioStatus(() => AppLanguage.Get("S_NoCustomStations"));
    }

    public async Task LoadImportPlaylistFileAsync(string filePath)
    {
        if (_playlistFileParser == null || string.IsNullOrWhiteSpace(filePath))
            return;

        var result = await _playlistFileParser.ParseFileAsync(filePath, _lifetimeCts.Token);
        ImportPreviewStations.Clear();
        foreach (var s in result.Stations)
            ImportPreviewStations.Add(s);

        HasImportPreview = ImportPreviewStations.Count > 0;
        if (result.Stations.Count == 0)
        {
            ImportSummaryText = AppLanguage.T("未在文件中识别到有效的 http/https 电台流", "No valid http/https streams found in file");
        }
        else if (result.WasTruncated)
        {
            ImportSummaryText = AppLanguage.T(
                $"已识别 {result.Stations.Count} 个电台（超限已截断，跳过 {result.SkippedCount} 个无效项）",
                $"Found {result.Stations.Count} station(s) (truncated at limit, skipped {result.SkippedCount})");
        }
        else
        {
            ImportSummaryText = AppLanguage.T(
                $"已识别 {result.Stations.Count} 个有效电台（跳过 {result.SkippedCount} 个无效/重复项）",
                $"Found {result.Stations.Count} valid station(s) (skipped {result.SkippedCount})");
        }
    }

    public async Task ConfirmImportCustomStationsAsync()
    {
        if (_customRadioStorage == null || ImportPreviewStations.Count == 0)
            return;

        var importedCount = await _customRadioStorage.AddStationsAsync(ImportPreviewStations.ToList(), _lifetimeCts.Token);
        CloseCustomStationPanel();

        var customCategory = RadioCategories.FirstOrDefault(c => string.Equals(c.Key, "custom", StringComparison.OrdinalIgnoreCase));
        if (customCategory != null && !string.Equals(SelectedRadioCategory?.Key, "custom", StringComparison.OrdinalIgnoreCase))
        {
            _currentCategoryKey = "custom";
            RadioSearchText = string.Empty;
            SelectedRadioCategory = customCategory;
        }
        await LoadCustomStationsIntoListAsync();

        if (importedCount > 0)
        {
            ClearRadioStatus();
        }
        else
        {
            SetRadioStatus(() => AppLanguage.T("导入的电台均已存在，未新增重复项", "All stations already exist; no duplicates added"));
        }
    }

    private void PlayRadioStation(RadioStation station)
    {
        _audioService.PlayRadioSoundFx(RadioFxKind.TuningSweep);

        var tracks = RadioStations.Select(s => s.ToTrack()).ToList();
        var startIndex = RadioStations.IndexOf(station);
        if (startIndex < 0) startIndex = 0;

        foreach (var t in tracks)
        {
            if (_favoriteIds.Contains(t.Id))
                t.IsFavorite = true;
        }

        _audioService.StartPlaybackContext(tracks, startIndex, shuffle: false,
            AppLanguage.T("网络电台", "Internet Radio"));
    }

    private void RebuildFavoriteSortOptions()
    {
        var currentKind = SelectedFavoriteSortOption?.Kind ?? FavoriteSortKind.DateAddedDesc;
        FavoriteSortOptions.Clear();
        FavoriteSortOptions.Add(new FavoriteSortOption(FavoriteSortKind.DateAddedDesc, AppLanguage.T("最新收藏", "Recently Added")));
        FavoriteSortOptions.Add(new FavoriteSortOption(FavoriteSortKind.TitleAsc, AppLanguage.T("歌曲名 (A-Z)", "Title (A-Z)")));
        FavoriteSortOptions.Add(new FavoriteSortOption(FavoriteSortKind.ArtistAsc, AppLanguage.T("歌手名 (A-Z)", "Artist (A-Z)")));
        FavoriteSortOptions.Add(new FavoriteSortOption(FavoriteSortKind.DurationDesc, AppLanguage.T("时长从长到短", "Duration (Longest)")));
        FavoriteSortOptions.Add(new FavoriteSortOption(FavoriteSortKind.DurationAsc, AppLanguage.T("时长从短到长", "Duration (Shortest)")));

        SelectedFavoriteSortOption = FavoriteSortOptions.FirstOrDefault(o => o.Kind == currentKind)
                                     ?? FavoriteSortOptions[0];
    }

    public void ApplyFavoriteFilterAndSort()
    {
        var songs = Favorites.Where(t => !t.IsLiveStream).ToList();
        var stations = Favorites.Where(t => t.IsLiveStream).ToList();

        FavoriteSongsTabTitle = $"{AppLanguage.T("歌曲", "Songs")} ({songs.Count})";
        FavoriteStationsTabTitle = $"{AppLanguage.T("电台", "Radio")} ({stations.Count})";

        var keyword = FavoriteFilterText.Trim();
        IEnumerable<Track> songQuery = songs;
        if (keyword.Length > 0)
        {
            songQuery = songQuery.Where(t =>
                (!string.IsNullOrEmpty(t.Title) && t.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(t.Artist) && t.Artist.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(t.Album) && t.Album.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        var sortKind = SelectedFavoriteSortOption?.Kind ?? FavoriteSortKind.DateAddedDesc;
        IEnumerable<Track> sortedSongs = sortKind switch
        {
            FavoriteSortKind.TitleAsc => songQuery.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            FavoriteSortKind.ArtistAsc => songQuery.OrderBy(t => t.DisplayArtist, StringComparer.CurrentCultureIgnoreCase),
            FavoriteSortKind.DurationDesc => songQuery.OrderByDescending(t => t.Duration),
            FavoriteSortKind.DurationAsc => songQuery.OrderBy(t => t.Duration),
            _ => songQuery.Reverse()
        };

        var filteredSongList = sortedSongs.ToList();
        VisibleFavoriteTracks.Clear();
        foreach (var track in filteredSongList)
            VisibleFavoriteTracks.Add(track);

        HasVisibleFavoriteTracks = VisibleFavoriteTracks.Count > 0;
        if (songs.Count == 0)
        {
            FavoriteSongStatusMessage = AppLanguage.T("暂无收藏歌曲，在播放或搜索时点击爱心即可收藏。", "No favorite songs yet. Click the heart icon while playing or searching to add favorites.");
        }
        else if (VisibleFavoriteTracks.Count == 0)
        {
            FavoriteSongStatusMessage = AppLanguage.T("未找到匹配的收藏歌曲。", "No matching favorite songs found.");
        }
        else
        {
            FavoriteSongStatusMessage = string.Empty;
        }

        FavoriteStations.Clear();
        foreach (var station in stations.AsEnumerable().Reverse())
            FavoriteStations.Add(station);

        HasFavoriteStations = FavoriteStations.Count > 0;
        FavoriteStationStatusMessage = HasFavoriteStations
            ? string.Empty
            : AppLanguage.T("暂无收藏电台，可在网络电台(📻)中点击爱心收藏常用频道。", "No favorite radio stations yet. Click the heart icon in Internet Radio (📻) to favorite stations.");
    }

    private void FavoriteRadioStation(RadioStation station)
    {
        var track = station.ToTrack();
        ToggleFavoriteCommand.Execute(track).Subscribe();
        if (string.Equals(SelectedRadioCategory?.Key, "favorites", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadRadioStationsByCategoryAsync("favorites");
        }
    }
}

public enum FavoriteSortKind
{
    DateAddedDesc,
    TitleAsc,
    ArtistAsc,
    DurationDesc,
    DurationAsc
}

public sealed class FavoriteSortOption
{
    public FavoriteSortKind Kind { get; }
    public string DisplayName { get; }
    public FavoriteSortOption(FavoriteSortKind kind, string displayName)
    {
        Kind = kind;
        DisplayName = displayName;
    }
    public override string ToString() => DisplayName;
}

public sealed class RadioCategoryItem
{
    public string Key { get; }
    public string DisplayName { get; }
    public RadioCategoryItem(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }
    public override string ToString() => DisplayName;
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
