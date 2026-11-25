using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IAudioService
{
    bool IsPlaying { get; }
    TimeSpan CurrentPosition { get; }
    TimeSpan Duration { get; }
    float Volume { get; set; }
    /// <summary>当前实际生效的播放倍速（仅播客单集允许非 1.0x，普通歌曲与直播电台恒为 1.0x）。</summary>
    float PlaybackRate => 1.0f;
    /// <summary>播客专属记忆倍速偏好。</summary>
    float PodcastPlaybackRate => 1.0f;

    Track? CurrentTrack { get; }
    IReadOnlyList<Track> Playlist { get; }

    IObservable<float[]> SpectrumData { get; }
    IObservable<Track?> TrackChanged { get; }
    IObservable<PlaybackState> StateChanged { get; }
    IObservable<TimeSpan> PositionChanged { get; }
    IObservable<Track?> TrackEnded { get; }
    /// <summary>任意曲目（含队列上下文内非末集）真正自然播放结束时发射，供播客完播标记等消费。</summary>
    IObservable<Track?> NaturalTrackCompleted => System.Reactive.Linq.Observable.Empty<Track?>();
    IObservable<PlaybackRecoveryNotice> PlaybackRecoveryNotices { get; }
    IObservable<TrackMediaInfo> TrackMediaInfo { get; }
    IObservable<string?> LiveMetadataChanged => System.Reactive.Linq.Observable.Empty<string?>();
    IObservable<float> PlaybackRateChanged => System.Reactive.Linq.Observable.Empty<float>();

    void LoadTracks(IEnumerable<Track> tracks);
    void LoadFiles(IEnumerable<string> filePaths);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    /// <summary>相对当前播放位置快进或快退（直播电台自动忽略，结果自动夹逼在安全范围内）。</summary>
    void SeekRelative(TimeSpan offset) { }
    /// <summary>设置播客倍速偏好；当前播放播客单集时立即生效，切回普通歌曲或直播电台自动恢复 1.0x。</summary>
    void SetPlaybackRate(float rate) { }
    void Next();
    void SetNextCallback(Func<CancellationToken, Task<Track?>>? callback);
    /// <summary>自然播完进入自动续播前调用；返回 true 时拦截本次自动续播（如睡眠定时播完当前曲停止）。</summary>
    void SetBeforeAutoAdvanceCallback(Func<Track?, bool>? callback) { }
    void Previous();
    void Shuffle();
    void SetShuffle(bool shuffled)
    {
        if (IsShuffled != shuffled)
            Shuffle();
    }
    void SetRepeatMode(string mode);
    void PlayAtIndex(int index);
    void PlayTrack(Track track);
    void StartPlaybackContext(IEnumerable<Track> tracks, int startIndex = 0, bool shuffle = false, string? contextName = null);
    /// <summary>插播单曲（单元素上下文）：瞬态播放不进主歌单，曲终按模式回落（电台回 DJ/列表循环回主歌单/顺序即止）。</summary>
    void PlayOneShot(Track track, string? contextName = null);
    void PlayNextInQueue(Track track);
    void AddToQueue(Track track);
    void ClearPlaybackContext();

    bool IsShuffled { get; }
    string RepeatMode { get; }
    IReadOnlyList<Track> PlaybackQueue { get; }
    string PlaybackContextName { get; }

    void AddTracks(IEnumerable<Track> tracks);
    void RemoveTrack(Track track);
    /// <summary>
    /// 重排主列表中两首非瞬态曲的顺序。索引语义为 canonical 投影（主列表去掉瞬态上下文曲），
    /// 与 PlaylistViewModel.Tracks 顺序一致。当前曲被移动时仅修正内部索引，播放不中断。
    /// </summary>
    void MoveTrack(int oldIndex, int newIndex);
    void ClearPlaylist();
    void PlayTtsAudio(byte[] audioData);
    void StopTts();
    IObservable<bool> TtsStateChanged { get; }
    /// <summary>TTS 播放链实时峰值（0..1，播放线程触发）。消费方：说话徽章辉光。</summary>
    event Action<float>? TtsLevelChanged;
    IObservable<string> TtsError { get; }
    bool IsRadioSoundFxEnabled { get; set; }
    void PlayRadioSoundFx(RadioFxKind kind);
    void SetUrlResolver(Func<string, Task<string?>> resolver);
    void SetSpeechMixMode(string mode);
    /// <summary>按住说话录音期间临时压低背景音乐音量（避免外放回声污染识别），结束时恢复。</summary>
    void SetVoiceCaptureActive(bool active) { }
}
