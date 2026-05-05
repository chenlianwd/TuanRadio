using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class MainWindowViewModelTests
{
    [Fact]
    public async Task RadioNext_AddsRecommendedTrackToDisplayedPlaylist()
    {
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audio = new AudioService();
        var dj = new Mock<IDJService>();
        var minimax = new Mock<IMinimaxService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        var recommended = new Track
        {
            Id = "recommended",
            SourceId = "test:recommended",
            Title = "Recommended",
            Artist = "AIRadio",
            FilePath = "http://example.com/recommended.mp3"
        };

        dj.Setup(x => x.RecommendNextTrackAsync(It.IsAny<Track?>()))
            .ReturnsAsync(recommended);

        var vm = new MainWindowViewModel(
            audio,
            dj.Object,
            minimax.Object,
            storage.Object,
            search.Object,
            stt.Object);

        try
        {
            vm.PlaylistVM.AddExternalTrack(new Track
            {
                Id = "current",
                SourceId = "test:current",
                Title = "Current",
                Artist = "AIRadio",
                FilePath = "http://example.com/current.mp3"
            });

            vm.PlayerVM.NextCommand.Execute().Subscribe();
            await Task.Delay(250);

            Assert.Contains(vm.PlaylistVM.Tracks, t => t.SourceId == "test:recommended");
            Assert.Single(vm.PlaylistVM.Tracks.Where(t => t.SourceId == "test:recommended"));
            Assert.Single(audio.Playlist.Where(t => t.SourceId == "test:recommended"));
        }
        finally
        {
            vm.Dispose();
            audio.Dispose();
        }
    }
}
