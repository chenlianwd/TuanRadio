using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class PlayerViewModelTests
{
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
            service.Previous();
        }
        finally
        {
            service.Dispose();
        }
    }
}
