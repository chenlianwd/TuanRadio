using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
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

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly IDJService _djService;
    private readonly ILLMService _llmService;
    private readonly IMusicSearchService _musicSearchService;
    private readonly IRecommendationService _recommendationService;
    private readonly ISttService _sttService;
    private readonly IDisposable _trackEndedSub;
    private readonly IDisposable _trackChangedSub;
    private readonly IDisposable _playbackHistorySub;
    private readonly IDisposable _clockSub;
    private readonly IDisposable _darkModePersistSub;
    private readonly IDisposable _languageTtsSub;
    private readonly IDisposable _speechMixSub;
    private readonly IDisposable _spectrumStyleSub;
    private IDisposable? _sttLanguageSub;
    private readonly Action _characterSettingsHandler;
    private readonly Action _onLanguageChanged;
    private int _autoRadioAdvancing;
    private int _disposed;
    private readonly SemaphoreSlim _ttsLock = new(1, 1);
    // 自然结束续播与手动 Next 的 nextCallback 两条推荐管线共用此门串行化，
    // 避免并发时双份推荐请求、双份加列表、双次切歌
    private readonly SemaphoreSlim _advanceGate = new(1, 1);
    private readonly SemaphoreSlim _programLoadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly MusicAccountStore? _accountStore;
    private readonly KugouVerificationService? _kugouVerification;

    public PlayerViewModel PlayerVM { get; }
    public PlaylistViewModel PlaylistVM { get; }
    public ChatViewModel ChatVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public SpectrumViewModel SpectrumVM { get; }

    public List<CharacterProfile> Characters { get; } = CharacterProfile.Presets;

    public event Action<string, string>? DjVisualCue; // expression, motion

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    [Reactive] public bool IsSettingsOpen { get; set; }
    [Reactive] public bool IsLibraryOpen { get; set; }
    [Reactive] public bool IsCharacterPickerOpen { get; set; }
    [Reactive] public CharacterProfile SelectedCharacter { get; set; }
    [Reactive] public bool IsDarkMode { get; set; } = true;
    [Reactive] public bool IsCurrentFavorite { get; set; }
    [Reactive] public bool IsCompactMode { get; set; }
    [Reactive] public RadioProgram? CurrentRadioProgram { get; set; }
    [Reactive] public bool HasCurrentRadioProgram { get; set; }
    [Reactive] public bool IsProgramLoading { get; set; }
    [Reactive] public string ProgramStatusText { get; set; } = AppLanguage.T(
        "打开节目单时，DJ 会按当前收听风格生成下一组候选歌曲。",
        "Open Program and the DJ will curate the next set from your current listening style.");

    /// <summary>当前时间，1s 推进，供 ClockStage 绑定（spec §5.5）。</summary>
    [Reactive] public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;
    [Reactive] public string LocalizedDayOfWeek { get; private set; } = string.Empty;
    [Reactive] public string LocalizedDate { get; private set; } = string.Empty;

    /// <summary>统一电台状态，由子 VM flags 派生（spec §5.2）。</summary>
    [ObservableAsProperty] public RadioState CurrentState { get; }

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
    public ReactiveCommand<Unit, Unit> ToggleCompactTopmostCommand { get; }
    public ReactiveCommand<Unit, Unit> UseDarkThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> UseLightThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCurrentFavoriteCommand { get; }
    public ReactiveCommand<Unit, Unit> LikeCurrentTrackCommand { get; }
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
        KugouVerificationService? kugouVerification = null)
    {
        _audioService = audioService;
        _djService = djService;
        _sttService = sttService;
        _llmService = llmService;
        _musicSearchService = musicSearchService;
        _recommendationService = recommendationService ?? new RecommendationService(llmService, musicSearchService);
        _accountStore = accountStore;
        _kugouVerification = kugouVerification;

        SelectedCharacter = Characters[0];

        PlayerVM = new PlayerViewModel(_audioService);
        var kugouPlaylistService = accountStore != null && httpClient != null
            ? new KugouPlaylistService(httpClient, accountStore)
            : null;
        PlaylistVM = new PlaylistViewModel(
            _audioService,
            musicSearchService,
            playlistFile,
            kugouPlaylistService: kugouPlaylistService);
        ChatVM = new ChatViewModel(_djService, _audioService, musicSearchService, sttService,
            track => PlaylistVM.AddExternalTrack(track), _recommendationService);
        SettingsVM = new SettingsViewModel(_llmService, secureStorage, settingsFile, accountStore, httpClient, kugouVerification);
        SpectrumVM = new SpectrumViewModel(_audioService);

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
        ToggleCompactTopmostCommand = ReactiveCommand.Create(() =>
        {
            SettingsVM.CompactModeTopmost = !SettingsVM.CompactModeTopmost;
            SettingsVM.SaveUiStateCommand.Execute().Subscribe();
        });
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
        LikeCurrentTrackCommand = ReactiveCommand.Create(() => RecordCurrentTrackFeedback(MusicFeedbackAction.Like));
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
        _onLanguageChanged = RefreshLocalizedProgramText;
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
                _ = HandleAutoRadioTrackEndedAsync(current);
            });

        _trackChangedSub = _audioService.TrackChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track => IsCurrentFavorite = track?.IsFavorite == true);

        // TrackChanged 也会在载入列表、删除曲目和重试音源时触发；只有真正进入播放态
        // 才能算作已播放历史，避免尚未播放的歌曲污染 DJ 的风格上下文。
        _playbackHistorySub = _audioService.StateChanged
            .Where(state => state == PlaybackState.Playing)
            .Subscribe(_ =>
            {
                var current = _audioService.CurrentTrack;
                if (current != null)
                    _recommendationService.RecordPlayedTrack(current);
            });

        // 统一电台状态机：从子 VM flags 派生 CurrentState（spec §5.2）
        this.WhenAnyValue(
                x => x.ChatVM.HasFailure,
                x => x.ChatVM.IsSpeaking,
                x => x.PlaylistVM.IsSearching,
                x => x.ChatVM.IsProcessing,
                x => x.PlayerVM.IsPlaying,
                DeriveRadioState)
            .ObserveOn(RxApp.MainThreadScheduler)
            .ToProperty(this, x => x.CurrentState);

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
        if (_musicSearchService is not MultiSourceMusicService multi)
        {
            if (requireAlternative)
                return null;

            var directUrl = await _musicSearchService.GetPlayUrlAsync(sourceId, cancellationToken);
            return string.IsNullOrWhiteSpace(directUrl)
                ? null
                : new TrackUrlResolution(directUrl, sourceId);
        }

        var onlineTrack = new OnlineTrack
        {
            Id = sourceId,
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            DurationMs = (long)track.Duration.TotalMilliseconds
        };

        var url = requireAlternative
            ? await multi.GetAlternativePlayUrlAsync(onlineTrack, cancellationToken)
            : await multi.GetPlayUrlAsync(onlineTrack, cancellationToken);

        return string.IsNullOrWhiteSpace(url)
            ? null
            : new TrackUrlResolution(url, onlineTrack.Id);
    }

    private void ToggleCurrentFavorite()
    {
        var current = _audioService.CurrentTrack;
        if (current == null) return;

        if (!PlaylistVM.Tracks.Contains(current))
            PlaylistVM.AddExternalTrack(current);

        PlaylistVM.ToggleFavoriteCommand.Execute(current).Subscribe();
        // 命令内部切换的是列表内的匹配实例（与 current 可能不是同一引用），必须从匹配实例回读
        IsCurrentFavorite = PlaylistVM.FindMatchingTrack(current)?.IsFavorite ?? current.IsFavorite;
    }

    private async Task OpenProgramAsync()
    {
        PlaylistVM.TabIndex = 3;
        IsLibraryOpen = true;
        if (!HasCurrentRadioProgram)
            await LoadProgramAsync(force: false);
    }

    private async Task LoadProgramAsync(bool force)
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
            var request = CreateRecommendationRequest(_audioService.CurrentTrack);
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

    private RecommendationRequest CreateRecommendationRequest(Track? current)
    {
        var recentlyPlayed = GetRecentlyPlayedSnapshot();
        return new RecommendationRequest
        {
            UserIntentKey = RecommendationIntentKeys.ContinueStation,
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
        CurrentRadioProgram = program;
        HasCurrentRadioProgram = program?.Tracks.Any(track => track.IsPlayable) == true;
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

    private async Task TellSongStoryAsync()
    {
        if (IsDisposed)
            return;

        var current = _audioService.CurrentTrack;
        if (current == null) return;
        var story = await _djService.GenerateSongStoryAsync(current, _lifetimeCts.Token);
        if (IsDisposed)
            return;

        if (story.Lines.Count == 0) return;
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
            return;

        _recommendationService.RecordFeedback(new UserMusicFeedback
        {
            TrackId = trackId,
            Action = action
        });

        // CALM/FIRE 同步切换会话级氛围偏好，让按钮立即影响后续推荐
        if (action == MusicFeedbackAction.Calmer)
            _recommendationService.SetMoodBias("calm");
        else if (action == MusicFeedbackAction.Energetic)
            _recommendationService.SetMoodBias("energetic");

        if (action == MusicFeedbackAction.Dislike)
            ChatVM.AddAssistantMessage(AppLanguage.T("收到，这首本轮先避开。", "Got it. I will avoid this track in the current session."));
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
        new MultiSourceMusicService(new System.Net.Http.HttpClient()),
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

        IsDarkMode = SettingsVM.IsDarkMode;
        // 启动时恢复上次的窗口模式（简洁/标准）
        IsCompactMode = SettingsVM.StartInCompactMode;
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

            if (PlaylistVM.Tracks.Count == 0)
                await AnnounceEmptyLibraryAsync(cancellationToken);
            else
                await AnnounceStartupRecommendationAsync(cancellationToken);
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
        await SpeakDjTextAsync(text, cancellationToken);
        if (!IsDisposed && !cancellationToken.IsCancellationRequested &&
            PlaylistVM.Tracks.Count > 0 && !_audioService.IsPlaying)
            _audioService.Play();
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

            // LLM 失败/未配置时 GenerateChatResponseAsync 返回兜底文案并置 LastFailure：
            // 问候路径不能把兜底或“请先在设置中配置 AI 服务。”当角色台词播报
            if (_djService.LastFailure is { })
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

    private async System.Threading.Tasks.Task AnnounceStartupRecommendationAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (IsDisposed || cancellationToken.IsCancellationRequested)
                return;

            var current = _audioService.CurrentTrack;
            var originalTrack = current;
            var originalCount = PlaylistVM.Tracks.Count;

            // 选曲启发式（收藏优先、排除当前曲、避开同歌手）统一收敛在 RecommendationService
            var recommended = RecommendationService.PickStartupRecommendation(
                PlaylistVM.Favorites,
                PlaylistVM.Tracks,
                current);

            if (recommended == null) return;

            var script = await GenerateTrackIntroductionAsync(
                current ?? new Track { Title = AppLanguage.T("无", "None"), Artist = AppLanguage.T("未知", "Unknown") },
                recommended,
                cancellationToken);

            if (IsDisposed || cancellationToken.IsCancellationRequested)
                return;

            if (_audioService.IsPlaying ||
                !IsSameTrack(_audioService.CurrentTrack, originalTrack) ||
                PlaylistVM.Tracks.Count != originalCount)
            {
                Log.Debug("Skipped stale startup recommendation because playback changed");
                return;
            }

            ChatVM.AddAssistantMessage(script.Text);
            DjVisualCue?.Invoke(script.Expression, script.Motion);

            if (_audioService.IsPlaying || !IsSameTrack(_audioService.CurrentTrack, originalTrack))
            {
                Log.Debug("Skipped startup recommendation TTS because playback started");
                return;
            }

            await SpeakDjTextAsync(script.Text, cancellationToken);

            Log.Information("AI recommended: {Track}", recommended.Title);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to generate startup recommendation");
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

    private async System.Threading.Tasks.Task<bool> PlayWithFreshRecommendation(Track current)
    {
        if (IsDisposed)
            return false;

        var recommended = await GetRecommendedTrackAsync(current);
        if (IsDisposed || recommended == null)
        {
            Log.Information("Fresh recommendation returned null, falling back to playlist rotation");
            return false;
        }

        if (current != null && IsSameTrackIdentity(recommended, current))
        {
            var retry = await GetRecommendedTrackAsync(current);
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

        var script = await GenerateTrackIntroductionAsync(current!, recommended, _lifetimeCts.Token);
        if (IsDisposed || !IsSameTrack(_audioService.CurrentTrack, current)) return true;

        ChatVM.AddAssistantMessage(script.Text);
        DjVisualCue?.Invoke(script.Expression, script.Motion);
        await SpeakDjTextAsync(script.Text);

        // AddExternalTrack 在宽松同曲命中时“合并且不入列”，随后严格身份查找会落空，
        // 导致“return true 却不播放”且上层跳过轮换兜底 → 电台停播。
        // 回退用与合并口径一致的宽松查找，保证总能拿到可播实例。
        var playable = PlaylistVM.Tracks.FirstOrDefault(t => IsSameTrackIdentity(t, recommended))
                       ?? PlaylistVM.FindMatchingTrack(recommended);
        if (playable != null && IsSameTrack(_audioService.CurrentTrack, current))
            _audioService.PlayTrack(playable);
        return true;
    }

    private async System.Threading.Tasks.Task PlayWithPlaylistRotation(Track current)
    {
        if (IsDisposed)
            return;

        var pool = PlaylistVM.Tracks.Where(t => t != current).ToList();
        if (pool.Count == 0) return;

        var next = RecommendationService.PickDiversifiedTrack(pool, current);
        if (next == null) return;

        if (IsDisposed)
            return;

        if (!PlaylistVM.Tracks.Contains(next))
            PlaylistVM.AddExternalTrack(next);

        if (PlaylistVM.Tracks.IndexOf(next) < 0) return;

        var script = await GenerateTrackIntroductionAsync(current, next, _lifetimeCts.Token);
        if (IsDisposed || !IsSameTrack(_audioService.CurrentTrack, current)) return;

        ChatVM.AddAssistantMessage(script.Text);
        DjVisualCue?.Invoke(script.Expression, script.Motion);
        await SpeakDjTextAsync(script.Text);

        // 串场期间列表可能被增删，旧索引会指向错误曲目，播放前必须重查
        var playable = PlaylistVM.Tracks.FirstOrDefault(t => IsSameTrack(t, next));
        if (playable != null && !IsDisposed && IsSameTrack(_audioService.CurrentTrack, current))
            _audioService.PlayTrack(playable);
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

    private async System.Threading.Tasks.Task<Track?> GetRecommendedTrackAsync(Track? current)
    {
        AttachRecommendationContext(current);
        var request = CreateRecommendationRequest(current);

        try
        {
            var recommended = await RequestRecommendedTrackAsync(request, _lifetimeCts.Token);
            if (IsDisposed)
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
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || IsDisposed)
        {
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

    private Task<Track?> GetNextTrackForAudioServiceAsync()
    {
        if (IsDisposed)
            return Task.FromResult<Track?>(null);

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            return GetNextTrackForAudioServiceCoreAsync();

        var completion = new TaskCompletionSource<Track?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRegistration = _lifetimeCts.Token.Register(
            () => completion.TrySetResult(null));
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await GetNextTrackForAudioServiceCoreAsync());
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
            }
        });
        return completion.Task;
    }

    private async Task<Track?> GetNextTrackForAudioServiceCoreAsync()
    {
        if (IsDisposed)
            return null;

        await _advanceGate.WaitAsync(_lifetimeCts.Token);
        try
        {
            if (IsDisposed)
                return null;

            await DjTtsInterop.StopTtsWithoutBlockingUiAsync(_audioService, _lifetimeCts.Token);
            var current = _audioService.CurrentTrack;
            AttachRecommendationContext(current);
            var recommended = await GetRecommendedTrackAsync(current);
            if (IsDisposed || !IsSameTrack(_audioService.CurrentTrack, current))
                return null;

            if (recommended != null &&
                !PlaylistVM.Tracks.Any(t => IsSameTrack(t, recommended)))
            {
                PlaylistVM.AddExternalTrack(recommended);
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
        Track current,
        Track next,
        CancellationToken cancellationToken)
        => _djService is DJService djService
            ? djService.GenerateTrackIntroductionAsync(current, next, cancellationToken)
            : _djService.GenerateTrackIntroductionAsync(current, next).WaitAsync(cancellationToken);

    private static bool IsSameTrack(Track? left, Track? right) => TrackComparer.IsSameTrack(left, right);

    private static bool IsSameTrackIdentity(Track? left, Track? right) => TrackComparer.IsSameTrackIdentity(left, right);

    private static string StripDjControlTags(string text)
    {
        var cleaned = Regex.Replace(text, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<cmd>\s*\{.*?\}\s*</cmd>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"【(?:play:.+?|next|pause|resume)】", "", RegexOptions.IgnoreCase);
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
        _playbackHistorySub?.Dispose();
        _darkModePersistSub?.Dispose();
        _languageTtsSub?.Dispose();
        _speechMixSub?.Dispose();
        _spectrumStyleSub?.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
        _sttLanguageSub?.Dispose();
        _clockSub?.Dispose();
        SettingsVM.CharacterSettingsChanged -= _characterSettingsHandler;
        PlayerVM?.Dispose();
        ChatVM?.Dispose();
        SpectrumVM?.Dispose();
        PlaylistVM?.Dispose();
        SettingsVM?.Dispose();
    }
}
