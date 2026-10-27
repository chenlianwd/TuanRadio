// TODO: Additional edge case tests needed (L23):
// - Very long input (>100 chars)
// - Mixed Chinese+English input
// - Input with special characters
// - "播放音乐" (generic music request)
// - "推荐一首歌" (recommendation request)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
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
        CreateVm(Mock<IListeningProfileService>? profileMock = null)
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
        sttMock.Setup(x => x.TranscribeAsync(It.IsAny<string>()))
            .ReturnsAsync("测试语音");

        var vm = new ChatViewModel(djMock.Object, audioMock.Object, searchMock.Object, sttMock.Object,
            listeningProfile: profileMock?.Object);
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
    public void AddAssistantMessage_StampsCurrentDjNameAsSender()
    {
        var (vm, djMock, _, _) = CreateVm();
        djMock.SetupGet(x => x.DjName).Returns("Lumen");

        vm.AddAssistantMessage("晚上好，欢迎回到电台。");

        var msg = vm.Messages.Last();
        Assert.Equal(MessageRole.Assistant, msg.Role);
        Assert.Equal("Lumen", msg.AuthorName);
        Assert.Equal("Lumen", msg.SenderName);
    }

    [Fact]
    public void AddMessage_DjNameUnavailable_FallsBackToGenericLabel()
    {
        // DjName 未设置（Moq 宽松 mock 返回 null）时不署名，气泡回退通用标签
        var (vm, _, _, _) = CreateVm();

        vm.AddAssistantMessage("兜底路径消息");

        var msg = vm.Messages.Last();
        Assert.Equal(string.Empty, msg.AuthorName);
        Assert.Equal("AI 主播", msg.SenderName);
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
    public void TtsPlaying_DoesNotShowWorkingNotice()
    {
        // 过程态（TTS 播报中）不再弹通知：反馈由品牌头徽章辉光与底部状态栏 SPEAKING 承担。
        // 直驱 RefreshStatus：TtsStateChanged 订阅经 ObserveOn(MainThreadScheduler) 派发
        // 在无 dispatcher 的纯 Fact 下不定时
        var (vm, _) = CreateVmWithTtsErrors();

        vm.IsSpeaking = true;
        vm.RefreshStatus();

        Assert.False(vm.ShowStatusNotice);
        Assert.False(vm.ShowStatusRecall);
    }

    [Fact]
    public void NewRoundAfterFailure_OldFailureDoesNotResurface()
    {
        // 失败通知显示后进入新一轮播报：旧失败翻篇，播报结束后不得经首个分支复活
        var (vm, _) = CreateVmWithTtsErrors();

        vm.HasFailure = true;
        vm.RefreshStatus();
        Assert.True(vm.ShowStatusNotice);

        vm.IsSpeaking = true;
        vm.RefreshStatus();
        vm.IsSpeaking = false;
        vm.RefreshStatus();

        Assert.False(vm.ShowStatusNotice);
        Assert.False(vm.ShowStatusRecall);
    }

    [Fact]
    public void ParseResponse_StripsEmotionTags()
    {
        // 旧文本尾标已移除：情绪标签照常剥离，【next】不再是指令、按普通文本保留
        var response = "今天天气真好呢[happy]【next】";
        var parsed = ChatViewModel.ParseDjResponse(response);

        Assert.DoesNotContain("[happy]", parsed.DisplayText);
        Assert.Equal("happy", parsed.Emotion);
        Assert.Null(parsed.Command);
        Assert.Contains("【next】", parsed.DisplayText);
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

        djMock.Verify(x => x.GenerateChatResponseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // 点歌 = 插播单曲：PlayOneShot 直接收到解析后的曲目，不再入主歌单
        audioMock.Verify(x => x.PlayOneShot(
            It.Is<Track>(t => t.Title == "丑八怪" && t.SourceId == "netease:ugly"),
            It.IsAny<string?>()), Times.Once);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_ChatPath_FeedsListenerChatProfileOnceWithRawText()
    {
        // 性格画像喂料：用户消息原文单点入库（消毒截断在服务端），归纳触发由服务内部把关
        var profileMock = new Mock<IListeningProfileService>();
        profileMock.Setup(x => x.RefreshImpressionAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var (vm, djMock, _, _) = CreateVm(profileMock);
        djMock.Setup(x => x.GenerateChatResponseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("好的[happy]");

        vm.InputText = "放点轻松的";
        await vm.SendMessageCommand.Execute().FirstAsync();

        profileMock.Verify(x => x.RecordListenerChat("放点轻松的"), Times.Once);
        profileMock.Verify(x => x.RefreshImpressionAsync(It.IsAny<CancellationToken>(), false), Times.Once);
    }

    [Fact]
    public async Task SendMessage_SongRequestShortcut_AlsoFeedsListenerChatProfile()
    {
        // 点歌短路路径同样喂料：「来点周杰伦的歌」是最强的口味/性格信号，
        // 只喂 LLM 聊天分支会让印象系统性偏向非音乐话题
        var profileMock = new Mock<IListeningProfileService>();
        profileMock.Setup(x => x.RefreshImpressionAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var (vm, djMock, audioMock, searchMock) = CreateVm(profileMock);
        searchMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());
        searchMock.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync("http://example.com/s.mp3");

        vm.InputText = "放周杰伦的晴天";
        await vm.SendMessageCommand.Execute().FirstAsync();

        profileMock.Verify(x => x.RecordListenerChat("放周杰伦的晴天"), Times.Once);
    }

    [Fact]
    public async Task SendMessage_ArtistSongRequest_SearchesByArtist()
    {
        var (vm, djMock, audioMock, searchMock) = CreateVm();
        var unrelatedResult = new OnlineTrack
        {
            Id = "netease:unrelated",
            Title = "李白",
            Artist = "李荣"
        };
        var artistResult = new OnlineTrack
        {
            Id = "netease:li-ronghao",
            Title = "模特",
            Artist = "李荣浩"
        };
        searchMock.Setup(x => x.SearchAsync("李荣浩", 5))
            .ReturnsAsync(new List<OnlineTrack> { unrelatedResult, artistResult });
        searchMock.Setup(x => x.GetPlayUrlAsync("netease:li-ronghao"))
            .ReturnsAsync("http://example.com/model.mp3");

        vm.InputText = "播放李荣浩的歌";
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.GenerateChatResponseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        searchMock.Verify(x => x.SearchAsync("李荣浩", 5), Times.Once);
        searchMock.Verify(x => x.SearchAsync("李荣浩的歌", 5), Times.Never);
        audioMock.Verify(x => x.PlayOneShot(
            It.Is<Track>(t => t.Title == "模特" && t.Artist == "李荣浩"),
            It.IsAny<string?>()), Times.Once);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
        Assert.Contains(vm.Messages, message => message.Content == "好，我来找李荣浩的歌。");
    }

    [Fact]
    public async Task SendMessage_BareSongTitleMatchingArtistName_KeepsTitleMatch()
    {
        var (vm, djMock, audioMock, searchMock) = CreateVm();
        var titleResult = new OnlineTrack
        {
            Id = "netease:title-match",
            Title = "王菲",
            Artist = "演唱者"
        };
        var artistResult = new OnlineTrack
        {
            Id = "netease:artist-match",
            Title = "红豆",
            Artist = "王菲"
        };
        searchMock.Setup(x => x.SearchAsync("王菲", 3))
            .ReturnsAsync(new List<OnlineTrack> { titleResult, artistResult });
        searchMock.Setup(x => x.SearchAsync("王菲", 5))
            .ReturnsAsync(new List<OnlineTrack> { titleResult, artistResult });
        searchMock.Setup(x => x.GetPlayUrlAsync("netease:title-match"))
            .ReturnsAsync("http://example.com/title-match.mp3");

        vm.InputText = "王菲";
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.GenerateChatResponseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        searchMock.Verify(x => x.GetPlayUrlAsync("netease:title-match"), Times.Once);
        searchMock.Verify(x => x.GetPlayUrlAsync("netease:artist-match"), Times.Never);
        audioMock.Verify(x => x.PlayOneShot(
            It.Is<Track>(t => t.Title == "王菲" && t.Artist == "演唱者"),
            It.IsAny<string?>()), Times.Once);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_ShortChatWithoutConfidentSongMatch_UsesDjChat()
    {
        var (vm, djMock, audioMock, searchMock) = CreateVm();
        searchMock.Setup(x => x.SearchAsync("好累", 3))
            .ReturnsAsync(new List<OnlineTrack>());
        djMock.Setup(x => x.GenerateChatResponseAsync("好累", It.IsAny<CancellationToken>()))
            .ReturnsAsync("听起来有点累，先缓一缓。");

        vm.InputText = "好累";
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.GenerateChatResponseAsync("好累", It.IsAny<CancellationToken>()), Times.Once);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
        audioMock.Verify(x => x.PlayAtIndex(It.IsAny<int>()), Times.Never);
        audioMock.Verify(x => x.PlayOneShot(It.IsAny<Track>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_GreetingWithExactSameNamedSong_DoesNotHijackToPlay()
    {
        // 2026-10-27 实测回归："在吗"被裸歌名路径当歌名（乐库恰有《在吗》-空匪，精确命中），
        // 打招呼变成切歌；问候/疑问句式必须落回聊天，且不再吃乐库预检
        var (vm, djMock, audioMock, searchMock) = CreateVm();
        var sameNamed = new OnlineTrack { Id = "netease:zaima", Title = "在吗", Artist = "空匪" };
        searchMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack> { sameNamed });
        djMock.Setup(x => x.GenerateChatResponseAsync("在吗", It.IsAny<CancellationToken>()))
            .ReturnsAsync("在的，我在。[happy]");

        vm.InputText = "在吗";
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.GenerateChatResponseAsync("在吗", It.IsAny<CancellationToken>()), Times.Once);
        searchMock.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        audioMock.Verify(x => x.PlayOneShot(It.IsAny<Track>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_ExplicitBookTitle_SkipsChitChatGuards()
    {
        // 书名号逃生门：《晚安》与问候语撞名，裸打会被闲聊守卫拦下；
        // 整段书名号包裹是显式歌名信号，跳过守卫直走秒播预检
        var (vm, djMock, audioMock, searchMock) = CreateVm();
        var track = new OnlineTrack { Id = "netease:wanan", Title = "晚安", Artist = "颜人中" };
        searchMock.Setup(x => x.SearchAsync("晚安", 3))
            .ReturnsAsync(new List<OnlineTrack> { track });
        searchMock.Setup(x => x.SearchAsync("晚安", 5))
            .ReturnsAsync(new List<OnlineTrack> { track });
        searchMock.Setup(x => x.GetPlayUrlAsync("netease:wanan"))
            .ReturnsAsync("http://example.com/wanan.mp3");

        vm.InputText = "《晚安》";
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.GenerateChatResponseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        audioMock.Verify(x => x.PlayOneShot(
            It.Is<Track>(t => t.Title == "晚安" && t.SourceId == "netease:wanan"),
            It.IsAny<string?>()), Times.Once);
    }

    [Theory]
    [InlineData("在吗")]
    [InlineData("早")]
    [InlineData("今天的天气咋样啊")]
    [InlineData("今天天气怎么样")]
    [InlineData("晚上好")]
    [InlineData("hi there")]
    [InlineData("晴天吧")]
    public async Task SendMessage_ChitChatPhrasing_GoesToChatWithoutSongPreflight(string input)
    {
        // 问候/疑问/句末语气助词的闲聊句式：不进裸歌名路径（顺带省一次乐库预检请求）；
        // 真歌名裸打（如"丑八怪"）仍走秒播捷径——由既有用例锚定
        var (vm, djMock, _, searchMock) = CreateVm();
        djMock.Setup(x => x.GenerateChatResponseAsync(input, It.IsAny<CancellationToken>()))
            .ReturnsAsync("嗯。[happy]");

        vm.InputText = input;
        await vm.SendMessageCommand.Execute().FirstAsync();

        djMock.Verify(x => x.GenerateChatResponseAsync(input, It.IsAny<CancellationToken>()), Times.Once);
        searchMock.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
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
        djMock.Verify(x => x.GenerateChatResponseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        searchMock.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        audioMock.Verify(x => x.PlayOneShot(
            It.Is<Track>(t => t.SourceId == "netease:fresh"),
            It.IsAny<string?>()), Times.Once);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_GenericRecommendation_PassesSharedPlaybackHistoryToProgramService()
    {
        var current = new Track { Id = "current", Title = "Yellow", Artist = "Coldplay" };
        var history = new[]
        {
            new Track { Id = "history-1", Title = "Creep", Artist = "Radiohead" },
            new Track { Id = "history-2", Title = "Wonderwall", Artist = "Oasis" }
        };
        var recommended = new Track
        {
            Id = "fresh",
            SourceId = "netease:fresh",
            Title = "Fresh Song",
            Artist = "New Artist",
            FilePath = "http://example.com/fresh.mp3"
        };
        var playlist = new List<Track> { current };
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(x => x.CurrentTrack).Returns(current);
        audio.Setup(x => x.Playlist).Returns(() => playlist.AsReadOnly());
        audio.Setup(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()))
            .Callback<IEnumerable<Track>>(tracks => playlist.AddRange(tracks));
        var dj = new Mock<IDJService>();
        dj.SetupGet(x => x.TtsEnabled).Returns(false);
        dj.SetupGet(x => x.CurrentEmotion).Returns("neutral");
        var recommendations = new Mock<IRecommendationService>();
        recommendations.SetupGet(x => x.RecentlyPlayed).Returns(history);
        RecommendationRequest? capturedRequest = null;
        recommendations.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .Callback<RecommendationRequest>(request => capturedRequest = request)
            .ReturnsAsync(recommended);
        var vm = new ChatViewModel(
            dj.Object,
            audio.Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            recommendationService: recommendations.Object);

        try
        {
            vm.InputText = "再推荐点同类型的歌";
            await vm.SendMessageCommand.Execute().FirstAsync();

            Assert.NotNull(capturedRequest);
            Assert.Equal(history.Select(track => track.Id), capturedRequest!.RecentlyPlayed.Select(track => track.Id));
            var context = Assert.IsType<RecommendationContext>(current.Tag);
            Assert.Equal(history.Select(track => track.Id), context.RecentlyPlayed.Select(track => track.Id));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    // Note: uses real AudioService (requires LibVLC native libs). Consider mocking for CI reliability (M44).
    public async Task AudioService_Volume_SetAndGet()
    {
        var service = new AudioService();
        try
        {
            service.Volume = 0.5f;
            // 未播放时 Volume getter 直接返回用户音量缓存，可精确回读
            Assert.Equal(0.5, service.Volume, precision: 2);
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
            // TTS 空数据经 MediaFoundationReader 失败→结束链路异步完成：事件驱动轮询替代
            // 固定等待，慢机器/无声卡环境不会因 500ms 不够而误报
            var deadline = Environment.TickCount64 + 5000;
            while (!ttsEnded && Environment.TickCount64 < deadline)
                Thread.Sleep(20);
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
        // 注入假录音设备：headless 测试不得打开真实麦克风
        vm.RecordingDeviceFactory = () => new FakeWaveIn();
        try
        {
            Assert.True(vm.BeginHoldToTalk());
            Assert.True(vm.IsListening);
        }
        finally
        {
            vm.Dispose();
        }
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

    [Fact]
    public void HasFailure_DefaultsFalse_AndIsReactive()
    {
        var (vm, _, _, _) = CreateVm();
        Assert.False(vm.HasFailure);
        vm.HasFailure = true;
        Assert.True(vm.HasFailure);
    }

    [Fact]
    public void BeginHoldToTalk_WhenBusy_ReturnsFalseWithoutStarting()
    {
        // 按住被拒必须可判定：静默拒绝会让调用方照常进入按压视觉，用户误以为录音已开始
        var (vm, _, _, _) = CreateVm();

        vm.IsProcessing = true;
        Assert.False(vm.BeginHoldToTalk());
        Assert.False(vm.IsListening);

        vm.IsProcessing = false;
        vm.IsRecognizing = true;
        Assert.False(vm.BeginHoldToTalk());
        Assert.False(vm.IsListening);
    }

    [Theory]
    [InlineData("我们聊点别的", false)]          // 弱意图词单独出现：日常聊天，不得劫持为换歌
    [InlineData("有类似的软件吗", false)]
    [InlineData("新歌这个词我不太懂", false)]
    [InlineData("放一首歌", false)]              // 显式播放意图走歌曲解析分支
    [InlineData("推荐一首歌", true)]
    [InlineData("有什么推荐", true)]             // 强意图词单独出现仍视为推荐（既有语义）
    [InlineData("换一首", true)]
    [InlineData("来首", true)]
    [InlineData("来点别的歌", true)]             // 弱意图词 + 歌曲名词 → 推荐
    [InlineData("有没有类似的音乐", true)]
    public void IsFreshRecommendationRequest_DoesNotHijackDailyChat(string text, bool expected)
        => Assert.Equal(expected, ChatViewModel.IsFreshRecommendationRequest(text));
}
