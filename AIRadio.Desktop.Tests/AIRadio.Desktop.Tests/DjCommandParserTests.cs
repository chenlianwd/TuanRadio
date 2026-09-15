using AIRadio.Desktop.ViewModels;

namespace AIRadio.Desktop.Tests;

public class DjCommandParserTests
{
    [Fact]
    public void ParseResponse_SupportsJsonControlBlock()
    {
        var response = """
            我给你换一首更安静的。[calm]
            <cmd>{"action":"change_mood","mood":"calm"}</cmd>
            """;

        var parsed = ChatViewModel.ParseDjResponse(response);

        Assert.Equal("我给你换一首更安静的。", parsed.DisplayText);
        Assert.Equal("change_mood:calm", parsed.Command);
        Assert.Equal("calm", parsed.Emotion);
    }

    [Fact]
    public void ParseResponse_LegacyTailFormatIsNoLongerParsedAsCommand()
    {
        // 旧文本尾标格式已移除：协议只认 JSON 控制块，系统提示词自始只教学 JSON；
        // 模型若仍输出旧格式，按普通文本处理（不解析指令、不剥离文本）
        var parsed = ChatViewModel.ParseDjResponse("现在播放这首。[happy]【play:稻香】");

        Assert.Equal("现在播放这首。【play:稻香】", parsed.DisplayText);
        Assert.Null(parsed.Command);
        Assert.Equal("happy", parsed.Emotion);
    }
}
