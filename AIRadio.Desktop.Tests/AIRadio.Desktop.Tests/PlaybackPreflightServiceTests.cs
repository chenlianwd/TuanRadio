using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using Moq;

namespace AIRadio.Desktop.Tests;

public sealed class PlaybackPreflightServiceTests
{
    [Fact]
    public async Task Preflight_OnlyChecksNextThreeTracks_AndPublishesFailureCategory()
    {
        var tracks = Enumerable.Range(0, 5).Select(index => new Track
        {
            Id = index.ToString(), SourceId = "Fake:" + index, Title = "Song " + index
        }).ToArray();
        var audio = new Mock<IAudioService>();
        audio.SetupGet(service => service.StateChanged).Returns(Observable.Never<PlaybackState>());
        audio.SetupGet(service => service.CurrentTrack).Returns(tracks[0]);
        audio.SetupGet(service => service.PlaybackQueue).Returns(tracks);
        var broker = new Mock<IMusicSourceBroker>();
        var checkedIds = new List<string>();
        broker.Setup(service => service.ResolveTrackDetailedAsync(
                It.IsAny<OnlineTrack>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OnlineTrack track, bool _, CancellationToken _) =>
            {
                checkedIds.Add(track.Id);
                return new PlaybackResolutionOutcome(null,
                    track.Id.EndsWith(2.ToString(), StringComparison.Ordinal)
                        ? PlaybackFailureKind.AuthRequired : PlaybackFailureKind.NotFound);
            });
        using var preflight = new PlaybackPreflightService(audio.Object, broker.Object);

        await preflight.RunAsync();

        Assert.Equal(new[] { "Fake:1", "Fake:2", "Fake:3" }, checkedIds);
        Assert.Equal(TrackPlayability.Unchecked, tracks[4].Playability);
        Assert.Equal(TrackPlayability.Limited, tracks[2].Playability);
        Assert.Equal(PlaybackFailureKind.AuthRequired, tracks[2].PlaybackFailure);
    }

    [Fact]
    public async Task StalePreflight_CannotOverwriteNewerResult()
    {
        var current = new Track { Id = "1", SourceId = "Fake:1" };
        var next = new Track { Id = "2", SourceId = "Fake:2" };
        var audio = new Mock<IAudioService>();
        audio.SetupGet(service => service.StateChanged).Returns(Observable.Never<PlaybackState>());
        audio.SetupGet(service => service.CurrentTrack).Returns(current);
        audio.SetupGet(service => service.PlaybackQueue).Returns([current, next]);
        var oldResult = new TaskCompletionSource<PlaybackResolutionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var broker = new Mock<IMusicSourceBroker>();
        broker.Setup(service => service.ResolveTrackDetailedAsync(
                It.IsAny<OnlineTrack>(), false, It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1
                ? oldResult.Task
                : Task.FromResult(new PlaybackResolutionOutcome(null, PlaybackFailureKind.None)));
        using var preflight = new PlaybackPreflightService(audio.Object, broker.Object);

        var stale = preflight.RunAsync();
        await preflight.RunAsync();
        oldResult.SetResult(new PlaybackResolutionOutcome(null, PlaybackFailureKind.AuthRequired));
        await stale;

        Assert.Equal(TrackPlayability.Playable, next.Playability);
        Assert.Equal(PlaybackFailureKind.None, next.PlaybackFailure);
    }

    [Fact]
    public async Task CancelledPreflight_ClearsCheckingState()
    {
        var current = new Track { Id = "1", SourceId = "Fake:1" };
        var next = new Track { Id = "2", SourceId = "Fake:2" };
        var audio = new Mock<IAudioService>();
        audio.SetupGet(service => service.StateChanged).Returns(Observable.Never<PlaybackState>());
        audio.SetupGet(service => service.CurrentTrack).Returns(current);
        audio.SetupGet(service => service.PlaybackQueue).Returns([current, next]);
        var pending = new TaskCompletionSource<PlaybackResolutionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new Mock<IMusicSourceBroker>();
        broker.Setup(service => service.ResolveTrackDetailedAsync(
                It.IsAny<OnlineTrack>(), false, It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var preflight = new PlaybackPreflightService(audio.Object, broker.Object);

        var run = preflight.RunAsync();
        Assert.Equal(TrackPlayability.Checking, next.Playability);
        preflight.Dispose();
        Assert.Equal(TrackPlayability.Unchecked, next.Playability);
        pending.SetResult(new PlaybackResolutionOutcome(null, PlaybackFailureKind.None));
        await run;
        Assert.Equal(TrackPlayability.Unchecked, next.Playability);
    }
}
