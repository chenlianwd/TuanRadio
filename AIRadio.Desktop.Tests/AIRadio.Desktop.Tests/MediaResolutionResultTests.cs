using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;

namespace AIRadio.Desktop.Tests;

public sealed class MediaResolutionResultTests
{
    [Fact]
    public async Task DetailedResolution_PreservesPreviewClassification()
    {
        var broker = new MusicSourceBroker(new PreviewProvider());
        var outcome = await broker.ResolveTrackDetailedAsync(new OnlineTrack
        {
            Id = "preview:42", Title = "Song", Artist = "Artist"
        }, forceRefresh: true, CancellationToken.None);

        Assert.Null(outcome.Track);
        Assert.Equal(PlaybackFailureKind.PreviewOnly, outcome.Failure);
    }

    private sealed class PreviewProvider : IMusicProvider
    {
        public MusicProviderDescriptor Descriptor { get; } = new("preview", "Preview");

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
            => Task.FromResult(new List<OnlineTrack>());

        public Task<MediaResolutionResult> ResolveAsync(ProviderTrackRef track,
            IReadOnlyDictionary<string, string>? providerMetadata, CancellationToken cancellationToken)
            => Task.FromResult(MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.PreviewOnly));
    }
}
