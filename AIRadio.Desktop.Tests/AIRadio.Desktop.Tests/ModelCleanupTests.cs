using AIRadio.Desktop.Models;
using System;
using System.IO;
using System.Linq;

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

    [Fact]
    public void ProductionCode_DoesNotExposeLegacyMinimaxRuntimeSurface()
    {
        var projectRoot = FindRepositoryRoot();
        var productionFiles = Directory.GetFiles(Path.Combine(projectRoot, "AIRadio.Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var legacySymbols = new[]
        {
            "IMinimaxService",
            "MinimaxService",
            "MinimaxApiException",
            "FromMinimaxBaseResponse",
            "MinimaxApiKey"
        };

        var matches = productionFiles
            .SelectMany(path => legacySymbols
                .Where(symbol => File.ReadAllText(path).Contains(symbol, StringComparison.Ordinal))
                .Select(symbol => $"{Path.GetRelativePath(projectRoot, path)} contains {symbol}"))
            .ToArray();

        Assert.Empty(matches);
    }

    [Fact]
    public void DesktopProject_DoesNotReferenceUnusedEdgeTtsPackages()
    {
        var projectRoot = FindRepositoryRoot();
        var projectFile = File.ReadAllText(Path.Combine(projectRoot, "AIRadio.Desktop", "AIRadio.Desktop.csproj"));

        Assert.DoesNotContain("EdgeTTS.Net", projectFile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EdgeTtsSharp", projectFile, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AIRadio.Desktop", "AIRadio.Desktop.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find AIRadio repository root.");
    }
}
