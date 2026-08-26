using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IAudioService
{
    bool IsPlaying { get; }
    TimeSpan CurrentPosition { get; }
    TimeSpan Duration { get; }
    float Volume { get; set; }

    Track? CurrentTrack { get; }
    IReadOnlyList<Track> Playlist { get; }

    IObservable<float[]> SpectrumData { get; }
    IObservable<Track?> TrackChanged { get; }
    IObservable<PlaybackState> StateChanged { get; }
    IObservable<TimeSpan> PositionChanged { get; }
    IObservable<Track?> TrackEnded { get; }

    void LoadTracks(IEnumerable<Track> tracks);
    void LoadFiles(IEnumerable<string> filePaths);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void Next();
    void SetNextCallback(Func<Task<Track?>>? callback);
    void Previous();
    void Shuffle();
    void SetRepeatMode(string mode);
    void PlayAtIndex(int index);
    void PlayTrack(Track track);
    void StartPlaybackContext(IEnumerable<Track> tracks, int startIndex = 0, bool shuffle = false, string? contextName = null);
    void PlayNextInQueue(Track track);
    void AddToQueue(Track track);
    void ClearPlaybackContext();

    bool IsShuffled { get; }
    string RepeatMode { get; }
    IReadOnlyList<Track> PlaybackQueue { get; }
    string PlaybackContextName { get; }

    void AddTracks(IEnumerable<Track> tracks);
    void RemoveTrack(Track track);
    void ClearPlaylist();
    void PlayTtsAudio(byte[] audioData);
    void StopTts();
    IObservable<bool> TtsStateChanged { get; }
    IObservable<string> TtsError { get; }
    void SetUrlResolver(Func<string, Task<string?>> resolver);
    void SetSpeechMixMode(string mode);
}
