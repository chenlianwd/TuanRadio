using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class PlayerViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioService _audioService;

    [Reactive] public string TrackTitle { get; set; } = "未播放";
    [Reactive] public string TrackArtist { get; set; } = "";
    [Reactive] public bool IsPlaying { get; set; }
    [Reactive] public string PlayPauseText { get; set; } = "▶";
    [Reactive] public double CurrentSeconds { get; set; }
    [Reactive] public double TotalSeconds { get; set; }
    [Reactive] public float Volume { get; set; } = 0.8f;
    [Reactive] public string PositionText { get; set; } = "0:00";
    [Reactive] public string DurationText { get; set; } = "0:00";
    [Reactive] public bool IsShuffled { get; set; }
    [Reactive] public string ShuffleText { get; set; } = "🔀";
    [Reactive] public string RepeatMode { get; set; } = "list";
    [Reactive] public string RepeatText { get; set; } = "🔁 列表";

    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }

    private readonly IDisposable _trackSub;
    private readonly IDisposable _stateSub;
    private readonly IDisposable _posSub;
    private readonly IDisposable _volSub;

    public PlayerViewModel(IAudioService audioService)
    {
        _audioService = audioService;

        PlayCommand = ReactiveCommand.Create(() =>
        {
            if (_audioService.IsPlaying) _audioService.Pause();
            else _audioService.Play();
        });
        PreviousCommand = ReactiveCommand.Create(() => _audioService.Previous());
        NextCommand = ReactiveCommand.Create(() => _audioService.Next());

        ToggleShuffleCommand = ReactiveCommand.Create(() =>
        {
            _audioService.Shuffle();
            IsShuffled = _audioService.IsShuffled;
            ShuffleText = IsShuffled ? "🔀 ON" : "🔀";
        });

        ToggleRepeatCommand = ReactiveCommand.Create(() =>
        {
            var next = _audioService.RepeatMode switch
            {
                "list" => "single",
                "single" => "none",
                _ => "list"
            };
            _audioService.SetRepeatMode(next);
            RepeatMode = next;
            RepeatText = next switch
            {
                "single" => "🔂 单曲",
                "none" => "⏹ 无",
                _ => "🔁 列表"
            };
        });

        _trackSub = _audioService.TrackChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track =>
            {
                if (track != null)
                {
                    TrackTitle = track.Title;
                    TrackArtist = track.Artist;
                    TotalSeconds = track.Duration.TotalSeconds;
                    DurationText = FormatTime(track.Duration);
                }
                else
                {
                    TrackTitle = "未播放";
                    TrackArtist = "";
                    TotalSeconds = 0;
                    DurationText = "0:00";
                }
            });

        _stateSub = _audioService.StateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state =>
            {
                IsPlaying = state == PlaybackState.Playing;
                PlayPauseText = IsPlaying ? "⏸" : "▶";
            });

        _posSub = _audioService.PositionChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pos =>
            {
                CurrentSeconds = pos.TotalSeconds;
                PositionText = FormatTime(pos);
            });

        _volSub = this.WhenAnyValue(x => x.Volume)
            .Skip(1)
            .Subscribe(v => _audioService.Volume = v);
    }

    public void SeekTo(double seconds)
    {
        _audioService.Seek(TimeSpan.FromSeconds(seconds));
    }

    public void Dispose()
    {
        _trackSub?.Dispose();
        _stateSub?.Dispose();
        _posSub?.Dispose();
        _volSub?.Dispose();
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}
