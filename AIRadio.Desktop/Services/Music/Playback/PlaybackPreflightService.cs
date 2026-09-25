using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Avalonia.Threading;
using ReactiveUI;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>播放开始后并发预检后续最多三首；只解析地址，不启动媒体播放器。</summary>
public sealed class PlaybackPreflightService : IDisposable
{
    private readonly IAudioService _audio;
    private readonly IMusicSourceBroker _broker;
    private readonly IDisposable _stateSubscription;
    private readonly MusicAccountStore? _accounts;
    private readonly OpenSubsonicProvider? _openSubsonic;
    private readonly object _runGate = new();
    private CancellationTokenSource _run = new();
    private IReadOnlyList<Track> _checkedTracks = Array.Empty<Track>();
    private long _generation;
    private bool _disposed;

    public PlaybackPreflightService(IAudioService audio, IMusicSourceBroker broker,
        MusicAccountStore? accounts = null, OpenSubsonicProvider? openSubsonic = null)
    {
        _audio = audio;
        _broker = broker;
        _accounts = accounts;
        _openSubsonic = openSubsonic;
        _stateSubscription = audio.StateChanged.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state =>
            {
                if (state == PlaybackState.Playing)
                    _ = RunAsync();
                else
                    Cancel();
            });
        _broker.ProviderConfigurationChanged += OnAccountChanged;
        if (_accounts != null)
        {
            _accounts.NeteaseCookieChanged += OnAccountChanged;
            _accounts.KugouCredentialChanged += OnAccountChanged;
        }
        if (_openSubsonic != null)
            _openSubsonic.ConfigurationChanged += OnAccountChanged;
    }

    private void OnAccountChanged(object? sender, EventArgs args)
    {
        // 凭据刷新可能在后台线程完成；曲目状态会触发 Avalonia 绑定通知。
        if (Dispatcher.UIThread.CheckAccess())
            Cancel();
        else
            Dispatcher.UIThread.Post(Cancel);
    }

    internal static IReadOnlyList<Track> SelectUpcoming(IAudioService audio)
    {
        var current = audio.CurrentTrack;
        if (current == null) return Array.Empty<Track>();
        var queue = audio.PlaybackQueue;
        var index = FindCurrent(queue, current);
        if (index < 0)
        {
            queue = audio.Playlist;
            index = FindCurrent(queue, current);
        }
        return index < 0 ? Array.Empty<Track>() : queue.Skip(index + 1).Take(3).ToArray();
    }

    private static int FindCurrent(IReadOnlyList<Track> tracks, Track current)
    {
        for (var index = 0; index < tracks.Count; index++)
            if (ReferenceEquals(tracks[index], current) ||
                string.Equals(tracks[index].Id, current.Id, StringComparison.Ordinal))
                return index;
        return -1;
    }

    internal async Task RunAsync()
    {
        if (_disposed) return;
        long generation;
        CancellationToken token;
        lock (_runGate)
        {
            CancelCore();
            generation = _generation;
            token = _run.Token;
        }
        IReadOnlyList<Track> upcoming;
        lock (_runGate)
        {
            if (_disposed || generation != _generation) return;
            upcoming = SelectUpcoming(_audio);
            _checkedTracks = upcoming;
            foreach (var track in upcoming) track.Playability = TrackPlayability.Checking;
        }
        try
        {
            await Task.WhenAll(upcoming.Select(track => CheckAsync(track, generation, token)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 当前播放请求已更换或应用正在退出。
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Playback preflight failed");
        }
    }

    private async Task CheckAsync(Track track, long generation, CancellationToken cancellationToken)
    {
        PlaybackFailureKind failure;
        try
        {
            if (string.IsNullOrWhiteSpace(track.SourceId))
                failure = File.Exists(track.FilePath) ? PlaybackFailureKind.None : PlaybackFailureKind.NotFound;
            else
            {
                var carrier = new OnlineTrack
                {
                    Id = track.SourceId,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    DurationMs = (long)track.Duration.TotalMilliseconds,
                    ProviderMetadata = new Dictionary<string, string>(track.ProviderMetadata,
                        StringComparer.OrdinalIgnoreCase)
                };
                var result = await _broker.ResolveTrackDetailedAsync(carrier, forceRefresh: false, cancellationToken);
                failure = result.Failure;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Playback preflight failed for {Id}", track.SourceId ?? track.Id);
            failure = PlaybackFailureKind.SourceUnavailable;
        }
        if (!_disposed && !cancellationToken.IsCancellationRequested &&
            generation == Interlocked.Read(ref _generation))
            track.SetPlaybackFailure(failure);
    }

    private void Cancel()
    {
        lock (_runGate) CancelCore();
    }

    private void CancelCore()
    {
        _generation++;
        foreach (var track in _checkedTracks)
            track.Playability = TrackPlayability.Unchecked;
        _checkedTracks = Array.Empty<Track>();
        var previous = Interlocked.Exchange(ref _run, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stateSubscription.Dispose();
        _broker.ProviderConfigurationChanged -= OnAccountChanged;
        if (_accounts != null)
        {
            _accounts.NeteaseCookieChanged -= OnAccountChanged;
            _accounts.KugouCredentialChanged -= OnAccountChanged;
        }
        if (_openSubsonic != null)
            _openSubsonic.ConfigurationChanged -= OnAccountChanged;
        Cancel();
        _run.Dispose();
    }
}
