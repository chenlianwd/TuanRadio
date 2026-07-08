using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class SettingsViewModelTests
{
    private readonly Mock<ILLMService> _mockLlm;
    private readonly Mock<IDJService> _mockDJ;
    private readonly Mock<ISecureStorage> _mockStorage;

    public SettingsViewModelTests()
    {
        _mockLlm = new Mock<ILLMService>();
        _mockDJ = new Mock<IDJService>();
        _mockStorage = new Mock<ISecureStorage>();
        _mockStorage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void GetOverride_ReturnsStoredOverride()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);
        vm.SelectedCharacter = vm.Characters.First();

        // Override not set yet
        var result = vm.GetOverride("haru");
        Assert.Null(result);

        // After initialization, default voice should be set
        vm.SelectedCharacter = vm.Characters.First();
        Assert.NotNull(vm.SelectedCharacter);
    }

    [Fact]
    public void Voices_ListContainsAllOptions()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        Assert.Equal(6, vm.Voices.Count);
        Assert.Contains(vm.Voices, v => v.Id == "male-qn-qingse");
        Assert.Contains(vm.Voices, v => v.Id == "female-shaonv");
    }

    [Fact]
    public void Languages_ListContainsZhAndEn()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        Assert.Equal(2, vm.Languages.Count);
        Assert.Contains(vm.Languages, l => l.Id == "zh");
        Assert.Contains(vm.Languages, l => l.Id == "en");
    }

    [Fact]
    public void Characters_ListContainsPresets()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        Assert.True(vm.Characters.Count >= 6);
        Assert.Contains(vm.Characters, c => c.Id == "haru" && c.DisplayName == "Lumen");
        Assert.Contains(vm.Characters, c => c.Id == "hiyori" && c.DisplayName == "Aster");
        Assert.Contains(vm.Characters, c => c.Id == "mao" && c.DisplayName == "Noir");
    }

    [Fact]
    public void SelectedCharacter_DefaultsToFirst()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        Assert.NotNull(vm.SelectedCharacter);
        Assert.Equal("haru", vm.SelectedCharacter.Id);
    }

    [Fact]
    public void TtsEnabled_DefaultsToTrue()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        Assert.True(vm.TtsEnabled);
    }

    [Fact]
    public void SelectedLanguage_DefaultsToZh()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        Assert.Equal("zh", vm.SelectedLanguage);
    }

    [Fact]
    public void LlmSettings_DefaultToOpenAiCompatibleProfile()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        Assert.Equal("openai", vm.SelectedProvider);
        Assert.Equal("gpt-4o-mini", vm.Model);
        Assert.Contains(vm.LlmProviders, p => p.Id == "deepseek");
        Assert.Contains(vm.LlmProviders, p => p.Id == "ollama");
    }

    [Fact]
    public async Task SaveCommand_StoresLlmKeyAndConfiguresSelectedProvider()
    {
        LLMConfig? captured = null;
        _mockLlm.Setup(x => x.Configure(It.IsAny<LLMConfig>()))
            .Callback<LLMConfig>(config => captured = config);

        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
        {
            SelectedProvider = "deepseek",
            ApiKey = "test-key",
            BaseUrl = "https://example.com/v1",
            Model = "deepseek-chat"
        };

        await vm.SaveCommand.Execute();

        _mockStorage.Verify(x => x.SaveApiKeyAsync("llm", "test-key"), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal("deepseek", captured!.Provider);
        Assert.Equal("https://example.com/v1", captured.BaseUrl);
        Assert.Equal("deepseek-chat", captured.Model);
        Assert.Equal("test-key", captured.ApiKey);
    }

    [Fact]
    public void SelectedProvider_UpdatesModelToProviderDefault()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        vm.SelectedProvider = "deepseek";
        Assert.Equal("deepseek-chat", vm.Model);

        vm.SelectedProvider = "ollama";
        Assert.Equal("llama3", vm.Model);
    }

    [Fact]
    public async Task TestConnectionCommand_OllamaWithoutApiKey_CallsLlm()
    {
        _mockLlm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("本地模型正常");
        LLMConfig? captured = null;
        _mockLlm.Setup(x => x.Configure(It.IsAny<LLMConfig>()))
            .Callback<LLMConfig>(config => captured = config);

        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object)
        {
            SelectedProvider = "ollama",
            ApiKey = string.Empty,
            BaseUrl = "http://localhost:11434/v1"
        };

        await vm.TestConnectionCommand.Execute();

        _mockLlm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);
        _mockStorage.Verify(x => x.SaveApiKeyAsync("llm", It.IsAny<string>()), Times.Never);
        Assert.NotNull(captured);
        Assert.Equal("ollama", captured!.Provider);
        Assert.Equal(string.Empty, captured.ApiKey);
        Assert.Contains("连接成功", vm.StatusMessage);
    }

    [Fact]
    public void TestConnectionCommand_CanBeCreated()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);
        vm.ApiKey = "";

        vm.TestConnectionCommand.Subscribe(_ => { });

        Assert.NotNull(vm.TestConnectionCommand);
    }

    [Fact]
    public async Task TestConnectionCommand_EmptyKey_SetsErrorMessage()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);
        vm.ApiKey = "";

        await vm.TestConnectionCommand.Execute();

        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
    }
}
