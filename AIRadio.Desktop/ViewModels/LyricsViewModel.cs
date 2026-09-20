using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using Serilog;

namespace AIRadio.Desktop.ViewModels;

/// <summary>
/// 歌词状态：TrackChanged 触发取词（generation + SourceId 双卫兵防旧词晚到），
/// PositionChanged 驱动当前行滚动。状态文案真值表见 docs/plans/2026-09-11-lyrics-mode-design.md §4。
/// </summary>
public class LyricsViewModel : ViewModelBase, IDisposable
{
    private enum StatusKind
    {
        None,
        Loading,
        NoLyrics,
        Instrumental
    }

    private readonly IAudioService _audioService;
    private readonly ILyricService _lyricService;
    private readonly IDisposable _trackSub;
    private readonly IDisposable _positionSub;
    private readonly CancellationTokenSource _cts = new();
    // 常驻状态文案随语言切换重算；静态事件必须持委托在 Dispose 退订
    private readonly Action _onLanguageChanged;
    private IReadOnlyList<LyricLine>? _lines;
    private int _currentIndex = -2;
    private int _generation;
    private int _disposed;
    private StatusKind _statusKind;

    [Reactive] public string PreviousLineText { get; private set; } = string.Empty;
    [Reactive] public string CurrentLineText { get; private set; } = string.Empty;
    [Reactive] public string NextLineText { get; private set; } = string.Empty;
    [Reactive] public bool HasLyrics { get; private set; }
    [Reactive] public bool HasCurrentLine { get; private set; }
    [Reactive] public string LyricStatusText { get; private set; } = string.Empty;
    [Reactive] public bool HasStatusText { get; private set; }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public LyricsViewModel(IAudioService audioService, ILyricService lyricService)
    {
        _audioService = audioService;
        _lyricService = lyricService;

        _trackSub = _audioService.TrackChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track => OnTrackChanged(track));

        _positionSub = _audioService.PositionChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(position => UpdatePosition(position));

        _onLanguageChanged = RefreshStatusText;
        AppLanguage.Changed += _onLanguageChanged;
    }

    private void OnTrackChanged(Track? track)
    {
        if (IsDisposed)
            return;

        var generation = Interlocked.Increment(ref _generation);
        _lines = null;
        _currentIndex = -2;
        ClearLines();
        HasLyrics = false;

        if (track == null)
        {
            SetStatus(StatusKind.None);
            return;
        }

        SetStatus(StatusKind.Loading);
        _ = LoadLyricsAsync(track, generation);
    }

    private async System.Threading.Tasks.Task LoadLyricsAsync(Track track, int generation)
    {
        LyricResult? result = null;
        try
        {
            result = await _lyricService.GetLyricsAsync(track, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // Dispose 取消：此后属性应冻结，直接丢弃
        }
        catch (Exception ex)
        {
            // 取词失败是展示降级，不向上传播
            Log.Debug(ex, "Lyrics load failed for {SourceId}", track.SourceId);
        }

        RxApp.MainThreadScheduler.Schedule(() => ApplyResult(track, generation, result));
    }

    private void ApplyResult(Track track, int generation, LyricResult? result)
    {
        if (IsDisposed || Volatile.Read(ref _generation) != generation)
            return; // 快速切歌：旧词晚到丢弃

        if (!string.Equals(_audioService.CurrentTrack?.SourceId, track.SourceId, StringComparison.Ordinal))
            return; // 播放已切到别的曲目（含跨源重写 SourceId）：丢弃

        if (result is not { Lines.Count: > 0 })
        {
            HasLyrics = false;
            _lines = null;
            ClearLines();
            SetStatus(result is { IsInstrumental: true } ? StatusKind.Instrumental : StatusKind.NoLyrics);
            return;
        }

        _lines = result.Lines;
        HasLyrics = true;
        SetStatus(StatusKind.None);
        _currentIndex = -2;
        // 取词完成时往往已在歌曲中段，立即按当前进度定位
        UpdatePosition(_audioService.CurrentPosition);
    }

    private void UpdatePosition(TimeSpan position)
    {
        if (IsDisposed || !HasLyrics || _lines is not { Count: > 0 })
            return;

        var index = FindCurrentIndex(position);
        if (index == _currentIndex)
            return;

        _currentIndex = index;
        if (index < 0)
        {
            // 前奏段：当前句为空，预告第一句
            PreviousLineText = string.Empty;
            CurrentLineText = string.Empty;
            NextLineText = _lines[0].Text;
            HasCurrentLine = false;
        }
        else
        {
            PreviousLineText = index > 0 ? _lines[index - 1].Text : string.Empty;
            CurrentLineText = _lines[index].Text;
            NextLineText = index + 1 < _lines.Count ? _lines[index + 1].Text : string.Empty;
            HasCurrentLine = !string.IsNullOrWhiteSpace(CurrentLineText);
        }
    }

    /// <summary>最后一个 Time &lt;= position 的行下标；早于首行返回 -1。行列表已按 Time 升序。</summary>
    private int FindCurrentIndex(TimeSpan position)
    {
        int lo = 0, hi = _lines!.Count - 1, index = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_lines[mid].Time <= position)
            {
                index = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return index;
    }

    private void ClearLines()
    {
        PreviousLineText = string.Empty;
        CurrentLineText = string.Empty;
        NextLineText = string.Empty;
        HasCurrentLine = false;
    }

    private void SetStatus(StatusKind kind)
    {
        _statusKind = kind;
        RefreshStatusText();
    }

    private void RefreshStatusText()
    {
        LyricStatusText = _statusKind switch
        {
            StatusKind.Loading => AppLanguage.T("正在获取歌词…", "Loading lyrics..."),
            StatusKind.NoLyrics => AppLanguage.T("暂无歌词", "No lyrics available"),
            StatusKind.Instrumental => AppLanguage.T("纯音乐，请欣赏", "Instrumental — enjoy"),
            _ => string.Empty
        };
        HasStatusText = _statusKind != StatusKind.None;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _cts.Dispose();
        _trackSub?.Dispose();
        _positionSub?.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
    }
}
