using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class TrayAndCloseActionTests
{
    private static Mock<IAudioService> CreateAudioMock()
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        audio.Setup(x => x.CurrentTrack).Returns((Track?)null);
        audio.Setup(x => x.RepeatMode).Returns("radio");
        return audio;
    }
    [Theory]
    [InlineData("exit", "exit")]
    [InlineData("EXIT", "exit")]
    [InlineData("ask", "ask")]
    [InlineData("Ask", "ask")]
    [InlineData("tray", "tray")]
    [InlineData("other", "tray")]
    [InlineData(null, "tray")]
    [InlineData("", "tray")]
    public void NormalizeCloseAction_ReturnsExpectedAction(string? input, string expected)
    {
        var result = SettingsViewModel.NormalizeCloseAction(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MainWindowViewModel_ConfirmCloseDialog_TriggersHide_WhenTraySelected()
    {
        var audio = CreateAudioMock();
        var dj = new Mock<IDJService>();
        var llm = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var music = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        var tempPlaylist = Path.GetTempFileName();
        var tempSettings = Path.GetTempFileName();

        try
        {
            using var vm = new MainWindowViewModel(
                audio.Object, dj.Object, llm.Object, storage.Object, music.Object, stt.Object,
                tempPlaylist, tempSettings);

            var hideTriggered = false;
            var exitTriggered = false;
            vm.RequestHideWindow += () => hideTriggered = true;
            vm.RequestExitWindow += () => exitTriggered = true;

            vm.IsCloseConfirmOpen = true;
            vm.CloseDialogIsTray = true;
            vm.CloseDialogIsExit = false;
            vm.CloseDialogRemember = true;

            vm.ConfirmCloseDialogCommand.Execute().Subscribe();

            Assert.False(vm.IsCloseConfirmOpen);
            Assert.True(hideTriggered);
            Assert.False(exitTriggered);
            Assert.Equal("tray", vm.SettingsVM.CloseAction);
            Assert.True(vm.SettingsVM.HasPromptedCloseAction);
        }
        finally
        {
            try { File.Delete(tempPlaylist); } catch { }
            try { File.Delete(tempSettings); } catch { }
        }
    }

    [Fact]
    public void MainWindowViewModel_ConfirmCloseDialog_TriggersExit_WhenExitSelected()
    {
        var audio = CreateAudioMock();
        var dj = new Mock<IDJService>();
        var llm = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var music = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        var tempPlaylist = Path.GetTempFileName();
        var tempSettings = Path.GetTempFileName();

        try
        {
            using var vm = new MainWindowViewModel(
                audio.Object, dj.Object, llm.Object, storage.Object, music.Object, stt.Object,
                tempPlaylist, tempSettings);

            var hideTriggered = false;
            var exitTriggered = false;
            vm.RequestHideWindow += () => hideTriggered = true;
            vm.RequestExitWindow += () => exitTriggered = true;

            vm.IsCloseConfirmOpen = true;
            vm.CloseDialogIsTray = false;
            vm.CloseDialogIsExit = true;
            vm.CloseDialogRemember = false;

            vm.ConfirmCloseDialogCommand.Execute().Subscribe();

            Assert.False(vm.IsCloseConfirmOpen);
            Assert.False(hideTriggered);
            Assert.True(exitTriggered);
            // Remember 为 false 时不更新保存标记
            Assert.False(vm.SettingsVM.HasPromptedCloseAction);
        }
        finally
        {
            try { File.Delete(tempPlaylist); } catch { }
            try { File.Delete(tempSettings); } catch { }
        }
    }

    [Fact]
    public void MainWindowViewModel_CancelCloseDialog_JustDismissesDialog()
    {
        var audio = CreateAudioMock();
        var dj = new Mock<IDJService>();
        var llm = new Mock<ILLMService>();
        var storage = new Mock<ISecureStorage>();
        var music = new Mock<IMusicSearchService>();
        var stt = new Mock<ISttService>();

        var tempPlaylist = Path.GetTempFileName();
        var tempSettings = Path.GetTempFileName();

        try
        {
            using var vm = new MainWindowViewModel(
                audio.Object, dj.Object, llm.Object, storage.Object, music.Object, stt.Object,
                tempPlaylist, tempSettings);

            var hideTriggered = false;
            var exitTriggered = false;
            vm.RequestHideWindow += () => hideTriggered = true;
            vm.RequestExitWindow += () => exitTriggered = true;

            vm.IsCloseConfirmOpen = true;

            vm.CancelCloseDialogCommand.Execute().Subscribe();

            Assert.False(vm.IsCloseConfirmOpen);
            Assert.False(hideTriggered);
            Assert.False(exitTriggered);
        }
        finally
        {
            try { File.Delete(tempPlaylist); } catch { }
            try { File.Delete(tempSettings); } catch { }
        }
    }
}
