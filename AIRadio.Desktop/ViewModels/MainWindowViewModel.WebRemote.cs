using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.WebRemote;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private IWebRemoteService? _webRemoteService;
    private CompositeDisposable? _webRemoteSubs;
    private bool _webRemoteInitialized;
    private bool _syncingWebRemoteSetting;
    private volatile WebRemoteSnapshot _latestWebRemoteSnapshot = new();
    private CancellationTokenSource? _webRemoteCopyHintCts;

    [Reactive] public partial bool IsWebRemoteRunning { get; private set; }
    [Reactive] public partial int WebRemoteClientCount { get; private set; }
    [Reactive] public partial string WebRemoteUrl { get; private set; } = string.Empty;
    [Reactive] public partial string WebRemoteStatusText { get; private set; } = AppLanguage.Get("S_WebRemoteStopped");
    [Reactive] public partial string WebRemoteTooltip { get; private set; } = AppLanguage.Get("S_WebRemoteTitle");
    [Reactive] public partial string WebRemoteToggleButtonText { get; private set; } = AppLanguage.Get("S_WebRemoteStartNow");
    [Reactive] public partial IImage? WebRemoteQrImage { get; private set; }
    [Reactive] public partial string WebRemoteCopyButtonText { get; private set; } = AppLanguage.Get("S_WebRemoteCopyUrl");

    public ReactiveCommand<Unit, Unit>? ToggleWebRemoteCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? RefreshWebRemoteTokenCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? CopyWebRemoteUrlCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? OpenWebRemoteSettingsCommand { get; private set; }

    private void InitializeWebRemote(IWebRemoteService? webRemoteService)
    {
        _webRemoteService = webRemoteService;

        ToggleWebRemoteCommand = ReactiveCommand.CreateFromTask(ToggleWebRemoteAsync);
        RefreshWebRemoteTokenCommand = ReactiveCommand.CreateFromTask(RefreshWebRemoteTokenAsync);
        CopyWebRemoteUrlCommand = ReactiveCommand.CreateFromTask(CopyWebRemoteUrlAsync);
        OpenWebRemoteSettingsCommand = ReactiveCommand.Create(() =>
        {
            CloseTransientOverlays();
            IsSettingsOpen = true;
            SettingsVM.TabIndex = 3;
        });

        var subs = new CompositeDisposable();
        _webRemoteSubs = subs;

        if (_webRemoteService != null)
        {
            _webRemoteService.ConfigureHandlers(
                () => _latestWebRemoteSnapshot,
                DispatchWebRemoteCommandAsync);
            _webRemoteService.ConfigureBrowseHandler(DispatchWebRemoteBrowseAsync);

            Action onStatusChanged = () =>
                RxApp.MainThreadScheduler.Schedule(() =>
                {
                    if (!IsDisposed) RefreshWebRemoteUiState();
                });
            _webRemoteService.StatusChanged += onStatusChanged;
            subs.Add(Disposable.Create(() =>
            {
                if (_webRemoteService != null)
                    _webRemoteService.StatusChanged -= onStatusChanged;
            }));
        }

        // 高频进度变化走节流推播（100ms 合并），曲目/状态/歌词/聊天走即时推播
        subs.Add(PlayerVM.WhenAnyValue(x => x.CurrentSeconds)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: false)));

        subs.Add(PlayerVM.WhenAnyValue(
                x => x.TrackTitle,
                x => x.TrackArtist,
                x => x.TrackSource,
                x => x.TrackQualityText,
                x => x.TrackIsPreview,
                x => x.IsPlaying)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(PlayerVM.WhenAnyValue(
                x => x.IsLiveStream,
                x => x.IsPodcast,
                x => x.PlaybackRate,
                x => x.TotalSeconds,
                x => x.Volume,
                x => x.IsShuffled,
                x => x.RepeatMode)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(this.WhenAnyValue(
                x => x.IsCurrentFavorite,
                x => x.CurrentState,
                x => x.CurrentSceneMode,
                x => x.IsSleepTimerActive)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(this.WhenAnyValue(
                x => x.SleepTimerDisplayStatus,
                x => x.SleepTimerTargetMinutes,
                x => x.CanStopAfterCurrentTrack,
                x => x.SelectedCharacter)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(this.WhenAnyValue(
                x => x.IsProgramLoading,
                x => x.HasCurrentRadioProgram,
                x => x.ProgramStatusText)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(ChatVM.WhenAnyValue(x => x.IsSpeaking, x => x.IsProcessing)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        NotifyCollectionChangedEventHandler onChatChanged = (_, _) =>
            UpdateWebRemoteSnapshot(immediate: true);
        ChatVM.Messages.CollectionChanged += onChatChanged;
        subs.Add(Disposable.Create(() => ChatVM.Messages.CollectionChanged -= onChatChanged));

        Action onLyricsChanged = () => UpdateWebRemoteSnapshot(immediate: true);
        LyricsVM.LyricsChanged += onLyricsChanged;
        subs.Add(Disposable.Create(() => LyricsVM.LyricsChanged -= onLyricsChanged));

        // 监听设置页启停开关与端口修改（仅在 LoadLocalStateAsync 完毕后接管，避免加载瞬间重复启停）
        subs.Add(SettingsVM.WhenAnyValue(x => x.WebRemoteEnabled, x => x.WebRemotePort)
            .Skip(1)
            .Subscribe(cfg =>
            {
                if (!_webRemoteInitialized || _syncingWebRemoteSetting) return;
                _ = ApplyWebRemoteSettingsAsync(cfg.Item1, cfg.Item2);
            }));

        UpdateWebRemoteSnapshot(immediate: false);
        RefreshWebRemoteUiState();
    }

    private async Task RestoreWebRemoteAfterLoadAsync(CancellationToken ct)
    {
        if (IsDisposed) return;
        _webRemoteInitialized = true;
        if (SettingsVM.WebRemoteEnabled)
        {
            await ApplyWebRemoteSettingsAsync(true, SettingsVM.WebRemotePort, ct);
        }
        else
        {
            RefreshWebRemoteUiState();
        }
    }

    private async Task ToggleWebRemoteAsync()
    {
        var next = !SettingsVM.WebRemoteEnabled;
        _syncingWebRemoteSetting = true;
        try
        {
            SettingsVM.WebRemoteEnabled = next;
        }
        finally
        {
            _syncingWebRemoteSetting = false;
        }
        await ApplyWebRemoteSettingsAsync(next, SettingsVM.WebRemotePort);
    }

    private async Task RefreshWebRemoteTokenAsync()
    {
        var freshToken = LanAddressResolver.GenerateAccessToken();
        SettingsVM.WebRemoteToken = freshToken;
        if (_webRemoteService != null)
            await _webRemoteService.RotateTokenAsync(freshToken);
        SettingsVM.RequestUiStateSave();
        RefreshWebRemoteUiState();
    }

    private async Task CopyWebRemoteUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(WebRemoteUrl)) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(WebRemoteUrl);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Copy WebRemote URL failed");
        }

        _webRemoteCopyHintCts?.Cancel();
        _webRemoteCopyHintCts?.Dispose();
        var cts = new CancellationTokenSource();
        _webRemoteCopyHintCts = cts;
        WebRemoteCopyButtonText = AppLanguage.Get("S_WebRemoteCopied");
        try
        {
            await Task.Delay(1800, cts.Token);
            if (!cts.IsCancellationRequested && !IsDisposed)
                WebRemoteCopyButtonText = AppLanguage.Get("S_WebRemoteCopyUrl");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplyWebRemoteSettingsAsync(bool enabled, int port, CancellationToken ct = default)
    {
        if (_webRemoteService == null || IsDisposed)
            return;

        if (!enabled)
        {
            await _webRemoteService.StopAsync();
            RefreshWebRemoteUiState();
            return;
        }

        if (string.IsNullOrWhiteSpace(SettingsVM.WebRemoteToken))
            SettingsVM.WebRemoteToken = LanAddressResolver.GenerateAccessToken();

        var ok = await _webRemoteService.StartAsync(port, SettingsVM.WebRemoteToken, cancellationToken: ct);
        if (ok && _webRemoteService.Port != port)
        {
            _syncingWebRemoteSetting = true;
            SettingsVM.WebRemotePort = _webRemoteService.Port;
            SettingsVM.WebRemotePortText = _webRemoteService.Port.ToString();
            _syncingWebRemoteSetting = false;
        }
        else if (!ok)
        {
            _syncingWebRemoteSetting = true;
            SettingsVM.WebRemoteEnabled = false;
            _syncingWebRemoteSetting = false;
        }
        if (ok)
            UpdateWebRemoteSnapshot(immediate: false, force: true);
        RefreshWebRemoteUiState();
    }

    private void RefreshWebRemoteUiState()
    {
        var running = _webRemoteService is { IsRunning: true };
        IsWebRemoteRunning = running;
        WebRemoteClientCount = _webRemoteService?.ConnectedClientCount ?? 0;
        WebRemoteToggleButtonText = AppLanguage.Get(running ? "S_WebRemoteStopNow" : "S_WebRemoteStartNow");

        if (running && _webRemoteService != null)
        {
            var url = _webRemoteService.PrimaryUrl;
            if (!string.Equals(WebRemoteUrl, url, StringComparison.Ordinal))
            {
                WebRemoteUrl = url;
                try
                {
                    WebRemoteQrImage = QrCodeGenerator.RenderBitmap(url, modulePixels: 5, quietModules: 4);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "QR bitmap generation failed");
                    WebRemoteQrImage = null;
                }
            }

            var clients = WebRemoteClientCount;
            WebRemoteStatusText = AppLanguage.T(
                $"运行中 · {clients} 台设备已连接",
                $"Running · {clients} connected");
            WebRemoteTooltip = AppLanguage.T(
                $"局域网 Web 遥控（运行中，{clients} 台已连接）",
                $"LAN Web Remote (running, {clients} connected)");
        }
        else
        {
            WebRemoteUrl = string.Empty;
            WebRemoteQrImage = null;
            var err = _webRemoteService?.LastError;
            WebRemoteStatusText = string.IsNullOrWhiteSpace(err)
                ? AppLanguage.Get("S_WebRemoteStopped")
                : AppLanguage.T($"启动失败：{err}", $"Start failed: {err}");
            WebRemoteTooltip = AppLanguage.Get("S_WebRemoteTitle");
        }

        if (_webRemoteCopyHintCts == null || _webRemoteCopyHintCts.IsCancellationRequested)
            WebRemoteCopyButtonText = AppLanguage.Get("S_WebRemoteCopyUrl");
    }

    private void UpdateWebRemoteSnapshot(bool immediate, bool force = false)
    {
        if (!force && _webRemoteService is not { IsRunning: true })
            return;

        var currentTrack = _audioService.CurrentTrack;
        var hasTrack = currentTrack != null;
        var lyricLines = (LyricsVM.Lines ?? Array.Empty<LyricLine>())
            .Select(l => new WebRemoteLyricLine((long)l.Time.TotalMilliseconds, l.Text))
            .ToArray();
        var recentMsgs = ChatVM.Messages
            .TakeLast(30)
            .Select(m => new WebRemoteChatMessage(
                m.Role == Models.MessageRole.User ? "user" : "assistant",
                m.AuthorName,
                m.Content,
                m.Timestamp.ToString("HH:mm")))
            .ToArray();

            var accentColor = Converters.CharacterAccentConverter.GetAccent(SelectedCharacter);
            var accentHex = $"#{accentColor.R:X2}{accentColor.G:X2}{accentColor.B:X2}";

        _latestWebRemoteSnapshot = new WebRemoteSnapshot
        {
            Language = SettingsVM.SelectedLanguage == "en" ? "en" : "zh",
            Title = hasTrack ? PlayerVM.TrackTitle : AppLanguage.T("未播放", "Nothing playing"),
            Artist = hasTrack ? PlayerVM.TrackArtist : string.Empty,
            Source = hasTrack ? PlayerVM.TrackSource : string.Empty,
            Quality = hasTrack ? PlayerVM.TrackQualityText : string.Empty,
            IsPreview = hasTrack && PlayerVM.TrackIsPreview,
            HasTrack = hasTrack,
            IsPlaying = PlayerVM.IsPlaying,
            IsLiveStream = PlayerVM.IsLiveStream,
            IsPodcast = currentTrack?.IsPodcastEpisode ?? false,
            PlaybackRate = PlayerVM.PlaybackRate,
            IsFavorite = IsCurrentFavorite,
            PositionSeconds = PlayerVM.CurrentSeconds,
            DurationSeconds = PlayerVM.TotalSeconds,
            CanSeek = hasTrack && !PlayerVM.IsLiveStream && PlayerVM.TotalSeconds > 0,
            Volume = PlayerVM.Volume,
            IsShuffled = PlayerVM.IsShuffled,
            RepeatMode = PlayerVM.RepeatMode,
            RepeatLabel = PlayerVM.RepeatText,
            RadioState = CurrentState.ToString(),
            SceneMode = CurrentSceneMode.ToString(),
            IsFocusMode = IsFocusRadioMode,
            IsSleepTimerActive = IsSleepTimerActive,
            SleepTimerText = SleepTimerDisplayStatus,
            CanStopAfterCurrentTrack = CanStopAfterCurrentTrack,
            SleepTimerMinutes = SleepTimerTargetMinutes,
            DjName = SelectedCharacter?.DisplayName ?? _djService.DjName,
            DjAccentHex = accentHex,
            IsDjSpeaking = ChatVM.IsSpeaking,
            IsDjProcessing = ChatVM.IsProcessing,
            HasLyrics = LyricsVM.HasLyrics,
            LyricStatusText = LyricsVM.LyricStatusText,
            CurrentLyricIndex = LyricsVM.CurrentLineIndex,
            LyricLines = lyricLines,
            Messages = recentMsgs,
            IsProgramLoading = IsProgramLoading,
            DigestingEpisodeKey = _digestingEpisodeKey,
            ServerTimeUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _webRemoteService?.NotifyStateChanged(immediate);
    }

    private Task<WebRemoteBrowseResponse> DispatchWebRemoteBrowseAsync(WebRemoteBrowseRequest req, CancellationToken ct)
    {
        if (IsDisposed)
        {
            return Task.FromResult(new WebRemoteBrowseResponse
            {
                Ok = false,
                Section = req.Section,
                Error = "disposed"
            });
        }
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<WebRemoteBrowseResponse>(ct);

        var tcs = new TaskCompletionSource<WebRemoteBrowseResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelReg = ct.Register(() => tcs.TrySetCanceled(ct));
        RxApp.MainThreadScheduler.Schedule(() =>
        {
            _ = RunBrowseAsync();
        });
        return tcs.Task;

        async Task RunBrowseAsync()
        {
            try
            {
                var result = await ExecuteWebRemoteBrowseOnMainThreadAsync(req, ct);
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken.CanBeCanceled ? oce.CancellationToken : ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebRemote browse error: {Section}", req.Section);
                tcs.TrySetResult(new WebRemoteBrowseResponse
                {
                    Ok = false,
                    Section = req.Section,
                    Error = "browse_failed"
                });
            }
            finally
            {
                cancelReg.Dispose();
            }
        }
    }

    private async Task<WebRemoteBrowseResponse> ExecuteWebRemoteBrowseOnMainThreadAsync(WebRemoteBrowseRequest req, CancellationToken ct)
    {
        if (IsDisposed)
        {
            return new WebRemoteBrowseResponse
            {
                Ok = false,
                Section = req.Section,
                Error = "disposed"
            };
        }

        var section = string.IsNullOrWhiteSpace(req.Section)
            ? "program"
            : req.Section.Trim().ToLowerInvariant();
        var query = req.Query?.Trim();
        var currentTrack = _audioService.CurrentTrack;

        switch (section)
        {
            case "program":
            {
                if (req.Force || (!HasCurrentRadioProgram && !IsProgramLoading))
                {
                    var loadTask = LoadProgramAsync(force: req.Force);
                    if (!loadTask.IsCompleted)
                    {
                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromMilliseconds(2500), waitCts.Token));
                        if (completed == loadTask)
                        {
                            waitCts.Cancel();
                            await loadTask;
                        }
                    }
                    else
                    {
                        await loadTask;
                    }
                }
                ct.ThrowIfCancellationRequested();

                currentTrack = _audioService.CurrentTrack;
                var items = (CurrentRadioProgram?.Tracks ?? new List<RecommendedTrack>())
                    .Where(item => MatchesKeyword(query, item.Track.Title, item.Track.DisplayArtist, string.Join(' ', item.Tags), item.Reason))
                    .Take(50)
                    .Select(item =>
                    {
                        var trackId = !string.IsNullOrWhiteSpace(item.Track.Id)
                            ? item.Track.Id
                            : (item.Track.SourceId ?? string.Empty);
                        return new WebRemoteProgramTrackItem(
                            trackId,
                            item.Track.Title,
                            item.Track.DisplayArtist,
                            item.Track.Duration.TotalSeconds,
                            item.Tags.ToArray(),
                            item.Track.Playability.ToString(),
                            item.IsPlayable && !string.IsNullOrWhiteSpace(item.Track.FilePath),
                            item.Reason,
                            IsSameTrack(currentTrack, item.Track));
                    })
                    .ToArray();

                return new WebRemoteBrowseResponse
                {
                    Ok = true,
                    Section = "program",
                    IsLoading = IsProgramLoading,
                    StatusText = ProgramStatusText,
                    ProgramTitle = CurrentRadioProgram?.Title ?? string.Empty,
                    ProgramOpening = CurrentRadioProgram?.DjOpening ?? string.Empty,
                    ProgramTracks = items
                };
            }

            case "library":
            {
                var items = PlaylistVM.Tracks
                    .Select((track, idx) => (Track: track, Index: idx))
                    .Where(x => MatchesKeyword(query, x.Track.Title, x.Track.Artist, x.Track.Album))
                    .Take(200)
                    .Select(x => ToWebRemoteTrackItem(x.Track, x.Index, currentTrack))
                    .ToArray();

                var status = items.Length > 0
                    ? string.Empty
                    : PlaylistVM.Tracks.Count == 0
                        ? AppLanguage.T("播放列表为空", "Playlist is empty")
                        : AppLanguage.T("未找到匹配歌曲", "No matching tracks");

                return new WebRemoteBrowseResponse
                {
                    Ok = true,
                    Section = "library",
                    StatusText = status,
                    LibraryTracks = items
                };
            }

            case "favorites":
            {
                var favSongs = PlaylistVM.Favorites
                    .Where(t => !t.IsLiveStream && !t.IsPodcastEpisode)
                    .Reverse()
                    .Where(t => MatchesKeyword(query, t.Title, t.Artist, t.Album))
                    .Take(200)
                    .Select((t, idx) => ToWebRemoteTrackItem(t, idx, currentTrack))
                    .ToArray();

                var favStations = PlaylistVM.Favorites
                    .Where(t => t.IsLiveStream)
                    .Reverse()
                    .Where(t => MatchesKeyword(query, t.Title, t.Artist))
                    .Take(200)
                    .Select((t, idx) => ToWebRemoteTrackItem(t, idx, currentTrack))
                    .ToArray();

                var status = favSongs.Length > 0 || favStations.Length > 0
                    ? string.Empty
                    : PlaylistVM.Favorites.Count == 0
                        ? AppLanguage.T("暂无收藏内容", "No favorites yet")
                        : AppLanguage.T("未找到匹配的收藏内容", "No matching favorites");

                return new WebRemoteBrowseResponse
                {
                    Ok = true,
                    Section = "favorites",
                    StatusText = status,
                    FavoriteSongs = favSongs,
                    FavoriteStations = favStations
                };
            }

            case "radio":
            {
                if (!string.IsNullOrWhiteSpace(query))
                {
                    PlaylistVM.RadioSearchText = query;
                    await PlaylistVM.SearchRadioStationsAsync();
                }
                else
                {
                    var targetCategory = !string.IsNullOrWhiteSpace(req.Category)
                        ? req.Category.Trim()
                        : PlaylistVM.CurrentRadioCategoryKey;
                    var hadSearch = !string.IsNullOrWhiteSpace(PlaylistVM.RadioSearchText);
                    var categoryChanged = !string.IsNullOrWhiteSpace(req.Category) &&
                                          !string.Equals(req.Category.Trim(), PlaylistVM.CurrentRadioCategoryKey, StringComparison.OrdinalIgnoreCase);

                    if (hadSearch || categoryChanged || req.Force)
                        await PlaylistVM.SelectAndLoadRadioCategoryAsync(targetCategory, req.Force);
                    else if (PlaylistVM.RadioStations.Count == 0)
                        await PlaylistVM.LoadInitialRadioStationsAsync();
                }
                ct.ThrowIfCancellationRequested();

                currentTrack = _audioService.CurrentTrack;
                var categories = PlaylistVM.RadioCategories
                    .Select(c => new WebRemoteRadioCategoryItem(c.Key, c.DisplayName))
                    .ToArray();
                var selectedCat = PlaylistVM.SelectedRadioCategory?.Key ?? PlaylistVM.CurrentRadioCategoryKey;
                var stations = PlaylistVM.RadioStations
                    .Take(200)
                    .Select(s => new WebRemoteRadioStationItem(
                        s.StationUuid,
                        s.Name,
                        s.DisplayCountryAndTags,
                        s.Codec,
                        s.Bitrate,
                        s.DisplayQuality,
                        s.IsCustom,
                        s.IsFavorite,
                        IsSamePlayingStation(currentTrack, s.StationUuid)))
                    .ToArray();

                return new WebRemoteBrowseResponse
                {
                    Ok = true,
                    Section = "radio",
                    IsLoading = PlaylistVM.IsRadioLoading,
                    StatusText = PlaylistVM.RadioStatusMessage,
                    RadioCategories = categories,
                    SelectedRadioCategory = selectedCat,
                    RadioStations = stations
                };
            }

            case "podcasts":
            {
                if (PlaylistVM.PodcastFeeds.Count == 0)
                    await PlaylistVM.LoadInitialPodcastsAsync();

                if (!string.IsNullOrWhiteSpace(req.Feed))
                {
                    var feedKey = req.Feed.Trim();
                    var targetFeed = PlaylistVM.PodcastFeeds.FirstOrDefault(f =>
                        string.Equals(f.RssUrl, feedKey, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.FeedId, feedKey, StringComparison.OrdinalIgnoreCase));
                    if (targetFeed != null &&
                        (!ReferenceEquals(targetFeed, PlaylistVM.SelectedPodcastFeed) ||
                         req.Force ||
                         PlaylistVM.AllPodcastEpisodes.Count == 0))
                    {
                        await PlaylistVM.SelectAndLoadPodcastFeedAsync(targetFeed, req.Force);
                    }
                }
                else if (req.Force && PlaylistVM.SelectedPodcastFeed != null)
                {
                    await PlaylistVM.LoadPodcastEpisodesAsync(PlaylistVM.SelectedPodcastFeed, forceRefresh: true);
                }
                ct.ThrowIfCancellationRequested();

                currentTrack = _audioService.CurrentTrack;
                var feeds = PlaylistVM.PodcastFeeds
                    .Select(f => new WebRemotePodcastFeedItem(f.FeedId, f.Title, f.DisplaySubTitle, f.RssUrl))
                    .ToArray();
                var episodes = PlaylistVM.AllPodcastEpisodes
                    .Where(ep => MatchesKeyword(query, ep.Title, ep.Summary))
                    .Take(200)
                    .Select(ep => new WebRemotePodcastEpisodeItem(
                        ep.EpisodeKey,
                        ep.Title,
                        ep.DisplayMeta,
                        ep.ShortSummary,
                        ep.ResumePositionMs,
                        ep.IsCompleted,
                        ep.DisplayProgressBadge,
                        !string.IsNullOrWhiteSpace(ep.Summary)
                            || !string.IsNullOrWhiteSpace(ep.TranscriptExcerpt)
                            || (_podcastTranscriptionService != null && !string.IsNullOrWhiteSpace(ep.AudioUrl)),
                        ep.IsDigesting || string.Equals(_digestingEpisodeKey, ep.EpisodeKey, StringComparison.OrdinalIgnoreCase),
                        IsSamePlayingPodcast(currentTrack, ep.EpisodeKey)))
                    .ToArray();

                var podStatus = episodes.Length > 0
                    ? string.Empty
                    : !string.IsNullOrWhiteSpace(PlaylistVM.PodcastStatusMessage)
                        ? PlaylistVM.PodcastStatusMessage
                        : AppLanguage.Get("S_PodcastEmptyEpisodes");

                return new WebRemoteBrowseResponse
                {
                    Ok = true,
                    Section = "podcasts",
                    IsLoading = PlaylistVM.IsPodcastLoading,
                    StatusText = podStatus,
                    PodcastFeeds = feeds,
                    SelectedPodcastFeedUrl = PlaylistVM.SelectedPodcastFeed?.RssUrl ?? string.Empty,
                    PodcastEpisodes = episodes
                };
            }

            default:
                return new WebRemoteBrowseResponse
                {
                    Ok = false,
                    Section = section,
                    Error = "unknown_section"
                };
        }
    }

    private Task<WebRemoteCommandResult> DispatchWebRemoteCommandAsync(WebRemoteCommand cmd)
    {
        if (IsDisposed)
            return Task.FromResult(WebRemoteCommandResult.Fail("disposed"));

        var tcs = new TaskCompletionSource<WebRemoteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        RxApp.MainThreadScheduler.Schedule(() =>
        {
            _ = RunCommandAsync();
        });
        return tcs.Task;

        async Task RunCommandAsync()
        {
            try
            {
                var result = await ExecuteWebRemoteCommandOnMainThreadAsync(cmd);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebRemote command dispatch error: {Action}", cmd.Action);
                tcs.TrySetResult(WebRemoteCommandResult.Fail("command_failed"));
            }
        }
    }

    private async Task<WebRemoteCommandResult> ExecuteWebRemoteCommandOnMainThreadAsync(WebRemoteCommand cmd)
    {
        if (IsDisposed)
            return WebRemoteCommandResult.Fail("disposed");

        switch (cmd.Action?.Trim().ToLowerInvariant())
        {
            case "toggle_play":
                PlayerVM.PlayCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "previous":
                PlayerVM.PreviousCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "next":
                PlayerVM.NextCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "toggle_favorite":
                if (_audioService.CurrentTrack is { IsPodcastEpisode: true })
                    return WebRemoteCommandResult.Fail("podcast_favorite_unsupported");
                ToggleCurrentFavoriteCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "toggle_shuffle":
                PlayerVM.ToggleShuffleCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "cycle_repeat":
                PlayerVM.ToggleRepeatCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "set_volume":
                if (cmd.Value is not double vol || double.IsNaN(vol) || double.IsInfinity(vol))
                    return WebRemoteCommandResult.Fail("invalid_volume");
                var clampedVol = (float)Math.Clamp(vol, 0.0, 1.0);
                _audioService.Volume = clampedVol;
                PlayerVM.Volume = clampedVol;
                UpdateWebRemoteSnapshot(immediate: true);
                return WebRemoteCommandResult.Success();

            case "seek":
                if (cmd.Value is not double sec || double.IsNaN(sec) || double.IsInfinity(sec))
                    return WebRemoteCommandResult.Fail("invalid_position");
                if (_audioService.CurrentTrack is { IsLiveStream: true })
                    return WebRemoteCommandResult.Fail("live_stream_not_seekable");
                var maxDur = _audioService.Duration.TotalSeconds;
                var clampedSec = maxDur > 0 ? Math.Clamp(sec, 0.0, maxDur) : Math.Max(0.0, sec);
                _audioService.Seek(TimeSpan.FromSeconds(clampedSec));
                return WebRemoteCommandResult.Success();

            case "seek_relative":
                if (cmd.Value is not double offsetSec || double.IsNaN(offsetSec) || double.IsInfinity(offsetSec))
                    return WebRemoteCommandResult.Fail("invalid_offset");
                if (_audioService.CurrentTrack is { IsLiveStream: true })
                    return WebRemoteCommandResult.Fail("live_stream_not_seekable");
                var clampedOffset = Math.Clamp(offsetSec, -300.0, 300.0);
                _audioService.SeekRelative(TimeSpan.FromSeconds(clampedOffset));
                UpdateWebRemoteSnapshot(immediate: true);
                return WebRemoteCommandResult.Success();

            case "set_playback_rate":
                if (cmd.Value is not double reqRate || double.IsNaN(reqRate) || double.IsInfinity(reqRate) || reqRate <= 0)
                    return WebRemoteCommandResult.Fail("invalid_playback_rate");
                if (_audioService.CurrentTrack is not { IsPodcastEpisode: true })
                    return WebRemoteCommandResult.Fail("podcast_only_action");
                PlayerVM.SetPlaybackRate((float)reqRate, notifyPersist: true);
                UpdateWebRemoteSnapshot(immediate: true);
                return WebRemoteCommandResult.Success();

            case "cycle_playback_rate":
                if (_audioService.CurrentTrack is not { IsPodcastEpisode: true })
                    return WebRemoteCommandResult.Fail("podcast_only_action");
                PlayerVM.CyclePlaybackRate();
                UpdateWebRemoteSnapshot(immediate: true);
                return WebRemoteCommandResult.Success();

            case "toggle_focus_mode":
                ToggleFocusModeCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "set_sleep_timer":
                if (cmd.IntValue is not int mins)
                    return WebRemoteCommandResult.Fail("invalid_minutes");
                if (mins is 0 or 15 or 30 or 45 or 60 or 90 or -1)
                {
                    SetSleepTimerCommand.Execute(mins).Subscribe();
                    return WebRemoteCommandResult.Success();
                }
                return WebRemoteCommandResult.Fail("unsupported_duration");

            case "feedback":
                switch (cmd.Text?.Trim().ToLowerInvariant())
                {
                    case "like":
                        if (_audioService.CurrentTrack is { IsPodcastEpisode: true })
                            return WebRemoteCommandResult.Fail("podcast_favorite_unsupported");
                        if (_audioService.CurrentTrack != null && !IsCurrentFavorite)
                            ToggleCurrentFavoriteCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "dislike":
                        DislikeCurrentTrackCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "similar":
                        SimilarToCurrentTrackCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "calm":
                        CalmerRecommendationCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "energetic":
                        EnergeticRecommendationCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "story":
                        TellSongStoryCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    default:
                        return WebRemoteCommandResult.Fail("unknown_feedback");
                }

            case "chat":
                var chatText = cmd.Text?.Trim();
                if (string.IsNullOrWhiteSpace(chatText))
                    return WebRemoteCommandResult.Fail("empty_message");
                if (chatText.Length > 500)
                    chatText = chatText[..500];
                if (ChatVM.IsProcessing)
                    return WebRemoteCommandResult.Fail("dj_busy");
                ChatVM.InputText = chatText;
                ChatVM.SendMessageCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "play_program_track":
            {
                var progTracks = CurrentRadioProgram?.Tracks;
                if (progTracks == null || progTracks.Count == 0)
                    return WebRemoteCommandResult.Fail("track_not_found");

                RecommendedTrack? item = null;
                var key = cmd.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    item = progTracks.FirstOrDefault(t =>
                        string.Equals(t.Track.Id, key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.Track.SourceId, key, StringComparison.OrdinalIgnoreCase));
                }
                else if (cmd.IntValue is int idx && idx >= 0 && idx < progTracks.Count)
                {
                    item = progTracks[idx];
                }

                if (item == null)
                    return WebRemoteCommandResult.Fail("track_not_found");
                if (!item.IsPlayable || string.IsNullOrWhiteSpace(item.Track.FilePath))
                    return WebRemoteCommandResult.Fail("track_not_playable");

                PlayProgramTrack(item);
                return WebRemoteCommandResult.Success();
            }

            case "refresh_program":
                _ = LoadProgramAsync(force: true);
                return WebRemoteCommandResult.Success();

            case "play_library_track":
            {
                Track? target = null;
                var key = cmd.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    target = PlaylistVM.Tracks.FirstOrDefault(t =>
                        string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.SourceId, key, StringComparison.OrdinalIgnoreCase));
                }
                else if (cmd.IntValue is int idx && idx >= 0 && idx < PlaylistVM.Tracks.Count)
                {
                    target = PlaylistVM.Tracks[idx];
                }

                if (target == null)
                    return WebRemoteCommandResult.Fail("track_not_found");

                PlaylistVM.PlayFromLibraryCommand.Execute(target).Subscribe();
                return WebRemoteCommandResult.Success();
            }

            case "enqueue_library_track":
            {
                var key = cmd.Text?.Trim();
                var target = string.IsNullOrWhiteSpace(key)
                    ? null
                    : PlaylistVM.Tracks.FirstOrDefault(t =>
                          string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(t.SourceId, key, StringComparison.OrdinalIgnoreCase))
                      ?? PlaylistVM.Favorites.FirstOrDefault(t =>
                          !t.IsLiveStream && !t.IsPodcastEpisode &&
                          (string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(t.SourceId, key, StringComparison.OrdinalIgnoreCase)));
                if (target == null)
                    return WebRemoteCommandResult.Fail("track_not_found");

                if (cmd.IntValue == 2)
                    PlaylistVM.EnqueueTrackCommand.Execute(target).Subscribe();
                else
                    PlaylistVM.PlayTrackNextCommand.Execute(target).Subscribe();
                return WebRemoteCommandResult.Success();
            }

            case "play_favorite":
            {
                var key = cmd.Text?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                    return WebRemoteCommandResult.Fail("favorite_not_found");

                var prefixed = key.StartsWith(RadioStation.SourcePrefix, StringComparison.OrdinalIgnoreCase)
                    ? key
                    : RadioStation.SourcePrefix + key;
                var favTrack = PlaylistVM.Favorites.FirstOrDefault(t =>
                    string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.SourceId, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Id, prefixed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.SourceId, prefixed, StringComparison.OrdinalIgnoreCase));
                if (favTrack == null)
                    return WebRemoteCommandResult.Fail("favorite_not_found");

                PlaylistVM.PlayFavoriteCommand.Execute(favTrack).Subscribe();
                return WebRemoteCommandResult.Success();
            }

            case "play_radio_station":
            {
                var (station, favTrack) = await ResolveRadioStationTargetAsync(cmd.Text);
                if (station != null)
                {
                    PlaylistVM.PlayRadioStationCommand.Execute(station).Subscribe();
                    return WebRemoteCommandResult.Success();
                }
                if (favTrack != null)
                {
                    PlaylistVM.PlayFavoriteCommand.Execute(favTrack).Subscribe();
                    return WebRemoteCommandResult.Success();
                }
                return WebRemoteCommandResult.Fail("station_not_found");
            }

            case "toggle_radio_favorite":
            {
                var uuid = ExtractStationUuid(cmd.Text);
                var (station, favTrack) = await ResolveRadioStationTargetAsync(cmd.Text);
                if (station != null)
                {
                    PlaylistVM.FavoriteRadioStationCommand.Execute(station).Subscribe();
                }
                else if (favTrack != null)
                {
                    PlaylistVM.ToggleFavoriteCommand.Execute(favTrack).Subscribe();
                    if (string.Equals(PlaylistVM.SelectedRadioCategory?.Key, "favorites", StringComparison.OrdinalIgnoreCase))
                        _ = PlaylistVM.LoadRadioStationsByCategoryAsync("favorites");
                }
                else
                {
                    return WebRemoteCommandResult.Fail("station_not_found");
                }

                if (_audioService.CurrentTrack is { } cur &&
                    (string.Equals(cur.Id, RadioStation.SourcePrefix + uuid, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(cur.SourceId, RadioStation.SourcePrefix + uuid, StringComparison.OrdinalIgnoreCase)))
                {
                    var isFav = PlaylistVM.Favorites.Any(t =>
                        string.Equals(t.Id, RadioStation.SourcePrefix + uuid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.SourceId, RadioStation.SourcePrefix + uuid, StringComparison.OrdinalIgnoreCase));
                    cur.IsFavorite = isFav;
                    IsCurrentFavorite = isFav;
                }
                UpdateWebRemoteSnapshot(immediate: true);
                return WebRemoteCommandResult.Success();
            }

            case "play_podcast_episode":
            {
                var epKey = cmd.Text?.Trim();
                var episode = string.IsNullOrWhiteSpace(epKey)
                    ? null
                    : PlaylistVM.AllPodcastEpisodes.FirstOrDefault(ep =>
                        string.Equals(ep.EpisodeKey, epKey, StringComparison.OrdinalIgnoreCase));
                if (episode == null)
                    return WebRemoteCommandResult.Fail("episode_not_found");

                var started = await PlaylistVM.TryPlayPodcastEpisodeAsync(episode, fromStart: cmd.IntValue == 1);
                return started
                    ? WebRemoteCommandResult.Success()
                    : WebRemoteCommandResult.Fail("unsafe_audio_url");
            }

            case "digest_podcast_episode":
            {
                var epKey = cmd.Text?.Trim();
                var episode = string.IsNullOrWhiteSpace(epKey)
                    ? null
                    : PlaylistVM.AllPodcastEpisodes.FirstOrDefault(ep =>
                        string.Equals(ep.EpisodeKey, epKey, StringComparison.OrdinalIgnoreCase));
                if (episode == null)
                    return WebRemoteCommandResult.Fail("episode_not_found");
                var canTranscribe = _podcastTranscriptionService != null && !string.IsNullOrWhiteSpace(episode.AudioUrl);
                if (string.IsNullOrWhiteSpace(episode.Summary) && !canTranscribe && string.IsNullOrWhiteSpace(episode.TranscriptExcerpt))
                    return WebRemoteCommandResult.Fail("empty_shownotes");

                _ = DigestPodcastEpisodeAsync(episode);
                return WebRemoteCommandResult.Success();
            }

            default:
                return WebRemoteCommandResult.Fail("unknown_action");
        }
    }

    private async Task<(RadioStation? Station, Track? FavoriteTrack)> ResolveRadioStationTargetAsync(string? rawStationUuid)
    {
        var uuid = ExtractStationUuid(rawStationUuid);
        if (string.IsNullOrWhiteSpace(uuid))
            return (null, null);

        var station = PlaylistVM.RadioStations.FirstOrDefault(s =>
            string.Equals(s.StationUuid, uuid, StringComparison.OrdinalIgnoreCase));
        if (station != null)
            return (station, null);

        await PlaylistVM.EnsureCustomStationsLoadedAsync();
        station = PlaylistVM.CustomStationsCache.FirstOrDefault(s =>
            string.Equals(s.StationUuid, uuid, StringComparison.OrdinalIgnoreCase));
        if (station != null)
            return (station, null);

        var sourceId = RadioStation.SourcePrefix + uuid;
        var favTrack = PlaylistVM.Favorites.FirstOrDefault(t =>
            t.IsLiveStream &&
            (string.Equals(t.Id, sourceId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)));
        return (null, favTrack);
    }

    private static string ExtractStationUuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var trimmed = raw.Trim();
        return trimmed.StartsWith(RadioStation.SourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[RadioStation.SourcePrefix.Length..]
            : trimmed;
    }

    private static bool MatchesKeyword(string? keyword, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return true;
        var q = keyword.Trim();
        foreach (var f in fields)
        {
            if (!string.IsNullOrWhiteSpace(f) && f.Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string FormatTrackSourceName(Track track)
    {
        var sourceId = track.SourceId;
        if (string.IsNullOrWhiteSpace(sourceId))
            return AppLanguage.T("本地", "Local");
        var separator = sourceId.IndexOf(':');
        return separator > 0 ? AppLanguage.MusicSourceName(sourceId[..separator]) : string.Empty;
    }

    private static WebRemoteTrackItem ToWebRemoteTrackItem(Track track, int index, Track? currentTrack)
    {
        string? stationUuid = null;
        if (track.IsLiveStream)
        {
            var raw = !string.IsNullOrWhiteSpace(track.SourceId) ? track.SourceId : track.Id;
            stationUuid = ExtractStationUuid(raw);
        }

        return new WebRemoteTrackItem(
            index,
            track.Id,
            track.Title,
            track.DisplayArtist,
            track.DisplayAlbum,
            FormatTrackSourceName(track),
            track.DisplayQuality,
            track.Duration.TotalSeconds,
            track.IsFavorite,
            currentTrack != null && IsSameTrack(currentTrack, track),
            track.IsLiveStream,
            stationUuid);
    }

    private static bool IsSamePlayingStation(Track? currentTrack, string stationUuid)
    {
        if (currentTrack == null || !currentTrack.IsLiveStream || string.IsNullOrWhiteSpace(stationUuid))
            return false;
        var expected = RadioStation.SourcePrefix + stationUuid;
        return string.Equals(currentTrack.SourceId, expected, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(currentTrack.Id, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePlayingPodcast(Track? currentTrack, string episodeKey)
    {
        if (currentTrack == null || !currentTrack.IsPodcastEpisode || string.IsNullOrWhiteSpace(episodeKey))
            return false;
        if (string.Equals(currentTrack.SourceId, PodcastFeed.SourcePrefix + episodeKey, StringComparison.OrdinalIgnoreCase))
            return true;
        return currentTrack.ProviderMetadata.TryGetValue("episode_key", out var k) &&
               string.Equals(k, episodeKey, StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeWebRemote()
    {
        _webRemoteCopyHintCts?.Cancel();
        _webRemoteCopyHintCts?.Dispose();
        _webRemoteCopyHintCts = null;
        _webRemoteSubs?.Dispose();
        _webRemoteSubs = null;
        try { _webRemoteService?.StopAsync().Wait(TimeSpan.FromMilliseconds(400)); } catch { }
    }
}
