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
    public void ParseResponse_KeepsLegacyCommandCompatibility()
    {
        var parsed = ChatViewModel.ParseDjResponse("现在播放这首。[happy]【play:稻香】");

        Assert.Equal("现在播放这首。", parsed.DisplayText);
        Assert.Equal("play:稻香", parsed.Command);
        Assert.Equal("happy", parsed.Emotion);
    }
}
