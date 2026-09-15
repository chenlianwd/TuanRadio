using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class DJServiceTests
{
    private readonly Mock<ILLMService> _mockLlm;
    private readonly DJService _djService;

    public DJServiceTests()
    {
        _mockLlm = new Mock<ILLMService>();
        _djService = new DJService(_mockLlm.Object);
    }

    [Fact]
    public void Initialize_WithChineseProfile_SetsChinesePrompt()
    {
        var profile = new DJProfile
        {
            Name = "小音",
            Description = "活泼开朗",
            VoiceId = "female-shaonv",
            Language = "zh"
        };

        _djService.Initialize(profile);

        Assert.Equal("neutral", _djService.CurrentEmotion);
        Assert.True(_djService.TtsEnabled);
    }

    [Fact]
    public void Initialize_WithEnglishProfile_SetsEnglishPrompt()
    {
        var profile = new DJProfile
        {
            Name = "DJ Alex",
            Description = "Fun radio host",
            VoiceId = "male-qn-qingse",
            Language = "en"
        };

        _djService.Initialize(profile);

        Assert.Equal("neutral", _djService.CurrentEmotion);
        Assert.True(_djService.TtsEnabled);
    }

    [Fact]
    public void Initialize_WithCustomSystemPrompt_UsesCustomPrompt()
    {
        var profile = new DJProfile
        {
            Name = "Test",
            Description = "Test DJ",
            SystemPrompt = "你是一个测试主播",
            Language = "zh"
        };

        _djService.Initialize(profile);

        Assert.Equal("neutral", _djService.CurrentEmotion);
    }

    [Fact]
    public void Initialize_SetsTtsEnabled_FromProfile()
    {
        var profileWithTts = new DJProfile { TtsEnabled = true };
        var profileWithoutTts = new DJProfile { TtsEnabled = false };

        _djService.Initialize(profileWithTts);
        Assert.True(_djService.TtsEnabled);

        _djService.Initialize(profileWithoutTts);
        Assert.False(_djService.TtsEnabled);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_ReturnsDJScript()
    {
        var current = new Track { Title = "歌曲A", Artist = "歌手A" };
        var next = new Track { Title = "歌曲B", Artist = "歌手B" };

        _mockLlm
            .Setup(m => m.GenerateTrackIntroductionAsync(current, next))
            .ReturnsAsync("即将播放歌曲B，太好听了！[happy]");

        var result = await _djService.GenerateTrackIntroductionAsync(current, next);

        Assert.NotNull(result);
        Assert.Equal("happy", result.Emotion);
        Assert.NotEmpty(result.Expression);
        Assert.NotEmpty(result.Motion);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_FallbackOnException()
    {
        var current = new Track { Title = "歌曲A", Artist = "歌手A" };
        var next = new Track { Title = "歌曲B", Artist = "歌手B" };

        _mockLlm
            .Setup(m => m.GenerateTrackIntroductionAsync(current, next))
            .ThrowsAsync(new Exception("API error"));

        var result = await _djService.GenerateTrackIntroductionAsync(current, next);

        Assert.NotNull(result);
        Assert.Contains("歌曲B", result.Text);
        Assert.Equal("happy", result.Emotion);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_FallbackWhenLlmTruncatesTitle()
    {
        // 复现线上缺陷：模型在字数上限压力下于歌名中间停笔，串场词只写到《雨
        var current = new Track { Title = "告白气球", Artist = "周杰伦" };
        var next = new Track { Title = "雨爱", Artist = "杨丞琳" };

        _mockLlm
            .Setup(m => m.GenerateTrackIntroductionAsync(current, next))
            .ReturnsAsync("气球载着甜蜜飘远了，接下来，让杨丞琳的《雨");

        var result = await _djService.GenerateTrackIntroductionAsync(current, next);

        Assert.NotNull(result);
        Assert.Contains("《雨爱》", result.Text);
        Assert.Equal("happy", result.Emotion);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_KeepsLlmTextWhenTitleComplete()
    {
        var current = new Track { Title = "告白气球", Artist = "周杰伦" };
        var next = new Track { Title = "雨爱", Artist = "杨丞琳" };

        _mockLlm
            .Setup(m => m.GenerateTrackIntroductionAsync(current, next))
            .ReturnsAsync("气球载着甜蜜飘远了，接下来，让杨丞琳的《雨爱》[calm]");

        var result = await _djService.GenerateTrackIntroductionAsync(current, next);

        Assert.Equal("气球载着甜蜜飘远了，接下来，让杨丞琳的《雨爱》", result.Text);
        Assert.Equal("calm", result.Emotion);
    }

    [Fact]
    public async Task GenerateChatResponseAsync_UpdatesEmotion()
    {
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("这首歌太棒了！[excited]");

        var response = await _djService.GenerateChatResponseAsync("放首歌");

        Assert.Contains("太棒了", response);
    }

    [Fact]
    public async Task GenerateChatResponseAsync_FallbackOnException()
    {
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ThrowsAsync(new Exception("Network error"));

        var response = await _djService.GenerateChatResponseAsync("你好");

        Assert.NotEmpty(response);
    }

    [Fact]
    public async Task GenerateSpeechAsync_ReturnsAudioBytes()
    {
        var mockTts = new Mock<ITtsService>();
        byte[] fakeAudio = new byte[] { 0x1, 0x2, 0x3 };
        mockTts
            .Setup(m => m.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(fakeAudio);

        var service = new DJService(_mockLlm.Object, mockTts.Object);
        var result = await service.GenerateSpeechAsync("测试语音");

        Assert.NotNull(result);
        Assert.Equal(3, result.Length);
    }

    [Fact]
    public async Task GenerateSpeechAsync_ReturnsNullOnEmptyText()
    {
        var result = await _djService.GenerateSpeechAsync("");
        Assert.Null(result);

        result = await _djService.GenerateSpeechAsync(null!);
        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateSpeechAsync_ReturnsNullOnException()
    {
        var mockTts = new Mock<ITtsService>();
        mockTts
            .Setup(m => m.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("TTS failed"));

        var service = new DJService(_mockLlm.Object, mockTts.Object);
        var result = await service.GenerateSpeechAsync("测试");

        Assert.Null(result);
    }

    [Fact]
    public async Task CorrectTranscriptionAsync_PreCancelledToken_Throws()
    {
        var llm = new Mock<ILLMService>();
        var service = new DJService(llm.Object, null, null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 应用关闭期间不得把取消吞成"纠错失败"，否则后台流程继续空转
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CorrectTranscriptionAsync("拿手青音樂", cts.Token));
    }

    [Fact]
    public async Task CorrectTranscriptionAsync_EmptyReply_ReturnsOriginalTranscript()
    {
        var llm = new Mock<ILLMService>();
        var service = new DJService(llm.Object, null, null);
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("   ");

        var result = await service.CorrectTranscriptionAsync("来点轻音乐", CancellationToken.None);

        Assert.Equal("来点轻音乐", result);
    }

    [Theory]
    [InlineData("来点轻音乐", "来点轻音乐")]                       // 正常纠错透传
    [InlineData("修正后：来点轻音乐", "来点轻音乐")]               // 标签前缀
    [InlineData("“来点轻音乐”", "来点轻音乐")]         // 引号包裹
    [InlineData("来点轻音乐\n（注：仅修正错别字）", "来点轻音乐")] // 后随解释行
    [InlineData("", null)]                                          // 空回复 → 放弃修正
    [InlineData("根据识别结果分析，用户的原话可能是想要听一些轻音乐来放松心情", null)]  // 异常膨胀（31字 > 原文5+20） → 放弃修正
    public void NormalizeCorrectedTranscript_StripsDecoration(string reply, string? expected)
    {
        Assert.Equal(expected, DJService.NormalizeCorrectedTranscript(reply, "来点轻音乐"));
    }

    [Fact]
    public async Task CorrectTranscriptionAsync_PassesThroughLlmCorrection()
    {
        var llm = new Mock<ILLMService>();
        var service = new DJService(llm.Object, null, null);
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("来点轻音乐");

        var result = await service.CorrectTranscriptionAsync("拿手青音樂", CancellationToken.None);

        Assert.Equal("来点轻音乐", result);
    }

    [Fact]
    public async Task CorrectTranscriptionAsync_LlmFailure_ReturnsOriginalTranscript()
    {
        var llm = new Mock<ILLMService>();
        var service = new DJService(llm.Object, null, null);
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        var result = await service.CorrectTranscriptionAsync("拿手青音樂", CancellationToken.None);

        // 纠错是体验增强：失败必须回退原文，不能阻塞语音流程
        Assert.Equal("拿手青音樂", result);
    }

    [Fact]
    public async Task RecommendNextTrackAsync_SkipsTracksAlreadyInPlaylist()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new DJService(llm.Object, null, search.Object);
        var existing = new Track
        {
            Title = "Existing Song",
            Artist = "Known Artist",
            SourceId = "netease:old",
            FilePath = "http://example.com/old.mp3"
        };
        var current = new Track
        {
            Title = "Current Song",
            Artist = "Known Artist",
            SourceId = "netease:current",
            Tag = new RecommendationContext
            {
                ExcludedTracks = new[] { existing }
            }
        };

        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("Existing Song - Known Artist");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), 10))
            .ReturnsAsync(new List<OnlineTrack>
            {
                new() { Id = "netease:old", Title = "Existing Song", Artist = "Known Artist" },
                new() { Id = "netease:new", Title = "Fresh Song", Artist = "New Artist" }
            });
        search.Setup(x => x.GetPlayUrlAsync("netease:new"))
            .ReturnsAsync("http://example.com/new.mp3");

        var result = await service.RecommendNextTrackAsync(current);

        Assert.NotNull(result);
        Assert.Equal("netease:new", result!.SourceId);
        search.Verify(x => x.GetPlayUrlAsync("netease:old"), Times.Never);
    }

    [Fact]
    public async Task GenerateSongStoryAsync_UnconfiguredLlm_ReturnsEmptyStory()
    {
        // 未配置的 LLMService.ChatAsync 返回“请先在设置中配置 AI 服务。”提示文案，
        // 故事路径必须直接返回空故事，不能把提示文案当故事朗读
        var service = new DJService(new LLMService(new HttpClient()));

        var story = await service.GenerateSongStoryAsync(
            new Track { Title = "晴天", Artist = "周杰伦" }, CancellationToken.None);

        Assert.Empty(story.Lines);
        Assert.Equal("晴天", story.Title);
    }

    [Fact]
    public async Task GenerateChatResponseAsync_AccumulatesHistory()
    {
        _mockLlm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("response1");

        await _djService.GenerateChatResponseAsync("hello");

        _mockLlm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("response2");

        await _djService.GenerateChatResponseAsync("world");

        // Verify history was passed (2 calls, second call should have 2 messages in history)
        _mockLlm.Verify(x => x.ChatAsync(
            "world",
            It.Is<List<ChatMessage>>(h => h.Count >= 2)), Times.Once);
    }

    [Fact]
    public async Task GenerateChatResponseAsync_TrimsHistoryInUserAssistantPairs()
    {
        // 回归：Anthropic 要求首条非 system 消息必须是 user，裁剪必须按 user/assistant 成对删除，
        // 逐条删除会让历史以 assistant 开头，长对话后请求被 400 拒绝
        _djService.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });

        var captured = new List<List<ChatMessage>>();
        _mockLlm
            .Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("回复")
            .Callback<string, List<ChatMessage>>((_, history) => captured.Add(history.ToList()));

        for (var i = 0; i < 15; i++)
            await _djService.GenerateChatResponseAsync($"消息{i}");

        var last = captured[^1];
        Assert.Equal(21, last.Count); // system + 上限 20 条
        Assert.Equal(MessageRole.System, last[0].Role);
        Assert.Equal(MessageRole.User, last[1].Role);
        for (var i = 1; i < last.Count; i++)
            Assert.Equal(i % 2 == 1 ? MessageRole.User : MessageRole.Assistant, last[i].Role);
    }

    // ---------- 聊天注入长期收听画像（docs/plans/2026-09-15-dj-chat-profile-injection-design.md） ----------

    private sealed class FakeListeningProfile : IListeningProfileService
    {
        public ListenerProfileSnapshot Snapshot { get; set; } = ListenerProfileSnapshot.Empty;
        public bool Enabled { get; set; } = true;

        public ListenerProfileSnapshot GetSnapshot()
            => Enabled ? Snapshot : ListenerProfileSnapshot.Empty;

        public void RecordEvent(ListeningEventData eventData) { }
        public void NotifyPlaybackStarted(Track track) { }
        public void NotifyPlaybackEndedNaturally(Track track) { }
        public void NotifyPositionSampled(Track track, TimeSpan position) { }
        public void NotifyTrackSwitched(Track? next) { }
        public void NotifyPlaybackPaused() { }
        public void Reset() { }
        public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshDigestAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ListenerProfileSnapshot BuildThresholdSnapshot(string digestText) => new()
    {
        TotalEventsIngested = ListeningProfileService.InjectionMinEvents,
        TopArtists = new[]
        {
            new ArtistAffinity { Artist = "周杰伦", Score = 5, PlayCount = 10 },
            new ArtistAffinity { Artist = "陈绮贞", Score = 3, PlayCount = 6 },
            new ArtistAffinity { Artist = "五月天", Score = 2, PlayCount = 4 },
        },
        AvoidArtists = new[] { new ArtistAffinity { Artist = "差评歌手", Score = -3 } },
        MoodUsage = new Dictionary<string, int> { ["calm"] = 5, ["energetic"] = 2 },
        DislikeBlacklist = new[] { new ProfileMusicRef("不爱听", "某人") },
        Digest = new TasteDigestData { Text = digestText, Language = "zh" },
    };

    private static List<List<ChatMessage>> SetupHistoryCapture(Mock<ILLMService> mock)
    {
        var captured = new List<List<ChatMessage>>();
        mock.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("回复[calm]")
            .Callback<string, List<ChatMessage>>((_, history) => captured.Add(history.ToList()));
        return captured;
    }

    [Fact]
    public async Task ChatProfileInjection_AddsTasteAndBlacklistToSystemSnapshot()
    {
        var mock = new Mock<ILLMService>();
        var captured = SetupHistoryCapture(mock);
        var profile = new FakeListeningProfile { Snapshot = BuildThresholdSnapshot("偏爱华语流行与 City Pop") };
        var dj = new DJService(mock.Object, profile: profile);
        dj.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });

        await dj.GenerateChatResponseAsync("来点歌");

        var system = captured[^1][0];
        Assert.Equal(MessageRole.System, system.Role);
        Assert.Contains("测试人设", system.Content);
        Assert.Contains("偏爱华语流行与 City Pop", system.Content);
        Assert.Contains("周杰伦", system.Content);
        Assert.Contains("差评歌手", system.Content);
        Assert.Contains("calm", system.Content);
        Assert.Contains("不爱听 - 某人", system.Content);
    }

    [Fact]
    public async Task ChatProfileInjection_DoesNotPersistAcrossCalls()
    {
        var mock = new Mock<ILLMService>();
        var captured = SetupHistoryCapture(mock);
        var profile = new FakeListeningProfile { Snapshot = BuildThresholdSnapshot("旧摘要") };
        var dj = new DJService(mock.Object, profile: profile);
        dj.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });

        await dj.GenerateChatResponseAsync("第一句");
        profile.Snapshot = BuildThresholdSnapshot("新摘要");
        await dj.GenerateChatResponseAsync("第二句");

        // 拼接只发生在快照副本：第二次调用只含新画像段，持久历史未落画像
        var secondSystem = captured[1][0].Content;
        Assert.DoesNotContain("旧摘要", secondSystem);
        Assert.Contains("新摘要", secondSystem);
        // Split 出 2 段 = 画像段标题恰好出现一次（无跨调用叠加）
        Assert.Equal(2, secondSystem.Split("听众长期口味").Length);
    }

    [Fact]
    public async Task ChatProfileInjection_NullDisabledOrColdStart_KeepsSystemIdentical()
    {
        var baselineMock = new Mock<ILLMService>();
        var baselineCapture = SetupHistoryCapture(baselineMock);
        var baseline = new DJService(baselineMock.Object);
        baseline.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });
        await baseline.GenerateChatResponseAsync("你好");
        var expected = baselineCapture[^1][0].Content;

        // Enabled=false：GetSnapshot 返回空 → 不注入
        var disabledMock = new Mock<ILLMService>();
        var disabledCapture = SetupHistoryCapture(disabledMock);
        var disabled = new DJService(disabledMock.Object,
            profile: new FakeListeningProfile { Enabled = false, Snapshot = BuildThresholdSnapshot("不该出现") });
        disabled.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });
        await disabled.GenerateChatResponseAsync("你好");
        Assert.Equal(expected, disabledCapture[^1][0].Content);

        // 冷启动（门槛未达且无黑名单）：不注入
        var coldMock = new Mock<ILLMService>();
        var coldCapture = SetupHistoryCapture(coldMock);
        var cold = new DJService(coldMock.Object,
            profile: new FakeListeningProfile
            {
                Snapshot = new ListenerProfileSnapshot
                {
                    TotalEventsIngested = 5,
                    TopArtists = new[] { new ArtistAffinity { Artist = "周杰伦", Score = 1 } },
                    Digest = new TasteDigestData { Text = "过早结论" },
                },
            });
        cold.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });
        await cold.GenerateChatResponseAsync("你好");
        Assert.Equal(expected, coldCapture[^1][0].Content);
    }

    [Fact]
    public async Task ChatProfileInjection_BlacklistWithoutThreshold_InjectsAvoidLineOnly()
    {
        var mock = new Mock<ILLMService>();
        var captured = SetupHistoryCapture(mock);
        var profile = new FakeListeningProfile
        {
            Snapshot = new ListenerProfileSnapshot
            {
                TotalEventsIngested = 3,
                TopArtists = new[] { new ArtistAffinity { Artist = "某人", Score = -3 } },
                DislikeBlacklist = new[] { new ProfileMusicRef("不爱听", "某人") },
            },
        };
        var dj = new DJService(mock.Object, profile: profile);
        dj.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });

        await dj.GenerateChatResponseAsync("来点歌");

        var content = captured[^1][0].Content;
        Assert.Contains("不要主动推荐这些歌", content);
        Assert.Contains("不爱听 - 某人", content);
        Assert.DoesNotContain("听众长期口味", content);
    }

    [Fact]
    public async Task ChatProfileInjection_FollowsProfileLanguage()
    {
        var mock = new Mock<ILLMService>();
        var captured = SetupHistoryCapture(mock);
        var profile = new FakeListeningProfile { Snapshot = BuildThresholdSnapshot("Taste summary text") };
        var dj = new DJService(mock.Object, profile: profile);
        dj.Initialize(new DJProfile { Name = "DJ Alex", SystemPrompt = "Test persona", Language = "en" });

        await dj.GenerateChatResponseAsync("play something");

        var content = captured[^1][0].Content;
        Assert.Contains("Listener long-term taste", content);
        Assert.Contains("Taste summary text", content);
        Assert.Contains("Never suggest these songs", content);
        Assert.DoesNotContain("听众长期口味", content);
    }

    [Fact]
    public async Task ChatProfileInjection_SurvivesHistoryTrimming()
    {
        var mock = new Mock<ILLMService>();
        var captured = SetupHistoryCapture(mock);
        var profile = new FakeListeningProfile { Snapshot = BuildThresholdSnapshot("稳定摘要") };
        var dj = new DJService(mock.Object, profile: profile);
        dj.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });

        for (var i = 0; i < 12; i++)
            await dj.GenerateChatResponseAsync($"消息{i}");

        var last = captured[^1];
        Assert.True(last.Count >= 21, $"history should be at cap, got {last.Count}");
        Assert.Contains("稳定摘要", last[0].Content);
        Assert.Contains("测试人设", last[0].Content);
    }

    [Fact]
    public async Task ChatProfileInjection_UninitializedEmptyHistory_DoesNotInjectOrThrow()
    {
        // Initialize 未调用（空历史）：即使画像达标也不注入，且不抛异常
        var mock = new Mock<ILLMService>();
        var captured = SetupHistoryCapture(mock);
        var profile = new FakeListeningProfile { Snapshot = BuildThresholdSnapshot("不该出现") };
        var dj = new DJService(mock.Object, profile: profile);

        var response = await dj.GenerateChatResponseAsync("你好");

        Assert.Equal("回复[calm]", response);
        var history = Assert.Single(captured);
        Assert.Empty(history);
    }

    [Fact]
    public async Task ChatProfileInjection_ReinitializeHasNoResidue()
    {
        var mock = new Mock<ILLMService>();
        var captured = SetupHistoryCapture(mock);
        var profile = new FakeListeningProfile { Snapshot = BuildThresholdSnapshot("摘要") };
        var dj = new DJService(mock.Object, profile: profile);
        dj.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });
        await dj.GenerateChatResponseAsync("第一句");

        // 角色切换：Initialize 重建全新 system，画像段只随当次调用注入一份
        dj.Initialize(new DJProfile { Name = "Lumen", SystemPrompt = "新人设", Language = "zh" });
        await dj.GenerateChatResponseAsync("第二句");

        var content = captured[^1][0].Content;
        Assert.StartsWith("新人设", content);
        Assert.DoesNotContain("测试人设", content);
        // Split 出 2 段 = 画像段标题恰好出现一次（重建后无旧段残留）
        Assert.Equal(2, content.Split("听众长期口味").Length);
    }
}
