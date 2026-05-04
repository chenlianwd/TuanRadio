using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;
using PlaylistViewModel = AIRadio.Desktop.ViewModels.PlaylistViewModel;

namespace AIRadio.Desktop.Tests;

public class PlaylistViewModelTests
{
    private static (PlaylistViewModel vm, Mock<IAudioService> audioMock, Mock<IMusicSearchService> searchMock)
        CreateVm()
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TrackEnded).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audioMock.Setup(x => x.StateChanged).Returns(new System.Reactive.Subjects.Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());

        var searchMock = new Mock<IMusicSearchService>();
        searchMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var vm = new PlaylistViewModel(audioMock.Object, searchMock.Object);
        return (vm, audioMock, searchMock);
    }

    [Fact]
    public void InitialTabIndex_IsZero()
    {
        var (vm, _, _) = CreateVm();
        Assert.Equal(0, vm.TabIndex);
    }

    [Fact]
    public void ShowPlaylistCommand_SetsTabIndexToZero()
    {
        var (vm, _, _) = CreateVm();
        vm.TabIndex = 2;
        vm.ShowPlaylistCommand.Execute().Subscribe();
        Assert.Equal(0, vm.TabIndex);
    }

    [Fact]
    public void ShowFavoritesCommand_SetsTabIndexToOne()
    {
        var (vm, _, _) = CreateVm();
        vm.ShowFavoritesCommand.Execute().Subscribe();
        Assert.Equal(1, vm.TabIndex);
    }

    [Fact]
    public void ShowSearchCommand_SetsTabIndexToTwo()
    {
        var (vm, _, _) = CreateVm();
        vm.ShowSearchCommand.Execute().Subscribe();
        Assert.Equal(2, vm.TabIndex);
    }

    [Fact]
    public async Task ToggleFavorite_AddsToFavoritesWhenTrue()
    {
        var (vm, _, _) = CreateVm();
        var track = new Track { Title = "Test", Artist = "Me", IsFavorite = false };

        vm.Tracks.Add(track);
        vm.ToggleFavoriteCommand.Execute(track).Subscribe();

        Assert.True(track.IsFavorite);
        Assert.Contains(track, vm.Favorites);
    }

    [Fact]
    public async Task ToggleFavorite_RemovesFromFavoritesWhenFalse()
    {
        var (vm, _, _) = CreateVm();
        var track = new Track { Title = "Test", Artist = "Me", IsFavorite = true };
        vm.Favorites.Add(track);

        vm.ToggleFavoriteCommand.Execute(track).Subscribe();

        Assert.False(track.IsFavorite);
        Assert.DoesNotContain(track, vm.Favorites);
    }

    [Fact]
    public async Task RemoveTrackCommand_RemovesTrackAndCallsAudioService()
    {
        var (vm, audioMock, _) = CreateVm();
        var track = new Track { Title = "Test", FilePath = "" };
        vm.Tracks.Add(track);

        vm.RemoveTrackCommand.Execute(track).Subscribe();

        audioMock.Verify(x => x.RemoveTrack(track), Times.Once);
        Assert.DoesNotContain(track, vm.Tracks);
    }

    [Fact]
    public void PlayFavoriteCommand_PlaysTrackAtIndex()
    {
        var (vm, audioMock, _) = CreateVm();
        var track1 = new Track { Title = "A", FilePath = "" };
        var track2 = new Track { Title = "B", FilePath = "" };
        vm.Tracks.Add(track1);
        vm.Tracks.Add(track2);

        vm.PlayFavoriteCommand.Execute(track1).Subscribe();

        audioMock.Verify(x => x.PlayAtIndex(0), Times.Once);
    }

    [Fact]
    public void PlayFavoriteCommand_DoesNothingWhenTrackNotInPlaylist()
    {
        var (vm, audioMock, _) = CreateVm();
        var track = new Track { Title = "NotInList", FilePath = "" };
        vm.Tracks.Add(new Track { Title = "A", FilePath = "" });

        vm.PlayFavoriteCommand.Execute(track).Subscribe();

        audioMock.Verify(x => x.PlayAtIndex(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task AddOnlineCommand_AddsTrackToPlaylist()
    {
        var (vm, audioMock, searchMock) = CreateVm();
        searchMock.Setup(x => x.GetPlayUrlAsync("track1"))
            .ReturnsAsync("http://example.com/track1.mp3");

        var onlineTrack = new OnlineTrack { Id = "track1", Title = "Online Song", Artist = "Artist" };

        vm.AddOnlineCommand.Execute(onlineTrack).Subscribe();

        Assert.Single(vm.Tracks);
        Assert.Equal("Online Song", vm.Tracks[0].Title);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Once);
    }

    [Fact]
    public async Task AddOnlineCommand_DoesNotDuplicateExistingUrl()
    {
        var (vm, audioMock, searchMock) = CreateVm();
        searchMock.Setup(x => x.GetPlayUrlAsync("track1"))
            .ReturnsAsync("http://existing.com/track1.mp3");

        var existing = new Track { Title = "Existing", FilePath = "http://existing.com/track1.mp3" };
        vm.Tracks.Add(existing);

        var onlineTrack = new OnlineTrack { Id = "track1", Title = "Online Song", Artist = "Artist" };
        vm.AddOnlineCommand.Execute(onlineTrack).Subscribe();

        Assert.Single(vm.Tracks);
    }

    [Fact]
    public async Task SearchAsync_ClearsAndFillsSearchResults()
    {
        var (vm, _, searchMock) = CreateVm();
        var results = new List<OnlineTrack>
        {
            new OnlineTrack { Id = "1", Title = "Song A" },
            new OnlineTrack { Id = "2", Title = "Song B" }
        };
        searchMock.Setup(x => x.SearchAsync("test", 20))
            .ReturnsAsync(results);

        vm.SearchText = "test";
        vm.SearchCommand.Execute().Subscribe();

        // Wait a moment for async to complete
        System.Threading.Thread.Sleep(100);

        Assert.Equal(2, vm.SearchResults.Count);
        Assert.Equal(2, vm.TabIndex); // auto-switches to search tab
    }

    [Fact]
    public void SearchAsync_DoesNothingWhenTextEmpty()
    {
        var (vm, _, searchMock) = CreateVm();
        vm.SearchCommand.Execute().Subscribe();
        System.Threading.Thread.Sleep(50);
        searchMock.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void AddFiles_AddsTracksFromPaths()
    {
        var (vm, audioMock, _) = CreateVm();
        var path = "D:\\test.mp3";

        vm.AddFiles(new[] { path });

        Assert.NotEmpty(vm.Tracks);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Once);
    }
}