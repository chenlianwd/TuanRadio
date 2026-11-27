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
    private readonly IDisposable? _playbackRateSub;
    // 最近一次 LibVLC 实际轨道信息；语言切换重算文案时判断是否仍属当前曲目
    private TrackMediaInfo? _lastMediaInfo;
    // 常驻文案随语言切换重算；静态事件必须持委托在 Dispose 退订
    private readonly Action _onLanguageChanged;

    public IAudioService AudioService => _audioService;

    [Reactive] public partial string TrackTitle { get; set; } = AppLanguage.T("未播放", "Nothing playing");
    [Reactive] public partial string TrackArtist { get; set; } = "";
    [Reactive] public partial string TrackSource { get; set; } = "";
    // 实际播放音质：行内显示品级文案（标准/高清/无损），编码/码率等技术细节见 Tooltip；
    // LibVLC 解析值为准，音源声明值（OpenSubsonic 等）作兜底；空串隐藏徽标
    [Reactive] public partial string TrackQualityText { get; set; } = "";
    [Reactive] public partial string TrackQualityShort { get; set; } = "";
    [Reactive] public partial string TrackQualityDetail { get; set; } = "";
    [Reactive] public partial bool HasQuality { get; set; }
    [Reactive] public partial string TrackPreviewText { get; set; } = AppLanguage.T("试听片段", "Preview");
    [Reactive] public partial bool TrackIsPreview { get; set; }
    public static readonly float[] PodcastPlaybackRates = [0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f];
    public const int PodcastSkipBackwardSeconds = 15;
    public const int PodcastSkipForwardSeconds = 30;

    [Reactive] public partial bool IsPlaying { get; set; }
    [Reactive] public partial bool IsLiveStream { get; set; }
    [Reactive] public partial bool IsPodcast { get; set; }
    [Reactive] public partial float PlaybackRate { get; set; } = 1.0f;
    [Reactive] public partial string PlaybackRateText { get; set; } = "1.0x";
    [Reactive] public partial bool IsNonDefaultPlaybackRate { get; set; }
    [Reactive] public partial double CurrentSeconds { get; set; }
    [Reactive] public partial double TotalSeconds { get; set; }
    [Reactive] public partial float Volume { get; set; } = 0.8f;
    [Reactive] public partial string PositionText { get; set; } = "0:00";
    [Reactive] public partial string DurationText { get; set; } = "0:00";
    [Reactive] public partial bool IsShuffled { get; set; }
    [Reactive] public partial string ShuffleTip { get; set; } = AppLanguage.T("随机播放：关", "Shuffle: off");
    [Reactive] public partial string RepeatMode { get; set; } = "radio";
    [Reactive] public partial string RepeatText { get; set; } = "DJ";
    [Reactive] public partial string RepeatModeTip { get; set; } = AppLanguage.T("电台模式", "Radio mode"); // 语言切换时由 _onLanguageChanged 重算
    [Reactive] public partial bool IsRepeatOff { get; set; }
    [Reactive] public partial bool IsRepeatSingle { get; set; }
    [Reactive] public partial double RepeatButtonOpacity { get; set; } = 1.0;

    private bool _isDragging;
    private string? _liveStreamNowPlaying;
    private readonly IDisposable? _liveMetadataSub;

    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleShuffleCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRepeatCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipBackwardCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipForwardCommand { get; }
    public ReactiveCommand<Unit, Unit> CyclePlaybackRateCommand { get; }

    /// <summary>用户主动调整播客倍速时触发，供宿主持久化至 settings.json。</summary>
    public event Action<float>? PodcastPlaybackRateChanged;
    /// <summary>用户主动切换循环模式时触发，供宿主持久化至 settings.json。</summary>
    public event Action<string>? RepeatModeChanged;
    /// <summary>用户主动切换随机播放时触发，供宿主持久化至 settings.json。</summary>
    public event Action<bool>? ShuffleChanged;

    public PlayerViewModel(IAudioService audioService)
    {
        _audioService = audioService;
        UpdateRepeatMode(_audioService.RepeatMode);
        // 与服务端的当前状态对齐（音量/随机），避免启动瞬间两端不同步
        Volume = _audioService.Volume;
        UpdateShuffleState(_audioService.IsShuffled);

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
            UpdateShuffleState(_audioService.IsShuffled);
            ShuffleChanged?.Invoke(IsShuffled);
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
            RepeatModeChanged?.Invoke(next);
        });

        SkipBackwardCommand = ReactiveCommand.Create(() => SkipRelative(-PodcastSkipBackwardSeconds));
        SkipForwardCommand = ReactiveCommand.Create(() => SkipRelative(PodcastSkipForwardSeconds));
        CyclePlaybackRateCommand = ReactiveCommand.Create(CyclePlaybackRate);

        IsPodcast = _audioService.CurrentTrack?.IsPodcastEpisode ?? false;
        SyncPlaybackRateFromService();

        _trackSub = _audioService.TrackChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track =>
            {
                _lastMediaInfo = null;
                _liveStreamNowPlaying = null;
                IsLiveStream = track?.IsLiveStream ?? false;
                IsPodcast = track?.IsPodcastEpisode ?? false;
                if (track != null)
                {
                    TrackTitle = track.Title;
                    TrackArtist = track.DisplayArtist;
                    TrackSource = GetTrackSource(track);
                    if (IsLiveStream)
                    {
                        TotalSeconds = 0;
                        CurrentSeconds = 0;
                        PositionText = "0:00";
                        DurationText = AppLanguage.Get("S_LiveBadge");
                    }
                    else
                    {
                        TotalSeconds = track.Duration.TotalSeconds;
                        DurationText = FormatTime(track.Duration);
                    }
                    RefreshQualityTexts(track);
                }
                else
                {
                    IsLiveStream = false;
                    IsPodcast = false;
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
                SyncPlaybackRateFromService();
            });

        _playbackRateSub = _audioService.PlaybackRateChanged
            ?.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(rate => ApplyPlaybackRateUi(IsPodcast ? rate : 1.0f));

        _liveMetadataSub = _audioService.LiveMetadataChanged
            ?.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(metadata =>
            {
                if (IsLiveStream && _audioService.CurrentTrack is { IsLiveStream: true } current)
                {
                    _liveStreamNowPlaying = metadata;
                    TrackArtist = FormatTrackArtist(current, _liveStreamNowPlaying);
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
            });

        _posSub = _audioService.PositionChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pos =>
            {
                if (IsLiveStream)
                {
                    PositionText = FormatTime(pos);
                    return;
                }
                if (!_isDragging)
                {
                    CurrentSeconds = pos.TotalSeconds;
                }
                PositionText = FormatTime(pos);
            });

        _volSub = this.WhenAnyValue(x => x.Volume)
            .Skip(1)
            .Subscribe(v => _audioService.Volume = v);

        _onLanguageChanged = () =>
        {
            UpdateRepeatMode(RepeatMode);
            UpdateShuffleState(IsShuffled);
            TrackPreviewText = AppLanguage.T("试听片段", "Preview");
            if (_audioService.CurrentTrack is { } currentTrack)
            {
                currentTrack.RefreshLocalization();
                TrackArtist = FormatTrackArtist(currentTrack, _liveStreamNowPlaying);
                TrackSource = GetTrackSource(currentTrack);
                if (IsLiveStream)
                    DurationText = AppLanguage.Get("S_LiveBadge");
                RefreshQualityTexts(currentTrack);
            }
            // 仅重置占位文案；有曲目在播时保留真实标题
            if (TrackTitle is "未播放" or "Nothing playing")
                TrackTitle = AppLanguage.T("未播放", "Nothing playing");
        };
        AppLanguage.Changed += _onLanguageChanged;
    }

    private static string FormatTrackArtist(Track track, string? liveNowPlaying)
    {
        if (!string.IsNullOrWhiteSpace(liveNowPlaying))
            return liveNowPlaying;
        return track.DisplayArtist;
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
        if (IsLiveStream) return;
        _audioService.Seek(TimeSpan.FromSeconds(seconds));
    }

    public void StartSeek()
    {
        if (IsLiveStream) return;
        _isDragging = true;
    }

    public void EndSeek(double seconds)
    {
        if (IsLiveStream) return;
        _isDragging = false;
        _audioService.Seek(TimeSpan.FromSeconds(seconds));
    }

    public void SkipRelative(int seconds)
    {
        if (IsLiveStream || seconds == 0) return;
        _audioService.SeekRelative(TimeSpan.FromSeconds(seconds));
    }

    public void SetPlaybackRate(float rate, bool notifyPersist = true)
    {
        var normalized = Services.AudioService.NormalizePlaybackRate(rate);
        _audioService.SetPlaybackRate(normalized);
        ApplyPlaybackRateUi(IsPodcast ? normalized : 1.0f);
        if (notifyPersist)
            PodcastPlaybackRateChanged?.Invoke(normalized);
    }

    public void CyclePlaybackRate()
    {
        var baseRate = IsPodcast
            ? PlaybackRate
            : (_audioService.PodcastPlaybackRate > 0f ? _audioService.PodcastPlaybackRate : 1.0f);
        SetPlaybackRate(GetNextPlaybackRate(baseRate), notifyPersist: true);
    }

    public void SyncPlaybackRateFromService()
    {
        if (!IsPodcast)
        {
            ApplyPlaybackRateUi(1.0f);
            return;
        }
        var raw = _audioService.PlaybackRate > 0f
            ? _audioService.PlaybackRate
            : (_audioService.PodcastPlaybackRate > 0f ? _audioService.PodcastPlaybackRate : 1.0f);
        ApplyPlaybackRateUi(raw);
    }

    internal static float GetNextPlaybackRate(float current)
    {
        var normalized = Services.AudioService.NormalizePlaybackRate(current);
        for (var i = 0; i < PodcastPlaybackRates.Length; i++)
        {
            if (Math.Abs(PodcastPlaybackRates[i] - normalized) < 0.02f)
                return PodcastPlaybackRates[(i + 1) % PodcastPlaybackRates.Length];
        }
        // 非标准档位向上取最近档位，越界回 1.0x
        foreach (var preset in PodcastPlaybackRates)
        {
            if (preset > normalized)
                return preset;
        }
        return 1.0f;
    }

    internal static string FormatPlaybackRate(float rate)
    {
        var normalized = Services.AudioService.NormalizePlaybackRate(rate);
        return Math.Abs(normalized - MathF.Round(normalized)) < 0.005f
            ? $"{normalized:0.0}x"
            : $"{normalized:0.##}x";
    }

    private void ApplyPlaybackRateUi(float rate)
    {
        var normalized = Services.AudioService.NormalizePlaybackRate(rate);
        PlaybackRate = normalized;
        PlaybackRateText = FormatPlaybackRate(normalized);
        IsNonDefaultPlaybackRate = Math.Abs(normalized - 1.0f) > 0.01f;
    }

    public void Dispose()
    {
        _trackSub?.Dispose();
        _stateSub?.Dispose();
        _posSub?.Dispose();
        _volSub?.Dispose();
        _mediaInfoSub?.Dispose();
        _liveMetadataSub?.Dispose();
        _playbackRateSub?.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
    }

    /// <summary>组装播放质量文案：LibVLC 实际值优先，未到达时回落音源声明值。</summary>
    private void RefreshQualityTexts(Track track)
    {
        var info = _lastMediaInfo is { } i && i.TrackId == track.Id ? i : null;
        var codec = info?.Codec ?? track.DeclaredCodec;
        var bitrateKbps = info?.BitrateKbps ?? track.DeclaredBitrateKbps;
        TrackQualityText = FormatQualityText(codec, bitrateKbps);
        TrackQualityShort = FormatQualityText(codec, bitrateKbps);
        TrackQualityDetail = FormatQualityDetail(codec, bitrateKbps, info?.SampleRateHz, info?.Channels);
        HasQuality = !string.IsNullOrEmpty(TrackQualityText);
        TrackIsPreview = track.IsPreviewStream;
    }

    /// <summary>行内展示音质品级文案（标准/高清/无损）；品级不可判时回退裸编码名，保住悬停技术细节的入口。</summary>
    private static string FormatQualityText(string? codec, int? bitrateKbps)
    {
        return ClassifyQualityTier(codec, bitrateKbps) switch
        {
            QualityTier.Lossless => AppLanguage.T("无损", "Lossless"),
            QualityTier.HighDefinition => AppLanguage.T("高清", "HD"),
            QualityTier.Standard => AppLanguage.T("标准", "Standard"),
            _ => codec ?? string.Empty
        };
    }

    // 无损编码短名：MediaCodecNames 产出的 FLAC/ALAC/PCM 与各音源声明值的常见写法，忽略大小写
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLAC", "ALAC", "PCM", "WAV", "AIFF", "APE"
    };

    // 已知有损编码短名：无论码率多高都不判无损（DTS 1411k、AC3 640k 等高码率有损真实存在）；
    // 未收录的编码名（含 LibVLC 未识别的回退串）保留 hires 声明码率兜底路径
    private static readonly HashSet<string> LossyCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP3", "AAC", "AC3", "EAC3", "DTS", "WMA", "OPUS", "VORBIS"
    };

    /// <summary>
    /// 音质品级归类：无损编码（无需码率）判无损；已知有损编码按码率分档但封顶高清；
    /// 编码缺失或未识别时按码率分档、≥600k 视 hires 判无损；有损编码缺码率无法诚实分档判 None。
    /// </summary>
    internal static QualityTier ClassifyQualityTier(string? codec, int? bitrateKbps)
    {
        if (!string.IsNullOrEmpty(codec) && LosslessCodecs.Contains(codec))
            return QualityTier.Lossless;
        if (bitrateKbps is >= 600 && (string.IsNullOrEmpty(codec) || !LossyCodecs.Contains(codec)))
            return QualityTier.Lossless;
        return bitrateKbps switch
        {
            >= 256 => QualityTier.HighDefinition,
            > 0 => QualityTier.Standard,
            _ => QualityTier.None
        };
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

    /// <summary>启动加载持久化设置后，将 AudioService 当前循环模式与随机状态同步到 UI。</summary>
    public void SyncPlaybackModesFromService()
    {
        UpdateRepeatMode(_audioService.RepeatMode);
        UpdateShuffleState(_audioService.IsShuffled);
    }

    private void UpdateShuffleState(bool shuffled)
    {
        IsShuffled = shuffled;
        ShuffleTip = shuffled
            ? AppLanguage.T("随机播放：开", "Shuffle: on")
            : AppLanguage.T("随机播放：关", "Shuffle: off");
    }

    private void UpdateRepeatMode(string mode)
    {
        mode = Services.AudioService.NormalizeRepeatMode(mode);
        RepeatMode = mode;
        IsRepeatOff = mode == "none";
        IsRepeatSingle = mode == "single";
        RepeatButtonOpacity = IsRepeatOff ? 0.45 : 1.0;
        (RepeatText, RepeatModeTip) = mode switch
        {
            "radio" => ("DJ", AppLanguage.T("电台模式", "Radio mode")),
            "list" => ("ALL", AppLanguage.T("列表循环", "Repeat all")),
            "single" => ("ONE", AppLanguage.T("单曲循环", "Repeat one")),
            _ => ("OFF", AppLanguage.T("关闭循环", "Repeat off"))
        };
    }
}

internal enum QualityTier
{
    None,
    Standard,
    HighDefinition,
    Lossless
}
