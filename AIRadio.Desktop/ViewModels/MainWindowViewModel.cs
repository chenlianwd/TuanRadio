using ReactiveUI;
using ReactiveUI.SourceGenerators;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
#if TUANRADIO_SLIM_CORE
    public bool HasExperimentalProviders => false;
#else
    public bool HasExperimentalProviders => true;
#endif
    private readonly IAudioService _audioService;
    private readonly IDJService _djService;
    private readonly ILLMService _llmService;
    private readonly IMusicSearchService _musicSearchService;
    private readonly IRecommendationService _recommendationService;
    private readonly ISttService _sttService;
    private readonly IListeningProfileService? _listeningProfile;
    private readonly IDisposable _trackEndedSub;
    private readonly IDisposable _trackChangedSub;
    private readonly IDisposable? _profileTrackChangedSub;
    private readonly IDisposable _playbackHistorySub;
    private readonly IDisposable? _positionSampleSub;
    private readonly IDisposable _weatherCitySub;
    private readonly IDisposable? _playbackRecoverySub;
    private readonly IDisposable _clockSub;
    private readonly IDisposable _darkModePersistSub;
    private readonly IDisposable _compactLyricsSub;
    private readonly IDisposable _radioFxSub;
    private readonly IDisposable _languageTtsSub;
    private readonly IDisposable _speechMixSub;
    private readonly IDisposable _spectrumStyleSub;
    private IDisposable? _sttLanguageSub;
    private readonly Action _characterSettingsHandler;
    private readonly Action _onLanguageChanged;
    private int _autoRadioAdvancing;
    private int _disposed;
    private readonly SemaphoreSlim _ttsLock = new(1, 1);
    // 串场话密度：自上次 DJ 开口以来的电台自动推进次数（自然结束续播链；含正在推进的这次）。
    // 手动切歌走 nextCallback 管线不经此计数（有意：跳过的歌不计入 DJ 节目节奏）。
    // 真实电台与 Spotify AI DJ 都是每 4~5 首才开口一次，每首歌必串场本身就是机器人感的来源
    private int _transitionsSinceLastSpeech;
    // 上次开口时的时间带（DJService.GetTimeBand，跨时段报到判定基准；null=节奏链尚未开过口）
    private int? _lastSpokenTimeBand;
    // 自然结束续播与手动 Next 的 nextCallback 两条推荐管线共用此门串行化，
    // 避免并发时双份推荐请求、双份加列表、双次切歌
    private readonly SemaphoreSlim _advanceGate = new(1, 1);
    private readonly SemaphoreSlim _programLoadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly MusicAccountStore? _accountStore;
    private readonly KugouVerificationService? _kugouVerification;
    private readonly LocalLibraryProvider? _localLibrary;
    private readonly PlaybackPreflightService? _playbackPreflight;

    public PlayerViewModel PlayerVM { get; }
    public PlaylistViewModel PlaylistVM { get; }
    public ChatViewModel ChatVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public SpectrumViewModel SpectrumVM { get; }
    public WeatherViewModel WeatherVM { get; }
    public LyricsViewModel LyricsVM { get; }

    public List<CharacterProfile> Characters { get; } = CharacterProfile.Presets;

    public event Action<string, string>? DjVisualCue; // expression, motion

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    [Reactive] public partial bool IsSettingsOpen { get; set; }
    [Reactive] public partial bool IsLibraryOpen { get; set; }
    [Reactive] public partial bool IsCharacterPickerOpen { get; set; }
    [Reactive] public partial CharacterProfile SelectedCharacter { get; set; }
    [Reactive] public partial bool IsDarkMode { get; set; } = true;
    [Reactive] public partial bool IsCurrentFavorite { get; set; }
    [Reactive] public partial bool IsCompactMode { get; set; }
    [Reactive] public partial bool IsLyricsMode { get; set; }

    /// <summary>舞台点击切歌词与手型光标的守门：无在播曲目时不进空歌词层、光标不提示可点。
    /// 由 TrackChanged 事件流维护，与 _audioService.CurrentTrack 同步。</summary>
    [Reactive] public partial bool HasCurrentTrack { get; private set; }
    [Reactive] public partial RadioProgram? CurrentRadioProgram { get; set; }
    [Reactive] public partial bool HasCurrentRadioProgram { get; set; }
    [Reactive] public partial bool IsProgramLoading { get; set; }
    [Reactive] public partial string ProgramStatusText { get; set; } = AppLanguage.T(
        "打开节目单时，DJ 会按当前收听风格生成下一组候选歌曲。",
        "Open Program and the DJ will curate the next set from your current listening style.");
    [Reactive] public partial string PlaybackRecoveryMessage { get; private set; } = string.Empty;
    [Reactive] public partial bool HasPlaybackRecoveryFailure { get; private set; }
    [Reactive] public partial bool IsCompactLyricsVisible { get; private set; }

    /// <summary>当前时间，1s 推进，供 ClockStage 绑定（spec §5.5）。</summary>
    [Reactive] public partial DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;
    [Reactive] public partial string LocalizedDayOfWeek { get; private set; } = string.Empty;
    [Reactive] public partial string LocalizedDate { get; private set; } = string.Empty;

    /// <summary>统一电台状态，由子 VM flags 派生（spec §5.2）。</summary>
    [ObservableAsProperty] public partial RadioState CurrentState { get; }

    /// <summary>状态派生纯函数：Error &gt; Speaking &gt; Searching &gt; Curating &gt; Playing &gt; Idle。</summary>
    public static RadioState DeriveRadioState(bool hasFailure, bool isSpeaking, bool isSearching, bool isProcessing, bool isPlaying)
        => hasFailure ? RadioState.Error
           : isSpeaking ? RadioState.Speaking
           : isSearching ? RadioState.Searching
           : isProcessing ? RadioState.Curating
           : isPlaying ? RadioState.Playing
           : RadioState.Idle;

    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFavoritesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenProgramCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenKugouPlaylistsCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshProgramCommand { get; }
    public ReactiveCommand<RecommendedTrack, Unit> PlayProgramTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCharacterPickerCommand { get; }
    public ReactiveCommand<CharacterProfile, Unit> SelectCharacterCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCompactModeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLyricsModeCommand { get; }
    public ReactiveCommand<Unit, Unit> CycleSpectrumStyleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCompactTopmostCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCompactLyricsCommand { get; }
    public ReactiveCommand<Unit, Unit> UseDarkThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> UseLightThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCurrentFavoriteCommand { get; }
    public ReactiveCommand<Unit, Unit> DislikeCurrentTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> SimilarToCurrentTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> CalmerRecommendationCommand { get; }
    public ReactiveCommand<Unit, Unit> EnergeticRecommendationCommand { get; }
    public ReactiveCommand<Unit, Unit> TellSongStoryCommand { get; }

    public MainWindowViewModel(
        IAudioService audioService,
        IDJService djService,
        ILLMService llmService,
        ISecureStorage secureStorage,
        IMusicSearchService musicSearchService,
        ISttService sttService,
        string playlistFile,
        string settingsFile,
        IRecommendationService? recommendationService = null,
        MusicAccountStore? accountStore = null,
        System.Net.Http.HttpClient? httpClient = null,
        KugouVerificationService? kugouVerification = null,
        ILyricService? lyricService = null,
        IListeningProfileService? listeningProfile = null,
        IWeatherService? weatherService = null,
        LocalLibraryProvider? localLibrary = null,
        OpenSubsonicProvider? openSubsonic = null,
        AudiusProvider? audius = null)
    {
        _audioService = audioService;
        _djService = djService;
        _sttService = sttService;
        _llmService = llmService;
        _musicSearchService = musicSearchService;
        _recommendationService = recommendationService ?? new RecommendationService(llmService, musicSearchService);
        _accountStore = accountStore;
        _kugouVerification = kugouVerification;
        _localLibrary = localLibrary;
        _listeningProfile = listeningProfile;

        SelectedCharacter = Characters[0];

        PlayerVM = new PlayerViewModel(_audioService);
        var kugouPlaylistService = accountStore != null && httpClient != null
            ? new KugouPlaylistService(httpClient, accountStore)
            : null;
        PlaylistVM = new PlaylistViewModel(
            _audioService,
            musicSearchService,
            playlistFile,
            kugouPlaylistService: kugouPlaylistService,
            localLibrary: localLibrary);
        if (musicSearchService is IMusicSourceBroker broker)
            _playbackPreflight = new PlaybackPreflightService(_audioService, broker, accountStore, openSubsonic);
        ChatVM = new ChatViewModel(_djService, _audioService, musicSearchService, sttService,
            track => PlaylistVM.AddExternalTrack(track), _recommendationService, listeningProfile);
        SettingsVM = new SettingsViewModel(_llmService, secureStorage, settingsFile, accountStore, httpClient, kugouVerification,
            listeningProfile, musicSearchService, openSubsonic, audius);
        SpectrumVM = new SpectrumViewModel(_audioService);
        WeatherVM = new WeatherViewModel(
            weatherService ?? new WeatherService(httpClient ?? new System.Net.Http.HttpClient()));
        LyricsVM = new LyricsViewModel(
            _audioService,
            lyricService ?? new LyricService(httpClient ?? new System.Net.Http.HttpClient()));

        // 设置页城市变更即重新取天气（含清空回到 IP 定位）。
        // TextBox 逐键更新源：直接订阅会每个按键发一次 geocoding，先节流
        _weatherCitySub = SettingsVM.WhenAnyValue(x => x.WeatherCity)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(600), RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(city => _ = WeatherVM.RefreshWeatherAsync(city));

        // 天气快照推给 DJ 串场的环境感知（时间/天气上下文，见 DJService.BuildTransitionPrompt）
        WeatherVM.WeatherSnapshotChanged += OnWeatherSnapshotChanged;

        // 酷狗 20028 风控：命中挑战时自动弹浏览器滑块验证（冷却限频），完成后播放自然恢复
        if (_kugouVerification != null)
            _kugouVerification.ChallengeDetected += OnKugouChallengeDetected;

        // Set URL resolver for re-fresh of online track URLs (prevents 403 from expired links)
        if (_audioService is Services.AudioService audioSvc)
        {
            audioSvc.SetUrlResolver(async id => await musicSearchService.GetPlayUrlAsync(id));
            audioSvc.SetTrackUrlResolver((track, cancellationToken) =>
                ResolveTrackUrlAsync(track, requireAlternative: false, cancellationToken));
            audioSvc.SetFallbackTrackUrlResolver((track, cancellationToken) =>
                ResolveTrackUrlAsync(track, requireAlternative: true, cancellationToken));
            audioSvc.SetNextCallback(GetNextTrackForAudioServiceAsync);
        }

        ToggleSettingsCommand = ReactiveCommand.Create(() => { IsSettingsOpen = !IsSettingsOpen; });
        ToggleLibraryCommand = ReactiveCommand.Create(() => { IsLibraryOpen = !IsLibraryOpen; });
        OpenPlaylistCommand = ReactiveCommand.Create(() =>
        {
            PlaylistVM.TabIndex = 0;
            IsLibraryOpen = true;
        });
        OpenFavoritesCommand = ReactiveCommand.Create(() =>
        {
            PlaylistVM.TabIndex = 1;
            IsLibraryOpen = true;
        });
        OpenSearchCommand = ReactiveCommand.Create(() =>
        {
            PlaylistVM.TabIndex = 2;
            IsLibraryOpen = true;
        });
        OpenProgramCommand = ReactiveCommand.CreateFromTask(OpenProgramAsync);
        OpenKugouPlaylistsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            PlaylistVM.TabIndex = 4;
            IsLibraryOpen = true;
            await PlaylistVM.LoadKugouPlaylistsAsync();
        });
        RefreshProgramCommand = ReactiveCommand.CreateFromTask(() => LoadProgramAsync(force: true));
        PlayProgramTrackCommand = ReactiveCommand.Create<RecommendedTrack>(PlayProgramTrack);
        ToggleCharacterPickerCommand = ReactiveCommand.Create(() => { IsCharacterPickerOpen = !IsCharacterPickerOpen; });
        ToggleThemeCommand = ReactiveCommand.Create(() => { IsDarkMode = !IsDarkMode; });
        ToggleCompactModeCommand = ReactiveCommand.Create(ToggleCompactMode);
        ToggleLyricsModeCommand = ReactiveCommand.Create(ToggleLyricsMode);
        CycleSpectrumStyleCommand = ReactiveCommand.Create(CycleSpectrumStyle);
        ToggleCompactTopmostCommand = ReactiveCommand.Create(() =>
        {
            SettingsVM.CompactModeTopmost = !SettingsVM.CompactModeTopmost;
            SettingsVM.SaveUiStateCommand.Execute().Subscribe();
        });
        ToggleCompactLyricsCommand = ReactiveCommand.Create(() =>
        {
            SettingsVM.CompactShowLyrics = !SettingsVM.CompactShowLyrics;
            SettingsVM.SaveUiStateCommand.Execute().Subscribe();
        });
        _compactLyricsSub = this.WhenAnyValue(
            x => x.SettingsVM.CompactShowLyrics,
            x => x.LyricsVM.HasCurrentLine,
            (show, hasLine) => show && hasLine)
            .Subscribe(visible => IsCompactLyricsVisible = visible);
        _radioFxSub = this.WhenAnyValue(x => x.SettingsVM.RadioSoundFxEnabled)
            .Subscribe(enabled => _audioService.IsRadioSoundFxEnabled = enabled);
        UseDarkThemeCommand = ReactiveCommand.Create(() => { IsDarkMode = true; });
        UseLightThemeCommand = ReactiveCommand.Create(() => { IsDarkMode = false; });

        // Apply initial theme variant + sync RequestedThemeVariant on change (spec §5.4.2)
        if (Avalonia.Application.Current is { } app0)
            app0.RequestedThemeVariant = IsDarkMode ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        _darkModePersistSub = this.WhenAnyValue(x => x.IsDarkMode)
            .Skip(1)
            .Subscribe(isDark =>
            {
                if (Avalonia.Application.Current is { } app)
                    app.RequestedThemeVariant = isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                SettingsVM.IsDarkMode = isDark;
                SettingsVM.SaveUiStateCommand.Execute().Subscribe();
        });
        ToggleCurrentFavoriteCommand = ReactiveCommand.Create(ToggleCurrentFavorite);
        DislikeCurrentTrackCommand = ReactiveCommand.Create(() => RecordCurrentTrackFeedback(MusicFeedbackAction.Dislike));
        SimilarToCurrentTrackCommand = ReactiveCommand.Create(() => RecordCurrentTrackFeedback(MusicFeedbackAction.Similar));
        CalmerRecommendationCommand = ReactiveCommand.Create(() => RecordCurrentTrackFeedback(MusicFeedbackAction.Calmer));
        EnergeticRecommendationCommand = ReactiveCommand.Create(() => RecordCurrentTrackFeedback(MusicFeedbackAction.Energetic));
        TellSongStoryCommand = ReactiveCommand.CreateFromTask(TellSongStoryAsync);
        SelectCharacterCommand = ReactiveCommand.Create<CharacterProfile>(character =>
        {
            SwitchCharacter(character);
            _ = AnnounceCharacterGreetingAsync(_lifetimeCts.Token);
        });

        // Re-apply character when settings are saved
        _characterSettingsHandler = () => SwitchCharacter(SelectedCharacter);
        SettingsVM.CharacterSettingsChanged += _characterSettingsHandler;
        _languageTtsSub = SettingsVM.WhenAnyValue(x => x.SelectedLanguage, x => x.TtsEnabled)
            .Skip(1)
            .Subscribe(_ => SwitchCharacter(SelectedCharacter));
        _speechMixSub = SettingsVM.WhenAnyValue(x => x.SpeechMixMode)
            .Subscribe(mode => _audioService.SetSpeechMixMode(mode));
        _spectrumStyleSub = SettingsVM.WhenAnyValue(x => x.SelectedSpectrumStyle)
            .Subscribe(style => SpectrumVM.SelectedStyle = style);
        _onLanguageChanged = () =>
        {
            RefreshLocalizedProgramText();
            WeatherVM.RebuildTooltips();
        };
        AppLanguage.Changed += _onLanguageChanged;
        RefreshLocalizedClockText();

        // Sync STT language with settings
        if (_sttService is WhisperSttService whisper)
        {
            whisper.Language = SettingsVM.SelectedLanguage == "en" ? "en" : "zh";
            _sttLanguageSub = SettingsVM.WhenAnyValue(x => x.SelectedLanguage)
                .Subscribe(lang => whisper.Language = lang == "en" ? "en" : "zh");
        }

        _trackEndedSub = _audioService.TrackEnded
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(current =>
            {
                if (current == null) return;
                // 自然播完先结算画像（Completed + 清除跳过待判态），再走自动续播
                _listeningProfile?.NotifyPlaybackEndedNaturally(current);
                _ = HandleAutoRadioTrackEndedAsync(current);
            });

        _trackChangedSub = _audioService.TrackChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track =>
            {
                IsCurrentFavorite = track?.IsFavorite == true;
                HasCurrentTrack = track != null;
            });

        // 跳过判定必须与 TrackChanged 发射同步评估：经 ObserveOn 派发到 UI 线程时，
        // 新曲的进度样本可能已把旧曲样本覆盖，导致漏记跳过。判定在服务内加锁，线程安全。
        _profileTrackChangedSub = _listeningProfile == null
            ? null
            : _audioService.TrackChanged
                .Subscribe(track => _listeningProfile.NotifyTrackSwitched(track));

        // TrackChanged 也会在载入列表、删除曲目和重试音源时触发；只有真正进入播放态
        // 才能算作已播放历史，避免尚未播放的歌曲污染 DJ 的风格上下文。
        _playbackHistorySub = _audioService.StateChanged
            .Subscribe(state =>
            {
                var current = _audioService.CurrentTrack;
                if (state == PlaybackState.Playing)
                {
                    if (current != null)
                    {
                        _recommendationService.RecordPlayedTrack(current);
                        _listeningProfile?.NotifyPlaybackStarted(current);
                    }
                }
                else if (state == PlaybackState.Paused)
                {
                    // 暂停后再切歌不算跳过（前曲非播放态）；不响应 Stopped：
                    // 在线曲目的换曲主路径会先短暂 Stopped 再 TrackChanged
                    _listeningProfile?.NotifyPlaybackPaused();
                }
            });

        // 跳过判定用的进度样本：样本与曲目身份绑定（500ms 粒度，仅缓存一个数值）
        _positionSampleSub = _listeningProfile == null
            ? null
            : _audioService.PositionChanged
                .Subscribe(position =>
                {
                    var current = _audioService.CurrentTrack;
                    if (current != null)
                        _listeningProfile.NotifyPositionSampled(current, position);
                });

        _playbackRecoverySub = _audioService.PlaybackRecoveryNotices?
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notice =>
            {
                HasPlaybackRecoveryFailure = notice.IsBlocked;
                PlaybackRecoveryMessage = notice.Message;
            });

        // 统一电台状态机：从子 VM flags 派生 CurrentState（spec §5.2）。
        // 源生成器 OAPH 契约：ToProperty 返回的 helper 必须赋给生成的
        // _currentStateHelper 字段，getter 才能读到派生值（null 回退是初始占位）
        _currentStateHelper = this.WhenAnyValue(
                x => x.ChatVM.HasFailure,
                x => x.HasPlaybackRecoveryFailure,
                x => x.ChatVM.IsSpeaking,
                x => x.PlaylistVM.IsSearching,
                x => x.ChatVM.IsProcessing,
                x => x.PlayerVM.IsPlaying,
                (chatFailure, playbackFailure, speaking, searching, processing, playing) =>
                    DeriveRadioState(chatFailure || playbackFailure, speaking, searching, processing, playing))
            .ObserveOn(RxApp.MainThreadScheduler)
            .ToProperty(this, nameof(CurrentState));

        // 1s 时钟推进（spec §5.5）
        _clockSub = Observable.Interval(TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                Now = DateTimeOffset.Now;
                RefreshLocalizedClockText();
            });
    }

    private async Task<TrackUrlResolution?> ResolveTrackUrlAsync(
        Track track,
        bool requireAlternative,
        CancellationToken cancellationToken)
    {
        var sourceId = track.SourceId ?? track.Id;
        if (_musicSearchService is not IMusicSourceBroker multi)
        {
            if (requireAlternative)
                return null;

            var directUrl = await _musicSearchService.GetPlayUrlAsync(sourceId, cancellationToken);
            return string.IsNullOrWhiteSpace(directUrl)
                ? null
                : new TrackUrlResolution(directUrl, sourceId, track.ProviderMetadata);
        }

        var onlineTrack = new OnlineTrack
        {
            Id = sourceId,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            DurationMs = (long)track.Duration.TotalMilliseconds,
            ProviderMetadata = new Dictionary<string, string>(
                track.ProviderMetadata,
                StringComparer.OrdinalIgnoreCase)
        };

        // AudioService 的 _trackUrlResolver 委托只在刷新/恢复语境被调用（重试前刷新、
        // 播放前后台换新直链），必须 forceRefresh 绕过缓存——否则重试会拿到刚失败的陈旧 URL
        string? url;
        ResolveTrackResult? resolved = null;
        if (requireAlternative)
        {
            url = await multi.GetAlternativePlayUrlAsync(onlineTrack, cancellationToken);
        }
        else
        {
            resolved = await multi.ResolveTrackAsync(onlineTrack, forceRefresh: true, cancellationToken);
            url = resolved?.Url;
        }

        return string.IsNullOrWhiteSpace(url)
            ? null
            : new TrackUrlResolution(
                url,
                onlineTrack.Id,
                onlineTrack.ProviderMetadata,
                resolved?.Codec,
                resolved?.BitrateKbps,
                resolved?.IsPreview ?? false,
                resolved?.Headers);
    }

    private void ToggleCurrentFavorite()
    {
        var current = _audioService.CurrentTrack;
        if (current == null) return;

        if (!PlaylistVM.Tracks.Contains(current))
            PlaylistVM.AddExternalTrack(current);

        PlaylistVM.ToggleFavoriteCommand.Execute(current).Subscribe();
        // 命令内部切换的是列表内的匹配实例（与 current 可能不是同一引用），必须从匹配实例回读
        var favorited = PlaylistVM.FindMatchingTrack(current)?.IsFavorite ?? current.IsFavorite;
        IsCurrentFavorite = favorited;

        // 收藏即"喜欢"信号（原反馈行 LIKE 已并入心形）：正向反馈同时落会话与长期画像，
        // 推荐与 DJ 聊天两条消费链都会越来越偏向这类歌。取消收藏只动歌单不回撤画像
        // （事件为唯一事实不可撤回，听感影响随半衰期自然衰减）。
        if (!favorited)
            return;

        var trackId = current.SourceId;
        if (string.IsNullOrWhiteSpace(trackId))
            trackId = current.Id;
        if (!string.IsNullOrWhiteSpace(trackId))
            _recommendationService.RecordFeedback(new UserMusicFeedback
            {
                TrackId = trackId,
                Action = MusicFeedbackAction.Like
            });
        _listeningProfile?.RecordEvent(new ListeningEventData
        {
            Type = ListeningEventType.Like,
            Title = current.Title,
            Artist = current.Artist,
            SourceId = trackId ?? string.Empty
        });
        ChatVM.AddAssistantMessage(AppLanguage.T(
            "已收藏，这首也会记进你的长期口味画像。",
            "Favorited — this also goes into your long-term taste profile."));
    }

    private async Task OpenProgramAsync()
    {
        PlaylistVM.TabIndex = 3;
        IsLibraryOpen = true;
        if (!HasCurrentRadioProgram)
            await LoadProgramAsync(force: false);
    }

    private async Task LoadProgramAsync(bool force, string? intentKey = null)
    {
        if (IsDisposed || (!force && HasCurrentRadioProgram))
            return;

        var gateAcquired = false;
        try
        {
            await _programLoadGate.WaitAsync(_lifetimeCts.Token);
            gateAcquired = true;

            if (IsDisposed || (!force && HasCurrentRadioProgram))
                return;

            IsProgramLoading = true;
            ProgramStatusText = AppLanguage.T("DJ 正在编排节目单…", "The DJ is curating your program...");
            var request = CreateRecommendationRequest(_audioService.CurrentTrack, intentKey);
            var program = _recommendationService is RecommendationService recommendationService
                ? await recommendationService.CreateProgramAsync(request, _lifetimeCts.Token)
                : await _recommendationService.CreateProgramAsync(request).WaitAsync(_lifetimeCts.Token);
            UpdateCurrentProgram(program);
            ProgramStatusText = HasCurrentRadioProgram
                ? string.Empty
                : AppLanguage.T("暂时没有找到可播放的候选歌曲，请稍后重新编排。", "No playable candidates were found. Try refreshing the program later.");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || IsDisposed)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load radio program");
            ProgramStatusText = AppLanguage.T("节目单生成失败，请检查 AI 与音源连接后重试。", "Program generation failed. Check the AI and music source connections and try again.");
        }
        finally
        {
            if (gateAcquired)
            {
                IsProgramLoading = false;
                _programLoadGate.Release();
            }
        }
    }

    private void PlayProgramTrack(RecommendedTrack item)
    {
        if (IsDisposed || item is not { IsPlayable: true } || string.IsNullOrWhiteSpace(item.Track.FilePath))
            return;

        var existing = PlaylistVM.FindMatchingTrack(item.Track);
        if (existing == null)
        {
            PlaylistVM.AddExternalTrack(item.Track);
            existing = PlaylistVM.FindMatchingTrack(item.Track) ?? item.Track;
        }

        if (PlaylistVM.Tracks.Any(track => IsSameTrackIdentity(track, existing)))
            _audioService.PlayTrack(existing);
    }

    private RecommendationRequest CreateRecommendationRequest(Track? current, string? intentKey = null)
    {
        var recentlyPlayed = GetRecentlyPlayedSnapshot();
        intentKey ??= RecommendationIntentKeys.ContinueStation;
        return new RecommendationRequest
        {
            UserIntentKey = intentKey,
            UserIntent = intentKey == RecommendationIntentKeys.SimilarToCurrent
                ? AppLanguage.T("与当前歌曲气质相近", "similar in vibe to the current track")
                : string.Empty,
            CurrentTrack = current,
            Favorites = PlaylistVM.Favorites.ToList(),
            Playlist = PlaylistVM.Tracks.ToList(),
            RecentlyPlayed = recentlyPlayed,
            ExcludedTracks = recentlyPlayed
        };
    }

    private List<Track> GetRecentlyPlayedSnapshot()
        => _recommendationService.RecentlyPlayed?.ToList() ?? new List<Track>();

    private void UpdateCurrentProgram(RadioProgram? program)
    {
        // 服务侧持有的节目单可能停留在旧语言（语言切换只重译了 VM 副本），
        // 进入 VM 前按当前语言统一重译，避免旧语言节目单经自动续播回灌并触发错误语言的 DJ 开场白
        if (program != null)
            RecommendationService.ApplyLocalization(program);
        // 引用门控：自动续播每首都会回灌服务侧当前节目单，只有真正新生成的那份才触发台呼
        var isNewProgram = program != null && !ReferenceEquals(program, CurrentRadioProgram);
        CurrentRadioProgram = program;
        HasCurrentRadioProgram = program?.Tracks.Any(track => track.IsPlayable) == true;
        if (isNewProgram && HasCurrentRadioProgram)
            _audioService.PlayRadioSoundFx(RadioFxKind.StationChime);
    }

    private void RefreshLocalizedProgramText()
    {
        RefreshLocalizedClockText();
        if (CurrentRadioProgram != null)
        {
            RecommendationService.ApplyLocalization(CurrentRadioProgram);
            // 链式绑定与 ItemsSource 都会对"同实例"短路，就地改字段不会重绘；
            // 换一份新 RadioProgram/新列表让标题与行容器整体重绑（RecommendedTrack 无 INPC）。
            CurrentRadioProgram = new RadioProgram
            {
                Title = CurrentRadioProgram.Title,
                Context = CurrentRadioProgram.Context,
                DjOpening = CurrentRadioProgram.DjOpening,
                Tracks = CurrentRadioProgram.Tracks
                    .Select(item => new RecommendedTrack
                    {
                        Track = item.Track,
                        Reason = item.Reason,
                        Tags = item.Tags.ToList(),
                        Score = item.Score,
                        Source = item.Source,
                        IsPlayable = item.IsPlayable,
                        PlayUrl = item.PlayUrl
                    })
                    .ToList()
            };
            foreach (var item in CurrentRadioProgram.Tracks)
                item.Track.RefreshLocalization();
        }

        ProgramStatusText = IsProgramLoading
            ? AppLanguage.T("DJ 正在编排节目单…", "The DJ is curating your program...")
            : HasCurrentRadioProgram
                ? string.Empty
                : IsProgramFailureText(ProgramStatusText)
                    ? AppLanguage.T("节目单生成失败，请检查 AI 与音源连接后重试。", "Program generation failed. Check the AI and music source connections and try again.")
                    : IsProgramEmptyText(ProgramStatusText)
                        ? AppLanguage.T("暂时没有找到可播放的候选歌曲，请稍后重新编排。", "No playable candidates were found. Try refreshing the program later.")
                        : AppLanguage.T(
                            "打开节目单时，DJ 会按当前收听风格生成下一组候选歌曲。",
                            "Open Program and the DJ will curate the next set from your current listening style.");
    }

    private void RefreshLocalizedClockText()
    {
        var culture = CultureInfo.GetCultureInfo(AppLanguage.Current == "en" ? "en-US" : "zh-CN");
        LocalizedDayOfWeek = Now.ToString("dddd", culture);
        LocalizedDate = Now.ToString("dd-MMM-yyyy", culture);
    }

    private static bool IsProgramFailureText(string value)
        => value.StartsWith("节目单生成失败", StringComparison.Ordinal) ||
           value.StartsWith("Program generation failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsProgramEmptyText(string value)
        => value.StartsWith("暂时没有找到可播放", StringComparison.Ordinal) ||
           value.StartsWith("No playable candidates", StringComparison.OrdinalIgnoreCase);

    // 空闲引导（反馈行与 STORY 共用）：静默早退会被当成"按钮无效"
    private static string NoTrackGuidanceText => AppLanguage.T(
        "现在没有在播的歌，先点一首或让我开个台吧。",
        "Nothing is playing yet — pick a song or ask me to start the station.");

    private async Task TellSongStoryAsync()
    {
        if (IsDisposed)
            return;

        var current = _audioService.CurrentTrack;
        if (current == null)
        {
            ChatVM.AddAssistantMessage(NoTrackGuidanceText);
            return;
        }
        var story = await _djService.GenerateSongStoryAsync(current, _lifetimeCts.Token);
        if (IsDisposed)
            return;

        if (story.Lines.Count == 0)
        {
            // LLM 失败/未配置时静默返回同样会被当成"按钮无效"
            ChatVM.AddAssistantMessage(AppLanguage.T(
                "这首的故事暂时没组织好，稍后再试试。",
                "Couldn't piece together a story for this one — try again in a bit."));
            return;
        }
        var joined = string.Join(" ", story.Lines.Select(l => l.Text));
        ChatVM.AddAssistantMessage(joined);
        await SpeakDjTextAsync(joined);
    }

    private void RecordCurrentTrackFeedback(MusicFeedbackAction action)
    {
        var current = _audioService.CurrentTrack;
        var trackId = current?.SourceId;
        if (string.IsNullOrWhiteSpace(trackId))
            trackId = current?.Id;
        if (string.IsNullOrWhiteSpace(trackId))
        {
            ChatVM.AddAssistantMessage(NoTrackGuidanceText);
            return;
        }

        _recommendationService.RecordFeedback(new UserMusicFeedback
        {
            TrackId = trackId,
            Action = action
        });

        // 长期画像同步记录同一信号（Calmer/Energetic 事件本身即 mood 信号，不另记 MoodSet）
        if (current != null && _listeningProfile != null)
        {
            var profileType = action switch
            {
                MusicFeedbackAction.Dislike => ListeningEventType.Dislike,
                MusicFeedbackAction.Similar => ListeningEventType.Similar,
                MusicFeedbackAction.Calmer => ListeningEventType.Calmer,
                MusicFeedbackAction.Energetic => ListeningEventType.Energetic,
                _ => (ListeningEventType?)null,
            };
            if (profileType.HasValue)
                _listeningProfile.RecordEvent(new ListeningEventData
                {
                    Type = profileType.Value,
                    Title = current.Title,
                    Artist = current.Artist,
                    SourceId = trackId,
                });
        }

        // CALM/FIRE 同步切换会话级氛围偏好，让按钮立即影响后续推荐
        if (action == MusicFeedbackAction.Calmer)
            _recommendationService.SetMoodBias("calm");
        else if (action == MusicFeedbackAction.Energetic)
            _recommendationService.SetMoodBias("energetic");

        // NOPE 立即切歌：没人会把不喜欢的歌听完；会话避开已落，Next 的推荐回调不会再选它。
        // 单曲队列下 Next 会绕回重播本曲（刚拉黑的歌从头再来是二次折磨），切不动就只记录不切。
        var skipRequested = action == MusicFeedbackAction.Dislike && CanSkipAwayFromCurrentTrack();
        if (skipRequested)
            _audioService.Next();

        // SIM 立即以当前歌为锚重排节目单（不打断在播曲目，歌单模式只更新节目单 tab）
        if (action == MusicFeedbackAction.Similar)
            _ = LoadProgramAsync(force: true, intentKey: RecommendationIntentKeys.SimilarToCurrent);

        // 反馈只影响后续节目单，用户无感即"按钮无效"：每个动作给一句即时可见回应
        var ack = action switch
        {
            MusicFeedbackAction.Dislike => skipRequested
                ? AppLanguage.T(
                    "收到，这就跳过，本轮不再安排它。",
                    "Got it — skipping it now; benched for this session.")
                : AppLanguage.T(
                    "收到，本轮不再安排它。",
                    "Got it — benched for this session."),
            MusicFeedbackAction.Similar => AppLanguage.T(
                "收到，正在以这首为锚重新编排接下来的节目单。",
                "Got it — re-curating the upcoming program around this track."),
            MusicFeedbackAction.Calmer => AppLanguage.T(
                "好，接下来把氛围调得舒缓一点。",
                "Sure — easing the vibe down from here."),
            MusicFeedbackAction.Energetic => AppLanguage.T(
                "收到，接下来往燃一点的方向调。",
                "Got it — turning the energy up from here."),
            _ => string.Empty,
        };
        if (!string.IsNullOrEmpty(ack))
            ChatVM.AddAssistantMessage(ack);
    }

    /// <summary>
    /// NOPE 切歌前置判定：电台模式经推荐回调取曲（Dislike 已落会话避开）永远切得动；
    /// 列表模式下队列里除当前曲外还有别的曲目才切——单曲绕回会重播刚拉黑的歌。
    /// </summary>
    private bool CanSkipAwayFromCurrentTrack()
    {
        if (_audioService.RepeatMode == "radio")
            return true;

        var current = _audioService.CurrentTrack;
        return _audioService.Playlist.Any(track => !IsSameTrackIdentity(track, current));
    }

    private void SwitchCharacter(CharacterProfile character)
    {
        if (character == null) return;
        SelectedCharacter = character;
        IsCharacterPickerOpen = false;

        // Apply per-character overrides from settings if available
        var ov = SettingsVM.GetOverride(character.Id);
        var voiceId = ov?.VoiceId ?? character.VoiceId;
        var personality = ov?.Personality ?? character.PersonalityPrompt;

        _djService.Initialize(new DJProfile
        {
            Name = character.DisplayName,
            Description = character.Description,
            VoiceId = voiceId,
            TtsEnabled = SettingsVM.TtsEnabled,
            SystemPrompt = personality,
            Language = SettingsVM.SelectedLanguage
        });

        Log.Information("Switched to character: {Name} (voice: {Voice})", character.DisplayName, voiceId);
    }

    // Parameterless ctor for designer/testing only; production uses DI with shared HttpClient singleton
    public MainWindowViewModel() : this(
        new AudioService(),
        new DJService(new LLMService(new System.Net.Http.HttpClient()), new EdgeTtsService(new System.Net.Http.HttpClient())),
        new LLMService(new System.Net.Http.HttpClient()),
        new WindowsSecureStorage(),
        new MusicSourceBroker(new System.Net.Http.HttpClient()),
        new WhisperSttService(),
        PlaylistViewModel.DefaultPlaylistFile,
        SettingsViewModel.DefaultSettingsFile)
    {
    }

    public async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadLocalStateAsync(cancellationToken);
        await StartSessionAsync(cancellationToken);
    }

    /// <summary>本地状态恢复：设置/歌单/主题/简洁模式/角色，纯本地读取不依赖网络，先于音乐代理执行。</summary>
    public async System.Threading.Tasks.Task LoadLocalStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed)
            return;

        await SettingsVM.LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed)
            return;

        await PlaylistVM.LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed)
            return;

        if (_localLibrary != null)
        {
            await _localLibrary.LoadAsync(cancellationToken);
            // 补录 playlist.json 中尚未进入索引的本地文件；已有曲库时也不能漏掉旧导入项。
            var missingFiles = PlaylistVM.Tracks
                .Where(track => string.IsNullOrWhiteSpace(track.SourceId) &&
                                !string.IsNullOrWhiteSpace(track.FilePath) &&
                                System.IO.File.Exists(track.FilePath) &&
                                !_localLibrary.ContainsFile(track.FilePath))
                .Select(track => track.FilePath)
                .ToArray();
            if (missingFiles.Length > 0)
                await _localLibrary.AddFilesAsync(missingFiles, cancellationToken);
        }

        // 长期收听画像：本地文件读取失败按空画像运行，不阻塞启动；
        // 开关初值在 settings 加载完成后推送（DI 构造顺序不保证 settings 先就绪）
        if (_listeningProfile != null)
        {
            await _listeningProfile.LoadAsync(cancellationToken);
            _listeningProfile.Enabled = SettingsVM.ListenerProfileEnabled;
        }

        // 天气取数：设置页有城市时经城市订阅自动触发，这里只补 IP 定位路径的首次取数
        if (string.IsNullOrWhiteSpace(SettingsVM.WeatherCity))
            _ = WeatherVM.RefreshWeatherAsync(SettingsVM.WeatherCity);

        IsDarkMode = SettingsVM.IsDarkMode;
        // 启动时恢复上次的窗口模式（简洁/标准）与歌词模式
        IsCompactMode = SettingsVM.StartInCompactMode;
        IsLyricsMode = SettingsVM.ShowLyricsInStage;
        // Apply initial character
        SwitchCharacter(SelectedCharacter);
        _audioService.SetSpeechMixMode(SettingsVM.SpeechMixMode);
    }

    /// <summary>会话开场：账号状态刷新、欢迎语与开播推荐，依赖音源代理就绪。</summary>
    public async System.Threading.Tasks.Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed)
            return;

        await SettingsVM.RefreshAccountStatusAsync();

        // 在线曲目 v2 歌单不再持久化临时直链：播放前由 AudioService 懒解析，
        // 启动无需批量刷新 URL，避免可播前的额外等待
        await AnnounceWelcomeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed)
            return;

        // AI startup recommendation: analyze playlist and recommend a song
        _ = AnnounceStartupFollowupAsync(_lifetimeCts.Token);
    }

    private void ToggleCompactMode()
    {
        IsCompactMode = !IsCompactMode;
        if (IsCompactMode)
            CloseOverlays();

        // 记住窗口模式，下次启动直接进入上次模式（复用主题切换的保存链路）
        SettingsVM.StartInCompactMode = IsCompactMode;
        SettingsVM.SaveUiStateCommand.Execute().Subscribe();
    }

    private void ToggleLyricsMode()
    {
        IsLyricsMode = !IsLyricsMode;
        // 与简洁模式同款记忆链路：时钟舞台与歌词视图的偏好跨启动保留
        SettingsVM.ShowLyricsInStage = IsLyricsMode;
        SettingsVM.SaveUiStateCommand.Execute().Subscribe();
    }

    /// <summary>舞台频谱点击：循环频谱样式（柱状→镜像→波形→粒子），与设置页下拉同源，走 UI 状态保存链路持久化。</summary>
    private void CycleSpectrumStyle()
    {
        var styles = SettingsVM.SpectrumStyles;
        var current = styles.FindIndex(s => string.Equals(s.Id, SettingsVM.SelectedSpectrumStyle, StringComparison.OrdinalIgnoreCase));
        SettingsVM.SelectedSpectrumStyle = styles[(current + 1) % styles.Count].Id;
        SettingsVM.SaveUiStateCommand.Execute().Subscribe();
    }

    public void CloseOverlays()
    {
        IsSettingsOpen = false;
        IsLibraryOpen = false;
        IsCharacterPickerOpen = false;
    }

    private async System.Threading.Tasks.Task AnnounceStartupFollowupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(1400, cancellationToken);
            if (IsDisposed)
                return;

            // 非空歌单的开台播放已并入 AnnounceWelcomeAsync（欢迎语后直接播 DJ 推荐新歌），
            // 这里只保留空歌单的引导开场
            if (PlaylistVM.Tracks.Count == 0)
                await AnnounceEmptyLibraryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsDisposed)
        {
            // 应用关闭时取消启动串场，不再访问已释放的 ViewModel。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup follow-up failed");
        }
    }

    private async System.Threading.Tasks.Task AnnounceEmptyLibraryAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed || cancellationToken.IsCancellationRequested)
            return;

        var text = AppLanguage.T(
            "歌单还是空的。告诉我今天的心情，或者搜一首歌，我来帮你开台。",
            "No tracks yet. Tell me a mood or search a song, and I'll build today's station.");

        ChatVM.AddAssistantMessage(text);

        await SpeakDjTextAsync(text, cancellationToken);
        if (!IsDisposed && !cancellationToken.IsCancellationRequested &&
            PlaylistVM.Tracks.Count > 0 && !_audioService.IsPlaying)
            _audioService.Play();
    }

    private async System.Threading.Tasks.Task AnnounceWelcomeAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed || cancellationToken.IsCancellationRequested)
            return;

        var text = AppLanguage.T(
            $"这里是 {SelectedCharacter.DisplayName}，电台已经上线。我会陪你听一会儿歌，也会按今天的心情帮你找下一首。",
            $"This is {SelectedCharacter.DisplayName}. The station is online. I'll keep you company and tune the music to your mood.");

        ChatVM.AddAssistantMessage(text);
        DjVisualCue?.Invoke("smile", "wave");

        if (PlaylistVM.Tracks.Count == 0)
        {
            // 空歌单保留引导开场（AnnounceStartupFollowupAsync），不发起推荐也不开播
            await SpeakDjTextAsync(text, cancellationToken);
            return;
        }

        // 开台即播 DJ 推荐新歌：推荐搜索与欢迎语 TTS 并行，说完衔接介绍词开播。
        // LoadTracks 后 CurrentTrack 默认指向歌单第 1 首（未播放态），以它为开台基线：
        // staleness 守卫按"基线未被用户换掉"判定，若传 null 会在真实语义下全程短路。
        // 预取挂独立 linked CTS：链条放弃（让位/超时）时显式取消，
        // 防止迟到结果经 staleness 闸门（兜底播的正是基线曲，CurrentTrack 未变）推节目单气泡/台呼
        var baseline = _audioService.CurrentTrack;
        using var prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var prefetched = GetRecommendedTrackAsync(baseline, prefetchCts.Token);
        await SpeakDjTextAsync(text, cancellationToken);
        if (IsDisposed || cancellationToken.IsCancellationRequested)
            return;

        await StartStationPlaybackAsync(baseline, prefetched, prefetchCts, cancellationToken);
    }

    /// <summary>开台推荐预取预算：欢迎语 TTS 已并行消化大半，超时不再等待，交给歌单轮换先出声。</summary>
    private static readonly TimeSpan StartupRecommendationBudget = TimeSpan.FromSeconds(15);

    /// <summary>开台播放：消费欢迎语期间预取的推荐并播放 DJ 挑的新歌；
    /// 推荐超预算或失败时沿 歌单轮换→歌单第 1 首 阶梯回退，保证开台总能出声。</summary>
    private async System.Threading.Tasks.Task StartStationPlaybackAsync(
        Track? baselineTrack,
        System.Threading.Tasks.Task<Track?> prefetchedRecommendation,
        CancellationTokenSource prefetchCts,
        CancellationToken cancellationToken)
    {
        // 用户已在播（如欢迎语前自己点了歌）：开台链让位不抢播，预取一并作废
        if (_audioService.IsPlaying)
        {
            prefetchCts.Cancel();
            return;
        }

        var played = false;
        try
        {
            Track? recommended = null;
            try
            {
                recommended = await prefetchedRecommendation.WaitAsync(StartupRecommendationBudget);
            }
            catch (TimeoutException)
            {
                prefetchCts.Cancel();
                Log.Information(
                    "Startup recommendation exceeded {Budget:F0}s budget, falling back to playlist rotation",
                    StartupRecommendationBudget.TotalSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 预取自身 fault（真实服务几乎不可能）：留痕后照常走轮换阶梯
                Log.Warning(ex, "Startup recommendation prefetch failed");
            }

            // 开台必开口：stationOpening 标记走"第一首歌"开场并豁免静默节奏
            played = recommended != null && await PlayWithFreshRecommendation(baselineTrack, recommended, stationOpening: true);
            if (!played && !IsDisposed && !cancellationToken.IsCancellationRequested)
                played = await PlayWithPlaylistRotation(baselineTrack, stationOpening: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsDisposed)
        {
            // 应用关闭时取消开台播放。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup station playback failed");
        }

        if (!played && !IsDisposed && !cancellationToken.IsCancellationRequested &&
            !_audioService.IsPlaying && PlaylistVM.Tracks.Count > 0)
        {
            _audioService.Play();
        }
    }

    private async System.Threading.Tasks.Task AnnounceCharacterGreetingAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (IsDisposed || cancellationToken.IsCancellationRequested)
                return;

            var prompt = SettingsVM.SelectedLanguage == "en"
                ? "You have just taken over this radio station. Greet me in your own DJ personality and voice style. Do not mention settings."
                : "你刚刚接管这个电台。请用你的主播人设和语气，主动向我打个招呼，不要提到设置。";

            var response = await _djService.GenerateChatResponseAsync(prompt, cancellationToken);
            if (IsDisposed || cancellationToken.IsCancellationRequested)
                return;

            // LLM 失败/未配置时 GenerateChatResponseAsync 返回兜底文案并置 LastChatFailure：
            // 问候路径不能把兜底或“请先在设置中配置 AI 服务。”当角色台词播报
            if (_djService.LastChatFailure is { })
                return;

            var text = StripDjControlTags(response);
            if (string.IsNullOrWhiteSpace(text)) return;

            ChatVM.AddAssistantMessage(text);
            DjVisualCue?.Invoke("smile", "wave");

            await SpeakDjTextAsync(text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsDisposed)
        {
            // 应用关闭时取消角色欢迎词。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to generate character greeting");
        }
    }

    private async System.Threading.Tasks.Task HandleAutoRadioTrackEndedAsync(Track current)
    {
        if (IsDisposed)
            return;

        if (Interlocked.Exchange(ref _autoRadioAdvancing, 1) == 1) return;
        if (_audioService.RepeatMode != "radio") { _autoRadioAdvancing = 0; return; }
        try
        {
            await _advanceGate.WaitAsync(_lifetimeCts.Token);
            try
            {
                // 等门期间手动 Next 可能已完成推进，本次自然结束续播作废
                if (IsDisposed || !IsSameTrack(_audioService.CurrentTrack, current))
                    return;

                // 换曲静默计数：在阶梯上游（推荐/轮换两档共用的入口）递增一次，
                // 两档决策读到同一状态；开口的档位会经 MarkTransitionSpoken 清零
                _transitionsSinceLastSpeech++;
                await DjTtsInterop.StopTtsWithoutBlockingUiAsync(_audioService, _lifetimeCts.Token);
                var success = await PlayWithFreshRecommendation(current);
                if (!success && !IsDisposed)
                    await PlayWithPlaylistRotation(current);
            }
            finally
            {
                _advanceGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || IsDisposed)
        {
            // 关闭时取消尚未完成的推荐、串场和播放切换。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto radio continuation failed");
        }
        finally
        {
            Interlocked.Exchange(ref _autoRadioAdvancing, 0);
        }
    }

    /// <summary>电台串场话密度：每 N 次换曲开口一次（对齐真实电台与 Spotify AI DJ 的 4~5 首一开口）。</summary>
    internal const int TransitionSpeechInterval = 4;

    /// <summary>
    /// 串场节奏决策（决定权在应用层而非 LLM："说不说"可控可测，LLM 只负责"说什么"）：
    /// 开台必说；跨时间带（如午后→傍晚）的第一首要开口报个到；其余静默到
    /// <see cref="TransitionSpeechInterval"/> 次换曲才再说。点歌/聊天的应答走聊天链路
    /// 天然必有回应，不经此调度。
    /// </summary>
    internal static bool ShouldSpeakTransition(
        bool stationOpening,
        int transitionsSinceLastSpeech,
        int? lastSpokenTimeBand,
        int currentTimeBand)
        => stationOpening
           || (lastSpokenTimeBand is { } band && band != currentTimeBand)
           || transitionsSinceLastSpeech >= TransitionSpeechInterval;

    /// <summary>记录"本次开口"：清零静默计数并以当前时间带刷新跨时段判定基准。</summary>
    private void MarkTransitionSpoken()
    {
        _transitionsSinceLastSpeech = 0;
        _lastSpokenTimeBand = DJService.GetTimeBand(DateTime.Now.Hour);
    }

    private async System.Threading.Tasks.Task<bool> PlayWithFreshRecommendation(
        Track? current,
        Track? preloadedRecommendation = null,
        bool stationOpening = false)
    {
        if (IsDisposed)
            return false;

        var recommended = preloadedRecommendation ?? await GetRecommendedTrackAsync(current, _lifetimeCts.Token);
        if (IsDisposed || recommended == null)
        {
            Log.Information("Fresh recommendation returned null, falling back to playlist rotation");
            return false;
        }

        if (current != null && IsSameTrackIdentity(recommended, current))
        {
            var retry = await GetRecommendedTrackAsync(current, _lifetimeCts.Token);
            if (IsDisposed)
                return false;

            if (retry != null && !IsSameTrackIdentity(retry, current))
                recommended = retry;
        }

        if (!PlaylistVM.Tracks.Any(t => IsSameTrackIdentity(t, recommended)))
        {
            if (IsDisposed)
                return false;
            PlaylistVM.AddExternalTrack(recommended);
        }

        // 串场节奏：决策权在应用层（stationOpening/静默计数/跨时间带），LLM 只负责"说什么"。
        // 开台时给串场的"上一首"传 null——开台链的 current 是未播过的基线曲，只作 staleness 参照，
        // 直接传给串场会伪造"刚刚放完《基线曲》"（83ec2f4 的第一首歌分支自此在生产接线）
        var speak = ShouldSpeakTransition(
            stationOpening, _transitionsSinceLastSpeech, _lastSpokenTimeBand,
            DJService.GetTimeBand(DateTime.Now.Hour));
        DJScript? script = speak
            ? await GenerateTrackIntroductionAsync(stationOpening ? null : current, recommended, _lifetimeCts.Token)
            : null;
        // IsPlaying 复查：介绍词生成/TTS 的长窗口内用户可能手动开播（全局播放键播的正是基线曲，
        // CurrentTrack 引用不变、staleness 拦不住），此时让位不抢播；电台链 EndReached 后恒 false 零影响。
        // 返回"是否真的开播"：stale/未播一律 false，开台链据此走兜底阶梯（电台链轮换同样被拦，行为等价）
        if (IsDisposed || _audioService.IsPlaying || !IsSameTrack(_audioService.CurrentTrack, current)) return false;

        if (script != null)
        {
            ChatVM.AddAssistantMessage(script.Text);
            DjVisualCue?.Invoke(script.Expression, script.Motion);
            await SpeakDjTextAsync(script.Text);
            MarkTransitionSpoken();
        }

        // AddExternalTrack 在宽松同曲命中时“合并且不入列”，随后严格身份查找会落空，
        // 导致“return true 却不播放”且上层跳过轮换兜底 → 电台停播。
        // 回退用与合并口径一致的宽松查找，保证总能拿到可播实例。
        var playable = PlaylistVM.Tracks.FirstOrDefault(t => IsSameTrackIdentity(t, recommended))
                       ?? PlaylistVM.FindMatchingTrack(recommended);
        if (playable != null && !_audioService.IsPlaying && IsSameTrack(_audioService.CurrentTrack, current))
        {
            _audioService.PlayTrack(playable);
            return true;
        }
        return false;
    }

    /// <summary>返回是否真的调用了 PlayTrack 开播（staleness 守卫拦下或列表失效时为 false，由上层兜底）。</summary>
    private async System.Threading.Tasks.Task<bool> PlayWithPlaylistRotation(Track? current, bool stationOpening = false)
    {
        if (IsDisposed)
            return false;

        var pool = PlaylistVM.Tracks.Where(t => t != current).ToList();
        if (pool.Count == 0) return false;

        var next = RecommendationService.PickDiversifiedTrack(pool, current);
        if (next == null) return false;

        if (IsDisposed)
            return false;

        if (!PlaylistVM.Tracks.Contains(next))
            PlaylistVM.AddExternalTrack(next);

        if (PlaylistVM.Tracks.IndexOf(next) < 0) return false;

        // 同推荐档：开台按"第一首歌"开场 + 应用层节奏决策（计数在推荐/轮换阶梯上游统一递增，
        // 两档看到同一状态，接力时不会重复计数）
        var speak = ShouldSpeakTransition(
            stationOpening, _transitionsSinceLastSpeech, _lastSpokenTimeBand,
            DJService.GetTimeBand(DateTime.Now.Hour));
        DJScript? script = speak
            ? await GenerateTrackIntroductionAsync(stationOpening ? null : current, next, _lifetimeCts.Token)
            : null;
        // 同推荐档：IsPlaying 复查防介绍词窗口内被抢播（开台链基线曲引用不变的竞态）
        if (IsDisposed || _audioService.IsPlaying || !IsSameTrack(_audioService.CurrentTrack, current)) return false;

        if (script != null)
        {
            ChatVM.AddAssistantMessage(script.Text);
            DjVisualCue?.Invoke(script.Expression, script.Motion);
            await SpeakDjTextAsync(script.Text);
            MarkTransitionSpoken();
        }

        // 串场期间列表可能被增删，旧索引会指向错误曲目，播放前必须重查
        var playable = PlaylistVM.Tracks.FirstOrDefault(t => IsSameTrack(t, next));
        if (playable != null && !IsDisposed && !_audioService.IsPlaying && IsSameTrack(_audioService.CurrentTrack, current))
        {
            _audioService.PlayTrack(playable);
            return true;
        }
        return false;
    }

    private void AttachRecommendationContext(Track? current)
    {
        if (current == null) return;

        var recentlyPlayed = GetRecentlyPlayedSnapshot();

        current.Tag = new RecommendationContext
        {
            Favorites = PlaylistVM.Favorites.ToList(),
            RecentlyPlayed = recentlyPlayed,
            ExcludedTracks = PlaylistVM.Tracks.Concat(recentlyPlayed).ToList()
        };
    }

    private async System.Threading.Tasks.Task<Track?> GetRecommendedTrackAsync(Track? current, CancellationToken cancellationToken)
    {
        AttachRecommendationContext(current);
        var request = CreateRecommendationRequest(current);

        try
        {
            var recommended = await RequestRecommendedTrackAsync(request, cancellationToken);
            if (IsDisposed)
                return null;

            // 60 秒推荐超时被 AudioService 抛弃后它会自行做列表轮换兜底（当前曲目已变）：
            // 这条迟到管线不能再更新节目单/推开场白气泡，否则播出内容与 UI 错位。
            // 开台链超时后显式取消 linked token 走这里的取消分支，同理静默作废
            if (!IsSameTrack(_audioService.CurrentTrack, current))
                return null;

            var prevOpening = CurrentRadioProgram?.DjOpening;
            UpdateCurrentProgram(_recommendationService.CurrentProgram);
            // 新节目单的开场白作为 DJ 气泡推荐理由（去重，避免续播每首刷屏）
            if (CurrentRadioProgram != null && CurrentRadioProgram.DjOpening != prevOpening
                && !string.IsNullOrWhiteSpace(CurrentRadioProgram.DjOpening))
            {
                ChatVM.AddAssistantMessage(CurrentRadioProgram.DjOpening);
            }
            if (recommended != null)
                return recommended;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested || IsDisposed)
        {
            // 滤波器须认传入令牌：开台预取的 linked CTS 取消后这里干净返回 null，
            // 否则 OCE 逃逸成 faulted task（无人 await）或误入下方 DJ 兜底白跑一轮
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Program recommendation failed, falling back to DJ single-track recommendation");
        }

        try
        {
            var fallback = await DjTtsInterop.RequestDjRecommendationAsync(_djService, current, _lifetimeCts.Token);
            return IsDisposed ? null : fallback;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || IsDisposed)
        {
            return null;
        }
    }

    private Task<Track?> GetNextTrackForAudioServiceAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
            return Task.FromResult<Track?>(null);

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return GetNextTrackForAudioServiceCoreAsync(cancellationToken);

        var completion = new TaskCompletionSource<Track?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRegistration = _lifetimeCts.Token.Register(
            () => completion.TrySetResult(null));
        // AudioService 的 60s 超时令牌到达即解除等待（底层推荐请求经 CoreAsync 贯穿同一令牌真正取消）
        var timeoutRegistration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await GetNextTrackForAudioServiceCoreAsync(cancellationToken));
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || IsDisposed)
            {
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Audio next-track callback failed");
                completion.TrySetResult(null);
            }
            finally
            {
                cancellationRegistration.Dispose();
                timeoutRegistration.Dispose();
            }
        });
        return completion.Task;
    }

    private async Task<Track?> GetNextTrackForAudioServiceCoreAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
            return null;

        await _advanceGate.WaitAsync(cancellationToken);
        try
        {
            if (IsDisposed)
                return null;

            await DjTtsInterop.StopTtsWithoutBlockingUiAsync(_audioService, cancellationToken);
            var current = _audioService.CurrentTrack;
            AttachRecommendationContext(current);
            var recommended = await GetRecommendedTrackAsync(current, cancellationToken);
            if (IsDisposed || !IsSameTrack(_audioService.CurrentTrack, current))
                return null;

            if (recommended != null &&
                !PlaylistVM.Tracks.Any(t => IsSameTrack(t, recommended)))
            {
                PlaylistVM.AddExternalTrack(recommended);
                // AddExternalTrack 宽松同曲命中时"合并且不入列"：返回 Tracks 内的既有实例，
                // 否则新实例不带收藏标记等状态，正在播的歌在 UI 上显示为未收藏
                var merged = PlaylistVM.FindMatchingTrack(recommended);
                if (merged != null)
                    recommended = merged;
            }

            return IsDisposed ? null : recommended;
        }
        finally
        {
            _advanceGate.Release();
        }
    }

    private Task<Track?> RequestRecommendedTrackAsync(
        RecommendationRequest request,
        CancellationToken cancellationToken)
        => _recommendationService is RecommendationService recommendationService
            ? recommendationService.GetNextTrackAsync(request, cancellationToken)
            : _recommendationService.GetNextTrackAsync(request).WaitAsync(cancellationToken);

    private Task<DJScript> GenerateTrackIntroductionAsync(
        Track? current,
        Track next,
        CancellationToken cancellationToken)
        => _djService is DJService djService
            ? djService.GenerateTrackIntroductionAsync(current, next, cancellationToken)
            : _djService.GenerateTrackIntroductionAsync(current, next).WaitAsync(cancellationToken);

    private void OnWeatherSnapshotChanged(WeatherInfo? weather)
        => _djService.UpdateWeatherSnapshot(weather);

    private static bool IsSameTrack(Track? left, Track? right) => TrackComparer.IsSameTrack(left, right);

    private static bool IsSameTrackIdentity(Track? left, Track? right) => TrackComparer.IsSameTrackIdentity(left, right);

    private static string StripDjControlTags(string text)
    {
        var cleaned = Regex.Replace(text, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<cmd>\s*\{.*?\}\s*</cmd>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return cleaned.Trim();
    }

    private async System.Threading.Tasks.Task SpeakDjTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (IsDisposed || !_djService.TtsEnabled || string.IsNullOrWhiteSpace(text)) return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token,
            cancellationToken);
        var token = linkedCts.Token;

        try
        {
            await _ttsLock.WaitAsync(token);
            token.ThrowIfCancellationRequested();

            try
            {
                var speechData = await DjTtsInterop.GenerateSpeechAsync(_djService, text, token);
                if (speechData is { Length: > 0 } && !IsDisposed && !token.IsCancellationRequested)
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                    // 只有先见过本次 TTS 的 playing=true 才接受 false 完成：
                    // 防止上一段 TTS 迟到的结束通知把等待提前放行（下一首在 DJ 人声未播完时开播）
                    var sawTtsPlaying = 0;
                    var sub = _audioService.TtsStateChanged.Subscribe(playing =>
                    {
                        if (playing)
                        {
                            System.Threading.Volatile.Write(ref sawTtsPlaying, 1);
                        }
                        else if (System.Threading.Volatile.Read(ref sawTtsPlaying) == 1)
                        {
                            tcs.TrySetResult(true);
                        }
                    });
                    try
                    {
                        // PlayTtsAudio 全同步：成功时此刻已发过 true；未见 true 说明播放启动失败，不必等待
                        _audioService.PlayTtsAudio(speechData);
                        if (System.Threading.Volatile.Read(ref sawTtsPlaying) == 1)
                        {
                            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested || IsDisposed)
                    {
                        // 应用关闭时停止等待 TTS 播放完成。
                    }
                    catch (TimeoutException)
                    {
                        Log.Warning("Timed out waiting for TTS playback to finish");
                    }
                    finally
                    {
                        sub.Dispose();
                    }
                }
            }
            finally
            {
                _ttsLock.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || IsDisposed)
        {
            // 应用关闭时取消尚未开始的串场任务。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to speak DJ text");
        }
    }

    /// <summary>
    /// 酷狗 20028 风控挑战回调：冷却与互斥通过后，后台运行验证流程
    /// （打开浏览器滑块页 + 轮询恢复）。不阻塞当前曲目的跳过/回退逻辑；
    /// 验证通过后后续曲目自然恢复播放。
    /// </summary>
    private void OnKugouChallengeDetected(KugouChallenge challenge)
    {
        var verification = _kugouVerification;
        if (verification == null || !verification.TryBeginAutoTrigger())
            return;

        var cookie = _accountStore?.KugouCookie;
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await verification.RunVerificationAsync(cookie, challenge.Hash, _lifetimeCts.Token);
                Log.Information("Kugou risk-control verification finished: {Outcome} (hash {Hash})",
                    outcome, challenge.Hash);
            }
            catch (Exception ex)
            {
                // 无 catch 的 fire-and-forget 异常无人观察（.NET 默认不上抛不记日志），现场不可诊断
                Log.Warning(ex, "Kugou risk-control verification failed (hash {Hash})", challenge.Hash);
            }
            finally
            {
                verification.EndVerification();
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        if (_kugouVerification != null)
            _kugouVerification.ChallengeDetected -= OnKugouChallengeDetected;
        // AudioService 由 DI 容器持有并在随后统一释放。这里不能在 Avalonia
        // 关闭线程同步 Stop NAudio，否则设备线程异常时会再次把窗口关闭卡住。
        _trackEndedSub?.Dispose();
        _trackChangedSub?.Dispose();
        _playbackPreflight?.Dispose();
        _profileTrackChangedSub?.Dispose();
        _playbackHistorySub?.Dispose();
        _positionSampleSub?.Dispose();
        _playbackRecoverySub?.Dispose();
        _darkModePersistSub?.Dispose();
        _compactLyricsSub?.Dispose();
        _radioFxSub?.Dispose();
        _languageTtsSub?.Dispose();
        _speechMixSub?.Dispose();
        _spectrumStyleSub?.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
        _sttLanguageSub?.Dispose();
        _clockSub?.Dispose();
        _weatherCitySub.Dispose();
        if (WeatherVM != null)
            WeatherVM.WeatherSnapshotChanged -= OnWeatherSnapshotChanged;
        WeatherVM?.Dispose();
        SettingsVM.CharacterSettingsChanged -= _characterSettingsHandler;
        PlayerVM?.Dispose();
        ChatVM?.Dispose();
        SpectrumVM?.Dispose();
        LyricsVM?.Dispose();
        PlaylistVM?.Dispose();
        SettingsVM?.Dispose();
    }
}
