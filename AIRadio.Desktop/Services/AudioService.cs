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
    private readonly List<Track> _playlist = new();
    private int _currentIndex = -1;
    private bool _shuffle;
    private string _repeatMode = "none";
    private string _speechMixMode = "duck";
    private bool _resumeAfterTts;
    private readonly System.Threading.Timer _positionTimer;
    private readonly System.Threading.Timer _spectrumTimer;
    private long _lastPositionMs;
    private readonly Random _rng = new();
    private PlaybackState _currentState = PlaybackState.Stopped;

    // Crossfade
    private float _userVolume = 0.8f;
    private const double CrossfadeSeconds = 2.0;
    private bool _isFading;
    private readonly System.Threading.Timer _fadeTimer;

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
            // Retry with fresh URL before advancing
            if (CurrentTrack != null && _currentIndex >= 0)
                PlayTrack(_currentIndex, isRetry: true);
            else if (_playlist.Count > 1)
                Next();
        };
        _player.EndReached += (_, _) =>
        {
            SetState(PlaybackState.Ended);
            OnTrackEndReached();
        };

        // No audio callbacks — use simulated spectrum (VLC handles output normally)
        _spectrumTimer = new System.Threading.Timer(_ => EmitSimulatedSpectrum(), null, 100, 33);

        // Duck main player volume when TTS is speaking
        _ttsStateSubject.Subscribe(ttsPlaying =>
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
                    _player.Volume = (int)(_userVolume * 20);
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

        _trackEndedSubject.OnNext(CurrentTrack);

        if (_repeatMode == "single" && CurrentTrack != null)
        {
            PlayTrack(_currentIndex);
        }
        else if (_repeatMode == "list")
        {
            Next();
        }
        else
        {
            Stop();
        }
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

    public void Next()
    {
        if (_playlist.Count == 0) return;

        if (_shuffle)
        {
            _currentIndex = _rng.Next(_playlist.Count);
        }
        else
        {
            _currentIndex = (_currentIndex + 1) % _playlist.Count;
        }

        PlayTrack(_currentIndex);
    }

    public void Previous()
    {
        if (_playlist.Count == 0) return;

        if (_player.Time > 3000)
        {
            Seek(TimeSpan.Zero);
            return;
        }

        _currentIndex = (_currentIndex - 1 + _playlist.Count) % _playlist.Count;
        PlayTrack(_currentIndex);
    }

    public void Shuffle() => _shuffle = !_shuffle;

    public void SetRepeatMode(string mode) => _repeatMode = mode;

    public void PlayAtIndex(int index)
    {
        if (index < 0 || index >= _playlist.Count) return;
        _currentIndex = index;
        PlayTrack(_currentIndex);
    }

    private void PlayTrack(int index, bool isRetry = false)
    {
        if (index < 0 || index >= _playlist.Count) return;

        var track = _playlist[index];
        try
        {
            // Stop current playback - do NOT dispose old media here,
            // LibVLC may still be using it internally during cleanup
            _player.Stop();

            string filePath = track.FilePath;

            // For URL tracks with a source ID, refresh URL to avoid 403/404 from expired links
            if (!string.IsNullOrEmpty(track.SourceId) && _urlResolver != null)
            {
                if (filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (isRetry)
                    {
                        // Sync refresh on retry to get fresh URL
                        var newUrl = RefreshTrackUrlSync(track);
                        if (!string.IsNullOrEmpty(newUrl))
                            filePath = newUrl;
                    }
                    else
                    {
                        // Fire-and-forget refresh for next play
                        _ = RefreshTrackUrlAsync(track);
                    }
                }
            }

            var isUrl = filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
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

    private async System.Threading.Tasks.Task RefreshTrackUrlAsync(Track track)
    {
        try
        {
            var newUrl = await _urlResolver!(track.SourceId);
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

    private string? RefreshTrackUrlSync(Track track)
    {
        try
        {
            var task = _urlResolver!(track.SourceId);
            var newUrl = task.GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(newUrl) && newUrl != track.FilePath)
            {
                track.FilePath = newUrl;
                Log.Debug("Sync refreshed URL for track {Track}", track.Title);
                return newUrl;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to sync refresh URL for {Track}", track.Title);
        }
        return null;
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
                else
                    Stop();
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
    }

    private readonly Subject<bool> _ttsStateSubject = new();
    private string? _currentTtsFile;

    public void PlayTtsAudio(byte[] audioData)
    {
        try
        {
            _ttsOutput?.Stop();
            _ttsOutput?.Dispose();
            _ttsOutput = null;
            _ttsReader?.Dispose();
            _ttsReader = null;

            if (_currentTtsFile != null)
            {
                try { File.Delete(_currentTtsFile); } catch { }
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(tempPath, audioData);
            _currentTtsFile = tempPath;

            _ttsReader = new MediaFoundationReader(tempPath);
            _ttsOutput = new WaveOutEvent
            {
                DesiredLatency = 120,
                Volume = 1.0f
            };
            _ttsOutput.Init(_ttsReader);
            _ttsOutput.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null)
                    Log.Warning(e.Exception, "TTS NAudio playback failed");
                else
                    Log.Information("TTS playback ended");

                _ttsStateSubject.OnNext(false);
                try { File.Delete(tempPath); } catch { }
                if (_currentTtsFile == tempPath)
                    _currentTtsFile = null;
            };

            var signature = BitConverter.ToString(audioData.Take(Math.Min(8, audioData.Length)).ToArray());
            _ttsStateSubject.OnNext(true);
            _ttsOutput.Play();
            Log.Information("TTS NAudio playback started: {Bytes} bytes, header={Header}, file={File}", audioData.Length, signature, tempPath);
        }
        catch (Exception ex)
        {
            _ttsStateSubject.OnNext(false);
            Log.Warning(ex, "Failed to play TTS audio");
        }
    }
}
