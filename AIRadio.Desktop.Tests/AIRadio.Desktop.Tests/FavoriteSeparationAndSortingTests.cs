using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 收藏体系重构专项测试：
/// 1. 歌曲/电台双集合彻底解耦分流（VisibleFavoriteTracks vs FavoriteStations）；
/// 2. 纯单曲即时模糊搜索过滤（歌名、歌手、专辑）；
/// 3. 多维排序（最新收藏降序、歌名/歌手 A-Z 字典序、时长升降序）；
/// 4. 连播安全（PlayFavoriteCommand 播放歌曲排除直播流，播放电台单台独立直连）；
/// 5. Track 与 RadioStation 音质徽标契约对齐。
/// </summary>
public class FavoriteSeparationAndSortingTests
{
    private static string CreateTempPlaylistFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "playlist.json");
    }

    private static (PlaylistViewModel vm, Mock<IAudioService> audio, Mock<IMusicSearchService> search, Mock<IRadioBrowserService> radio)
        CreateTestVm(string? playlistFile = null)
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audioMock.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audioMock.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());

        var searchMock = new Mock<IMusicSearchService>();
        searchMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var radioMock = new Mock<IRadioBrowserService>();

        var vm = new PlaylistViewModel(
            audioMock.Object,
            searchMock.Object,
            playlistFile ?? CreateTempPlaylistFile(),
            radioBrowserService: radioMock.Object);

        return (vm, audioMock, searchMock, radioMock);
    }

    [Fact]
    public void FavoriteStations_Separation_MaintainsDualCollections()
    {
        var (vm, _, _, _) = CreateTestVm();

        var song1 = new Track { Id = "s1", Title = "Song 1", Artist = "Artist A" };
        var song2 = new Track { Id = "s2", Title = "Song 2", Artist = "Artist B" };
        var song3 = new Track { Id = "s3", Title = "Song 3", Artist = "Artist C" };

        var station1 = new Track
        {
            Id = "radio-browser:u1",
            SourceId = "radio-browser:u1",
            Title = "Lofi Station",
            Artist = "China · lofi",
            DeclaredCodec = "MP3",
            DeclaredBitrateKbps = 128
        };
        var station2 = new Track
        {
            Id = "radio-browser:u2",
            SourceId = "radio-browser:u2",
            Title = "Jazz FM",
            Artist = "US · jazz",
            DeclaredCodec = "AAC",
            DeclaredBitrateKbps = 64
        };

        vm.Favorites.Add(song1);
        vm.Favorites.Add(station1);
        vm.Favorites.Add(song2);
        vm.Favorites.Add(station2);
        vm.Favorites.Add(song3);

        vm.ApplyFavoriteFilterAndSort();

        Assert.Equal(5, vm.Favorites.Count);
        Assert.Equal(3, vm.VisibleFavoriteTracks.Count);
        Assert.Equal(2, vm.FavoriteStations.Count);

        Assert.All(vm.VisibleFavoriteTracks, t => Assert.False(t.IsLiveStream));
        Assert.All(vm.FavoriteStations, t => Assert.True(t.IsLiveStream));

        Assert.True(vm.HasVisibleFavoriteTracks);
        Assert.True(vm.HasFavoriteStations);
        Assert.Contains("3", vm.FavoriteSongsTabTitle);
        Assert.Contains("2", vm.FavoriteStationsTabTitle);
    }

    [Fact]
    public void FavoriteTracksFilter_MatchingKeywords_ReturnsFilteredSubset()
    {
        var (vm, _, _, _) = CreateTestVm();

        var song1 = new Track { Id = "s1", Title = "晴天", Artist = "周杰伦", Album = "叶惠美" };
        var song2 = new Track { Id = "s2", Title = "稻香", Artist = "周杰伦", Album = "魔杰座" };
        var song3 = new Track { Id = "s3", Title = "Yellow", Artist = "Coldplay", Album = "Parachutes" };
        var station = new Track { Id = "radio-browser:u1", SourceId = "radio-browser:u1", Title = "周杰伦电台" };

        vm.Favorites.Add(song1);
        vm.Favorites.Add(song2);
        vm.Favorites.Add(song3);
        vm.Favorites.Add(station);

        vm.ApplyFavoriteFilterAndSort();
        Assert.Equal(3, vm.VisibleFavoriteTracks.Count);

        // 按歌手过滤 "周杰伦"
        vm.FavoriteFilterText = "周杰伦";
        vm.ApplyFavoriteFilterAndSort();
        Assert.Equal(2, vm.VisibleFavoriteTracks.Count);
        Assert.Contains(vm.VisibleFavoriteTracks, t => t.Title == "晴天");
        Assert.Contains(vm.VisibleFavoriteTracks, t => t.Title == "稻香");

        // 仅匹配歌名 "Yellow"
        vm.FavoriteFilterText = "yellow";
        vm.ApplyFavoriteFilterAndSort();
        Assert.Single(vm.VisibleFavoriteTracks);
        Assert.Equal("Yellow", vm.VisibleFavoriteTracks[0].Title);

        // 按专辑过滤 "叶惠美"
        vm.FavoriteFilterText = "叶惠美";
        vm.ApplyFavoriteFilterAndSort();
        Assert.Single(vm.VisibleFavoriteTracks);
        Assert.Equal("晴天", vm.VisibleFavoriteTracks[0].Title);

        // 无匹配项
        vm.FavoriteFilterText = "不存在的歌曲";
        vm.ApplyFavoriteFilterAndSort();
        Assert.Empty(vm.VisibleFavoriteTracks);
        Assert.False(vm.HasVisibleFavoriteTracks);
        Assert.False(string.IsNullOrEmpty(vm.FavoriteSongStatusMessage));

        // 清空恢复
        vm.FavoriteFilterText = string.Empty;
        vm.ApplyFavoriteFilterAndSort();
        Assert.Equal(3, vm.VisibleFavoriteTracks.Count);
        Assert.True(vm.HasVisibleFavoriteTracks);
    }

    [Fact]
    public void FavoriteTracksSort_DateAddedDesc_MaintainsLatestFirst()
    {
        var (vm, _, _, _) = CreateTestVm();

        var songA = new Track { Id = "sA", Title = "Song A" };
        var songB = new Track { Id = "sB", Title = "Song B" };
        var songC = new Track { Id = "sC", Title = "Song C" };

        vm.Favorites.Add(songA);
        vm.Favorites.Add(songB);
        vm.Favorites.Add(songC);

        vm.ApplyFavoriteFilterAndSort();

        // 默认最新收藏排在首位（C, B, A）
        Assert.Equal(3, vm.VisibleFavoriteTracks.Count);
        Assert.Equal("Song C", vm.VisibleFavoriteTracks[0].Title);
        Assert.Equal("Song B", vm.VisibleFavoriteTracks[1].Title);
        Assert.Equal("Song A", vm.VisibleFavoriteTracks[2].Title);
    }

    [Fact]
    public void FavoriteTracksSort_TitleAndArtist_Alphabetical()
    {
        var (vm, _, _, _) = CreateTestVm();

        var song1 = new Track { Id = "s1", Title = "Banana", Artist = "Zoe" };
        var song2 = new Track { Id = "s2", Title = "Apple", Artist = "Amy" };
        var song3 = new Track { Id = "s3", Title = "Cherry", Artist = "Bob" };

        vm.Favorites.Add(song1);
        vm.Favorites.Add(song2);
        vm.Favorites.Add(song3);

        // 按歌名升序
        vm.SelectedFavoriteSortOption = vm.FavoriteSortOptions.First(o => o.Kind == FavoriteSortKind.TitleAsc);
        vm.ApplyFavoriteFilterAndSort();

        Assert.Equal("Apple", vm.VisibleFavoriteTracks[0].Title);
        Assert.Equal("Banana", vm.VisibleFavoriteTracks[1].Title);
        Assert.Equal("Cherry", vm.VisibleFavoriteTracks[2].Title);

        // 按歌手名升序
        vm.SelectedFavoriteSortOption = vm.FavoriteSortOptions.First(o => o.Kind == FavoriteSortKind.ArtistAsc);
        vm.ApplyFavoriteFilterAndSort();

        Assert.Equal("Amy", vm.VisibleFavoriteTracks[0].Artist);
        Assert.Equal("Bob", vm.VisibleFavoriteTracks[1].Artist);
        Assert.Equal("Zoe", vm.VisibleFavoriteTracks[2].Artist);
    }

    [Fact]
    public void FavoriteTracksSort_Duration_LongestAndShortest()
    {
        var (vm, _, _, _) = CreateTestVm();

        var song1 = new Track { Id = "s1", Title = "Medium", Duration = TimeSpan.FromSeconds(180) };
        var song2 = new Track { Id = "s2", Title = "Longest", Duration = TimeSpan.FromSeconds(300) };
        var song3 = new Track { Id = "s3", Title = "Shortest", Duration = TimeSpan.FromSeconds(120) };

        vm.Favorites.Add(song1);
        vm.Favorites.Add(song2);
        vm.Favorites.Add(song3);

        // 时长降序
        vm.SelectedFavoriteSortOption = vm.FavoriteSortOptions.First(o => o.Kind == FavoriteSortKind.DurationDesc);
        vm.ApplyFavoriteFilterAndSort();

        Assert.Equal("Longest", vm.VisibleFavoriteTracks[0].Title);
        Assert.Equal("Medium", vm.VisibleFavoriteTracks[1].Title);
        Assert.Equal("Shortest", vm.VisibleFavoriteTracks[2].Title);

        // 时长升序
        vm.SelectedFavoriteSortOption = vm.FavoriteSortOptions.First(o => o.Kind == FavoriteSortKind.DurationAsc);
        vm.ApplyFavoriteFilterAndSort();

        Assert.Equal("Shortest", vm.VisibleFavoriteTracks[0].Title);
        Assert.Equal("Medium", vm.VisibleFavoriteTracks[1].Title);
        Assert.Equal("Longest", vm.VisibleFavoriteTracks[2].Title);
    }

    [Fact]
    public void PlayFavorites_ExcludesLiveStreamStations()
    {
        var (vm, audioMock, _, _) = CreateTestVm();

        var song1 = new Track { Id = "s1", Title = "Song 1" };
        var song2 = new Track { Id = "s2", Title = "Song 2" };
        var liveStation = new Track
        {
            Id = "radio-browser:u1",
            SourceId = "radio-browser:u1",
            Title = "Live Radio"
        };

        vm.Favorites.Add(song1);
        vm.Favorites.Add(liveStation);
        vm.Favorites.Add(song2);
        vm.ApplyFavoriteFilterAndSort();

        // 1. 点播歌曲 -> 启动连播上下文，上下文仅含纯单曲，排除 liveStation
        // 默认排序为最新收藏在前，因此 song2 位于 index 0，song1 位于 index 1
        vm.PlayFavoriteCommand.Execute(song2).Subscribe();

        audioMock.Verify(a => a.StartPlaybackContext(
            It.Is<IReadOnlyList<Track>>(list => list.Count == 2 && list.All(t => !t.IsLiveStream)),
            0,
            false,
            It.IsAny<string>()), Times.Once);

        vm.PlayFavoriteCommand.Execute(song1).Subscribe();

        audioMock.Verify(a => a.StartPlaybackContext(
            It.Is<IReadOnlyList<Track>>(list => list.Count == 2 && list.All(t => !t.IsLiveStream)),
            1,
            false,
            It.IsAny<string>()), Times.Once);

        // 2. 点播电台 -> 独立播放该台，不得调用 StartPlaybackContext
        vm.PlayFavoriteCommand.Execute(liveStation).Subscribe();

        audioMock.Verify(a => a.PlayTrack(liveStation), Times.Once);
        // StartPlaybackContext 次数仍为 2（未因播放电台而增加）
        audioMock.Verify(a => a.StartPlaybackContext(It.IsAny<IReadOnlyList<Track>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData("UNKNOWN", 0, "")]
    [InlineData("UNKNOWN", -1, "")]
    [InlineData("unknown", 128, "128k")]
    [InlineData("MP3", 128, "MP3 128k")]
    [InlineData("mp3", 320, "mp3 320k")]
    [InlineData("AAC", 64, "AAC 64k")]
    [InlineData("FLAC", 0, "")]
    [InlineData(null, 192, "192k")]
    public void Track_DisplayQuality_SanitizesAndHidesRedundantBadges(string? codec, int? bitrate, string expected)
    {
        var track = new Track
        {
            DeclaredCodec = codec,
            DeclaredBitrateKbps = bitrate
        };

        Assert.Equal(expected, track.DisplayQuality);
    }

    [Fact]
    public void SubTabSwitch_UpdatesIsFavoriteSongsSubTab()
    {
        var (vm, _, _, _) = CreateTestVm();

        Assert.Equal(0, vm.FavoriteSubTabIndex);
        Assert.True(vm.IsFavoriteSongsSubTab);

        vm.SetFavoriteSubTabCommand.Execute(1).Subscribe();
        Assert.Equal(1, vm.FavoriteSubTabIndex);
        Assert.False(vm.IsFavoriteSongsSubTab);

        vm.SetFavoriteSubTabCommand.Execute(0).Subscribe();
        Assert.Equal(0, vm.FavoriteSubTabIndex);
        Assert.True(vm.IsFavoriteSongsSubTab);
    }
}
