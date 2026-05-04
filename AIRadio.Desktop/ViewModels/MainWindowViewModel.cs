using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Serilog;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly IDJService _djService;
    private readonly IMinimaxService _minimaxService;
    private readonly IDisposable _trackEndedSub;

    public PlayerViewModel PlayerVM { get; }
    public PlaylistViewModel PlaylistVM { get; }
    public ChatViewModel ChatVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public SpectrumViewModel SpectrumVM { get; }

    public List<CharacterProfile> Characters { get; } = CharacterProfile.Presets;

    public event Action<string, string>? Live2DCommand; // expression, motion

    [Reactive] public bool IsSettingsOpen { get; set; }
    [Reactive] public bool IsCharacterPickerOpen { get; set; }
    [Reactive] public CharacterProfile SelectedCharacter { get; set; }

    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCharacterPickerCommand { get; }
    public ReactiveCommand<CharacterProfile, Unit> SelectCharacterCommand { get; }

    public MainWindowViewModel(
        IAudioService audioService,
        IDJService djService,
        IMinimaxService minimaxService,
        ISecureStorage secureStorage,
        IMusicSearchService musicSearchService,
        ISttService sttService)
    {
        _audioService = audioService;
        _djService = djService;
        _minimaxService = minimaxService;

        SelectedCharacter = Characters[0];

        PlayerVM = new PlayerViewModel(_audioService);
        PlaylistVM = new PlaylistViewModel(_audioService, musicSearchService);
        ChatVM = new ChatViewModel(_djService, _audioService, musicSearchService, sttService);
        SettingsVM = new SettingsViewModel(_minimaxService, _djService, secureStorage);
        SpectrumVM = new SpectrumViewModel(_audioService);

        // Set URL resolver for re-fresh of online track URLs (prevents 403 from expired links)
        if (_audioService is Services.AudioService audioSvc)
        {
            audioSvc.SetUrlResolver(async id => await musicSearchService.GetPlayUrlAsync(id));
        }

        ToggleSettingsCommand = ReactiveCommand.Create(() => { IsSettingsOpen = !IsSettingsOpen; });
        ToggleCharacterPickerCommand = ReactiveCommand.Create(() => { IsCharacterPickerOpen = !IsCharacterPickerOpen; });
        SelectCharacterCommand = ReactiveCommand.Create<CharacterProfile>(SwitchCharacter);

        // Re-apply character when settings are saved
        SettingsVM.CharacterSettingsChanged += () => SwitchCharacter(SelectedCharacter);

        _trackEndedSub = _audioService.TrackEnded
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(current =>
            {
                if (current == null) return;
                var next = _audioService.CurrentTrack;
                if (next == null || next == current) return;
                _ = HandleTrackTransitionAsync(current, next);
            });
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
            Log.Information("DJ: {Text}", script.Text);
            Live2DCommand?.Invoke(script.Expression, script.Motion);
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
        // Apply initial character
        SwitchCharacter(SelectedCharacter);

        // AI startup recommendation: analyze playlist and recommend a song
        await AnnounceStartupRecommendationAsync();
    }

    private async System.Threading.Tasks.Task AnnounceStartupRecommendationAsync()
    {
        try
        {
            var current = _audioService.CurrentTrack;
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

            Live2DCommand?.Invoke(script.Expression, script.Motion);

            if (_djService.TtsEnabled && !string.IsNullOrWhiteSpace(script.Text))
            {
                var speechData = await _djService.GenerateSpeechAsync(script.Text);
                if (speechData is { Length: > 0 })
                    _audioService.PlayTtsAudio(speechData);
            }

            Log.Information("AI recommended: {Track}", recommended.Title);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to generate startup recommendation");
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

    public void Dispose()
    {
        _trackEndedSub?.Dispose();
        PlayerVM?.Dispose();
    }
}
