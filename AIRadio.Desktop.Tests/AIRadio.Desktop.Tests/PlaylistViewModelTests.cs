using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;
using PlaylistViewModel = AIRadio.Desktop.ViewModels.PlaylistViewModel;

namespace AIRadio.Desktop.Tests;

public class PlaylistViewModelTests
{
    private static (PlaylistViewModel vm, Mock<IAudioService> audioMock, Mock<IMusicSearchService> searchMock)
        CreateVm(string? playlistFile = null, Func<string, string, Task>? writeAllTextAsync = null)
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TrackEnded).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audioMock.Setup(x => x.StateChanged).Returns(new System.Reactive.Subjects.Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());

        var searchMock = new Mock<IMusicSearchService>();
        searchMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var vm = new PlaylistViewModel(
            audioMock.Object,
            searchMock.Object,
            playlistFile ?? CreateTempPlaylistFile(),
            writeAllTextAsync);
        return (vm, audioMock, searchMock);
    }

    // Temp directories are created per-test and not cleaned up (M45).
    // OS temp cleanup handles them; for CI, consider adding IDisposable with cleanup.
    private static string CreateTempPlaylistFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "playlist.json");
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
    public Task ToggleFavorite_AddsToFavoritesWhenTrue()
    {
        var (vm, _, _) = CreateVm();
        var track = new Track { Id = "toggle-test-1", Title = "Test", Artist = "Me" };

        vm.Tracks.Add(track);
        vm.ToggleFavoriteCommand.Execute(track).Subscribe();

        Assert.True(track.IsFavorite);
        Assert.Contains(track, vm.Favorites);
        return Task.CompletedTask;
    }

    [Fact]
    public Task ToggleFavorite_RemovesFromFavoritesWhenFalse()
    {
        var (vm, _, _) = CreateVm();
        var track = new Track { Id = "toggle-test-2", Title = "Test", Artist = "Me" };
        vm.Tracks.Add(track);
        vm.ToggleFavoriteCommand.Execute(track).Subscribe(); // add first

        vm.ToggleFavoriteCommand.Execute(track).Subscribe(); // then remove

        Assert.False(track.IsFavorite);
        Assert.DoesNotContain(track, vm.Favorites);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ToggleFavorite_PersistsFavoriteIds()
    {
        var playlistFile = CreateTempPlaylistFile();
        var (vm, _, _) = CreateVm(playlistFile);
        var track = new Track
        {
            Id = "netease:ugly",
            Title = "丑八怪",
            Artist = "薛之谦",
            FilePath = "http://example.com/ugly.mp3",
            SourceId = "netease:ugly"
        };

        vm.Tracks.Add(track);
        vm.ToggleFavoriteCommand.Execute(track).Subscribe();
        await Task.Delay(150);

        var saved = await File.ReadAllTextAsync(playlistFile);
        Assert.Contains("\"FavoriteIds\"", saved);
        Assert.Contains("netease:ugly", saved);
        Assert.Contains("http://example.com/ugly.mp3", saved);
    }

    [Fact]
    public async Task LoadAsync_PreservesOnlineFavoriteWhenUrlRefreshFails()
    {
        var playlistFile = CreateTempPlaylistFile();
        await File.WriteAllTextAsync(playlistFile,
            """
            {
              "Tracks": [
                {
                  "Id": "netease:ugly",
                  "Title": "丑八怪",
                  "Artist": "薛之谦",
                  "Album": "",
                  "DurationMs": 240000,
                  "FilePath": "",
                  "SourceId": "netease:ugly",
                  "IsOnline": true,
                  "IsFavorite": true
                }
              ],
              "FavoriteIds": [ "netease:ugly" ]
            }
            """);

        var (vm, audioMock, searchMock) = CreateVm(playlistFile);
        searchMock.Setup(x => x.GetPlayUrlAsync("netease:ugly"))
            .ReturnsAsync((string?)null);

        await vm.LoadAsync();
        await Task.Delay(150);

        Assert.Single(vm.Tracks);
        Assert.True(vm.Tracks[0].IsFavorite);
        Assert.Single(vm.Favorites);
        audioMock.Verify(x => x.LoadTracks(It.Is<IEnumerable<Track>>(tracks =>
            tracks.Any(t => t.Id == "netease:ugly" && t.IsFavorite))), Times.Once);

        var saved = await File.ReadAllTextAsync(playlistFile);
        Assert.Contains("netease:ugly", saved);
        Assert.Contains("\"FavoriteIds\"", saved);
    }

    [Fact]
    public Task RemoveTrackCommand_RemovesTrackAndCallsAudioService()
    {
        var (vm, audioMock, _) = CreateVm();
        var track = new Track { Title = "Test", FilePath = "" };
        vm.Tracks.Add(track);

        vm.RemoveTrackCommand.Execute(track).Subscribe();

        audioMock.Verify(x => x.RemoveTrack(track), Times.Once);
        Assert.DoesNotContain(track, vm.Tracks);
        return Task.CompletedTask;
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
    public Task AddOnlineCommand_AddsTrackToPlaylist()
    {
        var (vm, audioMock, searchMock) = CreateVm();
        searchMock.Setup(x => x.GetPlayUrlAsync("track1"))
            .ReturnsAsync("http://example.com/track1.mp3");

        var onlineTrack = new OnlineTrack { Id = "track1", Title = "Online Song", Artist = "Artist" };

        vm.AddOnlineCommand.Execute(onlineTrack).Subscribe();

        Assert.Single(vm.Tracks);
        Assert.Equal("Online Song", vm.Tracks[0].Title);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Once);
        return Task.CompletedTask;
    }

    [Fact]
    public Task AddOnlineCommand_DoesNotDuplicateExistingUrl()
    {
        var (vm, audioMock, searchMock) = CreateVm();
        searchMock.Setup(x => x.GetPlayUrlAsync("track1"))
            .ReturnsAsync("http://existing.com/track1.mp3");

        var existing = new Track { Title = "Existing", FilePath = "http://existing.com/track1.mp3" };
        vm.Tracks.Add(existing);

        var onlineTrack = new OnlineTrack { Id = "track1", Title = "Online Song", Artist = "Artist" };
        vm.AddOnlineCommand.Execute(onlineTrack).Subscribe();

        Assert.Single(vm.Tracks);
        return Task.CompletedTask;
    }

    [Fact]
    public Task SearchAsync_ClearsAndFillsSearchResults()
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
        return Task.CompletedTask;
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

    [Fact]
    public async Task LoadAsync_MalformedJson_DoesNotThrow()
    {
        var playlistFile = CreateTempPlaylistFile();
        await File.WriteAllTextAsync(playlistFile, "{ invalid json !!!");

        var (vm, _, _) = CreateVm(playlistFile);

        var ex = await Record.ExceptionAsync(() => vm.LoadAsync());
        Assert.Null(ex);
        Assert.Empty(vm.Tracks);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentRequests_ProduceValidPlaylistFile()
    {
        var playlistFile = CreateTempPlaylistFile();
        var sync = new object();
        var activeWriters = 0;
        var maxConcurrentWriters = 0;

        async Task ObservedWriteAsync(string path, string contents)
        {
            lock (sync)
            {
                activeWriters++;
                maxConcurrentWriters = Math.Max(maxConcurrentWriters, activeWriters);
            }

            try
            {
                await Task.Delay(20);
                await File.WriteAllTextAsync(path, contents);
            }
            finally
            {
                lock (sync)
                    activeWriters--;
            }
        }

        var (vm, _, _) = CreateVm(playlistFile, ObservedWriteAsync);
        for (var i = 0; i < 20; i++)
        {
            vm.Tracks.Add(new Track
            {
                Id = $"track-{i}",
                Title = $"Track {i}",
                FilePath = $"http://example.com/{i}.mp3"
            });
        }

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => vm.SaveAsync()));

        var json = await File.ReadAllTextAsync(playlistFile);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, maxConcurrentWriters);
        Assert.Equal(20, doc.RootElement.GetProperty("Tracks").GetArrayLength());
    }
}
