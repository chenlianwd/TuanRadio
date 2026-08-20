using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using LibVLCSharp.Shared;
using NAudio.Wave;
using Serilog;
using PlaybackState = AIRadio.Desktop.Models.PlaybackState;

namespace AIRadio.Desktop.Services;

public sealed record TrackUrlResolution(string Url, string? SourceId);

public class AudioService : IAudioService, IDisposable
{
    private static readonly object LibVlcLifecycleGate = new();
    private readonly LibVLC _libVLC;
    private readonly MediaPlayer _player;
    private WaveOutEvent? _ttsOutput;
    private MediaFoundationReader? _ttsReader;
    private readonly Subject<float[]> _spectrumSubject = new();
    private readonly Subject<Track?> _trackChangedSubject = new();
    private readonly Subject<PlaybackState> _stateChangedSubject = new();
    private readonly Subject<TimeSpan> _positionChangedSubject = new();
    private readonly Subject<Track?> _trackEndedSubject = new();
    private readonly Subject<string> _ttsErrorSubject = new();
    private readonly List<Track> _playlist = new();
    private int _currentIndex = -1;
    private bool _shuffle;
    private string _repeatMode = "radio";
    private string _speechMixMode = "duck";
    private bool _resumeAfterTts;
    private bool _ttsWasPlayingWhenMusicPaused;
    private readonly object _ttsStateGate = new();
    private int _ttsSessionId;
    private readonly System.Threading.Timer _positionTimer;
    private readonly SpectrumAnalyzer _spectrumAnalyzer;
    private long _lastPositionMs;
    private int _positionCallbackActive;
    private int _positionOverlapWarningLogged;
    private long _trackStartedAtMs;
    private int _earlyEndRetryCount;
    private int _playbackErrorRetryCount;
    private int _playRequestId;
    private int _recoveryScheduled;
    private int _disposed;
    private readonly Random _rng = new();
    private PlaybackState _currentState = PlaybackState.Stopped;
    private readonly IDisposable _ttsDuckSub;
    private readonly IDisposable _ttsPauseSub;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _nativeCallbackGate = new();
    private readonly object _playerOperationGate = new();
    private readonly SemaphoreSlim _nextGate = new(1, 1);
    private readonly object _ttsOperationGate = new();
    private readonly ManualResetEventSlim _nativeCallbacksDrained = new(initialState: true);
    private int _activeNativeCallbacks;
    private int _nativeCleanupStarted;
    private int _managedCleanupCompleted;
    private readonly object _volumeGate = new();
    private int _pendingPlayerVolume = -1;
    private bool _volumeDrainRunning;
    private Task? _shutdownTtsCleanupTask;

    // Crossfade
    private float _userVolume = 0.8f;
    private const double CrossfadeSeconds = 2.0;
    private static readonly TimeSpan PlaybackCallbackReleaseDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan UrlRefreshTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NativeCleanupTimeout = TimeSpan.FromSeconds(2);
    private bool _isFading;
    private readonly System.Threading.Timer _fadeTimer;
    private Func<Task<Track?>>? _nextCallback;
    private EventHandler<StoppedEventArgs>? _ttsPlaybackStoppedHandler;

    internal enum EarlyEndRecoveryAction
    {
        RefreshCurrentSource,
        TryAlternativeSource,
        Advance
    }

    public bool IsPlaying => !IsDisposed && _currentState == PlaybackState.Playing;
    public TimeSpan CurrentPosition => IsDisposed
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(GetLastKnownPositionMs());
    public TimeSpan Duration => IsDisposed ? TimeSpan.Zero : CurrentTrack?.Duration ?? TimeSpan.Zero;
    public float Volume
    {
        get => _userVolume;
        set
        {
            _userVolume = Math.Clamp(value, 0f, 1f);
            if (IsDisposed)
                return;

            if (!_isFading)
                QueuePlayerVolume((int)(_userVolume * 100));
        }
    }

    public Track? CurrentTrack => _currentIndex >= 0 && _currentIndex < _playlist.Count ? _playlist[_currentIndex] : null;
    public IReadOnlyList<Track> Playlist => _playlist.AsReadOnly();
    public bool IsShuffled => _shuffle;
    public string RepeatMode => _repeatMode;

    public IObservable<float[]> SpectrumData => _spectrumSubject.AsObservable();
    public IObservable<Track?> TrackChanged => _trackChangedSubject.AsObservable();
    public IObservable<PlaybackState> StateChanged => _stateChangedSubject.AsObservable();
    public IObservable<TimeSpan> PositionChanged => _positionChangedSubject.AsObservable();
    public IObservable<Track?> TrackEnded => _trackEndedSubject.AsObservable();
    public IObservable<bool> TtsStateChanged => _ttsStateSubject.AsObservable();
    public IObservable<string> TtsError => _ttsErrorSubject.AsObservable();

    public void SetUrlResolver(Func<string, Task<string?>> resolver)
    {
        if (!IsDisposed)
            _urlResolver = resolver;
    }

    public void SetSpeechMixMode(string mode)
    {
        if (!IsDisposed)
            _speechMixMode = mode == "pause" ? "pause" : "duck";
    }

