using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.Collections.Generic;
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
    private readonly IDisposable _clockSub;
    private readonly IDisposable _darkModePersistSub;
    private readonly IDisposable _languageTtsSub;
    private readonly IDisposable _speechMixSub;
    private IDisposable? _sttLanguageSub;
    private readonly Action _characterSettingsHandler;
    private int _autoRadioAdvancing;
    private int _disposed;
    private readonly SemaphoreSlim _ttsLock = new(1, 1);
    // 自然结束续播与手动 Next 的 nextCallback 两条推荐管线共用此门串行化，
    // 避免并发时双份推荐请求、双份加列表、双次切歌
    private readonly SemaphoreSlim _advanceGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

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

    /// <summary>当前时间，1s 推进，供 ClockStage 绑定（spec §5.5）。</summary>
    [Reactive] public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;

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
    public ReactiveCommand<Unit, Unit> ToggleCharacterPickerCommand { get; }
    public ReactiveCommand<CharacterProfile, Unit> SelectCharacterCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCompactModeCommand { get; }
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
        string? playlistFile = null,
        IRecommendationService? recommendationService = null,
        string? settingsFile = null)
    {
        _audioService = audioService;
        _djService = djService;
        _sttService = sttService;
        _llmService = llmService;
        _musicSearchService = musicSearchService;
        _recommendationService = recommendationService ?? new RecommendationService(llmService, musicSearchService);

        SelectedCharacter = Characters[0];

        PlayerVM = new PlayerViewModel(_audioService);
        PlaylistVM = new PlaylistViewModel(_audioService, musicSearchService, playlistFile);
        ChatVM = new ChatViewModel(_djService, _audioService, musicSearchService, sttService,
            track => PlaylistVM.AddExternalTrack(track), _recommendationService);
        SettingsVM = new SettingsViewModel(_llmService, secureStorage, settingsFile);
        SpectrumVM = new SpectrumViewModel(_audioService);

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
        OpenProgramCommand = ReactiveCommand.Create(() =>
        {
            PlaylistVM.TabIndex = 3;
            IsLibraryOpen = true;
        });
        ToggleCharacterPickerCommand = ReactiveCommand.Create(() => { IsCharacterPickerOpen = !IsCharacterPickerOpen; });
        ToggleThemeCommand = ReactiveCommand.Create(() => { IsDarkMode = !IsDarkMode; });
        ToggleCompactModeCommand = ReactiveCommand.Create(ToggleCompactMode);
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
                SettingsVM.SaveCommand.Execute().Subscribe();
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
            .Subscribe(_ => Now = DateTimeOffset.Now);
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
            ChatVM.AddAssistantMessage(SettingsVM.SelectedLanguage == "en" ? "Got it. I will avoid this track in the current session." : "收到，这首本轮先避开。");
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

    private async System.Threading.Tasks.Task HandleTrackTransitionAsync(Track current, Track next)
    {
        try
        {
            if (IsDisposed)
                return;

            var script = await GenerateTrackIntroductionAsync(current, next, _lifetimeCts.Token);
            if (IsDisposed)
                return;

            ChatVM.AddAssistantMessage(script.Text);
            Log.Information("DJ: {Text}", script.Text);
            DjVisualCue?.Invoke(script.Expression, script.Motion);

            if (_djService.TtsEnabled && !string.IsNullOrWhiteSpace(script.Text))
            {
                var speechData = await GenerateSpeechAsync(script.Text, _lifetimeCts.Token);
                if (speechData is { Length: > 0 } && !IsDisposed)
                    _audioService.PlayTtsAudio(speechData);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DJ intro failed");
        }
    }

    // Parameterless ctor for designer/testing only; production uses DI with shared HttpClient singleton
    public MainWindowViewModel() : this(
        new AudioService(),
        new DJService(new LLMService(new System.Net.Http.HttpClient()), new EdgeTtsService(new System.Net.Http.HttpClient())),
        new LLMService(new System.Net.Http.HttpClient()),
        new WindowsSecureStorage(),
        new MultiSourceMusicService(new System.Net.Http.HttpClient()),
        new WhisperSttService())
    {
    }

    public async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken = default)
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
        SettingsVM.SaveCommand.Execute().Subscribe();
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

        var text = SettingsVM.SelectedLanguage == "en"
            ? "No tracks yet. Tell me a mood or search a song, and I'll build today's station."
            : "歌单还是空的。告诉我今天的心情，或者搜一首歌，我来帮你开台。";

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

        var text = SettingsVM.SelectedLanguage == "en"
            ? $"This is {SelectedCharacter.DisplayName}. The station is online. I'll keep you company and tune the music to your mood."
            : $"这里是 {SelectedCharacter.DisplayName}，电台已经上线。我会陪你听一会儿歌，也会按今天的心情帮你找下一首。";

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

            var response = await _djService.GenerateChatResponseAsync(prompt);
            if (IsDisposed || cancellationToken.IsCancellationRequested)
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
                current ?? new Track { Title = "无", Artist = "未知" },
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

                await StopTtsWithoutBlockingUiAsync(_lifetimeCts.Token);
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

        var playIndex = PlaylistVM.Tracks.FindIndex(t => IsSameTrackIdentity(t, recommended));
        if (playIndex >= 0 && IsSameTrack(_audioService.CurrentTrack, current))
            _audioService.PlayAtIndex(playIndex);
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
        var playIndex = PlaylistVM.Tracks.FindIndex(t => IsSameTrack(t, next));
        if (playIndex >= 0 && !IsDisposed && IsSameTrack(_audioService.CurrentTrack, current))
            _audioService.PlayAtIndex(playIndex);
    }

    private void AttachRecommendationContext(Track? current)
    {
        if (current == null) return;

        current.Tag = new RecommendationContext
        {
            Favorites = PlaylistVM.Favorites.ToList(),
            ExcludedTracks = PlaylistVM.Tracks.ToList()
        };
    }

    private async System.Threading.Tasks.Task<Track?> GetRecommendedTrackAsync(Track? current)
    {
        AttachRecommendationContext(current);
        var request = new RecommendationRequest
        {
            UserIntent = "继续当前电台",
            CurrentTrack = current,
            Favorites = PlaylistVM.Favorites.ToList(),
            Playlist = PlaylistVM.Tracks.ToList(),
            ExcludedTracks = PlaylistVM.Tracks.ToList()
        };

        try
        {
            var recommended = await RequestRecommendedTrackAsync(request, _lifetimeCts.Token);
            if (IsDisposed)
                return null;

            var prevOpening = CurrentRadioProgram?.DjOpening;
            CurrentRadioProgram = _recommendationService.CurrentProgram;
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
            var fallback = await RequestDjRecommendationAsync(current, _lifetimeCts.Token);
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

            await StopTtsWithoutBlockingUiAsync(_lifetimeCts.Token);
            var current = _audioService.CurrentTrack;
            AttachRecommendationContext(current);
            var recommended = await GetRecommendedTrackAsync(current);
            if (!IsDisposed && recommended != null &&
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

    private Task<Track?> RequestDjRecommendationAsync(
        Track? current,
        CancellationToken cancellationToken)
        => _djService is DJService djService
            ? djService.RecommendNextTrackAsync(current, cancellationToken)
            : _djService.RecommendNextTrackAsync(current).WaitAsync(cancellationToken);

    private Task<DJScript> GenerateTrackIntroductionAsync(
        Track current,
        Track next,
        CancellationToken cancellationToken)
        => _djService is DJService djService
            ? djService.GenerateTrackIntroductionAsync(current, next, cancellationToken)
            : _djService.GenerateTrackIntroductionAsync(current, next).WaitAsync(cancellationToken);

    private Task<byte[]?> GenerateSpeechAsync(string text, CancellationToken cancellationToken)
        => _djService is DJService djService
            ? djService.GenerateSpeechAsync(text, cancellationToken)
            : _djService.GenerateSpeechAsync(text).WaitAsync(cancellationToken);

    private async Task StopTtsWithoutBlockingUiAsync(CancellationToken cancellationToken)
    {
        var stopTask = Task.Factory.StartNew(
            _audioService.StopTts,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            Log.Warning("TTS stop did not complete within 2 seconds; continuing without blocking UI");
        }
    }

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
                var speechData = await GenerateSpeechAsync(text, token);
                if (speechData is { Length: > 0 } && !IsDisposed && !token.IsCancellationRequested)
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                    var sub = _audioService.TtsStateChanged.Subscribe(playing =>
                    {
                        if (!playing) tcs.TrySetResult(true);
                    });
                    _audioService.PlayTtsAudio(speechData);
                    try
                    {
                        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        // AudioService 由 DI 容器持有并在随后统一释放。这里不能在 Avalonia
        // 关闭线程同步 Stop NAudio，否则设备线程异常时会再次把窗口关闭卡住。
        _trackEndedSub?.Dispose();
        _trackChangedSub?.Dispose();
        _darkModePersistSub?.Dispose();
        _languageTtsSub?.Dispose();
        _speechMixSub?.Dispose();
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
