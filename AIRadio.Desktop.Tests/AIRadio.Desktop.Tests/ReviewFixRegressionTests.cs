using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Moq.Protected;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 2026-09 全量审查修复的回归测试：外部 JSON 形状防御、指令解析边界、
/// 曲目身份匹配口径、SSML/参数转义与 LLM 空回复语义。
/// </summary>
public class ReviewFixRegressionTests
{
    // ---------- L-C1: ParseJsonCommand 的畸形 JSON 边界 ----------

    [Fact]
    public void ParseDjResponse_NullQuery_DoesNotProduceEmptyPlayCommand()
    {
        var response = ChatViewModel.ParseDjResponse("好的 <cmd>{\"action\":\"play\",\"query\":null}</cmd>");
        Assert.Null(response.Command);
    }

    [Fact]
    public void ParseDjResponse_NonStringQuery_DoesNotThrow()
    {
        // query 为数字时 GetString 会抛 InvalidOperationException，必须被形状检查拦下
        var response = ChatViewModel.ParseDjResponse("<cmd>{\"action\":\"play\",\"query\":123}</cmd>");
        Assert.Null(response.Command);
    }

    [Fact]
    public void ParseDjResponse_NonStringAction_DoesNotThrow()
    {
        var response = ChatViewModel.ParseDjResponse("<cmd>{\"action\":1}</cmd>");
        Assert.Null(response.Command);
    }

    [Fact]
    public void ParseDjResponse_EmptyQuery_DoesNotProduceEmptyPlayCommand()
    {
        var response = ChatViewModel.ParseDjResponse("<cmd>{\"action\":\"play\",\"query\":\"  \"}</cmd>");
        Assert.Null(response.Command);
    }

    [Fact]
    public void ParseDjResponse_ValidQuery_StillProducesCommand()
    {
        var response = ChatViewModel.ParseDjResponse("<cmd>{\"action\":\"play\",\"query\":\"晴天\"}</cmd>");
        Assert.Equal("play:晴天", response.Command);
    }

    // ---------- L-P5: TrackComparer 大小写口径 ----------

    [Fact]
    public void TrackComparer_IsSameTrack_IgnoresSourceIdCase()
    {
        var left = new Track { Title = "A", SourceId = "kugou:ABC" };
        var right = new Track { Title = "B", SourceId = "kugou:abc" };
        Assert.True(TrackComparer.IsSameTrack(left, right));
        Assert.True(TrackComparer.IsSameTrackIdentity(left, right));
    }

    [Fact]
    public void TrackComparer_IsSameTrack_IgnoresFilePathCase()
    {
        var left = new Track { Title = "A", FilePath = @"C:\Music\Song.Mp3" };
        var right = new Track { Title = "B", FilePath = @"C:\music\song.mp3" };
        Assert.True(TrackComparer.IsSameTrack(left, right));
    }

    // ---------- L-P6: IsSameSongLoose 单边空 artist ----------

    [Fact]
    public void IsSameSongLoose_SingleEmptyArtist_NoLongerMatches()
    {
        // 单边 artist 缺失曾无条件判同曲：同名不同曲会被误合并（点歌被跳过）
        Assert.False(MusicIdentity.IsSameSongLoose("晴天", "", "晴天", "周杰伦"));
        Assert.False(MusicIdentity.IsSameSongLoose("晴天", "周杰伦", "晴天", ""));
    }

    [Fact]
    public void IsSameSongLoose_BothArtistsEmpty_StillMatchesByTitle()
    {
        Assert.True(MusicIdentity.IsSameSongLoose("晴天", "", "晴天", ""));
    }

    // ---------- L-I7: SSML voice 属性转义 ----------

    [Fact]
    public void BuildSsml_EscapesVoiceAttribute()
    {
        var ssml = EdgeTtsService.BuildSsml("正文", "zh-CN-Xiao'<&>Neural");
        Assert.DoesNotContain("name='zh-CN-Xiao'<&>Neural'", ssml);
        Assert.Contains("&apos;&lt;&amp;&gt;", ssml);
    }

    // ---------- L-M7: yt-dlp 参数结尾反斜杠 ----------

    [Theory]
    [InlineData("abc\\", "\"abc\\\\\"")]
    [InlineData("abc", "\"abc\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\"\"b\"")] // 引号前的反斜杠同样翻倍
    public void EscapeArg_TrailingBackslash_IsDoubled(string input, string expected)
    {
        Assert.Equal(expected, YouTubeMusicService.EscapeArg(input));
    }

    // ---------- M13/M14/L-M5: 音源 data:null 形状防御 ----------

    private static HttpClient CreateJsonClient(string json)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            });
        return new HttpClient(handler.Object) { BaseAddress = new Uri("http://127.0.0.1:1") };
    }

    [Fact]
    public async Task Kuwo_Search_WithDataNull_ReturnsEmptyInsteadOfThrowing()
    {
        var service = new KuwoMusicService(CreateJsonClient(@"{""code"":200,""data"":null}"));
        var tracks = await service.SearchAsync("测试", 5, CancellationToken.None);
        Assert.Empty(tracks);
    }

    [Fact]
    public async Task Kuwo_GetPlayUrl_WithDataNull_ReturnsNullInsteadOfThrowing()
    {
        var service = new KuwoMusicService(CreateJsonClient(@"{""code"":200,""data"":null}"));
        var url = await service.GetPlayUrlAsync("kuwo:123", CancellationToken.None);
        Assert.Null(url);
    }

    [Fact]
    public async Task Migu_Search_WithMusicsNull_ReturnsEmptyInsteadOfThrowing()
    {
        var service = new MiguMusicService(CreateJsonClient(@"{""musics"":null}"));
        var tracks = await service.SearchAsync("测试", 5, CancellationToken.None);
        Assert.Empty(tracks);
    }

    [Fact]
    public async Task Migu_GetPlayUrl_WithDataNull_ReturnsNullInsteadOfThrowing()
    {
        var service = new MiguMusicService(CreateJsonClient(@"{""data"":null}"));
        var url = await service.GetPlayUrlAsync("migu:123", CancellationToken.None);
        Assert.Null(url);
    }

    [Fact]
    public async Task Netease_GetPlayUrl_WithDataNull_ReturnsNullInsteadOfThrowing()
    {
        var service = new NeteaseMusicService(CreateJsonClient(@"{""code"":200,""data"":null}"), accounts: null);
        var url = await service.GetPlayUrlAsync("netease:123", CancellationToken.None);
        Assert.Null(url);
    }

    // ---------- L-I2/L-I3: LLM 空回复与未配置语义 ----------

    [Fact]
    public async Task LLMService_OpenAiNullContent_ThrowsInvalidResponse()
    {
        var service = new LLMService(CreateJsonClient(@"{""choices"":[{""message"":{""content"":null}}]}"));
        service.Configure(new LLMConfig
        {
            Provider = "local",
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama3",
            ApiKey = string.Empty
        });

        await Assert.ThrowsAsync<LlmApiException>(() =>
            service.ChatAsync("你好", new List<ChatMessage>()));
    }

    [Fact]
    public async Task LLMService_ChatRawAsync_Unconfigured_ThrowsInsteadOfReturningPromptText()
    {
        var service = new LLMService(CreateJsonClient("{}"));
        await Assert.ThrowsAsync<LlmApiException>(() =>
            service.ChatRawAsync("生成关键词", CancellationToken.None));
    }
}
