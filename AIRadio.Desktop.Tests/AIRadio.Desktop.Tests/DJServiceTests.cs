using System;
using System.Collections.Generic;
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
}
