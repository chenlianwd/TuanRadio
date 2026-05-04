using System;
using System.Collections.Generic;
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
    void Previous();
    void Shuffle();
    void SetRepeatMode(string mode);
    void PlayAtIndex(int index);

    bool IsShuffled { get; }
    string RepeatMode { get; }

    void AddTracks(IEnumerable<Track> tracks);
    void RemoveTrack(Track track);
    void ClearPlaylist();
    void PlayTtsAudio(byte[] audioData);
    IObservable<bool> TtsStateChanged { get; }
}
