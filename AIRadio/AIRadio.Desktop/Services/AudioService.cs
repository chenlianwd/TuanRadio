using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using LibVLCSharp.Shared;
using Serilog;

namespace AIRadio.Desktop.Services;

public class AudioService : IAudioService, IDisposable
{
    private readonly LibVLC _libVLC;
    private readonly MediaPlayer _player;
    private readonly Subject<float[]> _spectrumSubject = new();
    private readonly Subject<Track?> _trackChangedSubject = new();
    private readonly Subject<PlaybackState> _stateChangedSubject = new();
    private readonly Subject<TimeSpan> _positionChangedSubject = new();
    private readonly Subject<Track?> _trackEndedSubject = new();
    private readonly List<Track> _playlist = new();
    private int _currentIndex = -1;
    private bool _shuffle;
    private string _repeatMode = "list";
    private readonly System.Threading.Timer _positionTimer;
    private readonly System.Threading.Timer _spectrumTimer;
    private long _lastPositionMs;
    private readonly Random _rng = new();
    private PlaybackState _currentState = PlaybackState.Stopped;

    public bool IsPlaying => _player.IsPlaying;
    public TimeSpan CurrentPosition => TimeSpan.FromMilliseconds(_player.Time);
    public TimeSpan Duration => TimeSpan.FromMilliseconds(_player.Length);
    public float Volume
    {
        get => _player.Volume / 100f;
        set => _player.Volume = (int)(Math.Clamp(value, 0f, 1f) * 100);
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

    public AudioService()
    {
        Core.Initialize();
        _libVLC = new LibVLC();
        _player = new MediaPlayer(_libVLC);

        _player.Playing += (_, _) => SetState(PlaybackState.Playing);
        _player.Paused += (_, _) => SetState(PlaybackState.Paused);
        _player.Stopped += (_, _) => SetState(PlaybackState.Stopped);
        _player.EndReached += (_, _) =>
        {
            SetState(PlaybackState.Ended);
            OnTrackEndReached();
        };

        _positionTimer = new System.Threading.Timer(_ => EmitPosition(), null, 500, 500);
        _spectrumTimer = new System.Threading.Timer(_ => EmitSpectrum(), null, 100, 33); // ~30fps
    }

    private void SetState(PlaybackState state)
    {
        _currentState = state;
        _stateChangedSubject.OnNext(state);
    }

    private void OnTrackEndReached()
    {
        _trackEndedSubject.OnNext(CurrentTrack);

        if (_repeatMode == "single" && CurrentTrack != null)
        {
            PlayTrack(_currentIndex);
        }
        else
        {
            Next();
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

    private void PlayTrack(int index)
    {
        if (index < 0 || index >= _playlist.Count) return;

        var track = _playlist[index];
        try
        {
            var media = new Media(_libVLC, track.FilePath, FromType.FromPath);
            _player.Media = media;
            _player.Play();
            NotifyTrackChanged();
            Log.Information("Playing: {Track}", track);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to play track: {Track}", track);
            Next();
        }
    }

    private void NotifyTrackChanged()
    {
        _trackChangedSubject.OnNext(CurrentTrack);
    }

    private void EmitPosition()
    {
        if (_currentState == PlaybackState.Playing || _currentState == PlaybackState.Paused)
        {
            var pos = _player.Time;
            if (pos != _lastPositionMs)
            {
                _lastPositionMs = pos;
                _positionChangedSubject.OnNext(TimeSpan.FromMilliseconds(pos));
            }
        }
    }

    private void EmitSpectrum()
    {
        if (_currentState != PlaybackState.Playing)
        {
            return;
        }

        // Simulated spectrum from playback timing — real FFT requires VLC audio callbacks
        var bands = 16;
        var data = new float[bands];
        var time = DateTime.Now.Ticks / 10000.0;
        for (int i = 0; i < bands; i++)
        {
            var freq = 0.5 + i * 0.3;
            data[i] = (float)(0.3 + 0.3 * Math.Sin(time * 0.001 * freq + i)
                              + 0.2 * Math.Sin(time * 0.0023 * freq)
                              + 0.1 * Math.Sin(time * 0.005 * (i + 1)));
            data[i] = Math.Clamp(data[i], 0f, 1f);
        }
        _spectrumSubject.OnNext(data);
    }

    public void Dispose()
    {
        _spectrumTimer?.Dispose();
        _positionTimer?.Dispose();
        _player?.Dispose();
        _libVLC?.Dispose();
        _spectrumSubject.Dispose();
        _trackChangedSubject.Dispose();
        _stateChangedSubject.Dispose();
        _positionChangedSubject.Dispose();
        _trackEndedSubject.Dispose();
    }
}
