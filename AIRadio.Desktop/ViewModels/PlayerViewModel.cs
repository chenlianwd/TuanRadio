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
    private readonly IDisposable _trackSub;
    private readonly IDisposable _stateSub;
    private readonly IDisposable _posSub;
    private readonly IDisposable _volSub;
    // 常驻文案随语言切换重算；静态事件必须持委托在 Dispose 退订
    private readonly Action _onLanguageChanged;

    public IAudioService AudioService => _audioService;

    [Reactive] public string TrackTitle { get; set; } = "未播放";
    [Reactive] public string TrackArtist { get; set; } = "";
    [Reactive] public bool IsPlaying { get; set; }
    [Reactive] public string PlayPauseText { get; set; } = "▶";
    [Reactive] public double CurrentSeconds { get; set; }
    [Reactive] public double DisplaySeconds { get; set; }
    [Reactive] public double TotalSeconds { get; set; }
    [Reactive] public float Volume { get; set; } = 0.8f;
    [Reactive] public string PositionText { get; set; } = "0:00";
    [Reactive] public string DurationText { get; set; } = "0:00";
    [Reactive] public bool IsShuffled { get; set; }
    [Reactive] public string ShuffleText { get; set; } = "🔀";
    [Reactive] public string RepeatMode { get; set; } = "radio";
    [Reactive] public string RepeatText { get; set; } = "DJ";
    [Reactive] public string RepeatModeTip { get; set; } = "电台模式"; // 语言切换时由 _onLanguageChanged 重算

    private bool _isDragging;

    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }

    public PlayerViewModel(IAudioService audioService)
    {
        _audioService = audioService;
        UpdateRepeatMode(_audioService.RepeatMode);
        // 与服务端的当前状态对齐（音量/随机），避免启动瞬间两端不同步
        Volume = _audioService.Volume;
        IsShuffled = _audioService.IsShuffled;

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
                "radio" => "list",
                "list" => "single",
                "single" => "none",
                _ => "radio"
            };
            _audioService.SetRepeatMode(next);
            UpdateRepeatMode(next);
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
                    TrackTitle = AppLanguage.T("未播放", "Nothing playing");
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
                // 拖动期间 Slider 通过 TwoWay 绑定写入 CurrentSeconds，此处回写会把 thumb 拉回播放位置
                if (!_isDragging)
                {
                    CurrentSeconds = pos.TotalSeconds;
                    DisplaySeconds = pos.TotalSeconds;
                }
                PositionText = FormatTime(pos);
            });

        _volSub = this.WhenAnyValue(x => x.Volume)
            .Skip(1)
            .Subscribe(v => _audioService.Volume = v);

        _onLanguageChanged = () =>
        {
            UpdateRepeatMode(RepeatMode);
            // 仅重置占位文案；有曲目在播时保留真实标题
            if (TrackTitle is "未播放" or "Nothing playing")
                TrackTitle = AppLanguage.T("未播放", "Nothing playing");
        };
        AppLanguage.Changed += _onLanguageChanged;
    }

    public void SeekTo(double seconds)
    {
        _audioService.Seek(TimeSpan.FromSeconds(seconds));
    }

    public void StartSeek()
    {
        _isDragging = true;
    }

    public void EndSeek(double seconds)
    {
        _isDragging = false;
        _audioService.Seek(TimeSpan.FromSeconds(seconds));
    }

    public void Dispose()
    {
        _trackSub?.Dispose();
        _stateSub?.Dispose();
        _posSub?.Dispose();
        _volSub?.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private void UpdateRepeatMode(string mode)
    {
        RepeatMode = mode;
        (RepeatText, RepeatModeTip) = mode switch
        {
            "radio" => ("DJ", AppLanguage.T("电台模式", "Radio mode")),
            "list" => ("ALL", AppLanguage.T("列表循环", "Repeat all")),
            "single" => ("ONE", AppLanguage.T("单曲循环", "Repeat one")),
            _ => ("OFF", AppLanguage.T("关闭循环", "Repeat off"))
        };
    }
}
