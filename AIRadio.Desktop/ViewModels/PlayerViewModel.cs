using ReactiveUI;
using ReactiveUI.SourceGenerators;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public partial class PlayerViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly IDisposable _trackSub;
    private readonly IDisposable _stateSub;
    private readonly IDisposable _posSub;
    private readonly IDisposable _volSub;
    private readonly IDisposable? _mediaInfoSub;
    // 最近一次 LibVLC 实际轨道信息；语言切换重算文案时判断是否仍属当前曲目
    private TrackMediaInfo? _lastMediaInfo;
    // 常驻文案随语言切换重算；静态事件必须持委托在 Dispose 退订
    private readonly Action _onLanguageChanged;

    public IAudioService AudioService => _audioService;

    [Reactive] public partial string TrackTitle { get; set; } = AppLanguage.T("未播放", "Nothing playing");
    [Reactive] public partial string TrackArtist { get; set; } = "";
    [Reactive] public partial string TrackSource { get; set; } = "";
    // 实际播放编码/码率：LibVLC 解析值为准，音源声明值（OpenSubsonic 等）作兜底；空串隐藏徽标
    [Reactive] public partial string TrackQualityText { get; set; } = "";
    [Reactive] public partial string TrackQualityShort { get; set; } = "";
    [Reactive] public partial string TrackQualityDetail { get; set; } = "";
    [Reactive] public partial bool HasQuality { get; set; }
    [Reactive] public partial string TrackPreviewText { get; set; } = AppLanguage.T("试听片段", "Preview");
    [Reactive] public partial bool TrackIsPreview { get; set; }
    [Reactive] public partial bool IsPlaying { get; set; }
    [Reactive] public partial string PlayPauseText { get; set; } = "▶";
    [Reactive] public partial double CurrentSeconds { get; set; }
    [Reactive] public partial double DisplaySeconds { get; set; }
    [Reactive] public partial double TotalSeconds { get; set; }
    [Reactive] public partial float Volume { get; set; } = 0.8f;
    [Reactive] public partial string PositionText { get; set; } = "0:00";
    [Reactive] public partial string DurationText { get; set; } = "0:00";
    [Reactive] public partial bool IsShuffled { get; set; }
    [Reactive] public partial string ShuffleText { get; set; } = "🔀";
    [Reactive] public partial string RepeatMode { get; set; } = "radio";
    [Reactive] public partial string RepeatText { get; set; } = "DJ";
    [Reactive] public partial string RepeatModeTip { get; set; } = AppLanguage.T("电台模式", "Radio mode"); // 语言切换时由 _onLanguageChanged 重算

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
                _lastMediaInfo = null;
                if (track != null)
                {
                    TrackTitle = track.Title;
                    TrackArtist = track.DisplayArtist;
                    TrackSource = GetTrackSource(track);
                    TotalSeconds = track.Duration.TotalSeconds;
                    DurationText = FormatTime(track.Duration);
                    RefreshQualityTexts(track);
                }
                else
                {
                    TrackTitle = AppLanguage.T("未播放", "Nothing playing");
                    TrackArtist = "";
                    TrackSource = "";
                    TotalSeconds = 0;
                    DurationText = "0:00";
                    TrackQualityText = "";
                    TrackQualityShort = "";
                    TrackQualityDetail = "";
                    HasQuality = false;
                    TrackIsPreview = false;
                }
            });

        // Moq 宽松 mock 对未 Setup 的 IObservable 返回 null，此处用 ?. 容忍
        _mediaInfoSub = _audioService.TrackMediaInfo
            ?.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(info =>
            {
                // 快切歌时旧媒体的解析结果可能晚到，按 TrackId 丢弃
                if (_audioService.CurrentTrack is not { } current || info.TrackId != current.Id)
                    return;
                _lastMediaInfo = info;
                RefreshQualityTexts(current);
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
            TrackPreviewText = AppLanguage.T("试听片段", "Preview");
            if (_audioService.CurrentTrack is { } currentTrack)
            {
                currentTrack.RefreshLocalization();
                TrackArtist = currentTrack.DisplayArtist;
                TrackSource = GetTrackSource(currentTrack);
                RefreshQualityTexts(currentTrack);
            }
            // 仅重置占位文案；有曲目在播时保留真实标题
            if (TrackTitle is "未播放" or "Nothing playing")
                TrackTitle = AppLanguage.T("未播放", "Nothing playing");
        };
        AppLanguage.Changed += _onLanguageChanged;
    }

    private static string GetTrackSource(Track track)
    {
        var sourceId = track.SourceId;
        if (string.IsNullOrWhiteSpace(sourceId)) return string.Empty;
        var separator = sourceId.IndexOf(':');
        return separator > 0 ? AppLanguage.MusicSourceName(sourceId[..separator]) : string.Empty;
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
        _mediaInfoSub?.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
    }

    /// <summary>组装播放质量文案：LibVLC 实际值优先，未到达时回落音源声明值。</summary>
    private void RefreshQualityTexts(Track track)
    {
        var info = _lastMediaInfo is { } i && i.TrackId == track.Id ? i : null;
        var codec = info?.Codec ?? track.DeclaredCodec;
        var bitrateKbps = info?.BitrateKbps ?? track.DeclaredBitrateKbps;
        TrackQualityText = FormatQualityText(codec, bitrateKbps);
        TrackQualityShort = FormatQualityText(codec, bitrateKbps, shortForm: true);
        TrackQualityDetail = FormatQualityDetail(codec, bitrateKbps, info?.SampleRateHz, info?.Channels);
        HasQuality = !string.IsNullOrEmpty(TrackQualityText);
        TrackIsPreview = track.IsPreviewStream;
    }

    private static string FormatQualityText(string? codec, int? bitrateKbps, bool shortForm = false)
    {
        if (string.IsNullOrEmpty(codec))
            return string.Empty;
        return bitrateKbps is > 0
            ? shortForm ? $"{codec} {bitrateKbps}k" : $"{codec} · {bitrateKbps} kbps"
            : codec;
    }

    private static string FormatQualityDetail(string? codec, int? bitrateKbps, int? sampleRateHz, int? channels)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(codec))
            parts.Add(codec);
        if (bitrateKbps is > 0)
            parts.Add($"{bitrateKbps} kbps");
        if (sampleRateHz is > 0)
            parts.Add($"{sampleRateHz / 1000d:0.#} kHz");
        if (channels is > 0)
            parts.Add(channels switch
            {
                1 => AppLanguage.T("单声道", "mono"),
                2 => AppLanguage.T("立体声", "stereo"),
                _ => $"{channels} ch"
            });
        return string.Join(" · ", parts);
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
