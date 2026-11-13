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

public sealed class PlaylistViewModelCustomRadioTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _playlistFile;
    private readonly string _customStorageFile;

    public PlaylistViewModelCustomRadioTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", "CustomRadioVM", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _playlistFile = Path.Combine(_tempDir, "playlist.json");
        _customStorageFile = Path.Combine(_tempDir, "custom_stations.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private (PlaylistViewModel vm, CustomRadioStorage storage, Mock<IRadioBrowserService> radioMock, Mock<IRadioStreamProbeService> probeMock)
        CreateVm()
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audioMock.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audioMock.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());

        var searchMock = new Mock<IMusicSearchService>();
        var radioMock = new Mock<IRadioBrowserService>();
        radioMock.Setup(x => x.GetStationsByCategoryAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RadioStation>());
        radioMock.Setup(x => x.SearchStationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new RadioStation { StationUuid = "global-1", Name = "Global Campus", StreamUrl = "https://global.example.com/1.mp3" }
            });

        var probeMock = new Mock<IRadioStreamProbeService>();
        var storage = new CustomRadioStorage(_customStorageFile);
        var parser = new PlaylistFileParser();

        var vm = new PlaylistViewModel(
            audioMock.Object,
            searchMock.Object,
            _playlistFile,
            radioBrowserService: radioMock.Object,
            customRadioStorage: storage,
            radioStreamProbeService: probeMock.Object,
            playlistFileParser: parser);

        return (vm, storage, radioMock, probeMock);
    }

    [Fact]
    public async Task AddEditDeleteCustomStation_CascadesToFavoritesAndList()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var (vm, storage, _, _) = CreateVm();
            using (vm)
            using (storage)
            {
                Assert.Contains(vm.RadioCategories, c => c.Key == "custom");

                // 1. 新增自定义电台（使用局域网 IP 避开离线 DNS 解析）
                vm.OpenAddCustomStationCommand.Execute(Unit.Default).Subscribe();
                vm.CustomStationUrl = "http://192.168.1.100:8000/campus.mp3";
                vm.CustomStationName = "Campus FM";
                vm.CustomStationTags = "Campus, Indie";
                await vm.SaveCustomStationAsync(playAfterSave: false);

                Assert.False(vm.IsCustomStationPanelOpen);
                Assert.Equal("custom", vm.SelectedRadioCategory?.Key);
                Assert.Single(vm.RadioStations);
                var station = vm.RadioStations[0];
                Assert.True(station.IsCustom);
                Assert.Equal("Campus FM", station.Name);

                // 2. 收藏自定义电台，不应污染主歌单 Tracks
                vm.FavoriteRadioStationCommand.Execute(station).Subscribe();
                Assert.True(station.IsFavorite);
                Assert.Single(vm.Favorites);
                Assert.Empty(vm.Tracks);

                // 3. 编辑自定义电台，验证四级级联更新收藏夹 Track
                vm.EditCustomStationCommand.Execute(station).Subscribe();
                Assert.True(vm.IsCustomStationPanelOpen);
                Assert.True(vm.IsEditingCustomStation);
                vm.CustomStationName = "Campus FM HD";
                vm.CustomStationUrl = "http://192.168.1.100:8000/campus_hd.aac";
                vm.CustomStationCodec = "AAC";
                await vm.SaveCustomStationAsync(playAfterSave: false);

                var updated = vm.RadioStations.Single();
                Assert.Equal("Campus FM HD", updated.Name);
                Assert.Equal("http://192.168.1.100:8000/campus_hd.aac", updated.StreamUrl);
                var favTrack = vm.Favorites.Single();
                Assert.Equal("Campus FM HD", favTrack.Title);
                Assert.Equal("http://192.168.1.100:8000/campus_hd.aac", favTrack.ProviderMetadata["stream_url"]);

                // 4. 删除自定义电台，验证级联移除收藏夹防僵尸项
                await vm.DeleteCustomStationAsync(updated);
                Assert.Empty(vm.RadioStations);
                Assert.Empty(vm.Favorites);
                Assert.Empty(vm.FavoriteStations);
            }
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task SearchRadioStations_IncludesAndPinsCustomStationsFirst()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var (vm, storage, radioMock, _) = CreateVm();
            using (vm)
            using (storage)
            {
                await storage.AddStationAsync(new RadioStation
                {
                    StationUuid = RadioStation.CreateCustomUuid(),
                    Name = "My Campus Radio",
                    StreamUrl = "http://192.168.1.50:8000/campus",
                    Tags = "Campus"
                });

                // 切到其他分类后全局搜索
                vm.SelectedRadioCategory = vm.RadioCategories.First(c => c.Key == "lofi");
                vm.RadioSearchText = "Campus";
                await vm.SearchRadioStationsAsync();

                Assert.Equal(2, vm.RadioStations.Count);
                Assert.True(vm.RadioStations[0].IsCustom);
                Assert.Equal("My Campus Radio", vm.RadioStations[0].Name);
                Assert.Equal("Global Campus", vm.RadioStations[1].Name);

                // 切到 custom 分类下搜索，应纯本地过滤而不调用远程 API
                radioMock.Invocations.Clear();
                vm.SelectedRadioCategory = vm.RadioCategories.First(c => c.Key == "custom");
                vm.RadioSearchText = "Campus";
                await vm.SearchRadioStationsAsync();

                Assert.Single(vm.RadioStations);
                radioMock.Verify(x => x.SearchStationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            }
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task ProbeCustomStreamAndInvalidBitrateText_HandleStatesSafely()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var (vm, storage, _, probeMock) = CreateVm();
            using (vm)
            using (storage)
            {
                probeMock.Setup(x => x.ProbeAsync("http://192.168.1.99:8000/live", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new StreamProbeResult(true, "NAS FM", "AAC", 256, "Electronic", false, null));
                probeMock.Setup(x => x.ProbeAsync("http://192.168.1.99:8000/file.mp3", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new StreamProbeResult(true, null, "MP3", null, null, true, null));

                vm.OpenAddCustomStationCommand.Execute(Unit.Default).Subscribe();
                vm.CustomStationUrl = "http://192.168.1.99:8000/live";
                await vm.ProbeCustomStreamAsync();

                Assert.False(vm.ProbeIsWarning);
                Assert.Equal("NAS FM", vm.CustomStationName);
                Assert.Equal("AAC", vm.CustomStationCodec);
                Assert.Equal("256", vm.CustomStationBitrateText);

                vm.CustomStationUrl = "http://192.168.1.99:8000/file.mp3";
                await vm.ProbeCustomStreamAsync();
                Assert.True(vm.ProbeIsWarning);

                // 测试空/非数字码率文本安全落盘为 0
                vm.CustomStationBitrateText = "not-a-number";
                await vm.SaveCustomStationAsync(playAfterSave: false);
                var saved = Assert.Single(vm.RadioStations);
                Assert.Equal(0, saved.Bitrate);
            }
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