    public AudioService()
    {
        // LibVLC 的全局 native 初始化/销毁不是并发安全的；测试、重启窗口或
        // 多个宿主同时创建 AudioService 时必须串行化，否则会在 LibVLCNew 处直接
        // 触发 0xC0000005，而不是一个可捕获的托管异常。
        lock (LibVlcLifecycleGate)
        {
            Core.Initialize();
            _libVLC = new LibVLC();
            _player = new MediaPlayer(_libVLC);
        }

        _player.Playing += OnPlayerPlaying;
        _player.Paused += OnPlayerPaused;
        _player.Stopped += OnPlayerStopped;
        _player.EncounteredError += OnPlayerEncounteredError;
        _player.EndReached += OnPlayerEndReached;

        // 优先使用系统输出的真实 FFT；回环设备不可用或暂时没有回调时，
        // 仅在播放态启用视觉兜底，避免频谱区域冻结在最小高度。
        _spectrumAnalyzer = new SpectrumAnalyzer(() => _currentState == PlaybackState.Playing);
        _spectrumAnalyzer.SpectrumReady += OnSpectrumReady;
        _spectrumAnalyzer.Start();

        // Duck main player volume when TTS is speaking
        _ttsDuckSub = _ttsStateSubject.Subscribe(ttsPlaying =>
        {
            if (IsDisposed)
                return;

            if (ttsPlaying)
            {
                if (_speechMixMode == "pause" && _currentState == PlaybackState.Playing)
                {
                    _resumeAfterTts = true;
                    TryPlayerOperation(() => _player.Pause());
                }
                else
                {
                    _resumeAfterTts = false;
                    QueuePlayerVolume((int)(_userVolume * 35));
                }
            }
            else
            {
                if (_speechMixMode == "pause" && _resumeAfterTts)
                {
                    _resumeAfterTts = false;
                    TryPlayerOperation(() => _player.Play());
                }

                if (!_isFading)
                    QueuePlayerVolume((int)(_userVolume * 100));
            }
        });

        _fadeTimer = new System.Threading.Timer(_ => DoFadeStep(), null, Timeout.Infinite, Timeout.Infinite);
        _positionTimer = new System.Threading.Timer(_ => EmitPosition(), null, 500, 500);

        // Pause TTS when music is paused, resume when music plays
        _ttsPauseSub = _stateChangedSubject.Subscribe(state =>
        {
            if (IsDisposed)
                return;

            if (state == PlaybackState.Paused)
            {
                WaveOutEvent? output;
                lock (_ttsStateGate)
                {
                    output = _ttsOutput;
                    _ttsWasPlayingWhenMusicPaused = output?.PlaybackState == NAudio.Wave.PlaybackState.Playing;
                }

                if (_ttsWasPlayingWhenMusicPaused)
                {
                    try { output?.Pause(); } catch (Exception ex) { Log.Debug(ex, "TTS pause failed"); }
                }
            }
            else if (state == PlaybackState.Playing)
            {
                WaveOutEvent? output = null;
                lock (_ttsStateGate)
                {
                    if (_ttsWasPlayingWhenMusicPaused)
                    {
                        _ttsWasPlayingWhenMusicPaused = false;
                        output = _ttsOutput;
                    }
                }

                try { output?.Play(); } catch (Exception ex) { Log.Debug(ex, "TTS resume failed"); }
            }
        });
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private long GetLastKnownPositionMs() => Math.Max(0, Interlocked.Read(ref _lastPositionMs));

    private void QueuePlayerVolume(int volume)
    {
        if (IsDisposed)
            return;

        var shouldStartDrain = false;
        lock (_volumeGate)
        {
            if (IsDisposed)
                return;

            _pendingPlayerVolume = Math.Clamp(volume, 0, 100);
            if (!_volumeDrainRunning)
            {
                _volumeDrainRunning = true;
                shouldStartDrain = true;
            }
        }

        if (shouldStartDrain)
        {
            _ = Task.Factory.StartNew(
                DrainPlayerVolume,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    private void DrainPlayerVolume()
    {
        while (true)
        {
            int volume;
            lock (_volumeGate)
            {
                if (IsDisposed || _pendingPlayerVolume < 0)
                {
                    _pendingPlayerVolume = -1;
                    _volumeDrainRunning = false;
                    return;
                }

                volume = _pendingPlayerVolume;
                _pendingPlayerVolume = -1;
            }

            if (!TryEnterNativeCallback())
                continue;

            try
            {
                // 音量请求在专用后台线程串行执行；即使 LibVLC 原生调用异常或变慢，
                // 也不会把 Avalonia UI 线程和定时器线程一起拖住。
                lock (_playerOperationGate)
                {
                    if (!IsDisposed)
                        _player.Volume = volume;
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    Log.Debug(ex, "LibVLC volume update failed");
            }
            finally
            {
                ExitNativeCallback();
            }
        }
    }

    private bool TryEnterNativeCallback()
    {
        lock (_nativeCallbackGate)
        {
            if (IsDisposed || Volatile.Read(ref _nativeCleanupStarted) != 0)
                return false;

            if (_activeNativeCallbacks++ == 0)
                _nativeCallbacksDrained.Reset();

            return true;
        }
    }

    private void ExitNativeCallback()
    {
        lock (_nativeCallbackGate)
        {
            if (_activeNativeCallbacks <= 0)
                return;

            _activeNativeCallbacks--;
            if (_activeNativeCallbacks == 0)
                _nativeCallbacksDrained.Set();
        }
    }

    private bool TryPlayerOperation(Action operation)
    {
        if (!TryEnterNativeCallback())
            return false;

        try
        {
            lock (_playerOperationGate)
            {
                if (IsDisposed)
                    return false;

                operation();
                return true;
            }
        }
        finally
        {
            ExitNativeCallback();
        }
    }

    private void BeginNativeCleanup()
    {
        lock (_nativeCallbackGate)
        {
            Volatile.Write(ref _nativeCleanupStarted, 1);
            if (_activeNativeCallbacks == 0)
                _nativeCallbacksDrained.Set();
        }
    }

    private void CompleteManagedCleanup()
    {
        if (Interlocked.Exchange(ref _managedCleanupCompleted, 1) != 0)
            return;

        _fadeTimer.Dispose();
        _positionTimer.Dispose();
        _spectrumSubject.Dispose();
        _trackChangedSubject.Dispose();
        _stateChangedSubject.Dispose();
        _positionChangedSubject.Dispose();
        _trackEndedSubject.Dispose();
        _ttsStateSubject.Dispose();
        _ttsErrorSubject.Dispose();
        _lifetimeCts.Dispose();
        _nativeCallbacksDrained.Dispose();
    }

    private void OnPlayerPlaying(object? sender, EventArgs e) => SetState(PlaybackState.Playing);

    private void OnPlayerPaused(object? sender, EventArgs e) => SetState(PlaybackState.Paused);

    private void OnPlayerStopped(object? sender, EventArgs e) => SetState(PlaybackState.Stopped);

    private void OnPlayerEncounteredError(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        var index = _currentIndex;
        var requestId = Volatile.Read(ref _playRequestId);
        Log.Warning("Playback error on track: {Track}", CurrentTrack?.Title);
        SetState(PlaybackState.Stopped);
        if (CurrentTrack != null && index >= 0 && _playbackErrorRetryCount == 0)
        {
            _playbackErrorRetryCount++;
            SchedulePlaybackRetry(index, requestId, "playback error");
        }
    }

    private void OnPlayerEndReached(object? sender, EventArgs e)
    {
        if (IsDisposed || SuppressEarlyTrackEnd())
            return;

        SetState(PlaybackState.Ended);
        // LibVLC 的事件回调中不能同步 Stop/Play；延迟到回调返回后再处理续播。
        var requestId = Volatile.Read(ref _playRequestId);
        ScheduleAfterPlaybackCallback(requestId, () =>
        {
            // In radio mode, hand off to MainWindowViewModel's TrackEnded handler
            // to keep AudioService playlist and PlaylistVM in sync.
            if (IsDisposed)
                return;

            if (_repeatMode == "radio")
                _trackEndedSubject.OnNext(CurrentTrack);
            else
                OnTrackEndReached();
        });
    }

    private void OnSpectrumReady(float[] data)
    {
        if (!IsDisposed)
            _spectrumSubject.OnNext(data);
    }

    private void SetState(PlaybackState state)
    {
        if (IsDisposed)
            return;

        _currentState = state;
        _stateChangedSubject.OnNext(state);
    }

    private void OnTrackEndReached()
    {
        if (IsDisposed)
            return;

        var now = Environment.TickCount64;
        if (now - _lastAdvanceMs < 500) return; // debounce rapid re-entry
        _lastAdvanceMs = now;

        // Notify subscribers (auto-radio handler) BEFORE repeat-mode logic
        _trackEndedSubject.OnNext(CurrentTrack);

        if (_repeatMode == "single" && CurrentTrack != null)
        {
            PlayTrack(_currentIndex);
        }
        else if (_repeatMode == "list")
        {
            Next();
        }
        // "radio" and "none" mode: emit TrackEnded for handler to manage.
        // Auto-radio handler fires and selects/plays the next track.
        // If no handler is active, player naturally stays at end.
    }

    public void LoadTracks(IEnumerable<Track> tracks)
    {
        if (IsDisposed)
            return;

        CancelPendingPlayback();
        _playlist.Clear();
        _playlist.AddRange(tracks);
        _currentIndex = _playlist.Count > 0 ? 0 : -1;
        NotifyTrackChanged();
    }

    public void LoadFiles(IEnumerable<string> filePaths)
    {
        if (IsDisposed)
            return;

        CancelPendingPlayback();
        var tracks = new List<Track>();
        foreach (var path in filePaths)
        {
            tracks.Add(Track.FromFile(path));
        }
        _playlist.Clear();
        _playlist.AddRange(tracks);
        _currentIndex = _playlist.Count > 0 ? 0 : -1;
        NotifyTrackChanged();
    }

    public void AddTracks(IEnumerable<Track> tracks)
    {
        if (IsDisposed)
            return;

        _playlist.AddRange(tracks);
        if (_currentIndex < 0 && _playlist.Count > 0)
        {
            _currentIndex = 0;
            NotifyTrackChanged();
        }
    }

    public void RemoveTrack(Track track)
    {
        if (IsDisposed)
            return;

        var index = _playlist.IndexOf(track);
        if (index < 0) return;

        _playlist.RemoveAt(index);
        if (index < _currentIndex) _currentIndex--;
        else if (index == _currentIndex)
        {
            Stop();
            if (_currentIndex >= _playlist.Count) _currentIndex = _playlist.Count - 1;
            NotifyTrackChanged();
        }
    }

    public void ClearPlaylist()
    {
        if (IsDisposed)
            return;

        Stop();
        _playlist.Clear();
        _currentIndex = -1;
        NotifyTrackChanged();
    }

    public void Play()
    {
        if (IsDisposed)
            return;

        if (CurrentTrack == null) return;
        if (_currentState == PlaybackState.Playing) return;

        if (_currentState == PlaybackState.Paused)
        {
            TryPlayerOperation(() => _player.Play());
        }
        else
        {
            PlayTrack(_currentIndex);
        }
    }

    public void Pause()
    {
        if (!IsDisposed)
            TryPlayerOperation(() => _player.Pause());
    }

    public void Stop()
    {
        if (IsDisposed)
            return;

        CancelPendingPlayback();
        TryPlayerOperation(() => _player.Stop());
        SetState(PlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        if (IsDisposed)
            return;

        if (_currentState == PlaybackState.Playing || _currentState == PlaybackState.Paused)
        {
            var positionMs = Math.Max(0, (long)position.TotalMilliseconds);
            if (TryPlayerOperation(() => _player.Time = positionMs))
                Interlocked.Exchange(ref _lastPositionMs, positionMs);
        }
    }

    public void Shuffle()
    {
        if (!IsDisposed)
            _shuffle = !_shuffle;
    }

    public void SetRepeatMode(string mode)
    {
        if (!IsDisposed)
            _repeatMode = mode;
    }

    public void SetNextCallback(Func<Task<Track?>>? callback)
    {
        if (!IsDisposed)
            _nextCallback = callback;
    }

    public void Next()
    {
        if (IsDisposed)
            return;

        _ = NextAsync();
    }

    private async Task NextAsync()
    {
        if (IsDisposed || !await _nextGate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            var nextCallback = _nextCallback;
            if (_repeatMode == "radio" && nextCallback != null)
            {
                var track = await nextCallback();
                if (IsDisposed)
                    return;

                if (track != null)
                {
                    if (_currentIndex >= 0 && _currentIndex < _playlist.Count &&
                        _playlist[_currentIndex].FilePath == track.FilePath)
                    {
                        var retry = await nextCallback();
                        if (retry != null && retry.FilePath != track.FilePath)
                            track = retry;
                    }

                    if (IsDisposed)
                        return;

                    var index = FindTrackIndex(track);
                    if (index < 0)
                    {
                        AddTracks(new[] { track });
                        index = _playlist.Count - 1;
                    }
                    PlayAtIndex(index);
                }
                else if (_playlist.Count > 1 && _currentIndex >= 0)
                {
                    // 推荐服务暂时不可用时，仍保持在线电台连续播放，
                    // 回退到当前歌单中的下一首，而不是静默停在 Stopped。
                    var fallbackIndex = (_currentIndex + 1) % _playlist.Count;
                    PlayTrack(fallbackIndex);
                }
                return;
            }

            if (_playlist.Count == 0) return;

            var nextIndex = _shuffle
                ? _rng.Next(_playlist.Count)
                : (_currentIndex + 1) % _playlist.Count;
            PlayTrack(nextIndex);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Next failed");
        }
        finally
        {
            _nextGate.Release();
        }
    }

    public void Previous()
    {
        if (IsDisposed)
            return;

        try
        {
            if (_playlist.Count == 0) return;

            if (GetLastKnownPositionMs() > 3000)
            {
                Seek(TimeSpan.Zero);
                return;
            }

            var previousIndex = (_currentIndex - 1 + _playlist.Count) % _playlist.Count;
            PlayTrack(previousIndex);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Previous failed");
        }
    }

    public void PlayAtIndex(int index)
    {
        if (!IsDisposed)
            PlayTrack(index, isRetry: false);
    }

    private int FindTrackIndex(Track track)
    {
        for (int i = 0; i < _playlist.Count; i++)
        {
            var item = _playlist[i];
            if (!string.IsNullOrWhiteSpace(track.SourceId) && item.SourceId == track.SourceId)
                return i;
            if (!string.IsNullOrWhiteSpace(track.FilePath) && item.FilePath == track.FilePath)
                return i;
        }

        return -1;
    }

    private void PlayTrack(int index, bool isRetry = false, bool skipUrlRefresh = false)
    {
        lock (_playerOperationGate)
        {
            PlayTrackCore(index, isRetry, skipUrlRefresh);
        }
    }

    private void PlayTrackCore(int index, bool isRetry = false, bool skipUrlRefresh = false)
    {
        if (IsDisposed || index < 0 || index >= _playlist.Count) return;

        var requestId = Interlocked.Increment(ref _playRequestId);
        Interlocked.Exchange(ref _recoveryScheduled, 0);
        _currentIndex = index; // 确保 CurrentTrack 在 NotifyTrackChanged 时正确
        Interlocked.Exchange(ref _lastPositionMs, 0);
        _trackStartedAtMs = Environment.TickCount64;
        if (!isRetry)
        {
            _earlyEndRetryCount = 0;
            _playbackErrorRetryCount = 0;
        }
        var track = _playlist[index];
        Media? newMedia = null;
        var mediaAssigned = false;
        try
        {
            // Delayed dispose of old media — LibVLC may still reference it during cleanup
            string filePath = track.FilePath;

            // Online tracks may have stale URLs — always refresh before playing to avoid 403.
            // Fire-and-forget refresh only applies when we're in a retry path; normal
            // playback must await the fresh URL so we don't play with an expired link.
            if (!skipUrlRefresh &&
                !string.IsNullOrEmpty(track.SourceId) &&
                (_urlResolver != null || _trackUrlResolver != null))
            {
                var isOnlineUrl = filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                  filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (isRetry || string.IsNullOrWhiteSpace(filePath))
                {
                    _ = RefreshAndPlayTrackAsync(index, track, requestId, isRetry);
                    SetState(PlaybackState.Stopped);
                    NotifyTrackChanged();
                    return;
                }
                // For online tracks with an existing URL, refresh the URL in the background
                // to get a fresh link, but still play immediately with the current URL.
                // Only skip if the URL looks like a local file path.
                if (isOnlineUrl)
                {
                    _ = RefreshTrackUrlAsync(track, requestId);
                }
            }

            var isUrl = filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            // Validate URL to prevent playing arbitrary or malformed URLs
            if (isUrl && (!Uri.TryCreate(filePath, UriKind.Absolute, out var uri) ||
                          (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                Log.Warning("Invalid or unsupported URL for track {Track}: {Url}", track.Title, filePath);
                Next();
                return;
            }

            Media? oldMedia = null;
            if (!TryPlayerOperation(() =>
            {
                oldMedia = _player.Media;
                _player.Stop();
                newMedia = new Media(_libVLC, filePath, isUrl ? FromType.FromLocation : FromType.FromPath);
                if (isUrl)
                {
                    newMedia.AddOption(":network-caching=5000");
                    newMedia.AddOption(":http-reconnect");
                }

                _player.Media = newMedia;
                mediaAssigned = true;
                _player.Play();
            }))
            {
                newMedia?.Dispose();
                return;
            }

            if (oldMedia != null)
                _ = DisposeMediaAfterDelayAsync(oldMedia, _lifetimeCts.Token);

            QueuePlayerVolume(0);
            NotifyTrackChanged();
            StartFadeIn();
            Log.Information("Playing: {Track}", track);
        }
        catch (Exception ex)
        {
            if (!mediaAssigned)
            {
                try { newMedia?.Dispose(); } catch { }
            }
            Log.Error(ex, "Failed to play track: {Track}", track);
            Next();
        }
    }

    private bool SuppressEarlyTrackEnd()
    {
        if (IsDisposed)
            return true;

        var track = CurrentTrack;
        if (track == null || !LooksLikeEarlyEnd(track))
            return false;

        if (_currentIndex < 0)
            return true;

        var recoveryAction = GetEarlyEndRecoveryAction(_earlyEndRetryCount);
        _earlyEndRetryCount++;
        if (recoveryAction == EarlyEndRecoveryAction.RefreshCurrentSource)
        {
            Log.Warning(
                "Ignoring early end for {Track} at {Position}/{Duration}; retrying current track",
                track.Title,
                TimeSpan.FromMilliseconds(GetLastKnownPositionMs()),
                track.Duration);
            SchedulePlaybackRetry(
                _currentIndex,
                Volatile.Read(ref _playRequestId),
                "early end");
        }
        else if (recoveryAction == EarlyEndRecoveryAction.TryAlternativeSource)
        {
            Log.Warning(
                "Repeated early end for {Track} at {Position}/{Duration}; trying another source",
                track.Title,
                TimeSpan.FromMilliseconds(GetLastKnownPositionMs()),
                track.Duration);
            ScheduleAlternativeSourceRetry(
                _currentIndex,
                track,
                Volatile.Read(ref _playRequestId));
        }
        else
        {
            Log.Warning(
                "Ignoring repeated early end for {Track} at {Position}/{Duration}; advancing to next track",
                track.Title,
                TimeSpan.FromMilliseconds(GetLastKnownPositionMs()),
                track.Duration);
            ScheduleNextTrack(
                Volatile.Read(ref _playRequestId),
                "repeated early end");
        }

        return true;
    }

    internal static EarlyEndRecoveryAction GetEarlyEndRecoveryAction(int completedRecoveryCount)
        => completedRecoveryCount switch
        {
            <= 0 => EarlyEndRecoveryAction.RefreshCurrentSource,
            1 => EarlyEndRecoveryAction.TryAlternativeSource,
            _ => EarlyEndRecoveryAction.Advance
        };

    private bool LooksLikeEarlyEnd(Track track)
    {
        var durationMs = track.Duration.TotalMilliseconds;
        if (durationMs < 90_000)
            return false;

        var positionMs = GetLastKnownPositionMs();
        if (positionMs > 0 && durationMs - positionMs > 15_000)
            return true;

        var elapsedMs = Environment.TickCount64 - _trackStartedAtMs;
        return elapsedMs > 0 &&
               elapsedMs < 60_000 &&
               durationMs - elapsedMs > 30_000;
    }

    private async System.Threading.Tasks.Task RefreshTrackUrlAsync(Track track, int requestId)
    {
        try
        {
            var resolution = await ResolveUrlWithTimeoutAsync(track);
            if (resolution != null &&
                requestId == Volatile.Read(ref _playRequestId) &&
                _currentIndex >= 0 &&
                _currentIndex < _playlist.Count &&
                ReferenceEquals(_playlist[_currentIndex], track))
            {
                var changed = ApplyTrackUrlResolution(track, resolution);
                if (changed)
                    Log.Debug("Refreshed URL for track {Track}", track.Title);
            }
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            // 应用关闭时取消未完成的 URL 刷新，不再记录为播放故障。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh URL for {Track}", track.Title);
        }
    }

    private async System.Threading.Tasks.Task RefreshAndPlayTrackAsync(
        int index,
        Track track,
        int requestId,
        bool isRetry)
    {
        try
        {
            var resolution = await ResolveUrlWithTimeoutAsync(track);
            if (resolution != null &&
                requestId == Volatile.Read(ref _playRequestId) &&
                index >= 0 && index < _playlist.Count &&
                ReferenceEquals(_playlist[index], track))
            {
                if (ApplyTrackUrlResolution(track, resolution))
                    Log.Debug("Refreshed URL before play for track {Track}", track.Title);
                PlayTrack(index, isRetry, skipUrlRefresh: true);
                return;
            }

            ScheduleNextTrack(requestId, "play URL unavailable");
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            // 应用关闭时取消未完成的 URL 刷新，不再安排下一首。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh URL before play for {Track}", track.Title);
            ScheduleNextTrack(requestId, "play URL refresh failed");
        }
    }

    private async Task<TrackUrlResolution?> ResolveUrlWithTimeoutAsync(Track track)
    {
        using var resolverCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        try
        {
            var cancellationToken = _lifetimeCts.Token;
            if (_trackUrlResolver != null)
            {
                return await _trackUrlResolver(track, resolverCts.Token)
                    .WaitAsync(UrlRefreshTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            var url = await _urlResolver!(track.SourceId!)
                .WaitAsync(UrlRefreshTimeout, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(url)
                ? null
                : new TrackUrlResolution(url, track.SourceId);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return null;
        }
        catch (TimeoutException)
        {
            resolverCts.Cancel();
            Log.Warning(
                "Refreshing play URL timed out after {Seconds}s for {SourceId}",
                UrlRefreshTimeout.TotalSeconds,
                track.SourceId);
            return null;
        }
    }

    private async Task<TrackUrlResolution?> ResolveAlternativeUrlWithTimeoutAsync(Track track)
    {
        var resolver = _fallbackTrackUrlResolver;
        if (resolver == null)
            return null;

        using var resolverCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        try
        {
            return await resolver(track, resolverCts.Token)
                .WaitAsync(UrlRefreshTimeout, _lifetimeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            return null;
        }
        catch (TimeoutException)
        {
            resolverCts.Cancel();
            Log.Warning(
                "Resolving alternative play URL timed out after {Seconds}s for {SourceId}",
                UrlRefreshTimeout.TotalSeconds,
                track.SourceId);
            return null;
        }
    }

    private void SchedulePlaybackRetry(int index, int requestId, string reason)
    {
        if (IsDisposed || Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) != 0)
            return;

        var cancellationToken = _lifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Ensure the VLC callback has fully returned before calling Stop/Play.
                await Task.Delay(PlaybackCallbackReleaseDelay, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested &&
                    !IsDisposed &&
                    requestId == Volatile.Read(ref _playRequestId) &&
                    index == Volatile.Read(ref _currentIndex))
                {
                    Log.Information("Retrying track {TrackIndex} after {Reason}", index, reason);
                    PlayTrack(index, isRetry: true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 应用关闭时取消排队的重试。
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Scheduled playback retry failed for track index {TrackIndex}", index);
                ScheduleNextTrack(requestId, "scheduled retry failed");
            }
            finally
            {
                Volatile.Write(ref _recoveryScheduled, 0);
            }
        });
    }

    private void ScheduleAlternativeSourceRetry(int index, Track track, int requestId)
    {
        if (_fallbackTrackUrlResolver == null)
        {
            ScheduleNextTrack(requestId, "alternative source resolver unavailable");
            return;
        }

        if (IsDisposed || Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) != 0)
            return;

        var cancellationToken = _lifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PlaybackCallbackReleaseDelay, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested ||
                    IsDisposed ||
                    requestId != Volatile.Read(ref _playRequestId) ||
                    index != Volatile.Read(ref _currentIndex) ||
                    index < 0 ||
                    index >= _playlist.Count ||
                    !ReferenceEquals(_playlist[index], track))
                {
                    return;
                }

                var previousUrl = track.FilePath;
                var previousSourceId = track.SourceId;
                var resolution = await ResolveAlternativeUrlWithTimeoutAsync(track).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested ||
                    IsDisposed ||
                    requestId != Volatile.Read(ref _playRequestId) ||
                    index != Volatile.Read(ref _currentIndex) ||
                    index < 0 ||
                    index >= _playlist.Count ||
                    !ReferenceEquals(_playlist[index], track))
                {
                    return;
                }

                var sourceChanged = resolution != null &&
                    !string.Equals(previousSourceId, resolution.SourceId, StringComparison.Ordinal);
                var urlChanged = resolution != null &&
                    !string.Equals(previousUrl, resolution.Url, StringComparison.Ordinal);
                if (resolution != null && (sourceChanged || urlChanged))
                {
                    ApplyTrackUrlResolution(track, resolution);
                    Log.Information(
                        "Retrying {Track} with alternative source {SourceId}",
                        track.Title,
                        track.SourceId);
                    PlayTrack(index, isRetry: true, skipUrlRefresh: true);
                    return;
                }

                ScheduleNextTrack(requestId, "alternative source unavailable");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 应用关闭时取消替代音源解析。
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Alternative source recovery failed for {Track}", track.Title);
                ScheduleNextTrack(requestId, "alternative source recovery failed");
            }
            finally
            {
                Volatile.Write(ref _recoveryScheduled, 0);
            }
        });
    }

    private static bool ApplyTrackUrlResolution(Track track, TrackUrlResolution resolution)
    {
        var resolvedSourceId = string.IsNullOrWhiteSpace(resolution.SourceId)
            ? track.SourceId
            : resolution.SourceId;
        var changed = !string.Equals(track.FilePath, resolution.Url, StringComparison.Ordinal) ||
                      !string.Equals(track.SourceId, resolvedSourceId, StringComparison.Ordinal);
        track.FilePath = resolution.Url;
        track.SourceId = resolvedSourceId;
        return changed;
    }

    private void ScheduleNextTrack(int requestId, string reason)
    {
        ScheduleAfterPlaybackCallback(requestId, () =>
        {
            if (IsDisposed || requestId != Volatile.Read(ref _playRequestId))
                return;

            Log.Warning("Advancing after playback recovery failed: {Reason}", reason);
            Next();
        });
    }

    private void ScheduleAfterPlaybackCallback(int requestId, Action action)
    {
        if (IsDisposed)
            return;

        var cancellationToken = _lifetimeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PlaybackCallbackReleaseDelay, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested &&
                    !IsDisposed &&
                    requestId == Volatile.Read(ref _playRequestId))
                    action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 应用关闭时取消延迟续播。
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Deferred playback action failed");
            }
        });
    }

