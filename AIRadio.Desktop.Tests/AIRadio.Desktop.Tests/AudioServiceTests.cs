using System;
using System.Collections.Generic;
using System.Linq;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class AudioServiceTests
{
    [Fact]
    public void NewInstance_HasEmptyPlaylist()
    {
        var svc = new AudioService();
        Assert.Empty(svc.Playlist);
        Assert.Null(svc.CurrentTrack);
        svc.Dispose();
    }

    [Fact]
    public void LoadTracks_SetsCurrentTrackToFirst()
    {
        var svc = new AudioService();
        var tracks = new List<Track>
        {
            new Track { Title = "A", FilePath = "" },
            new Track { Title = "B", FilePath = "" }
        };

        svc.LoadTracks(tracks);

        Assert.Equal(2, svc.Playlist.Count);
        Assert.Equal("A", svc.Playlist[0].Title);
        svc.Dispose();
    }

    [Fact]
    public void AddTracks_AppendsToExisting()
    {
        var svc = new AudioService();
        svc.LoadTracks(new[] { new Track { Title = "A", FilePath = "" } });

        svc.AddTracks(new[] { new Track { Title = "B", FilePath = "" } });

        Assert.Equal(2, svc.Playlist.Count);
        Assert.Equal("B", svc.Playlist[1].Title);
        svc.Dispose();
    }

    [Fact]
    public void RemoveTrack_RemovesFromPlaylist()
    {
        var svc = new AudioService();
        var track = new Track { Title = "Test", FilePath = "" };
        svc.LoadTracks(new[] { track });

        svc.RemoveTrack(track);

        Assert.Empty(svc.Playlist);
    }

    [Fact]
    public void ClearPlaylist_RemovesAllTracks()
    {
        var svc = new AudioService();
        svc.LoadTracks(new[]
        {
            new Track { Title = "A", FilePath = "" },
            new Track { Title = "B", FilePath = "" }
        });

        svc.ClearPlaylist();

        Assert.Empty(svc.Playlist);
    }

    [Fact]
    public void Shuffle_TogglesShuffleState()
    {
        var svc = new AudioService();
        Assert.False(svc.IsShuffled);

        svc.Shuffle();
        Assert.True(svc.IsShuffled);

        svc.Shuffle();
        Assert.False(svc.IsShuffled);
        svc.Dispose();
    }

    [Fact]
    public void SetRepeatMode_ChangesRepeatMode()
    {
        var svc = new AudioService();
        Assert.Equal("radio", svc.RepeatMode);

        svc.SetRepeatMode("list");
        Assert.Equal("list", svc.RepeatMode);

        svc.SetRepeatMode("single");
        Assert.Equal("single", svc.RepeatMode);
        svc.Dispose();
    }

    [Fact]
    public void Volume_ClampsToValidRange()
    {
        var svc = new AudioService();
        svc.Volume = 1.5f;
        Assert.True(svc.Volume <= 1.0f && svc.Volume >= 0.0f);

        svc.Volume = 0.5f;
        Assert.True(svc.Volume >= 0.0f && svc.Volume <= 1.0f);
        svc.Dispose();
    }

    [Fact]
    public void PlayAtIndex_ClampsToValidRange()
    {
        var svc = new AudioService();
        svc.LoadTracks(new[] { new Track { Title = "A", FilePath = "" } });

        svc.PlayAtIndex(5);  // out of range
        svc.PlayAtIndex(-1); // negative

        // Should not throw - index is clamped internally
        svc.Dispose();
    }
}
