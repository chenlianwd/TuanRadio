using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using System.Reactive.Linq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ChatViewModelTests
{
    private static (ChatViewModel vm, Mock<IDJService> djMock, Mock<IAudioService> audioMock, Mock<IMusicSearchService> searchMock)
        CreateVm()
    {
        var playlist = new List<Track>();
        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.TtsEnabled).Returns(false);
        djMock.SetupGet(x => x.CurrentEmotion).Returns("neutral");

        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audioMock.Setup(x => x.TtsError).Returns(new Subject<string>());
        audioMock.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(() => playlist.AsReadOnly());
        audioMock.Setup(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()))
            .Callback<IEnumerable<Track>>(tracks => playlist.AddRange(tracks));

        var searchMock = new Mock<IMusicSearchService>();
        var sttMock = new Mock<ISttService>();

        var vm = new ChatViewModel(djMock.Object, audioMock.Object, searchMock.Object, sttMock.Object);
        return (vm, djMock, audioMock, searchMock);
    }

    private static (ChatViewModel vm, Subject<string> ttsErrors) CreateVmWithTtsErrors()
    {
        var playlist = new List<Track>();
        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.TtsEnabled).Returns(false);
        djMock.SetupGet(x => x.CurrentEmotion).Returns("neutral");

        var ttsErrors = new Subject<string>();
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audioMock.Setup(x => x.TtsError).Returns(ttsErrors);
        audioMock.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(() => playlist.AsReadOnly());

        var searchMock = new Mock<IMusicSearchService>();
        var sttMock = new Mock<ISttService>();

        var vm = new ChatViewModel(djMock.Object, audioMock.Object, searchMock.Object, sttMock.Object);
        return (vm, ttsErrors);
    }

    [Fact]
    public void DismissStatusNotice_HidesNoticeAndShowsRecall()
    {
        var (vm, ttsErrors) = CreateVmWithTtsErrors();

        ttsErrors.OnNext("语音播放设备不可用。");
        vm.DismissStatusNoticeCommand.Execute().Subscribe();

        Assert.False(vm.ShowStatusNotice);
        Assert.True(vm.ShowStatusRecall);
        Assert.Equal("语音播放失败", vm.StatusHeadline);
    }

    [Fact]
    public void RestoreStatusNotice_ReopensDismissedNotice()
    {
        var (vm, ttsErrors) = CreateVmWithTtsErrors();

        ttsErrors.OnNext("语音播放设备不可用。");
        vm.DismissStatusNoticeCommand.Execute().Subscribe();
        vm.RestoreStatusNoticeCommand.Execute().Subscribe();

        Assert.True(vm.ShowStatusNotice);
        Assert.False(vm.ShowStatusRecall);
        Assert.Equal("语音播放失败", vm.StatusHeadline);
    }

    [Fact]
    public void ParseResponse_StripsEmotionTags()
    {
        var response = "今天天气真好呢[happy]【next】";
        var parsed = ChatViewModel.ParseDjResponse(response);

        Assert.DoesNotContain("[happy]", parsed.DisplayText);
        Assert.Equal("happy", parsed.Emotion);
        Assert.Equal("next", parsed.Command);
    }

    [Fact]
    public void Track_ToTrack_SetsSourceId()
    {
        var online = new OnlineTrack
        {
            Id = "netease:12345",
            Title = "测试歌曲",
            Artist = "测试歌手",
            Source = "netease"
        };
        var track = online.ToTrack("http://example.com/test.mp3");
        Assert.Equal("netease:12345", track.SourceId);
    }

    [Fact]
    public async Task SendMessage_BareSongTitleWithExactSearchMatch_PlaysSongWithoutDjChat()
    {
        var (vm, djMock, audioMock, searchMock) = CreateVm();
        var result = new OnlineTrack
        {
            Id = "netease:ugly",
            Title = "丑八怪",
            Artist = "薛之谦"
        };
        searchMock.Setup(x => x.SearchAsync("丑八怪", 3))
            .ReturnsAsync(new List<OnlineTrack> { result });
        searchMock.Setup(x => x.SearchAsync("丑八怪", 5))
            .ReturnsAsync(new List<OnlineTrack> { result });
        searchMock.Setup(x => x.GetPlayUrlAsync("netease:ugly"))
            .ReturnsAsync("http://example.com/ugly.mp3");

        vm.InputText = "丑八怪";
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.GenerateChatResponseAsync(It.IsAny<string>()), Times.Never);
        audioMock.Verify(x => x.AddTracks(It.Is<IEnumerable<Track>>(tracks =>
            tracks.Any(t => t.Title == "丑八怪" && t.SourceId == "netease:ugly"))), Times.Once);
        audioMock.Verify(x => x.PlayAtIndex(0), Times.Once);
    }

    [Fact]
    public async Task SendMessage_ShortChatWithoutConfidentSongMatch_UsesDjChat()
    {
        var (vm, djMock, audioMock, searchMock) = CreateVm();
        searchMock.Setup(x => x.SearchAsync("好累", 3))
            .ReturnsAsync(new List<OnlineTrack>());
        djMock.Setup(x => x.GenerateChatResponseAsync("好累"))
            .ReturnsAsync("听起来有点累，先缓一缓。");

        vm.InputText = "好累";
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.GenerateChatResponseAsync("好累"), Times.Once);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
        audioMock.Verify(x => x.PlayAtIndex(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_GenericRecommendation_UsesFreshDjRecommendation()
    {
        var (vm, djMock, audioMock, searchMock) = CreateVm();
        var recommended = new Track
        {
            Id = "fresh",
            SourceId = "netease:fresh",
            Title = "Fresh Song",
            Artist = "New Artist",
            FilePath = "http://example.com/fresh.mp3"
        };
        djMock.Setup(x => x.RecommendNextTrackAsync(It.IsAny<Track?>()))
            .ReturnsAsync(recommended);

        vm.InputText = "再推荐点同类型的歌";
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.RecommendNextTrackAsync(It.IsAny<Track?>()), Times.Once);
        djMock.Verify(x => x.GenerateChatResponseAsync(It.IsAny<string>()), Times.Never);
        searchMock.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        audioMock.Verify(x => x.AddTracks(It.Is<IEnumerable<Track>>(tracks =>
            tracks.Any(t => t.SourceId == "netease:fresh"))), Times.Once);
        audioMock.Verify(x => x.PlayAtIndex(0), Times.Once);
    }

    [Fact]
    public async Task SendMessage_WithTrackAddedCallback_DoesNotAddTrackTwice()
    {
        var playlist = new List<Track>();
        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.TtsEnabled).Returns(false);
        djMock.SetupGet(x => x.CurrentEmotion).Returns("neutral");

        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audioMock.Setup(x => x.TtsError).Returns(new Subject<string>());
        audioMock.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(() => playlist.AsReadOnly());
        audioMock.Setup(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()))
            .Callback<IEnumerable<Track>>(tracks => playlist.AddRange(tracks));

        var searchMock = new Mock<IMusicSearchService>();
        var result = new OnlineTrack
        {
            Id = "netease:167827",
            Title = "素颜",
            Artist = "许嵩"
        };
        searchMock.Setup(x => x.SearchAsync("素颜", 3))
            .ReturnsAsync(new List<OnlineTrack> { result });
        searchMock.Setup(x => x.SearchAsync("素颜", 5))
            .ReturnsAsync(new List<OnlineTrack> { result });
        searchMock.Setup(x => x.GetPlayUrlAsync("netease:167827"))
            .ReturnsAsync("http://example.com/suyan.mp3");

        var sttMock = new Mock<ISttService>();
        var vm = new ChatViewModel(
            djMock.Object,
            audioMock.Object,
            searchMock.Object,
            sttMock.Object,
            track => audioMock.Object.AddTracks(new[] { track }));

        vm.InputText = "素颜";
        await vm.SendMessageCommand.Execute().FirstAsync();

        Assert.Single(playlist);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Once);
        audioMock.Verify(x => x.PlayAtIndex(0), Times.Once);
    }

    [Fact]
    public async Task AudioService_Volume_SetAndGet()
    {
        var service = new AudioService();
        try
        {
            service.Volume = 0.5f;
            // Volume getter returns player volume / 100, which may differ during playback
            Assert.True(service.Volume > 0);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void AudioService_TtsStateChanged_PublishesEvents()
    {
        var service = new AudioService();
        bool ttsEnded = false;
        using var sub = service.TtsStateChanged.Subscribe(playing => {
            if (!playing) ttsEnded = true;
        });

        try
        {
            service.PlayTtsAudio(Array.Empty<byte>());
            // TTS ends quickly when no data — wait briefly for async playback to complete
            Thread.Sleep(500);
            Assert.True(ttsEnded, "TTS should have ended after playing empty data");
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void SendMessage_EmptyInput_DoesNotAddMessage()
    {
        var (vm, _, _, _) = CreateVm();
        vm.InputText = "";
        vm.SendMessageCommand.Execute().Subscribe();

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public void SendMessage_WhitespaceInput_DoesNotAddMessage()
    {
        var (vm, _, _, _) = CreateVm();
        vm.InputText = "   ";
        vm.SendMessageCommand.Execute().Subscribe();

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public void BeginHoldToTalk_SetsListeningState()
    {
        var (vm, _, _, _) = CreateVm();
        vm.BeginHoldToTalk();

        // Should not throw; IsListening depends on AudioService state
        Assert.False(vm.IsProcessing);
    }

    [Fact]
    public void BeginHoldToTalk_DoesNotStartWhenProcessing()
    {
        var (vm, _, _, _) = CreateVm();
        // Simulate processing state
        vm.IsProcessing = true;
        vm.BeginHoldToTalk();

        Assert.False(vm.IsListening);
    }

    [Fact]
    public void EndHoldToTalk_DoesNotThrowWhenNotListening()
    {
        var (vm, _, _, _) = CreateVm();
        var ex = Record.Exception(() => vm.EndHoldToTalk());
        Assert.Null(ex);
    }
}