    private void CancelPendingPlayback()
    {
        Interlocked.Increment(ref _playRequestId);
        Volatile.Write(ref _recoveryScheduled, 0);
    }

    private async Task DisposeMediaAfterDelayAsync(Media media, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            // 关闭时立即进入 finally，避免旧 Media 被保留到进程结束。
        }
        finally
        {
            try { media.Dispose(); } catch { }
        }
    }

    private void StartFadeIn()
    {
        _isFading = true;
        _fadeStepStart = Environment.TickCount64;
        _fadeDirection = 1;
        _fadeTimer.Change(33, 33); // ~30fps
    }

    private void StartFadeOut()
    {
        if (_isFading && _fadeDirection == -1) return;
        _isFading = true;
        _fadeStepStart = Environment.TickCount64;
        _fadeDirection = -1;
        _fadeTimer.Change(33, 33);
    }

    private long _fadeStepStart;
    private int _fadeDirection = 1;
    private long _lastAdvanceMs;
    private Func<string, Task<string?>>? _urlResolver;
    private Func<Track, CancellationToken, Task<TrackUrlResolution?>>? _trackUrlResolver;
    private Func<Track, CancellationToken, Task<TrackUrlResolution?>>? _fallbackTrackUrlResolver;

    public void SetTrackUrlResolver(Func<Track, CancellationToken, Task<TrackUrlResolution?>> resolver)
    {
        if (!IsDisposed)
            _trackUrlResolver = resolver;
    }

    public void SetTrackUrlResolver(Func<Track, Task<string?>> resolver)
    {
        SetTrackUrlResolver(async (track, _) =>
        {
            var url = await resolver(track).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(url)
                ? null
                : new TrackUrlResolution(url, track.SourceId);
        });
    }

    public void SetFallbackTrackUrlResolver(Func<Track, CancellationToken, Task<TrackUrlResolution?>> resolver)
    {
        if (!IsDisposed)
            _fallbackTrackUrlResolver = resolver;
    }

    private void DoFadeStep()
    {
        if (!TryEnterNativeCallback())
            return;

        try
        {
            var elapsed = (Environment.TickCount64 - _fadeStepStart) / 1000.0;
            var progress = Math.Clamp(elapsed / CrossfadeSeconds, 0.0, 1.0);

            if (_fadeDirection == 1)
            {
                QueuePlayerVolume((int)(_userVolume * progress * 100));
                if (progress >= 1.0)
                {
                    _isFading = false;
                    QueuePlayerVolume((int)(_userVolume * 100));
                    _fadeTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            else
            {
                QueuePlayerVolume((int)(_userVolume * (1.0 - progress) * 100));
                if (progress >= 1.0)
                {
                    _isFading = false;
                    _fadeTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    // Auto-advance to next track
                    if (_repeatMode == "single" && CurrentTrack != null)
                        PlayTrack(_currentIndex);
                    else if (_repeatMode == "list")
                        Next();
                    // "none" mode: let auto-radio handler manage continuation
                }
            }
        }
        finally
        {
            ExitNativeCallback();
        }
    }

    private void NotifyTrackChanged()
    {
        if (!IsDisposed)
            _trackChangedSubject.OnNext(CurrentTrack);
    }

    private void EmitPosition()
    {
        if (Interlocked.Exchange(ref _positionCallbackActive, 1) != 0)
        {
            if (Interlocked.Exchange(ref _positionOverlapWarningLogged, 1) == 0)
                Log.Warning("Skipping overlapping position callback; LibVLC position query is still running");
            return;
        }

        if (!TryEnterNativeCallback())
        {
            Volatile.Write(ref _positionCallbackActive, 0);
            return;
        }

        try
        {
            // 暂停时保留上次成功查询的位置，不再额外进入 LibVLC 原生调用。
            if (_currentState != PlaybackState.Playing)
                return;

            long pos;
            lock (_playerOperationGate)
            {
                if (IsDisposed)
                    return;

                pos = Math.Max(0, _player.Time);
            }

            if (pos != Interlocked.Read(ref _lastPositionMs))
            {
                Interlocked.Exchange(ref _lastPositionMs, pos);
                _positionChangedSubject.OnNext(TimeSpan.FromMilliseconds(pos));
            }

            // Let LibVLC EndReached decide when a track is really over.
            // Some online sources report unstable duration and can otherwise fade out far too early.
        }
        catch (Exception ex) when (IsDisposed)
        {
            Log.Debug(ex, "Ignoring position update during audio shutdown");
        }
        finally
        {
            ExitNativeCallback();
            Volatile.Write(ref _positionCallbackActive, 0);
            Volatile.Write(ref _positionOverlapWarningLogged, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 先切断所有会触碰原生播放器的入口。尤其是位置定时器必须在
        // WASAPI/LibVLC 释放前停止，否则关闭期间会不断堆积 get_Time 线程。
        CancelPendingPlayback();
        _lifetimeCts.Cancel();
        _nextCallback = null;
        _urlResolver = null;
        _trackUrlResolver = null;
        _fallbackTrackUrlResolver = null;
        _currentState = PlaybackState.Stopped;
        _isFading = false;

        try { _fadeTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { _positionTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }

        _ttsDuckSub.Dispose();
        _ttsPauseSub.Dispose();

        // NAudio 的 Stop/Dispose 也可能等待设备线程，不能再从 Avalonia 关闭线程
        // 同步调用。后台任务会被原生清理流程统一等待；即使超过 UI 的等待上限，
        // 资源稍后恢复时仍会继续完成清理。
        _shutdownTtsCleanupTask = Task.Factory.StartNew(
            () => StopTtsInternal(notifyState: false),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _player.Playing -= OnPlayerPlaying;
        _player.Paused -= OnPlayerPaused;
        _player.Stopped -= OnPlayerStopped;
        _player.EncounteredError -= OnPlayerEncounteredError;
        _player.EndReached -= OnPlayerEndReached;
        _spectrumAnalyzer.SpectrumReady -= OnSpectrumReady;
        BeginNativeCleanup();

        // LibVLC 和 WASAPI 都可能在原生 Dispose 中等待设备/回调线程。
        // 将它们放入后台专用线程，并设置上限，不能再阻塞 Avalonia 关闭线程。
        var nativeCleanup = Task.Factory.StartNew(
            DisposeNativeResourcesAndComplete,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        if (!nativeCleanup.Wait(NativeCleanupTimeout))
            Log.Warning("Audio native resources did not finish disposing within {Seconds}s; shutdown will continue", NativeCleanupTimeout.TotalSeconds);
    }

    private void DisposeNativeResourcesAndComplete()
    {
        // 有原生调用卡住时，绝不能为了清理而并发 Dispose LibVLC；这会把卡死升级为
        // 原生崩溃。UI 只等待有限时间，但后台清理会继续等待回调真正退出，避免回调
        // 在 2 秒后恢复时永久错过释放机会。
        if (!_nativeCallbacksDrained.Wait(NativeCleanupTimeout))
        {
            Log.Warning(
                "Audio native callback did not drain within {Seconds}s; cleanup will continue in background",
                NativeCleanupTimeout.TotalSeconds);
            _nativeCallbacksDrained.Wait();
            Log.Information("Audio native callbacks drained; resuming deferred cleanup");
        }

        try
        {
            DisposeNativeResources();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unexpected audio native cleanup failure");
        }
        finally
        {
            CompleteManagedCleanup();
        }
    }

    private void DisposeNativeResources()
    {
        var playerCleanup = Task.Factory.StartNew(
            DisposePlayerResources,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var spectrumCleanup = Task.Factory.StartNew(
            DisposeSpectrumResources,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var ttsCleanup = _shutdownTtsCleanupTask;
        Task[] cleanupTasks = ttsCleanup == null
            ? new[] { playerCleanup, spectrumCleanup }
            : new[] { playerCleanup, spectrumCleanup, ttsCleanup };

        try
        {
            Task.WaitAll(cleanupTasks);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "One or more audio native cleanup tasks failed");
        }
    }

    private void DisposePlayerResources()
    {
        lock (LibVlcLifecycleGate)
        lock (_playerOperationGate)
        {
            try
            {
                _player.Stop();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "LibVLC player stop failed during shutdown");
            }

            try
            {
                _player.Media?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "LibVLC current media dispose failed during shutdown");
            }

            try
            {
                _player.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "LibVLC player dispose failed during shutdown");
            }

            try
            {
                _libVLC.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "LibVLC instance dispose failed during shutdown");
            }
        }
    }

    private void DisposeSpectrumResources()
    {
        try
        {
            _spectrumAnalyzer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Spectrum analyzer dispose failed during shutdown");
        }
    }

    private readonly Subject<bool> _ttsStateSubject = new();
    private string? _currentTtsFile;

    public void PlayTtsAudio(byte[] audioData)
    {
        lock (_ttsOperationGate)
        {
            PlayTtsAudioCore(audioData);
        }
    }

    private void PlayTtsAudioCore(byte[] audioData)
    {
        if (IsDisposed)
            return;

        var tempPath = string.Empty;
        try
        {
            StopTtsInternalCore(notifyState: false);
            tempPath = WriteTtsTempFile(audioData);
            var sessionId = Interlocked.Increment(ref _ttsSessionId);

            lock (_ttsStateGate)
            {
                _currentTtsFile = tempPath;
            }

            InitTtsPlayback(tempPath, sessionId, tempPath);
            var signature = BitConverter.ToString(audioData.Take(Math.Min(8, audioData.Length)).ToArray());
            if (IsDisposed)
            {
                StopTtsInternalCore(notifyState: false);
                return;
            }

            _ttsStateSubject.OnNext(true);
            _ttsOutput!.Play();
            Log.Information("TTS NAudio playback started: {Bytes} bytes, header={Header}, file={File}", audioData.Length, signature, tempPath);
        }
        catch (Exception ex)
        {
            StopTtsInternalCore(notifyState: false);
            CleanupTtsFile(tempPath);
            if (!IsDisposed)
            {
                _ttsStateSubject.OnNext(false);
                _ttsErrorSubject.OnNext("语音播放设备不可用，请检查 Windows 默认输出设备或关闭语音播报。");
            }
            Log.Warning(ex, "Failed to play TTS audio");
        }
    }

    private string WriteTtsTempFile(byte[] audioData)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, audioData);
        return path;
    }

    private void InitTtsPlayback(string filePath, int sessionId, string tempPath)
    {
        MediaFoundationReader? reader = null;
        WaveOutEvent? output = null;
        try
        {
            reader = new MediaFoundationReader(filePath);
            output = new WaveOutEvent { DesiredLatency = 120 };
            output.Init(reader);
            TrySetTtsVolume(output, 1.0f);
            EventHandler<StoppedEventArgs> stoppedHandler = (_, e) => OnTtsPlaybackStopped(e, sessionId, tempPath);
            output.PlaybackStopped += stoppedHandler;

            lock (_ttsStateGate)
            {
                _ttsReader = reader;
                _ttsOutput = output;
                _ttsPlaybackStoppedHandler = stoppedHandler;
            }
        }
        catch
        {
            try { output?.Dispose(); } catch { }
            try { reader?.Dispose(); } catch { }
            throw;
        }
    }

    private void OnTtsPlaybackStopped(StoppedEventArgs e, int sessionId, string tempPath)
    {
        if (IsDisposed)
            return;

        WaveOutEvent? output;
        MediaFoundationReader? reader;
        lock (_ttsStateGate)
        {
            // StopTtsInternal detaches the handler and clears the active file.
            // A late callback from that old output must not affect the new TTS session.
            if (_ttsSessionId != sessionId || _currentTtsFile != tempPath)
                return;

            output = _ttsOutput;
            reader = _ttsReader;
            if (output != null && _ttsPlaybackStoppedHandler != null)
                output.PlaybackStopped -= _ttsPlaybackStoppedHandler;
            _ttsPlaybackStoppedHandler = null;
            _ttsOutput = null;
            _ttsReader = null;
            _currentTtsFile = null;
            _ttsWasPlayingWhenMusicPaused = false;
        }

        if (e.Exception != null)
        {
            Log.Warning(e.Exception, "TTS NAudio playback failed");
        }
        else
        {
            Log.Information("TTS playback ended");
        }

        if (!IsDisposed)
            _ttsStateSubject.OnNext(false);

        // NAudio raises PlaybackStopped from its playback thread or the captured
        // synchronization context. Release native resources after the callback
        // has returned so the event path stays short and non-reentrant.
        _ = Task.Run(() => DisposeTtsResources(output, reader, tempPath));
    }

    private static void CleanupTtsFile(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            try { File.Delete(path); } catch { }
    }

    private static void TrySetTtsVolume(WaveOutEvent output, float volume)
    {
        try
        {
            output.Volume = volume;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set TTS output volume; continuing with device default volume");
        }
    }

    public void StopTts()
    {
        if (IsDisposed)
            return;

        StopTtsInternal(notifyState: true);
        Log.Information("TTS playback stopped");
    }

    private void StopTtsInternal(bool notifyState)
    {
        lock (_ttsOperationGate)
        {
            StopTtsInternalCore(notifyState);
        }
    }

    private void StopTtsInternalCore(bool notifyState)
    {
        WaveOutEvent? output;
        MediaFoundationReader? reader;
        string? ttsFile;

        lock (_ttsStateGate)
        {
            output = _ttsOutput;
            reader = _ttsReader;
            ttsFile = _currentTtsFile;
            if (output != null && _ttsPlaybackStoppedHandler != null)
                output.PlaybackStopped -= _ttsPlaybackStoppedHandler;
            _ttsPlaybackStoppedHandler = null;
            _ttsOutput = null;
            _ttsReader = null;
            _currentTtsFile = null;
            _ttsWasPlayingWhenMusicPaused = false;
        }

        DisposeTtsResources(output, reader, ttsFile);

        if (notifyState && !IsDisposed)
            _ttsStateSubject.OnNext(false);
    }

    private static void DisposeTtsResources(
        WaveOutEvent? output,
        MediaFoundationReader? reader,
        string? ttsFile)
    {
        try
        {
            output?.Stop();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TTS output stop failed during cleanup");
        }

        try
        {
            output?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TTS output dispose failed during cleanup");
        }

        try
        {
            reader?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TTS reader dispose failed during cleanup");
        }

        CleanupTtsFile(ttsFile ?? string.Empty);
    }
}
