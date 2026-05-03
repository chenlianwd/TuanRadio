using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
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

    [Reactive] public bool IsSettingsOpen { get; set; }

    public ReactiveCommand<Unit, Unit> ToggleSettingsCommand { get; }

    public MainWindowViewModel(
        IAudioService audioService,
        IDJService djService,
        IMinimaxService minimaxService,
        ISecureStorage secureStorage)
    {
        _audioService = audioService;
        _djService = djService;
        _minimaxService = minimaxService;

        PlayerVM = new PlayerViewModel(_audioService);
        PlaylistVM = new PlaylistViewModel(_audioService);
        ChatVM = new ChatViewModel(_djService);
        SettingsVM = new SettingsViewModel(_minimaxService, _djService, secureStorage);
        SpectrumVM = new SpectrumViewModel(_audioService);

        ToggleSettingsCommand = ReactiveCommand.Create(() => { IsSettingsOpen = !IsSettingsOpen; });

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

    private async System.Threading.Tasks.Task HandleTrackTransitionAsync(Track current, Track next)
    {
        try
        {
            var script = await _djService.GenerateTrackIntroductionAsync(current, next);
            Log.Information("DJ: {Text}", script.Text);
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
        new WindowsSecureStorage())
    {
    }

    public async System.Threading.Tasks.Task InitializeAsync()
    {
        await SettingsVM.LoadAsync();
    }

    public void Dispose()
    {
        _trackEndedSub?.Dispose();
        PlayerVM?.Dispose();
    }
}
