using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

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
    public void ChatMessage_AndBuiltInPersonality_FollowEnglishLanguage()
    {
        try
        {
            AppLanguage.Apply("en");
            CharacterProfile.RefreshLocalizedPresets();

            Assert.Equal("Me", new ChatMessage { Role = MessageRole.User }.SenderName);
            Assert.Equal("AI DJ", new ChatMessage { Role = MessageRole.Assistant }.SenderName);
            Assert.StartsWith("You are Lumen", CharacterProfile.Presets[0].PersonalityPrompt);
        }
        finally
        {
            AppLanguage.Apply("zh");
            CharacterProfile.RefreshLocalizedPresets();
        }
    }

    [Fact]
    public void TrackUnknownMetadata_UsesCurrentDisplayLanguage()
    {
        var track = new Track { Artist = "未知艺术家", Album = "未知专辑" };
        try
        {
            AppLanguage.Apply("en");
            track.RefreshLocalization();

            Assert.Equal("Unknown artist", track.DisplayArtist);
            Assert.Equal("Unknown album", track.DisplayAlbum);
            Assert.Equal("未知艺术家", track.Artist);
        }
        finally
        {
            AppLanguage.Apply("zh");
        }
    }

    [Fact]
    public void TrackDisplayMetadata_IsNotPersisted()
    {
        var json = JsonSerializer.Serialize(new Track());

        Assert.DoesNotContain("DisplayArtist", json);
        Assert.DoesNotContain("DisplayAlbum", json);
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
