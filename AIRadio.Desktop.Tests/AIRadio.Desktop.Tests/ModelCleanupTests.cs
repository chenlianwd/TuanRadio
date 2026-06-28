using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Tests;

public class ModelCleanupTests
{
    [Fact]
    public void ChatMessage_SenderName_UsesReadableChineseNames()
    {
        Assert.Equal("我", new ChatMessage { Role = MessageRole.User }.SenderName);
        Assert.Equal("AI 主播", new ChatMessage { Role = MessageRole.Assistant }.SenderName);
        Assert.Equal("系统", new ChatMessage { Role = MessageRole.System }.SenderName);
    }

    [Fact]
    public void ChatMessage_SenderName_DefaultRole_ReturnsEmpty()
    {
        // Verify the default case returns empty string
        var msg = new ChatMessage { Role = (MessageRole)999 };
        Assert.Equal(string.Empty, msg.SenderName);
    }

    [Fact]
    public void CharacterPresets_DoNotExposeLive2DModelDirectories()
    {
        Assert.All(CharacterProfile.Presets, character =>
        {
            Assert.False(string.IsNullOrWhiteSpace(character.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(character.VoiceId));
            Assert.False(string.IsNullOrWhiteSpace(character.PersonalityPrompt));
            Assert.DoesNotContain("Live2D", character.PersonalityPrompt, StringComparison.OrdinalIgnoreCase);
        });
    }
}
