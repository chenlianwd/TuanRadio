using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// Note: all tests use real AudioService (requires LibVLC native libs). Consider mocking for CI (L25).
public class PlayerViewModelTests
{
    [Fact]
    public void ToggleRepeatCommand_UpdatesModeTextAndTooltip()
    {
        var mode = "radio";
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.RepeatMode).Returns(() => mode);
        audio.Setup(x => x.SetRepeatMode(It.IsAny<string>()))
            .Callback<string>(value => mode = value);
        audio.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());

        using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
        Assert.Equal("电台模式", vm.RepeatModeTip);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("list", vm.RepeatMode);
        Assert.Equal("ALL", vm.RepeatText);
        Assert.Equal("列表循环", vm.RepeatModeTip);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("single", vm.RepeatMode);
        Assert.Equal("单曲循环", vm.RepeatModeTip);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("none", vm.RepeatMode);
        Assert.Equal("关闭循环", vm.RepeatModeTip);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("radio", vm.RepeatMode);
        Assert.Equal("电台模式", vm.RepeatModeTip);
    }

    private static Mock<IAudioService> CreatePositionalAudioMock(Subject<TimeSpan> positions)
    {
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.RepeatMode).Returns("radio");
        audio.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(x => x.PositionChanged).Returns(positions);
        audio.Setup(x => x.Seek(It.IsAny<TimeSpan>()));
        return audio;
    }

    [Fact]
    public void PlayerViewModel_SeekTo_UpdatesPositionTextAndForwardsToService()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var positions = new Subject<TimeSpan>();
            double? seekTarget = null;
            var audio = CreatePositionalAudioMock(positions);
            audio.Setup(x => x.Seek(It.IsAny<TimeSpan>()))
                .Callback<TimeSpan>(ts => seekTarget = ts.TotalSeconds);

            using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
            positions.OnNext(TimeSpan.FromSeconds(12));
            Assert.Equal(12, vm.CurrentSeconds);
            Assert.Equal("0:12", vm.PositionText);

            vm.SeekTo(30.0);
            Assert.Equal(30.0, seekTarget);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void PlayerViewModel_DraggingState_FreezesPositionUpdates()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var positions = new Subject<TimeSpan>();
            var audio = CreatePositionalAudioMock(positions);

            using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
            positions.OnNext(TimeSpan.FromSeconds(10));

            vm.StartSeek();
            positions.OnNext(TimeSpan.FromSeconds(50));
            // 拖动期间不得回写 CurrentSeconds，否则 Slider 会被播放位置拉回
            Assert.Equal(10, vm.CurrentSeconds);

            vm.EndSeek(50);
            positions.OnNext(TimeSpan.FromSeconds(51));
            Assert.Equal(51, vm.CurrentSeconds);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void PlayerViewModel_SyncsInitialVolumeAndShuffleFromService()
    {
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.RepeatMode).Returns("radio");
        audio.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.SetupGet(x => x.Volume).Returns(0.4f);
        audio.SetupGet(x => x.IsShuffled).Returns(true);

        using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
        Assert.Equal(0.4f, vm.Volume);
        Assert.True(vm.IsShuffled);
    }

    [Fact]
    public void AudioService_PlayAtIndex_InvalidIndex_NoCrash()
    {
        var service = new AudioService();
        try
        {
            service.PlayAtIndex(-1);
            service.PlayAtIndex(999);
            // No exception
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void AudioService_NextPrevious_DoNotThrow()
    {
        var service = new AudioService();
        service.LoadTracks(new[]
        {
            new Track { Title = "Song 1", FilePath = "http://example.com/1.mp3" },
            new Track { Title = "Song 2", FilePath = "http://example.com/2.mp3" }
        });

        try
        {
            service.Next();
            Assert.NotNull(service.CurrentTrack);

            service.Previous();
            Assert.NotNull(service.CurrentTrack);

            Assert.Equal(2, service.Playlist.Count);
        }
        finally
        {
            service.Dispose();
        }
    }
}
