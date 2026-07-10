using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
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

    [Fact]
    public void PlayerViewModel_SeekTo_DoesNotThrow()
    {
        var service = new AudioService();
        var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(service);

        try
        {
            vm.SeekTo(30.0);
            vm.StartSeek();
            vm.EndSeek(60.0);
            // No exception means success
        }
        finally
        {
            vm.Dispose();
            service.Dispose();
        }
    }

    [Fact]
    public void PlayerViewModel_DraggingState_Tracked()
    {
        var service = new AudioService();
        var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(service);

        try
        {
            vm.StartSeek();
            vm.EndSeek(100.0);
            // _isDragging is private; this test verifies StartSeek/EndSeek don't throw.
            // Position-related behavior is covered by AudioService integration.
        }
        finally
        {
            vm.Dispose();
            service.Dispose();
        }
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
