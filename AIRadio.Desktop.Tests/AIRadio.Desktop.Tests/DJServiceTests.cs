using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using Moq.Protected;
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
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("即将播放歌曲B，太好听了！[happy]");

        var result = await _djService.GenerateTrackIntroductionAsync(current, next);

        Assert.NotNull(result);
        Assert.Equal("happy", result.Emotion);
        Assert.NotEmpty(result.Expression);
        Assert.NotEmpty(result.Motion);
        // 兜底台词与断言巧合重合（happy+含歌名），Times.Once 锚定 LLM 真被调用而非静默落兜底
        _mockLlm.Verify(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_FallbackOnException()
    {
        var current = new Track { Title = "歌曲A", Artist = "歌手A" };
        var next = new Track { Title = "歌曲B", Artist = "歌手B" };

        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
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
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
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
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("气球载着甜蜜飘远了，接下来，让杨丞琳的《雨爱》[calm]");

        var result = await _djService.GenerateTrackIntroductionAsync(current, next);

        Assert.Equal("气球载着甜蜜飘远了，接下来，让杨丞琳的《雨爱》", result.Text);
        Assert.Equal("calm", result.Emotion);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_NotConfiguredLlm_ReturnsFallback()
    {
        // 真实 LLMService 未 Configure：不发起请求直接回退模板串场（不走 mock 的 ChatAsync）
        var llm = new LLMService(new HttpClient());
        var service = new DJService(llm);
        var next = new Track { Title = "歌曲B", Artist = "歌手B" };

        var result = await service.GenerateTrackIntroductionAsync(null, next);

        Assert.NotNull(result);
        Assert.Contains("歌曲B", result.Text);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_ConfiguredLlm_SendsPromptWithoutSystemMessage()
    {
        // 锚定 dispatch：真实 LLMService 路径必须走 ChatRawAsync（无 system 消息）——
        // 误改回 ChatAsync 会经 BuildMessages 注入硬编码"小音"system 形成双人设，此测试即红
        string? body = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>(
                (request, _) => body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("""{"choices":[{"message":{"content":"夜深了，来听《晴天》。[calm]"}}]}""")
            });
        var llm = new LLMService(new HttpClient(handler.Object));
        llm.Configure(new LLMConfig { Provider = "openai", ApiKey = "k", Model = "m", BaseUrl = "http://localhost" });
        var service = new DJService(llm);
        service.Initialize(new DJProfile { Name = "Lumen", Description = "晨间 DJ", Language = "zh" });

        var script = await service.GenerateTrackIntroductionAsync(
            new Track { Title = "起风了", Artist = "买辣椒也用券" },
            new Track { Title = "晴天", Artist = "周杰伦" });

        Assert.Contains("晴天", script.Text);
        // 请求体 JSON 会把中文转义成 \uXXXX，须解析后断言；唯一消息必须是 user
        //（ChatAsync 路径会多出注入"小音"的 system 消息，消息数/role 即 dispatch 事实）
        Assert.NotNull(body);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        var content = messages[0].GetProperty("content").GetString();
        Assert.NotNull(content);
        Assert.Contains("Lumen", content);
        Assert.Contains("晴天", content);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_IncludesListenerProfileInPrompt()
    {
        // 锚定画像接线：达门槛快照的口味段必须进串场提示词（调用点漏传即红）
        var profileService = new Mock<IListeningProfileService>();
        profileService.Setup(p => p.GetSnapshot()).Returns(new ListenerProfileSnapshot
        {
            TotalEventsIngested = 30,
            TopArtists = new[]
            {
                new ArtistAffinity { Artist = "陈奕迅" },
                new ArtistAffinity { Artist = "五月天" },
                new ArtistAffinity { Artist = "孙燕姿" }
            },
            Digest = new TasteDigestData { Text = "偏爱华语流行慢歌" }
        });
        var mockLlm = new Mock<ILLMService>();
        string? capturedPrompt = null;
        mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => capturedPrompt = prompt)
            .ReturnsAsync("夜里的雨刚好，来听《晴天》。[calm]");
        var service = new DJService(mockLlm.Object, profile: profileService.Object);
        service.Initialize(new DJProfile { Name = "Lumen", Language = "zh" });

        await service.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });

        Assert.NotNull(capturedPrompt);
        Assert.Contains("常听歌手", capturedPrompt);
        Assert.Contains("偏爱华语流行慢歌", capturedPrompt);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_SendsPersonaAndContextToLlm()
    {
        // 串场提示词必须带人设、时间与曲目信息（不再是无上下文的一句话模板）
        _djService.Initialize(new DJProfile
        {
            Name = "Lumen",
            Description = "明亮轻快的晨间 DJ",
            SystemPrompt = "你是名叫 Lumen 的中文电台 DJ。",
            Language = "zh"
        });
        _djService.UpdateWeatherSnapshot(new WeatherInfo(WeatherKind.Rain, 18, "杭州"));
        string? capturedPrompt = null;
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => capturedPrompt = prompt)
            .ReturnsAsync("夜里的雨刚好，来听《晴天》。[calm]");

        await _djService.GenerateTrackIntroductionAsync(
            new Track { Title = "起风了", Artist = "买辣椒也用券" },
            new Track { Title = "晴天", Artist = "周杰伦" });

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Lumen", capturedPrompt);
        Assert.Contains("起风了", capturedPrompt);
        Assert.Contains("晴天", capturedPrompt);
        Assert.Contains("杭州", capturedPrompt);
        Assert.Contains("雨", capturedPrompt);
        Assert.Contains("18", capturedPrompt);
        Assert.Contains("60~120 字", capturedPrompt);
        Assert.Contains("外文歌名不要翻译", capturedPrompt);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_RecordsSuccessfulTransitionForNextPrompt()
    {
        // 串场短期记忆：成功生成的串场原文必须进入下一条串场的提示词（防复读的接线锚点）
        _djService.Initialize(new DJProfile { Name = "Lumen", Language = "zh" });
        var responses = new[] { "第一段日光斜斜的串场，来听《晴天》。[calm]", "第二段完全不同的串场，来听《七里香》。[happy]" };
        var prompts = new List<string>();
        var call = -1;
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => { call++; prompts.Add(prompt); })
            .Returns(() => Task.FromResult(responses[call]));

        await _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });
        await _djService.GenerateTrackIntroductionAsync(
            new Track { Title = "晴天", Artist = "周杰伦" },
            new Track { Title = "七里香", Artist = "周杰伦" });

        Assert.Equal(2, prompts.Count);
        Assert.Contains("第一段日光斜斜的串场", prompts[1]);
        Assert.Contains("不要重复它们的开场", prompts[1]);
        // 第一条串场时自己还没有历史可注入
        Assert.DoesNotContain("最近几次串场", prompts[0]);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_FallbackTemplateNotRecorded()
    {
        // 模板兜底不是 DJ 真实语气，进了记忆会让"避免重复"的基准失真（污染防复读基准）
        _djService.Initialize(new DJProfile { Name = "Lumen", Language = "zh" });
        _mockLlm.Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ThrowsAsync(new Exception("Network error"));

        var fallback = await _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });
        Assert.Contains("接下来为你带来", fallback.Text);

        string? nextPrompt = null;
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => nextPrompt = prompt)
            .ReturnsAsync("真正生成的串场，来听《晴天》。[calm]");

        await _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });

        Assert.NotNull(nextPrompt);
        Assert.DoesNotContain("接下来为你带来", nextPrompt);
        Assert.DoesNotContain("最近几次串场", nextPrompt);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_TransitionMemoryCappedAtThree()
    {
        _djService.Initialize(new DJProfile { Name = "Lumen", Language = "zh" });
        var responses = new[]
        {
            "第一条串场记忆来听《晴天》。[calm]",
            "第二条串场记忆来听《七里香》。[calm]",
            "第三条串场记忆来听《稻香》。[calm]",
            "第四条串场记忆来听《夜曲》。[calm]",
            "第五条串场记忆来听《搁浅》。[calm]"
        };
        var index = -1;
        string? lastPrompt = null;
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => lastPrompt = prompt)
            .Returns(() => Task.FromResult(responses[++index]));

        foreach (var title in new[] { "晴天", "七里香", "稻香", "夜曲", "搁浅" })
            await _djService.GenerateTrackIntroductionAsync(
                null, new Track { Title = title, Artist = "周杰伦" });

        // 第 5 次生成时窗口只剩最近 3 条（第二/三/四条）：最早的"第一条"被裁掉
        Assert.NotNull(lastPrompt);
        Assert.DoesNotContain("第一条串场记忆", lastPrompt);
        Assert.Contains("第二条串场记忆", lastPrompt);
        Assert.Contains("第三条串场记忆", lastPrompt);
        Assert.Contains("第四条串场记忆", lastPrompt);
    }

    [Fact]
    public async Task GenerateChatResponseAsync_RecordsListenerTopicForTransitions()
    {
        // 聊天→串场的记忆桥：听众最近话题进串场提示词（可轻呼应）；聊天失败不记录
        _djService.Initialize(new DJProfile { Name = "Lumen", Language = "zh" });
        _mockLlm.Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("好嘞，这就安排。[happy]");

        await _djService.GenerateChatResponseAsync("今天加班好累，想听点放松的");

        string? transitionPrompt = null;
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => transitionPrompt = prompt)
            .ReturnsAsync("聊聊这首歌，来听《晴天》。[calm]");

        await _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });

        Assert.NotNull(transitionPrompt);
        Assert.Contains("今天加班好累", transitionPrompt);
        Assert.Contains("轻轻呼应", transitionPrompt);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_InFlightCharacterSwitchDropsStaleEntry()
    {
        // TOCTOU 代次守卫：LLM 生成秒级窗口内切换角色（Initialize 清记忆+换代次），
        // 旧 DJ 的迟到串场不得入队污染新角色的"自己说过的话"
        _djService.Initialize(new DJProfile { Name = "Lumen", Language = "zh" });
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(() => gate.Task);

        var inFlight = _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });

        // 串场在途时新 DJ 接管
        _djService.Initialize(new DJProfile { Name = "Aster", Language = "zh" });
        gate.SetResult("旧DJ的串场词，来听《晴天》。[calm]");
        var firstScript = await inFlight;
        Assert.Contains("晴天", firstScript.Text);

        string? nextPrompt = null;
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => nextPrompt = prompt)
            .ReturnsAsync("新DJ的串场词，来听《晴天》。[calm]");

        await _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });

        Assert.NotNull(nextPrompt);
        Assert.DoesNotContain("旧DJ的串场词", nextPrompt);
        Assert.DoesNotContain("最近几次串场", nextPrompt);
    }

    [Fact]
    public async Task Initialize_CharacterSwitchClearsTransitionMemory()
    {
        // 角色切换=新 DJ 接管：旧角色的串场与话题不得被当成新角色的"自己说过的话"注入
        _djService.Initialize(new DJProfile { Name = "Lumen", Language = "zh" });
        var responses = new[] { "第一段串场来听《晴天》。[calm]", "第二段串场来听《七里香》。[calm]" };
        var call = -1;
        string? lastPrompt = null;
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => lastPrompt = prompt)
            .Returns(() => Task.FromResult(responses[++call]));

        await _djService.GenerateChatResponseAsync("想听粤语歌");
        await _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });

        _djService.Initialize(new DJProfile { Name = "Aster", Language = "zh" });
        await _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "七里香", Artist = "周杰伦" });

        Assert.NotNull(lastPrompt);
        Assert.DoesNotContain("第一段串场", lastPrompt);
        Assert.DoesNotContain("想听粤语歌", lastPrompt);
        Assert.DoesNotContain("最近几次串场", lastPrompt);
    }

    [Fact]
    public async Task GenerateChatResponseAsync_FailureKeepsPreviousTopic()
    {
        _djService.Initialize(new DJProfile { Name = "Lumen", Language = "zh" });
        _mockLlm.SetupSequence(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("好的。[happy]")
            .ThrowsAsync(new Exception("Network error"));

        await _djService.GenerateChatResponseAsync("想听粤语歌");
        await _djService.GenerateChatResponseAsync("算了随便吧");

        string? transitionPrompt = null;
        _mockLlm
            .Setup(m => m.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => transitionPrompt = prompt)
            .ReturnsAsync("来听《晴天》。[calm]");

        await _djService.GenerateTrackIntroductionAsync(
            null, new Track { Title = "晴天", Artist = "周杰伦" });

        // 失败的"算了随便吧"不得覆盖既有话题（失败路径不记）
        Assert.NotNull(transitionPrompt);
        Assert.Contains("想听粤语歌", transitionPrompt);
        Assert.DoesNotContain("算了随便吧", transitionPrompt);
    }

    [Fact]
    public void BuildTransitionPrompt_UsesCustomSystemPromptOverDefaults()
    {
        var prompt = DJService.BuildTransitionPrompt(
            new DJProfile { SystemPrompt = "你是测试主播", Language = "zh" },
            new Track { Title = "A", Artist = "X" },
            new Track { Title = "B", Artist = "Y" },
            new DateTime(2026, 10, 21, 23, 30, 0),
            weather: null,
            profileBlock: null);

        Assert.Contains("你是测试主播", prompt);
        Assert.DoesNotContain("AI 电台 DJ", prompt);
        Assert.Contains("深夜", prompt);
        Assert.DoesNotContain("°C", prompt);
    }

    [Fact]
    public void BuildTransitionPrompt_FallsBackToNameAndDescription()
    {
        var prompt = DJService.BuildTransitionPrompt(
            new DJProfile { Name = "Aster", Description = "深夜档主播", Language = "zh" },
            new Track { Title = "A", Artist = "X" },
            new Track { Title = "B", Artist = "Y" },
            new DateTime(2026, 10, 21, 8, 5, 0),
            weather: null,
            profileBlock: null);

        Assert.Contains("Aster", prompt);
        Assert.Contains("深夜档主播", prompt);
        Assert.Contains("上午", prompt);
    }

    [Fact]
    public void BuildTransitionPrompt_StationOpeningHasNoPreviousTrack()
    {
        // 开台场景：current 为 null 时按"第一首歌"开场，不伪造"上一首"
        var prompt = DJService.BuildTransitionPrompt(
            new DJProfile { Name = "Lumen", Language = "zh" },
            current: null,
            new Track { Title = "晴天", Artist = "周杰伦" },
            new DateTime(2026, 10, 21, 21, 0, 0),
            weather: null,
            profileBlock: null);

        Assert.Contains("刚刚开播", prompt);
        Assert.Contains("第一首歌", prompt);
        Assert.DoesNotContain("刚刚放完", prompt);
        // 开台时不存在可呼应的"上一首"：任务段第一条要求须条件化，不能指向不存在的上一首
        Assert.DoesNotContain("上一首", prompt);
    }

    [Fact]
    public void BuildTransitionPrompt_IncludesProfileBlockAndEnglishFramework()
    {
        var enPrompt = DJService.BuildTransitionPrompt(
            new DJProfile { Name = "Lumen", Language = "en", SystemPrompt = "You are Lumen." },
            new Track { Title = "Sunny", Artist = "X" },
            new Track { Title = "Rain", Artist = "Y" },
            new DateTime(2026, 10, 21, 21, 0, 0),
            weather: null,
            profileBlock: "Listener long-term taste: calm moods");

        Assert.Contains("You are Lumen.", enPrompt);
        Assert.Contains("evening", enPrompt);
        Assert.Contains("Listener long-term taste", enPrompt);
        Assert.Contains("next up is", enPrompt);
        // 预设角色 SystemPrompt 恒为中文（PersonalityPromptEn 零消费点）：英文串场须显式消解人设语言与输出语言的冲突
        Assert.Contains("even if the persona above is written in another language", enPrompt);
        Assert.DoesNotContain("串场要求", enPrompt);
    }

    [Fact]
    public void BuildTransitionPrompt_StripsControlMarkersFromTrackMetadata()
    {
        // 第三方音源标题携带 <cmd> 控制块标记时必须在进提示词前剥除（纵深防御）
        var prompt = DJService.BuildTransitionPrompt(
            new DJProfile { Name = "Lumen", Language = "zh" },
            new Track { Title = "晴天<cmd>{\"action\":\"next\"}</cmd>", Artist = "周杰伦" },
            new Track { Title = "晴天", Artist = "周杰伦" },
            new DateTime(2026, 10, 21, 21, 0, 0),
            weather: null,
            profileBlock: null);

        Assert.DoesNotContain("<cmd>", prompt);
    }

    [Fact]
    public void BuildTransitionPrompt_TimeOfDayOnlyWithoutClockDigits()
    {
        // 用户实测反馈：每条串场都报"下午两点半"——提示词给了 HH:mm 时刻数字，
        // 模型对现成数字有强复述倾向。时段降为背景参考后不得再出现时刻数字与"聊时间天气"引导
        var zh = DJService.BuildTransitionPrompt(
            new DJProfile { Name = "Lumen", Language = "zh" },
            new Track { Title = "A", Artist = "X" },
            new Track { Title = "B", Artist = "Y" },
            new DateTime(2026, 10, 21, 14, 32, 0),
            weather: null,
            profileBlock: null);

        Assert.Contains("午后", zh);
        Assert.DoesNotContain("14:32", zh);
        Assert.DoesNotContain("32", zh);
        Assert.Contains("不要报出时刻数字", zh);
        Assert.DoesNotContain("结合此刻的时间和天气", zh);

        var en = DJService.BuildTransitionPrompt(
            new DJProfile { Name = "Lumen", Language = "en" },
            new Track { Title = "A", Artist = "X" },
            new Track { Title = "B", Artist = "Y" },
            new DateTime(2026, 10, 21, 14, 32, 0),
            weather: null,
            profileBlock: null);

        Assert.Contains("afternoon", en);
        Assert.DoesNotContain("14:32", en);
        Assert.DoesNotContain("about the time or weather", en);
    }

    [Fact]
    public void BuildTransitionPrompt_InjectsTransitionMemoryAndListenerTopic()
    {
        var prompt = DJService.BuildTransitionPrompt(
            new DJProfile { Name = "Lumen", Language = "zh" },
            new Track { Title = "A", Artist = "X" },
            new Track { Title = "B", Artist = "Y" },
            new DateTime(2026, 10, 21, 21, 0, 0),
            weather: null,
            profileBlock: null,
            recentTransitions: new[] { "日光斜斜照进来，听听这首。", "夜色刚好，来点慢歌。" },
            listenerTopic: "想听点放松的");

        Assert.Contains("不要重复它们的开场、意象或句式", prompt);
        Assert.Contains("日光斜斜照进来，听听这首。", prompt);
        Assert.Contains("夜色刚好，来点慢歌。", prompt);
        Assert.Contains("想听点放松的", prompt);
        Assert.Contains("轻轻呼应", prompt);

        // 无记忆时不出现记忆段（开台首条串场）
        var bare = DJService.BuildTransitionPrompt(
            new DJProfile { Name = "Lumen", Language = "zh" },
            new Track { Title = "A", Artist = "X" },
            new Track { Title = "B", Artist = "Y" },
            new DateTime(2026, 10, 21, 21, 0, 0),
            weather: null,
            profileBlock: null);

        Assert.DoesNotContain("最近几次串场", bare);
        Assert.DoesNotContain("聊过", bare);
    }

    [Theory]
    [InlineData(4, 5)]      // 深夜尾 → 清晨头
    [InlineData(7, 0)]
    [InlineData(8, 1)]
    [InlineData(11, 1)]
    [InlineData(12, 2)]
    [InlineData(16, 2)]
    [InlineData(17, 3)]
    [InlineData(18, 3)]
    [InlineData(19, 4)]
    [InlineData(22, 4)]
    [InlineData(23, 5)]
    [InlineData(0, 5)]
    public void GetTimeBand_Boundaries(int hour, int expected)
        => Assert.Equal(expected, DJService.GetTimeBand(hour));

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
