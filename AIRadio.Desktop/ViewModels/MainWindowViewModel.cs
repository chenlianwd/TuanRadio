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
using Serilog;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly IDJService _djService;
    private readonly IMinimaxService _minimaxService;
    private readonly IMusicSearchService _musicSearchService;
    private readonly IDisposable _trackEndedSub;
    private readonly IDisposable _trackChangedSub;
    private readonly IDisposable _darkModePersistSub;
    private readonly IDisposable _languageTtsSub;
    private readonly IDisposable _speechMixSub;
    private readonly Action _characterSettingsHandler;
    private int _autoRadioAdvancing;
    private readonly SemaphoreSlim _ttsLock = new(1, 1);

    public PlayerViewModel PlayerVM { get; }
    public PlaylistViewModel PlaylistVM { get; }
    public ChatViewModel ChatVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public SpectrumViewModel SpectrumVM { get; }

    public List<CharacterProfile> Characters { get; } = CharacterProfile.Presets;

    public event Action<string, string>? Live2DCommand; // expression, motion

    [Reactive] public bool IsSettingsOpen { get; set; }
    [Reactive] public bool IsLibraryOpen { get; set; }
    [Reactive] public bool IsCharacterPickerOpen { get; set; }
    [Reactive] public CharacterProfile SelectedCharacter { get; set; }
    [Reactive] public bool IsDarkMode { get; set; } = true;
    [Reactive] public bool IsCurrentFavorite { get; set; }

    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLibraryCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFavoritesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCharacterPickerCommand { get; }
    public ReactiveCommand<CharacterProfile, Unit> SelectCharacterCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> UseDarkThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> UseLightThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCurrentFavoriteCommand { get; }

    public MainWindowViewModel(
        IAudioService audioService,
        IDJService djService,
        IMinimaxService minimaxService,
        ISecureStorage secureStorage,
        IMusicSearchService musicSearchService,
        ISttService sttService,
        string? playlistFile = null)
    {
        _audioService = audioService;
        _djService = djService;
        _minimaxService = minimaxService;
        _musicSearchService = musicSearchService;

        SelectedCharacter = Characters[0];

        PlayerVM = new PlayerViewModel(_audioService);
        PlaylistVM = new PlaylistViewModel(_audioService, musicSearchService, playlistFile);
        ChatVM = new ChatViewModel(_djService, _audioService, musicSearchService, sttService,
            track => PlaylistVM.AddExternalTrack(track));
        SettingsVM = new SettingsViewModel(_minimaxService, _djService, secureStorage);
        SpectrumVM = new SpectrumViewModel(_audioService);

        // Set URL resolver for re-fresh of online track URLs (prevents 403 from expired links)
        if (_audioService is Services.AudioService audioSvc)
        {
            audioSvc.SetUrlResolver(async id => await musicSearchService.GetPlayUrlAsync(id));
            audioSvc.SetNextCallback(async () =>
            {
                var current = _audioService.CurrentTrack;
                AttachRecommendationContext(current);
                var recommended = await _djService.RecommendNextTrackAsync(current);
                if (recommended != null && !PlaylistVM.Tracks.Any(t => IsSameTrack(t, recommended)))
                    PlaylistVM.AddExternalTrack(recommended);
                return recommended;
            });
            audioSvc.SetPreviousCallback(async () =>
            {
                var current = _audioService.CurrentTrack;
                AttachRecommendationContext(current);
                var recommended = await _djService.RecommendNextTrackAsync(current);
                if (recommended != null && !PlaylistVM.Tracks.Any(t => IsSameTrack(t, recommended)))
                    PlaylistVM.AddExternalTrack(recommended);
                return recommended;
            });
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
        ToggleCharacterPickerCommand = ReactiveCommand.Create(() => { IsCharacterPickerOpen = !IsCharacterPickerOpen; });
        ToggleThemeCommand = ReactiveCommand.Create(() => { IsDarkMode = !IsDarkMode; });
        UseDarkThemeCommand = ReactiveCommand.Create(() => { IsDarkMode = true; });
        UseLightThemeCommand = ReactiveCommand.Create(() => { IsDarkMode = false; });

        // Persist IsDarkMode to settings when it changes
        _darkModePersistSub = this.WhenAnyValue(x => x.IsDarkMode)
            .Skip(1)
            .Subscribe(isDark =>
            {
                SettingsVM.IsDarkMode = isDark;
                SettingsVM.SaveCommand.Execute().Subscribe();
            });
        ToggleCurrentFavoriteCommand = ReactiveCommand.Create(ToggleCurrentFavorite);
        SelectCharacterCommand = ReactiveCommand.Create<CharacterProfile>(character =>
        {
            SwitchCharacter(character);
            _ = AnnounceCharacterGreetingAsync();
        });

        // Re-apply character when settings are saved
        _characterSettingsHandler = () => SwitchCharacter(SelectedCharacter);
        SettingsVM.CharacterSettingsChanged += _characterSettingsHandler;
        _languageTtsSub = SettingsVM.WhenAnyValue(x => x.SelectedLanguage, x => x.TtsEnabled)
            .Skip(1)
            .Subscribe(_ => SwitchCharacter(SelectedCharacter));
        _speechMixSub = SettingsVM.WhenAnyValue(x => x.SpeechMixMode)
            .Subscribe(mode => _audioService.SetSpeechMixMode(mode));

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
    }

    private void ToggleCurrentFavorite()
    {
        var current = _audioService.CurrentTrack;
        if (current == null) return;

        if (!PlaylistVM.Tracks.Contains(current))
            PlaylistVM.AddExternalTrack(current);

        PlaylistVM.ToggleFavoriteCommand.Execute(current).Subscribe();
        IsCurrentFavorite = current.IsFavorite;
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
            var script = await _djService.GenerateTrackIntroductionAsync(current, next);
            ChatVM.AddAssistantMessage(script.Text);
            Log.Information("DJ: {Text}", script.Text);
            Live2DCommand?.Invoke(script.Expression, script.Motion);

            if (_djService.TtsEnabled && !string.IsNullOrWhiteSpace(script.Text))
            {
                var speechData = await _djService.GenerateSpeechAsync(script.Text);
                if (speechData is { Length: > 0 })
                    _audioService.PlayTtsAudio(speechData);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DJ intro failed");
        }
    }

    public MainWindowViewModel() : this(
        new AudioService(),
        new DJService(new MinimaxService(new System.Net.Http.HttpClient())),
        new MinimaxService(new System.Net.Http.HttpClient()),
        new WindowsSecureStorage(),
        new MultiSourceMusicService(new System.Net.Http.HttpClient()),
        new WhisperSttService())
    {
    }

    public async System.Threading.Tasks.Task InitializeAsync()
    {
        await SettingsVM.LoadAsync();
        await PlaylistVM.LoadAsync();
        IsDarkMode = SettingsVM.IsDarkMode;
        // Apply initial character
        SwitchCharacter(SelectedCharacter);
        _audioService.SetSpeechMixMode(SettingsVM.SpeechMixMode);

        await AnnounceWelcomeAsync();

        // AI startup recommendation: analyze playlist and recommend a song
        _ = AnnounceStartupFollowupAsync();
    }

    public void CloseOverlays()
    {
        IsSettingsOpen = false;
        IsLibraryOpen = false;
        IsCharacterPickerOpen = false;
    }

    private async System.Threading.Tasks.Task AnnounceStartupFollowupAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(1400);
            if (PlaylistVM.Tracks.Count == 0)
                await AnnounceEmptyLibraryAsync();
            else
                await AnnounceStartupRecommendationAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup follow-up failed");
        }
    }

    private async System.Threading.Tasks.Task AnnounceEmptyLibraryAsync()
    {
        var text = SettingsVM.SelectedLanguage == "en"
            ? "No tracks yet. Tell me a mood or search a song, and I'll build today's station."
            : "歌单还是空的。告诉我今天的心情，或者搜一首歌，我来帮你开台。";

        ChatVM.AddAssistantMessage(text);

        await SpeakDjTextAsync(text);
        if (PlaylistVM.Tracks.Count > 0 && !_audioService.IsPlaying)
            _audioService.Play();
    }

    private async System.Threading.Tasks.Task AnnounceWelcomeAsync()
    {
        var text = SettingsVM.SelectedLanguage == "en"
            ? $"This is {SelectedCharacter.DisplayName}. The station is online. I'll keep you company and tune the music to your mood."
            : $"这里是 {SelectedCharacter.DisplayName}，电台已经上线。我会陪你听一会儿歌，也会按今天的心情帮你找下一首。";

        ChatVM.AddAssistantMessage(text);
        Live2DCommand?.Invoke("smile", "wave");
        await SpeakDjTextAsync(text);
        if (PlaylistVM.Tracks.Count > 0 && !_audioService.IsPlaying)
            _audioService.Play();
    }

    private async System.Threading.Tasks.Task AnnounceCharacterGreetingAsync()
    {
        try
        {
            var prompt = SettingsVM.SelectedLanguage == "en"
                ? "You have just taken over this radio station. Greet me in your own DJ personality and voice style. Do not mention settings."
                : "你刚刚接管这个电台。请用你的主播人设和语气，主动向我打个招呼，不要提到设置。";

            var response = await _djService.GenerateChatResponseAsync(prompt);
            var text = StripDjControlTags(response);
            if (string.IsNullOrWhiteSpace(text)) return;

            ChatVM.AddAssistantMessage(text);
            Live2DCommand?.Invoke("smile", "wave");

            await SpeakDjTextAsync(text);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to generate character greeting");
        }
    }

    private async System.Threading.Tasks.Task AnnounceStartupRecommendationAsync()
    {
        try
        {
            var current = _audioService.CurrentTrack;
            var originalTrack = current;
            var originalCount = PlaylistVM.Tracks.Count;
            Track? recommended = null;

            // Smart pick: prioritize favorites, exclude currently playing track, avoid same-artist repetition
            var favorites = PlaylistVM.Favorites.ToList();
            var allTracks = PlaylistVM.Tracks.ToList();
            if (favorites.Count > 0)
            {
                var candidates = favorites.Where(t => t != current).ToList();
                if (candidates.Count == 0)
                    candidates = favorites;
                recommended = PickDiversifiedTrack(candidates, current);
            }
            else if (allTracks.Count > 0)
            {
                var candidates = allTracks.Where(t => t != current).ToList();
                if (candidates.Count == 0)
                    candidates = allTracks;
                recommended = PickDiversifiedTrack(candidates, current);
            }

            if (recommended == null) return;

            var script = await _djService.GenerateTrackIntroductionAsync(
                current ?? new Track { Title = "无", Artist = "未知" },
                recommended);

            if (_audioService.IsPlaying ||
                !IsSameTrack(_audioService.CurrentTrack, originalTrack) ||
                PlaylistVM.Tracks.Count != originalCount)
            {
                Log.Debug("Skipped stale startup recommendation because playback changed");
                return;
            }

            ChatVM.AddAssistantMessage(script.Text);
            Live2DCommand?.Invoke(script.Expression, script.Motion);

            if (_audioService.IsPlaying || !IsSameTrack(_audioService.CurrentTrack, originalTrack))
            {
                Log.Debug("Skipped startup recommendation TTS because playback started");
                return;
            }

            await SpeakDjTextAsync(script.Text);

            Log.Information("AI recommended: {Track}", recommended.Title);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to generate startup recommendation");
        }
    }

    private async System.Threading.Tasks.Task HandleAutoRadioTrackEndedAsync(Track current)
    {
        if (Interlocked.Exchange(ref _autoRadioAdvancing, 1) == 1) return;
        if (_audioService.RepeatMode != "radio") { _autoRadioAdvancing = 0; return; }
        try
        {
            // Radio mode should expand the station with fresh online recommendations.
            if (ShouldUseFreshRadioRecommendations())
            {
                AttachRecommendationContext(current);
                var recommended = await _djService.RecommendNextTrackAsync(current);
                if (recommended == null)
                {
                    ChatVM.AddAssistantMessage(SettingsVM.SelectedLanguage == "en"
                        ? "Couldn't find a song to play next. The station needs more tracks to keep going."
                        : "推荐出错了，歌单里没新歌了。你可以搜一首，或者让我继续播现有的。");
                    Interlocked.Exchange(ref _autoRadioAdvancing, 0);
                    return;
                }

                if (current != null && IsSameTrackIdentity(recommended, current))
                {
                    var retry = await _djService.RecommendNextTrackAsync(current);
                    if (retry != null && !IsSameTrackIdentity(retry, current))
                        recommended = retry;
                }

                var alreadyInPlaylist = PlaylistVM.Tracks.Any(t => IsSameTrackIdentity(t, recommended));
                if (!alreadyInPlaylist)
                    PlaylistVM.AddExternalTrack(recommended);

                var script = await _djService.GenerateTrackIntroductionAsync(current!, recommended);
                if (!IsSameTrack(_audioService.CurrentTrack, current))
                    return;

                ChatVM.AddAssistantMessage(script.Text);
                Live2DCommand?.Invoke(script.Expression, script.Motion);
                await SpeakDjTextAsync(script.Text);

                var playIndex = PlaylistVM.Tracks.FindIndex(t => IsSameTrackIdentity(t, recommended));
                if (playIndex >= 0 && IsSameTrack(_audioService.CurrentTrack, current))
                    _audioService.PlayAtIndex(playIndex);
                return;
            }

            var pool = PlaylistVM.Tracks.Where(t => t != current).ToList();
            Track? next = null;

            if (pool.Count > 0)
            {
                next = PickDiversifiedTrack(pool, current);
            }

            if (next == null)
            {
                Interlocked.Exchange(ref _autoRadioAdvancing, 0);
                return;
            }

            // Add to playlist if not already there (for online recommendations)
            if (!PlaylistVM.Tracks.Contains(next))
            {
                PlaylistVM.AddExternalTrack(next);
            }

            var index = PlaylistVM.Tracks.IndexOf(next);
            if (index >= 0)
            {
                var script = await _djService.GenerateTrackIntroductionAsync(current, next);
                if (!IsSameTrack(_audioService.CurrentTrack, current))
                    return;

                ChatVM.AddAssistantMessage(script.Text);
                Live2DCommand?.Invoke(script.Expression, script.Motion);
                await SpeakDjTextAsync(script.Text);
                if (IsSameTrack(_audioService.CurrentTrack, current))
                    _audioService.PlayAtIndex(index);
            }
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

    private static Track? PickDiversifiedTrack(List<Track> pool, Track? current)
    {
        if (pool.Count == 0) return null;
        if (pool.Count == 1) return pool[0];

        // Try to avoid same artist as current
        var sameArtist = pool.Where(t => current != null && t.Artist == current.Artist).ToList();
        var differentArtist = pool.Except(sameArtist).ToList();

        var candidates = differentArtist.Count > 0 ? differentArtist : pool;
        var random = new Random();
        return candidates[random.Next(candidates.Count)];
    }

    private static bool ShouldUseFreshRadioRecommendations() => true;

    private void AttachRecommendationContext(Track? current)
    {
        if (current == null) return;

        current.Tag = new RecommendationContext
        {
            Favorites = PlaylistVM.Favorites.ToList(),
            ExcludedTracks = PlaylistVM.Tracks.ToList()
        };
    }

    private static bool IsSameTrack(Track? left, Track? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null)
            return false;
        if (!string.IsNullOrWhiteSpace(left.SourceId) && left.SourceId == right.SourceId)
            return true;
        if (!string.IsNullOrWhiteSpace(left.FilePath) && left.FilePath == right.FilePath)
            return true;
        return !string.IsNullOrWhiteSpace(left.Id) && left.Id == right.Id;
    }

    private static bool IsSameTrackIdentity(Track? left, Track? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null)
            return false;
        // SourceId is the stable identity for online tracks; URL may change on refresh.
        if (!string.IsNullOrWhiteSpace(left.SourceId) && !string.IsNullOrWhiteSpace(right.SourceId) &&
            left.SourceId == right.SourceId)
            return true;
        // Fall back to file path for local tracks.
        if (!string.IsNullOrWhiteSpace(left.FilePath) && !string.IsNullOrWhiteSpace(right.FilePath) &&
            left.FilePath == right.FilePath)
            return true;
        if (!string.IsNullOrWhiteSpace(left.Id) && !string.IsNullOrWhiteSpace(right.Id) && left.Id == right.Id)
            return true;
        return false;
    }

    private static string StripDjControlTags(string text)
    {
        var cleaned = Regex.Replace(text, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"【(?:play:.+?|next|pause|resume)】", "", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private async System.Threading.Tasks.Task SpeakDjTextAsync(string text)
    {
        if (!_djService.TtsEnabled || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            await _ttsLock.WaitAsync();
            try
            {
                var speechData = await _djService.GenerateSpeechAsync(text);
                if (speechData is { Length: > 0 })
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    var sub = _audioService.TtsStateChanged.Subscribe(playing =>
                    {
                        if (!playing) tcs.TrySetResult(true);
                    });
                    _audioService.PlayTtsAudio(speechData);
                    try
                    {
                        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
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
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to speak DJ text");
        }
    }

    public void Dispose()
    {
        _trackEndedSub?.Dispose();
        _trackChangedSub?.Dispose();
        _darkModePersistSub?.Dispose();
        _languageTtsSub?.Dispose();
        _speechMixSub?.Dispose();
        SettingsVM.CharacterSettingsChanged -= _characterSettingsHandler;
        PlayerVM?.Dispose();
        ChatVM?.Dispose();
        SpectrumVM?.Dispose();
        _ttsLock.Dispose();
    }
}
