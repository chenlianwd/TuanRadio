using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.Collections.Generic;
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
            SystemPrompt = personality
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
        // Apply initial character
        SwitchCharacter(SelectedCharacter);
    }

    public void Dispose()
    {
        _trackEndedSub?.Dispose();
        PlayerVM?.Dispose();
    }
}
