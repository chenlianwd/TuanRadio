using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
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
    private static string CreateTempPlaylistFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "playlist.json");
    }

    private static Mock<IAudioService> CreateAudioMock(List<Track> playlist, Func<Track?> currentTrack)
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => playlist.AsReadOnly());
        audio.Setup(x => x.CurrentTrack).Returns(currentTrack);
        audio.Setup(x => x.RepeatMode).Returns("radio");
        audio.Setup(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()))
            .Callback<IEnumerable<Track>>(tracks => playlist.AddRange(tracks));
        return audio;
    }

    [Fact]
    public async Task RadioNext_AddsRecommendedTrackToDisplayedPlaylist()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audio = new AudioService();
        var dj = new Mock<IDJService>();
        var minimax = new Mock<ILLMService>();
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
            stt.Object,
            CreateTempPlaylistFile());

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
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task AutoRadio_DoesNotPlayStaleRecommendationAfterCurrentTrackChanges()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var current = new Track
        {
            Id = "current",
            SourceId = "test:current",
            Title = "Current",
            Artist = "AIRadio",
            FilePath = "http://example.com/current.mp3"
        };
        var next = new Track
        {
            Id = "next",
            SourceId = "test:next",
            Title = "Next",
            Artist = "AIRadio",
            FilePath = "http://example.com/next.mp3"
        };
        var playlist = new List<Track>();
        Track? currentTrack = current;
        var audio = CreateAudioMock(playlist, () => currentTrack);
        var dj = new Mock<IDJService>();
        var minimax = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        dj.SetupGet(x => x.TtsEnabled).Returns(false);
        dj.Setup(x => x.GenerateTrackIntroductionAsync(current, next))
            .Callback(() => currentTrack = next)
            .ReturnsAsync(new DJScript { Text = "Next up", Expression = "smile", Motion = "wave" });

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            minimax.Object,
            storage.Object,
            search.Object,
            stt.Object,
            CreateTempPlaylistFile());

        try
        {
            vm.PlaylistVM.AddExternalTrack(current);
            vm.PlaylistVM.AddExternalTrack(next);

            var method = typeof(MainWindowViewModel).GetMethod("HandleAutoRadioTrackEndedAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            await (Task)method!.Invoke(vm, new object[] { current })!;

            audio.Verify(x => x.PlayAtIndex(It.IsAny<int>()), Times.Never);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void DislikeCurrentTrackCommand_RecordsCurrentTrackFeedback()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var current = new Track
        {
            Id = "local-current",
            SourceId = "netease:current",
            Title = "Current",
            Artist = "AIRadio",
            FilePath = "http://example.com/current.mp3"
        };
        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => current);
        var dj = new Mock<IDJService>();
        var minimax = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();
        var recommendations = new Mock<IRecommendationService>();
        recommendations.SetupGet(x => x.FeedbackHistory).Returns(Array.Empty<UserMusicFeedback>());

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            minimax.Object,
            storage.Object,
            search.Object,
            stt.Object,
            CreateTempPlaylistFile(),
            recommendations.Object);

        try
        {
            vm.DislikeCurrentTrackCommand.Execute().Subscribe();

            recommendations.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(feedback =>
                feedback.TrackId == "netease:current" &&
                feedback.Action == MusicFeedbackAction.Dislike)), Times.Once);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void Dispose_DoesNotSynchronouslyStopContainerOwnedAudioService()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var playlist = new List<Track>();
        var audio = CreateAudioMock(playlist, () => null);
        var vm = new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            CreateTempPlaylistFile());

        try
        {
            vm.Dispose();

            audio.Verify(x => x.StopTts(), Times.Never);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void ToggleCompactModeCommand_TogglesModeAndClosesOverlays()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audio = CreateAudioMock(new List<Track>(), () => null);
        var dj = new Mock<IDJService>();
        var llm = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var search = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        MainWindowViewModel? vm = null;
        try
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(settingsDir);
            vm = new MainWindowViewModel(
                audio.Object,
                dj.Object,
                llm.Object,
                storage.Object,
                search.Object,
                stt.Object,
                CreateTempPlaylistFile(),
                settingsFile: Path.Combine(settingsDir, "settings.json"));

            vm.IsSettingsOpen = true;
            vm.IsLibraryOpen = true;
            vm.IsCharacterPickerOpen = true;

            vm.ToggleCompactModeCommand.Execute(System.Reactive.Unit.Default).Subscribe();

            Assert.True(vm.IsCompactMode);
            Assert.False(vm.IsSettingsOpen);
            Assert.False(vm.IsLibraryOpen);
            Assert.False(vm.IsCharacterPickerOpen);

            vm.ToggleCompactModeCommand.Execute(System.Reactive.Unit.Default).Subscribe();
            Assert.False(vm.IsCompactMode);
        }
        finally
        {
            vm?.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}

