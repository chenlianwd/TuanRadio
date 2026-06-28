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

public class AudioService : IAudioService, IDisposable
{
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
    private readonly HashSet<int> _cancelledTtsSessions = new();
    private readonly System.Threading.Timer _positionTimer;
    private readonly System.Threading.Timer _spectrumTimer;
    private long _lastPositionMs;
    private long _trackStartedAtMs;
    private int _earlyEndRetryCount;
    private int _playbackErrorRetryCount;
    private int _playRequestId;
    private readonly Random _rng = new();
    private PlaybackState _currentState = PlaybackState.Stopped;
    private readonly IDisposable _ttsDuckSub;
    private readonly IDisposable _ttsPauseSub;

    // Crossfade
    private float _userVolume = 0.8f;
    private const double CrossfadeSeconds = 2.0;
    private bool _isFading;
    private readonly System.Threading.Timer _fadeTimer;
    private Func<Task<Track?>>? _nextCallback;
    private Func<Task<Track?>>? _previousCallback;

    public bool IsPlaying => _player.IsPlaying;
    public TimeSpan CurrentPosition => TimeSpan.FromMilliseconds(_player.Time);
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_player.Length);
    public float Volume
    {
        get => _player.Volume / 100f;
        set
        {
            _userVolume = Math.Clamp(value, 0f, 1f);
            if (!_isFading)
                _player.Volume = (int)(_userVolume * 100);
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

    public void SetUrlResolver(Func<string, Task<string?>> resolver) => _urlResolver = resolver;
    public void SetSpeechMixMode(string mode) => _speechMixMode = mode == "pause" ? "pause" : "duck";

    public AudioService()
    {
        Core.Initialize();
        _libVLC = new LibVLC();
        _player = new MediaPlayer(_libVLC);

        _player.Playing += (_, _) => SetState(PlaybackState.Playing);
        _player.Paused += (_, _) => SetState(PlaybackState.Paused);
        _player.Stopped += (_, _) => SetState(PlaybackState.Stopped);
        _player.EncounteredError += (_, _) =>
        {
            Log.Warning("Playback error on track: {Track}", CurrentTrack?.Title);
            SetState(PlaybackState.Stopped);
            if (CurrentTrack != null && _currentIndex >= 0 && _playbackErrorRetryCount == 0)
            {
                _playbackErrorRetryCount++;
                PlayTrack(_currentIndex, isRetry: true);
            }
        };
        _player.EndReached += (_, _) =>
        {
            if (SuppressEarlyTrackEnd())
                return;

            SetState(PlaybackState.Ended);
            // In radio mode, hand off to MainWindowViewModel's TrackEnded handler
            // to keep AudioService playlist and PlaylistVM in sync.
            if (_repeatMode == "radio")
                _trackEndedSubject.OnNext(CurrentTrack);
            else
                OnTrackEndReached();
        };

        // No audio callbacks — use simulated spectrum (VLC handles output normally)
        _spectrumTimer = new System.Threading.Timer(_ => EmitSimulatedSpectrum(), null, 100, 33);

        // Duck main player volume when TTS is speaking
        _ttsDuckSub = _ttsStateSubject.Subscribe(ttsPlaying =>
        {
            if (ttsPlaying)
            {
                if (_speechMixMode == "pause" && _player.IsPlaying)
                {
                    _resumeAfterTts = true;
                    _player.Pause();
                }
                else
                {
                    _resumeAfterTts = false;
                    _player.Volume = (int)(_userVolume * 35);
                }
            }
            else
            {
                if (_speechMixMode == "pause" && _resumeAfterTts)
                {
                    _resumeAfterTts = false;
                    _player.Play();
                }

                if (!_isFading)
                    _player.Volume = (int)(_userVolume * 100);
            }
        });

        _fadeTimer = new System.Threading.Timer(_ => DoFadeStep(), null, Timeout.Infinite, Timeout.Infinite);
        _positionTimer = new System.Threading.Timer(_ => EmitPosition(), null, 500, 500);

        // Pause TTS when music is paused, resume when music plays
        _ttsPauseSub = _stateChangedSubject.Subscribe(state =>
        {
            if (state == PlaybackState.Paused)
            {
                if (_ttsOutput?.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    _ttsWasPlayingWhenMusicPaused = true;
                    _ttsOutput.Pause();
                }
                else
                {
                    _ttsWasPlayingWhenMusicPaused = false;
                }
            }
            else if (state == PlaybackState.Playing && _ttsWasPlayingWhenMusicPaused)
            {
                _ttsWasPlayingWhenMusicPaused = false;
                _ttsOutput?.Play();
            }
        });
    }

    
    private void SetState(PlaybackState state)
    {
        _currentState = state;
        _stateChangedSubject.OnNext(state);
    }

    private void OnTrackEndReached()
    {
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
        _playlist.Clear();
        _playlist.AddRange(tracks);
        _currentIndex = _playlist.Count > 0 ? 0 : -1;
        NotifyTrackChanged();
    }

    public void LoadFiles(IEnumerable<string> filePaths)
    {
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
        _playlist.AddRange(tracks);
        if (_currentIndex < 0 && _playlist.Count > 0)
        {
            _currentIndex = 0;
            NotifyTrackChanged();
        }
    }

    public void RemoveTrack(Track track)
    {
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
        Stop();
        _playlist.Clear();
        _currentIndex = -1;
        NotifyTrackChanged();
    }

    public void Play()
    {
        if (CurrentTrack == null) return;
        if (_player.IsPlaying) return;

        if (_player.State == VLCState.Paused)
        {
            _player.Play();
        }
        else
        {
            PlayTrack(_currentIndex);
        }
    }

    public void Pause() => _player.Pause();

    public void Stop()
    {
        _player.Stop();
        SetState(PlaybackState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        if (_player.IsPlaying || _player.State == VLCState.Paused)
        {
            _player.Time = (long)position.TotalMilliseconds;
        }
    }

    public void Shuffle() => _shuffle = !_shuffle;

    public void SetRepeatMode(string mode) => _repeatMode = mode;

    public void SetNextCallback(Func<Task<Track?>>? callback) => _nextCallback = callback;
    public void SetPreviousCallback(Func<Task<Track?>>? callback) => _previousCallback = callback;

    public async void Next()
    {
        try
        {
            if (_repeatMode == "radio" && _nextCallback != null)
            {
                var track = await _nextCallback();
                if (track != null)
                {
                    if (_currentIndex >= 0 && _currentIndex < _playlist.Count &&
                        _playlist[_currentIndex].FilePath == track.FilePath)
                    {
                        var retry = await _nextCallback();
                        if (retry != null && retry.FilePath != track.FilePath)
                            track = retry;
                    }

                    var index = FindTrackIndex(track);
                    if (index < 0)
                    {
                        AddTracks(new[] { track });
                        index = _playlist.Count - 1;
                    }
                    PlayAtIndex(index);
                }
                return;
            }

            if (_playlist.Count == 0) return;

            if (_shuffle)
                _currentIndex = _rng.Next(_playlist.Count);
            else
                _currentIndex = (_currentIndex + 1) % _playlist.Count;

            PlayTrack(_currentIndex);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Next failed");
        }
    }

    public async void Previous()
    {
        try
        {
            if (_repeatMode == "radio" && _previousCallback != null)
            {
                var track = await _previousCallback();
                if (track != null)
                {
                    var index = FindTrackIndex(track);
                    if (index < 0)
                    {
                        AddTracks(new[] { track });
                        index = _playlist.Count - 1;
                    }
                    PlayAtIndex(index);
                    return;
                }
            }

            if (_playlist.Count == 0) return;

            if (_player.Time > 3000)
            {
                Seek(TimeSpan.Zero);
                return;
            }

            _currentIndex = (_currentIndex - 1 + _playlist.Count) % _playlist.Count;
            PlayTrack(_currentIndex);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Previous failed");
        }
    }

    public void PlayAtIndex(int index)
    {
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

    private void PlayTrack(int index, bool isRetry = false)
    {
        if (index < 0 || index >= _playlist.Count) return;

        var requestId = Interlocked.Increment(ref _playRequestId);
        _currentIndex = index; // 确保 CurrentTrack 在 NotifyTrackChanged 时正确
        _trackStartedAtMs = Environment.TickCount64;
        if (!isRetry)
        {
            _earlyEndRetryCount = 0;
            _playbackErrorRetryCount = 0;
        }
        var track = _playlist[index];
        try
        {
            // Delayed dispose of old media — LibVLC may still reference it during cleanup
            var oldMedia = _player.Media;
            _player.Stop();
            if (oldMedia != null)
                Task.Delay(2000).ContinueWith(_ => { try { oldMedia.Dispose(); } catch { } });

            string filePath = track.FilePath;

            // Online tracks may have stale URLs — always refresh before playing to avoid 403.
            // Fire-and-forget refresh only applies when we're in a retry path; normal
            // playback must await the fresh URL so we don't play with an expired link.
            if (!string.IsNullOrEmpty(track.SourceId) && _urlResolver != null)
            {
                var isOnlineUrl = filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                  filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (isRetry || string.IsNullOrWhiteSpace(filePath))
                {
                    _ = RefreshAndPlayTrackAsync(index, track, requestId);
                    SetState(PlaybackState.Stopped);
                    NotifyTrackChanged();
                    return;
                }
                // For online tracks with an existing URL, refresh the URL in the background
                // to get a fresh link, but still play immediately with the current URL.
                // Only skip if the URL looks like a local file path.
                if (isOnlineUrl)
                {
                    _ = RefreshTrackUrlAsync(track);
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

            var media = new Media(_libVLC, filePath, isUrl ? FromType.FromLocation : FromType.FromPath);
            if (isUrl)
            {
                media.AddOption(":network-caching=5000");
                media.AddOption(":http-reconnect");
            }
            _player.Media = media;
            _player.Volume = 0;
            _player.Play();
            NotifyTrackChanged();
            StartFadeIn();
            Log.Information("Playing: {Track}", track);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to play track: {Track}", track);
            Next();
        }
    }

    private bool SuppressEarlyTrackEnd()
    {
        var track = CurrentTrack;
        if (track == null || !LooksLikeEarlyEnd(track))
            return false;

        if (_earlyEndRetryCount == 0 && _currentIndex >= 0)
        {
            _earlyEndRetryCount++;
            Log.Warning(
                "Ignoring early end for {Track} at {Position}/{Duration}; retrying current track",
                track.Title,
                TimeSpan.FromMilliseconds(Math.Max(0, _player.Time)),
                track.Duration);
            PlayTrack(_currentIndex, isRetry: true);
        }
        else
        {
            Log.Warning(
                "Ignoring repeated early end for {Track} at {Position}/{Duration}; stopping instead of auto-switching",
                track.Title,
                TimeSpan.FromMilliseconds(Math.Max(0, _player.Time)),
                track.Duration);
            SetState(PlaybackState.Stopped);
        }

        return true;
    }

    private bool LooksLikeEarlyEnd(Track track)
    {
        var durationMs = track.Duration.TotalMilliseconds;
        if (durationMs < 90_000)
            return false;

        var positionMs = Math.Max(0, _player.Time);
        if (positionMs > 0 && durationMs - positionMs > 15_000)
            return true;

        var elapsedMs = Environment.TickCount64 - _trackStartedAtMs;
        return elapsedMs > 0 &&
               elapsedMs < 60_000 &&
               durationMs - elapsedMs > 30_000;
    }

    private async System.Threading.Tasks.Task RefreshTrackUrlAsync(Track track)
    {
        try
        {
            var newUrl = await _urlResolver!(track.SourceId!);
            if (!string.IsNullOrEmpty(newUrl) && newUrl != track.FilePath)
            {
                track.FilePath = newUrl;
                Log.Debug("Refreshed URL for track {Track}", track.Title);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh URL for {Track}", track.Title);
        }
    }

    private async System.Threading.Tasks.Task RefreshAndPlayTrackAsync(int index, Track track, int requestId)
    {
        try
        {
            var newUrl = await _urlResolver!(track.SourceId!);
            if (!string.IsNullOrEmpty(newUrl) && newUrl != track.FilePath)
            {
                track.FilePath = newUrl;
                Log.Debug("Refreshed URL before play for track {Track}", track.Title);
            }

            if (!string.IsNullOrEmpty(track.FilePath) &&
                requestId == Volatile.Read(ref _playRequestId) &&
                index >= 0 && index < _playlist.Count &&
                ReferenceEquals(_playlist[index], track))
            {
                PlayTrack(index, isRetry: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh URL before play for {Track}", track.Title);
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

    private void DoFadeStep()
    {
        var elapsed = (Environment.TickCount64 - _fadeStepStart) / 1000.0;
        var progress = Math.Clamp(elapsed / CrossfadeSeconds, 0.0, 1.0);

        if (_fadeDirection == 1)
        {
            _player.Volume = (int)(_userVolume * progress * 100);
            if (progress >= 1.0)
            {
                _isFading = false;
                _player.Volume = (int)(_userVolume * 100);
                _fadeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
        else
        {
            _player.Volume = (int)(_userVolume * (1.0 - progress) * 100);
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

    private void NotifyTrackChanged()
    {
        _trackChangedSubject.OnNext(CurrentTrack);
    }

    private void EmitSimulatedSpectrum()
    {
        if (_currentState != PlaybackState.Playing) return;

        var time = Environment.TickCount64 / 1000.0;
        var beat = 0.5 + 0.5 * Math.Sin(time * 3.2);
        var bassPulse = Math.Pow(Math.Max(0, Math.Sin(time * 5.8)), 2);
        var midPulse = 0.5 + 0.5 * Math.Sin(time * 2.1 + Math.Sin(time * 0.7));
        var treblePulse = _rng.NextDouble() * 0.35;
        var data = new float[32];
        for (int i = 0; i < data.Length; i++)
        {
            var band = i / (double)(data.Length - 1);
            var envelope = band < 0.25
                ? 0.55 * bassPulse
                : band < 0.7
                    ? 0.38 * midPulse
                    : 0.24 * treblePulse;
            var wave = 0.24 * Math.Sin(time * (7 + band * 12) + i * 0.55);
            var noise = (_rng.NextDouble() - 0.5) * 0.12;
            data[i] = (float)(0.12 + envelope + beat * 0.18 + wave + noise);
            data[i] = Math.Clamp(data[i], 0f, 1f);
        }
        _spectrumSubject.OnNext(data);
    }

    private void EmitPosition()
    {
        if (_currentState == PlaybackState.Playing || _currentState == PlaybackState.Paused)
        {
            var pos = _player.Time;
            var dur = _player.Length;

            if (pos != _lastPositionMs)
            {
                _lastPositionMs = pos;
                _positionChangedSubject.OnNext(TimeSpan.FromMilliseconds(pos));
            }

            // Let LibVLC EndReached decide when a track is really over.
            // Some online sources report unstable duration and can otherwise fade out far too early.
        }
    }

    public void Dispose()
    {
        _ttsDuckSub.Dispose();
        _ttsPauseSub.Dispose();
        _spectrumTimer?.Dispose();
        _fadeTimer?.Dispose();
        _positionTimer?.Dispose();
        _ttsOutput?.Stop();
        _ttsOutput?.Dispose();
        _ttsReader?.Dispose();
        _player?.Dispose();
        _libVLC?.Dispose();
        _spectrumSubject.Dispose();
        _trackChangedSubject.Dispose();
        _stateChangedSubject.Dispose();
        _positionChangedSubject.Dispose();
        _trackEndedSubject.Dispose();
        _ttsStateSubject.Dispose();
        _ttsErrorSubject.Dispose();
    }

    private readonly Subject<bool> _ttsStateSubject = new();
    private string? _currentTtsFile;

    public void PlayTtsAudio(byte[] audioData)
    {
        var tempPath = string.Empty;
        try
        {
            StopTtsInternal(notifyState: false);
            tempPath = WriteTtsTempFile(audioData);
            _currentTtsFile = tempPath;
            var sessionId = Interlocked.Increment(ref _ttsSessionId);

            InitTtsPlayback(tempPath, sessionId, tempPath);
            var signature = BitConverter.ToString(audioData.Take(Math.Min(8, audioData.Length)).ToArray());
            _ttsStateSubject.OnNext(true);
            _ttsOutput!.Play();
            Log.Information("TTS NAudio playback started: {Bytes} bytes, header={Header}, file={File}", audioData.Length, signature, tempPath);
        }
        catch (Exception ex)
        {
            StopTtsInternal(notifyState: false);
            CleanupTtsFile(tempPath);
            _ttsStateSubject.OnNext(false);
            _ttsErrorSubject.OnNext("语音播放设备不可用，请检查 Windows 默认输出设备或关闭语音播报。");
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
        _ttsReader = new MediaFoundationReader(filePath);
        _ttsOutput = new WaveOutEvent { DesiredLatency = 120 };
        _ttsOutput.Init(_ttsReader);
        TrySetTtsVolume(_ttsOutput, 1.0f);
        _ttsOutput.PlaybackStopped += (_, e) =>
        {
            if (IsTtsSessionCancelled(sessionId)) return;
            if (e.Exception != null)
                Log.Warning(e.Exception, "TTS NAudio playback failed");
            else
                Log.Information("TTS playback ended");

            _ttsStateSubject.OnNext(false);
            CleanupTtsFile(tempPath);
            if (_currentTtsFile == tempPath)
                _currentTtsFile = null;
        };
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
        StopTtsInternal(notifyState: true);
        Log.Information("TTS playback stopped");
    }

    private void StopTtsInternal(bool notifyState)
    {
        WaveOutEvent? output;
        MediaFoundationReader? reader;
        string? ttsFile;

        lock (_ttsStateGate)
        {
            if (_ttsOutput != null)
                _cancelledTtsSessions.Add(_ttsSessionId);

            output = _ttsOutput;
            reader = _ttsReader;
            ttsFile = _currentTtsFile;
            _ttsOutput = null;
            _ttsReader = null;
            _currentTtsFile = null;
            _ttsWasPlayingWhenMusicPaused = false;
        }

        try { output?.Stop(); } catch { } // Best-effort: may throw if device disconnected
        output?.Dispose();
        reader?.Dispose();
        if (ttsFile != null)
        {
            try { File.Delete(ttsFile); } catch { }
        }

        if (notifyState)
            _ttsStateSubject.OnNext(false);
    }

    private bool IsTtsSessionCancelled(int sessionId)
    {
        lock (_ttsStateGate)
        {
            return _cancelledTtsSessions.Remove(sessionId);
        }
    }
}
