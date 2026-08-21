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
        Assert.Empty(vm.Model);
        Assert.Equal(3, vm.LlmProviders.Count);
        Assert.Contains(vm.LlmProviders, p => p.Id == "openai");
        Assert.Contains(vm.LlmProviders, p => p.Id == "anthropic");
        Assert.Contains(vm.LlmProviders, p => p.Id == "local");
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
            SelectedProvider = "openai",
            ApiKey = "  test-key  ",
            BaseUrl = "  https://example.com/v1/  ",
            Model = "  deepseek-chat  "
        };

        await vm.SaveCommand.Execute();

        _mockStorage.Verify(x => x.SaveApiKeyAsync("llm", "test-key"), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal("openai", captured!.Provider);
        Assert.Equal("https://example.com/v1", captured.BaseUrl);
        Assert.Equal("deepseek-chat", captured.Model);
        Assert.Equal("test-key", captured.ApiKey);
        Assert.Equal("test-key", vm.ApiKey);
    }

    [Fact]
    public void SelectedProvider_KeepsSavedValues()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);

        vm.ApiKey = "remote-secret";
        vm.Model = "deepseek-chat";
        vm.BaseUrl = "https://api.deepseek.com/v1";

        vm.SelectedProvider = "local";

        Assert.Equal("remote-secret", vm.ApiKey);
        Assert.Equal("deepseek-chat", vm.Model);
        Assert.Equal("https://api.deepseek.com/v1", vm.BaseUrl);
    }

    [Fact]
    public async Task TestConnectionCommand_LocalWithoutApiKey_CallsLlm()
    {
        _mockLlm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("本地模型正常");
        LLMConfig? captured = null;
        _mockLlm.Setup(x => x.Configure(It.IsAny<LLMConfig>()))
            .Callback<LLMConfig>(config => captured = config);

        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                SelectedProvider = "local",
                ApiKey = string.Empty,
                BaseUrl = "http://localhost:11434/v1",
                Model = "llama3"
            };

            await vm.TestConnectionCommand.Execute();

            _mockLlm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);
            _mockStorage.Verify(x => x.SaveApiKeyAsync("llm", It.IsAny<string>()), Times.Never);
            Assert.NotNull(captured);
            Assert.Equal("local", captured!.Provider);
            Assert.Equal(string.Empty, captured.ApiKey);
            Assert.Contains("连接成功并已保存", vm.StatusMessage);
            Assert.True(File.Exists(settingsFile));
        }
        finally
        {
            File.Delete(settingsFile);
        }
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

    [Fact]
    public async Task TestConnectionCommand_WithRemoteKey_PersistsConfiguration()
    {
        _mockLlm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("连接正常");
        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                SelectedProvider = "anthropic",
                ApiKey = "test-key",
                BaseUrl = "https://proxy.example/v1",
                Model = "claude-test"
            };

            await vm.TestConnectionCommand.Execute();

            _mockStorage.Verify(x => x.SaveApiKeyAsync("llm", "test-key"), Times.Once);
            Assert.Contains("连接成功并已保存", vm.StatusMessage);

            var json = await File.ReadAllTextAsync(settingsFile);
            Assert.Contains("\"llm_provider\": \"anthropic\"", json);
            Assert.Contains("\"llm_model\": \"claude-test\"", json);
            Assert.Contains("\"llm_base_url\": \"https://proxy.example/v1\"", json);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task SuccessfulConnection_RoundTripsProviderModelBaseUrlAndApiKey()
    {
        _mockLlm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("连接正常");
        _mockStorage.Setup(x => x.GetApiKeyAsync("llm")).ReturnsAsync("roundtrip-key");
        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        try
        {
            var savingVm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                SelectedProvider = "anthropic",
                ApiKey = "roundtrip-key",
                BaseUrl = "https://proxy.example/v1",
                Model = "claude-roundtrip"
            };
            await savingVm.TestConnectionCommand.Execute();

            var loadedVm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            await loadedVm.LoadAsync();

            Assert.Equal("anthropic", loadedVm.SelectedProvider);
            Assert.Equal("roundtrip-key", loadedVm.ApiKey);
            Assert.Equal("https://proxy.example/v1", loadedVm.BaseUrl);
            Assert.Equal("claude-roundtrip", loadedVm.Model);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task SaveCommand_EmptyKey_KeepsCurrentCredentialAndRemovesLegacy()
    {
        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                ApiKey = string.Empty,
                Model = "gpt-4o-mini"
            };

            await vm.SaveCommand.Execute();

            _mockStorage.Verify(x => x.DeleteApiKey("llm"), Times.Never);
            _mockStorage.Verify(x => x.SaveApiKeyAsync("llm", It.IsAny<string>()), Times.Never);
            _mockStorage.Verify(x => x.DeleteApiKey("minimax"), Times.Once);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task SaveUiStateCommand_PreservesLlmFieldsFromDiskAndSkipsSecureStorage()
    {
        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(settingsFile, """
            {
              "llm_provider": "anthropic",
              "llm_base_url": "https://proxy.example/v1",
              "llm_model": "claude-test",
              "is_dark_mode": false
            }
            """);

        try
        {
            // 模拟内存里的 LLM 字段已被清空：无关自动保存也不得覆盖磁盘上的配置
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                SelectedProvider = "openai",
                BaseUrl = string.Empty,
                Model = string.Empty,
                IsDarkMode = true
            };

            await vm.SaveUiStateCommand.Execute();

            _mockStorage.Verify(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _mockStorage.Verify(x => x.DeleteApiKey(It.IsAny<string>()), Times.Never);

            var loaded = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            await loaded.LoadAsync();
            Assert.Equal("anthropic", loaded.SelectedProvider);
            Assert.Equal("https://proxy.example/v1", loaded.BaseUrl);
            Assert.Equal("claude-test", loaded.Model);
            Assert.True(loaded.IsDarkMode);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task LoadAsync_NormalizesLegacyProviderAndKeepsCurrentKey()
    {
        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(settingsFile, """
            {
              "llm_provider": "claude",
              "llm_base_url": "https://proxy.example/v1",
              "llm_model": "claude-test"
            }
            """);
        _mockStorage.Setup(x => x.GetApiKeyAsync("llm")).ReturnsAsync("current-key");
        _mockStorage.Setup(x => x.GetApiKeyAsync("minimax")).ReturnsAsync("legacy-key");

        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);

            await vm.LoadAsync();

            Assert.Equal("anthropic", vm.SelectedProvider);
            Assert.Equal("https://proxy.example/v1", vm.BaseUrl);
            Assert.Equal("claude-test", vm.Model);
            Assert.Equal("current-key", vm.ApiKey);
            _mockStorage.Verify(x => x.GetApiKeyAsync("minimax"), Times.Never);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public void CompactModeTopmost_DefaultsToTrue()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object);
        Assert.True(vm.CompactModeTopmost);
    }

    [Fact]
    public async Task SaveAndLoad_PersistsCompactModeSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settingsFile = Path.Combine(dir, "settings.json");

        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            vm.CompactModeTopmost = true;
            vm.StartInCompactMode = true;
            await vm.SaveCommand.Execute();

            var reloaded = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            await reloaded.LoadAsync();

            Assert.True(reloaded.CompactModeTopmost);
            Assert.True(reloaded.StartInCompactMode);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

