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
/// 播放列表/抽屉的网络电台标签页（Tab 5）ViewModel 测试：
/// 涵盖精选分类懒加载、流派分类切换响应、电台搜索、调谐音效与播放上下文启动、
/// 跨会话持久化反序列化（保留 stream_url 直链及码率编码声明）。
/// </summary>
public class PlaylistViewModelRadioTests
{
    private static string CreateTempPlaylistFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "playlist.json");
    }

    private static (PlaylistViewModel vm, Mock<IAudioService> audio, Mock<IMusicSearchService> search, Mock<IRadioBrowserService> radio)
        CreateRadioVm(string? playlistFile = null, Action<Mock<IRadioBrowserService>>? setupRadio = null)
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
        setupRadio?.Invoke(radioMock);

        var vm = new PlaylistViewModel(
            audioMock.Object,
            searchMock.Object,
            playlistFile ?? CreateTempPlaylistFile(),
            radioBrowserService: radioMock.Object);

        return (vm, audioMock, searchMock, radioMock);
    }

    [Fact]
    public async Task LoadInitialRadioStationsAsync_PopulatesStations_AndIsIdempotent()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var station1 = new RadioStation
            {
                StationUuid = "lofi-1",
                Name = "Lofi Hip Hop",
                StreamUrl = "https://stream.example.com/lofi.mp3",
                Codec = "MP3",
                Bitrate = 128
            };
            var station2 = new RadioStation
            {
                StationUuid = "lofi-2",
                Name = "Chillhop",
                StreamUrl = "https://stream.example.com/chill.mp3",
                Codec = "AAC",
                Bitrate = 192
            };

            var (vm, _, _, radioMock) = CreateRadioVm(setupRadio: mock =>
            {
                mock.Setup(x => x.GetStationsByCategoryAsync("lofi", false, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new RadioStationQueryResult(new[] { station1, station2 }, IsOfflineFallback: false));
            });

            Assert.Empty(vm.RadioStations);
            Assert.False(vm.HasRadioStations);

            // 首次调用：按默认 lofi 分类加载
            await vm.LoadInitialRadioStationsAsync();

            Assert.Equal(2, vm.RadioStations.Count);
            Assert.True(vm.HasRadioStations);
            Assert.Equal("Lofi Hip Hop", vm.RadioStations[0].Name);
            Assert.Equal("Chillhop", vm.RadioStations[1].Name);
            radioMock.Verify(x => x.GetStationsByCategoryAsync("lofi", false, It.IsAny<CancellationToken>()), Times.Once);

            // 再次调用：已有电台列表，幂等跳过不重复拉取
            await vm.LoadInitialRadioStationsAsync();
            radioMock.Verify(x => x.GetStationsByCategoryAsync("lofi", false, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task CategorySelection_UpdatesRadioStations()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var classicalStation = new RadioStation
            {
                StationUuid = "classical-1",
                Name = "KUSC Classical",
                StreamUrl = "https://stream.kusc.org/live.mp3",
                Codec = "MP3",
                Bitrate = 128
            };

            var (vm, _, _, radioMock) = CreateRadioVm(setupRadio: mock =>
            {
                mock.Setup(x => x.GetStationsByCategoryAsync("classical", false, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new RadioStationQueryResult(new[] { classicalStation }, IsOfflineFallback: false));
            });

            var classicalCategory = vm.RadioCategories.FirstOrDefault(c => c.Key == "classical");
            Assert.NotNull(classicalCategory);

            // 显式触发分类切换
            await vm.LoadRadioStationsByCategoryAsync(classicalCategory!.Key);

            Assert.Single(vm.RadioStations);
            Assert.Equal("KUSC Classical", vm.RadioStations[0].Name);
            Assert.True(vm.HasRadioStations);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task SearchRadioStations_ExecutesQuery_AndHandlesEmptyResult()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var station = new RadioStation
            {
                StationUuid = "search-1",
                Name = "BBC World Service",
                StreamUrl = "https://stream.bbc.co.uk/world.mp3"
            };

            var (vm, _, _, radioMock) = CreateRadioVm(setupRadio: mock =>
            {
                mock.Setup(x => x.SearchStationsAsync("BBC", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new RadioStationQueryResult(new[] { station }, IsOfflineFallback: false));
                mock.Setup(x => x.SearchStationsAsync("NonExistent", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new RadioStationQueryResult(Array.Empty<RadioStation>(), IsOfflineFallback: false));
            });

            // 1. 有结果搜索
            vm.RadioSearchText = "BBC";
            await vm.SearchRadioStationsAsync();

            Assert.Single(vm.RadioStations);
            Assert.Equal("BBC World Service", vm.RadioStations[0].Name);
            Assert.True(vm.HasRadioStations);
            Assert.Empty(vm.RadioStatusMessage);

            // 2. 无结果搜索
            vm.RadioSearchText = "NonExistent";
            await vm.SearchRadioStationsAsync();

            Assert.Empty(vm.RadioStations);
            Assert.False(vm.HasRadioStations);
            Assert.Contains("NonExistent", vm.RadioStatusMessage);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void PlayRadioStationCommand_PlaysTuningSweep_AndStartsPlaybackContext()
    {
        var (vm, audioMock, _, _) = CreateRadioVm();

        var station = new RadioStation
        {
            StationUuid = "tune-1",
            Name = "Retro Synthwave",
            StreamUrl = "https://stream.synthwave.fm/live.aac",
            Codec = "AAC",
            Bitrate = 256
        };
        vm.RadioStations.Add(station);

        vm.PlayRadioStationCommand.Execute(station).Subscribe();

        // 验证播放收音机调谐搜台音效
        audioMock.Verify(a => a.PlayRadioSoundFx(RadioFxKind.TuningSweep), Times.Once);

        // 验证启动单曲上下文，类型为 live stream 且携带 stream_url 直链
        audioMock.Verify(a => a.StartPlaybackContext(
            It.Is<IReadOnlyList<Track>>(tracks =>
                tracks.Count == 1 &&
                tracks[0].Id == "radio-browser:tune-1" &&
                tracks[0].IsLiveStream &&
                tracks[0].FilePath == "https://stream.synthwave.fm/live.aac" &&
                tracks[0].DeclaredCodec == "AAC" &&
                tracks[0].DeclaredBitrateKbps == 256),
            0,
            false,
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void FavoriteRadioStationCommand_TogglesFavoriteState()
    {
        var (vm, _, _, _) = CreateRadioVm();

        var station = new RadioStation
        {
            StationUuid = "fav-1",
            Name = "Favorite Radio",
            StreamUrl = "https://stream.fav.org/stream.mp3",
            IsFavorite = false
        };
        vm.RadioStations.Add(station);

        // 触发收藏
        vm.FavoriteRadioStationCommand.Execute(station).Subscribe();

        // 收藏列表中应存在该电台对应的 Track
        Assert.Contains(vm.Favorites, t => t.Id == "radio-browser:fav-1");
    }

    [Fact]
    public async Task LoadAsync_PreservesRadioTrackStreamUrlAndMetadata()
    {
        var playlistFile = CreateTempPlaylistFile();
        var json = """
            {
              "Version": 3,
              "Tracks": [
                {
                  "Id": "radio-browser:somafm-groovesalad",
                  "Title": "Groove Salad",
                  "Artist": "SomaFM",
                  "Album": "Ambient Radio",
                  "DurationMs": 0,
                  "SourceId": "radio-browser:somafm-groovesalad",
                  "IsFavorite": true,
                  "Provider": {
                    "Metadata": {
                      "stream_url": "https://ice1.somafm.com/groovesalad-128-mp3",
                      "codec": "MP3",
                      "bitrate": "128",
                      "is_live": "true"
                    }
                  }
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(playlistFile, json);

        var (vm, _, _, _) = CreateRadioVm(playlistFile);

        await vm.LoadAsync();

        Assert.Single(vm.Tracks);
        var loaded = vm.Tracks[0];

        // 普通在线单曲会丢弃 FilePath，但网络电台必须保留 stream_url 供开箱即播
        Assert.Equal("https://ice1.somafm.com/groovesalad-128-mp3", loaded.FilePath);
        Assert.Equal("MP3", loaded.DeclaredCodec);
        Assert.Equal(128, loaded.DeclaredBitrateKbps);
        Assert.True(loaded.IsLiveStream);
        Assert.True(loaded.IsFavorite);
        Assert.Single(vm.Favorites);
    }
}
