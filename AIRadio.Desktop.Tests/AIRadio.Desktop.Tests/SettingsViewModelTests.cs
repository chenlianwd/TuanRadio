using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
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

    private static string CreateTempSettingsFile()
        => Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task MusicProviderOrderAndDisabledState_RoundTripThroughSettings()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            var broker = new MusicSourceBroker(
                new LocalLibraryProvider(settingsFile + ".library"),
                new OpenSubsonicProvider(_mockStorage.Object));
            using (var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object,
                       settingsFile, musicSearch: broker))
            {
                await vm.MoveMusicProviderAsync("opensubsonic", -1);
                Assert.Equal("opensubsonic", vm.MusicProviders[0].Id);
                vm.MusicProviders[0].Enabled = false;
                await vm.ApplyMusicProviderOptionsAsync();
            }

            var restoredBroker = new MusicSourceBroker(
                new LocalLibraryProvider(settingsFile + ".library"),
                new OpenSubsonicProvider(_mockStorage.Object));
            using var restored = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object,
                settingsFile, musicSearch: restoredBroker);
            await restored.LoadAsync();
            Assert.Equal("opensubsonic", restored.MusicProviders[0].Id);
            Assert.False(restored.MusicProviders[0].Enabled);
            Assert.True(restored.MusicProviders[1].Enabled);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public void TabIndex_DefaultsToAiPage()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            Assert.Equal(0, vm.TabIndex);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public void ShowTabCommands_SetTabIndex()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            vm.ShowMusicTabCommand.Execute().Subscribe();
            Assert.Equal(1, vm.TabIndex);
            vm.ShowHostTabCommand.Execute().Subscribe();
            Assert.Equal(2, vm.TabIndex);
            vm.ShowAppearanceTabCommand.Execute().Subscribe();
            Assert.Equal(3, vm.TabIndex);
            vm.ShowProfileTabCommand.Execute().Subscribe();
            Assert.Equal(4, vm.TabIndex);
            vm.ShowAiTabCommand.Execute().Subscribe();
            Assert.Equal(0, vm.TabIndex);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task AudiusKey_SavesToSecureStorageAndNeverToSettingsJson()
    {
        string? stored = null;
        _mockStorage.Setup(x => x.SaveApiKeyAsync("audius-api-key-v1", It.IsAny<string>()))
            .Callback<string, string>((_, key) => stored = key)
            .Returns(Task.CompletedTask);
        _mockStorage.Setup(x => x.GetApiKeyAsync("audius-api-key-v1"))
            .ReturnsAsync(() => stored);
        _mockStorage.Setup(x => x.DeleteApiKey("audius-api-key-v1"))
            .Callback(() => stored = null);
        var settingsFile = CreateTempSettingsFile();
        var provider = new AudiusProvider(storage: _mockStorage.Object);
        try
        {
            using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object,
                settingsFile, audius: provider);
            vm.AudiusApiKeyInput = "local-test-key";
            await vm.SaveAudiusApiKeyCommand.Execute();
            Assert.Equal("local-test-key", stored);
            Assert.True(provider.HasApiKey);
            Assert.Empty(vm.AudiusApiKeyInput);
            await vm.SaveUiStateCommand.Execute();
            Assert.DoesNotContain("local-test-key", await File.ReadAllTextAsync(settingsFile));

            AppLanguage.Apply("en");
            Assert.Equal("API Key saved securely", vm.AudiusStatus);
            using var restored = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object,
                settingsFile, audius: new AudiusProvider(storage: _mockStorage.Object));
            await restored.LoadAsync();
            Assert.Equal("API Key saved securely", restored.AudiusStatus);
            restored.ClearAudiusApiKeyCommand.Execute().Subscribe();
            Assert.Null(stored);
        }
        finally
        {
            AppLanguage.Apply("zh");
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }
    [Fact]
    public async Task OpenSubsonicQuality_RoundTripsAndUpdatesProvider()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            var provider = new OpenSubsonicProvider(_mockStorage.Object);
            using (var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object,
                       settingsFile, openSubsonic: provider))
            {
                vm.SelectedOpenSubsonicQuality = "original";
                Assert.Equal(OpenSubsonicQuality.Original, provider.Quality);
                AppLanguage.Apply("en");
                Assert.Equal("original", vm.SelectedOpenSubsonicQuality);
                Assert.Equal("Original quality", vm.OpenSubsonicQualities.Single(x => x.Id == "original").DisplayName);
                AppLanguage.Apply("zh");
                await vm.SaveUiStateCommand.Execute();
            }
            var restoredProvider = new OpenSubsonicProvider(_mockStorage.Object);
            using var restored = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object,
                settingsFile, openSubsonic: restoredProvider);
            await restored.LoadAsync();
            Assert.Equal("original", restored.SelectedOpenSubsonicQuality);
            Assert.Equal(OpenSubsonicQuality.Original, restoredProvider.Quality);
        }
        finally
        {
            AppLanguage.Apply("zh");
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public void GetOverride_ReturnsStoredOverride()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
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
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());

        Assert.Equal(6, vm.Voices.Count);
        Assert.Contains(vm.Voices, v => v.Id == "male-qn-qingse");
        Assert.Contains(vm.Voices, v => v.Id == "female-shaonv");
    }

    [Fact]
    public void LanguageChange_ReplacesOptionListInstances()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        try
        {
            var oldVoices = vm.Voices;
            var oldProviders = vm.LlmProviders;
            try
            {
                AppLanguage.Apply("en");

                // 必须整体换新实例：同实例重推会被绑定按相等性去重，下拉项停留旧语言
                Assert.NotSame(oldVoices, vm.Voices);
                Assert.NotSame(oldProviders, vm.LlmProviders);
                Assert.Contains(vm.Voices, v => v.DisplayName == "Soft male");
                Assert.Contains(vm.LlmProviders, v => v.DisplayName == "OpenAI-compatible");
            }
            finally
            {
                AppLanguage.Apply("zh");
            }
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void Languages_ListContainsZhAndEn()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());

        Assert.Equal(2, vm.Languages.Count);
        Assert.Contains(vm.Languages, l => l.Id == "zh");
        Assert.Contains(vm.Languages, l => l.Id == "en");
    }

    [Fact]
    public void Characters_ListContainsPresets()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());

        Assert.True(vm.Characters.Count >= 6);
        Assert.Contains(vm.Characters, c => c.Id == "haru" && c.DisplayName == "Lumen");
        Assert.Contains(vm.Characters, c => c.Id == "hiyori" && c.DisplayName == "Aster");
        Assert.Contains(vm.Characters, c => c.Id == "mao" && c.DisplayName == "Noir");
    }

    [Fact]
    public void SelectedCharacter_DefaultsToFirst()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());

        Assert.NotNull(vm.SelectedCharacter);
        Assert.Equal("haru", vm.SelectedCharacter.Id);
    }

    [Fact]
    public void TtsEnabled_DefaultsToTrue()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());

        Assert.True(vm.TtsEnabled);
    }

    [Fact]
    public void SelectedLanguage_DefaultsToZh()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());

        Assert.Equal("zh", vm.SelectedLanguage);
    }

    [Fact]
    public void SelectedLanguage_English_RefreshesVisibleOptionsAndStatuses()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        try
        {
            vm.SelectedLanguage = "en";

            Assert.Equal("Test", vm.TestConnectionButtonText);
            Assert.Equal("Not signed in", vm.NeteaseAccountStatus);
            Assert.Contains(vm.SpectrumStyles, option => option.DisplayName == "Neon wave");
            Assert.Contains(vm.SpeechMixModes, option => option.DisplayName == "Duck volume while speaking");
            Assert.StartsWith("You are Lumen", vm.CharacterPersonality);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply("zh");
            CharacterProfile.RefreshLocalizedPresets();
        }
    }

    [Fact]
    public async Task LoadAsync_English_LocalizesPreviouslySavedBuiltInPersonality()
    {
        var settingsFile = CreateTempSettingsFile();
        await File.WriteAllTextAsync(settingsFile, """
            {
              "language": "en",
              "character_overrides": {
                "haru": {
                  "voice_id": "female-shaonv",
                  "personality": "你是名叫 Lumen 的中文电台 DJ。气质明亮、轻快、有元气，擅长把普通的一天说得轻盈一点。"
                }
              }
            }
            """);

        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
        try
        {
            await vm.LoadAsync();

            Assert.StartsWith("You are Lumen", vm.CharacterPersonality);
            Assert.StartsWith("You are Lumen", vm.GetOverride("haru")?.Personality);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply("zh");
            CharacterProfile.RefreshLocalizedPresets();
        }
    }

    [Fact]
    public async Task LanguageChange_RelocalizesExistingValidationStatus()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        try
        {
            await vm.TestConnectionCommand.Execute();
            Assert.Equal("请先输入 API Key", vm.StatusMessage);

            vm.SelectedLanguage = "en";

            Assert.Equal("Enter your API key first", vm.StatusMessage);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply("zh");
            CharacterProfile.RefreshLocalizedPresets();
        }
    }

    [Fact]
    public void LlmSettings_DefaultToOpenAiCompatibleProfile()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());

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
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());

        vm.ApiKey = "remote-secret";
        vm.Model = "deepseek-chat";
        vm.BaseUrl = "https://api.deepseek.com/v1";

        vm.SelectedProvider = "local";

        Assert.Equal("remote-secret", vm.ApiKey);
        Assert.Equal("deepseek-chat", vm.Model);
        Assert.Equal("https://api.deepseek.com/v1", vm.BaseUrl);
    }

    [Fact]
    public async Task TestConnection_NonAsciiApiKey_BlockedBeforeLlmCall()
    {
        // 实机事故：Key 复制混入中文，运行期报"Request headers must contain only ASCII
        // characters"却被归类成网络问题；入口拦截 + 准确文案
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        try
        {
            vm.ApiKey = "sk-正常前缀混入中文";
            vm.Model = "test-model";

            await vm.TestConnectionCommand.Execute();

            Assert.Contains("非 ASCII", vm.StatusMessage);
            _mockLlm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Never);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task TestConnection_FullWidthSpaceSuffix_TrimsThenProceeds()
    {
        // 尾缀全角空格（U+3000）是可修剪空白，Trim 后即合法 Key，不得误拦成死循环
        _mockLlm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("连接正常");

        var settingsFile = CreateTempSettingsFile();
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
        try
        {
            vm.ApiKey = "sk-valid\u3000";
            vm.Model = "test-model";

            await vm.TestConnectionCommand.Execute();

            _mockLlm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);
        }
        finally
        {
            vm.Dispose();
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task SaveCommand_NonAsciiApiKey_RejectedBeforeSecureStorageAndDisk()
    {
        var settingsFile = CreateTempSettingsFile();
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
        try
        {
            vm.ApiKey = "sk-全角Ｋｅｙ";
            vm.Model = "test-model";

            await vm.SaveCommand.Execute();

            _mockStorage.Verify(x => x.SaveApiKeyAsync("llm", It.IsAny<string>()), Times.Never);
            Assert.False(File.Exists(settingsFile));
            Assert.Contains("非 ASCII", vm.StatusMessage);
        }
        finally
        {
            vm.Dispose();
            File.Delete(settingsFile);
        }
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
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        vm.ApiKey = "";

        vm.TestConnectionCommand.Subscribe(_ => { });

        Assert.NotNull(vm.TestConnectionCommand);
    }

    [Fact]
    public async Task TestConnectionCommand_EmptyKey_SetsErrorMessage()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
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
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        Assert.True(vm.CompactModeTopmost);
    }

    [Fact]
    public async Task SaveAsync_RotatesPreviousSettingsToBakFile()
    {
        var settingsFile = CreateTempSettingsFile();
        await File.WriteAllTextAsync(settingsFile, """
            {
              "llm_provider": "anthropic"
            }
            """);
        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                Model = "new-model"
            };

            await vm.SaveCommand.Execute();

            var bak = settingsFile + ".bak";
            Assert.True(File.Exists(bak));
            Assert.Contains("\"llm_provider\": \"anthropic\"", await File.ReadAllTextAsync(bak));
            Assert.Contains("\"llm_model\": \"new-model\"", await File.ReadAllTextAsync(settingsFile));
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task LoadAsync_CorruptSettingsFile_FallsBackToBak()
    {
        var settingsFile = CreateTempSettingsFile();
        await File.WriteAllTextAsync(settingsFile, "{ not valid json");
        await File.WriteAllTextAsync(settingsFile + ".bak", """
            {
              "llm_provider": "anthropic",
              "llm_model": "claude-test"
            }
            """);
        _mockStorage.Setup(x => x.GetApiKeyAsync("llm")).ReturnsAsync("current-key");

        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);

            await vm.LoadAsync();

            Assert.Equal("anthropic", vm.SelectedProvider);
            Assert.Equal("claude-test", vm.Model);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task LoadAsync_MissingSettingsFile_FallsBackToBak()
    {
        var settingsFile = CreateTempSettingsFile();
        await File.WriteAllTextAsync(settingsFile + ".bak", """
            {
              "llm_base_url": "https://proxy.example/v1"
            }
            """);

        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);

            await vm.LoadAsync();

            Assert.Equal("https://proxy.example/v1", vm.BaseUrl);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task SaveAsync_LockedBakFile_DoesNotBlockSave()
    {
        var settingsFile = CreateTempSettingsFile();
        await File.WriteAllTextAsync(settingsFile, "{\"llm_provider\":\"anthropic\"}");
        await File.WriteAllTextAsync(settingsFile + ".bak", "{\"old\":true}");
        // 独占锁定 .bak，模拟杀毒/索引器/编辑器占用导致 File.Replace 失败
        await using var lockedBak = new FileStream(
            settingsFile + ".bak", FileMode.Open, FileAccess.Read, FileShare.None);

        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                Model = "new-model"
            };

            await vm.SaveCommand.Execute();

            var json = await File.ReadAllTextAsync(settingsFile);
            Assert.Contains("\"llm_model\": \"new-model\"", json);
            Assert.Equal("设置已保存", vm.StatusMessage);
        }
        finally
        {
            lockedBak.Dispose();
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task SaveAsync_FirstSave_CreatesNoBakFile()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                Model = "first-model"
            };

            await vm.SaveCommand.Execute();

            Assert.True(File.Exists(settingsFile));
            Assert.False(File.Exists(settingsFile + ".bak"));
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task SaveAsync_SecondSave_RotatesBakToLatestContent()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            vm.Model = "model-v1";
            await vm.SaveCommand.Execute();

            vm.Model = "model-v2";
            await vm.SaveCommand.Execute();

            var bak = await File.ReadAllTextAsync(settingsFile + ".bak");
            Assert.Contains("\"llm_model\": \"model-v1\"", bak);
            Assert.Contains("\"llm_model\": \"model-v2\"", await File.ReadAllTextAsync(settingsFile));
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task LoadAsync_WithSavedCookies_ShowsLoggedInPlaceholderWithoutProxy()
    {
        _mockStorage.Setup(x => x.GetApiKeyAsync("netease-cookie")).ReturnsAsync("MUSIC_U=stub");
        _mockStorage.Setup(x => x.GetApiKeyAsync("kugou-cookie")).ReturnsAsync("kg stub");
        var accountStore = new MusicAccountStore(_mockStorage.Object);
        await accountStore.LoadAsync();
        var settingsFile = CreateTempSettingsFile();

        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile, accountStore: accountStore);

            await vm.LoadAsync();

            // 昵称查询依赖本地音乐代理，加载期只按 cookie 有无恢复基础状态，不得发网络请求
            Assert.Equal("已登录", vm.NeteaseAccountStatus);
            Assert.Equal("已登录", vm.KugouAccountStatus);
        }
        finally
        {
            File.Delete(settingsFile);
        }
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

    [Fact]
    public async Task SaveAndLoad_PersistsLyricsStagePreference()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settingsFile = Path.Combine(dir, "settings.json");

        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            vm.ShowLyricsInStage = true;
            await vm.SaveCommand.Execute();

            var reloaded = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            await reloaded.LoadAsync();

            Assert.True(reloaded.ShowLyricsInStage);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task SaveAndLoad_PersistsSpectrumStyle()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile)
            {
                SelectedSpectrumStyle = "wave"
            };
            await vm.SaveCommand.Execute();

            var reloaded = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);
            await reloaded.LoadAsync();

            Assert.Equal("wave", reloaded.SelectedSpectrumStyle);
            Assert.Equal(4, reloaded.SpectrumStyles.Count);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task LanguageChange_PreservesAndRelocalizesAccountWarning()
    {
        var accountStore = new MusicAccountStore(_mockStorage.Object);
        await accountStore.SetNeteaseCookieAsync("MUSIC_U=test-cookie");
        using var httpClient = new HttpClient(new StaticResponseHandler("{\"data\":{\"code\":200,\"profile\":null}}"));
        var vm = new SettingsViewModel(
            _mockLlm.Object,
            _mockStorage.Object,
            CreateTempSettingsFile(),
            accountStore,
            httpClient);

        try
        {
            await vm.RefreshAccountStatusAsync();
            Assert.Contains("昵称获取失败", vm.NeteaseAccountStatus);

            vm.SelectedLanguage = "en";

            Assert.Equal("Signed in (nickname unavailable; login may have expired)", vm.NeteaseAccountStatus);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply("zh");
        }
    }

    [Fact]
    public void ResetListenerProfile_SecondClickWithinWindowExecutesClear()
    {
        var profile = new Mock<IListeningProfileService>();
        // 清除后有 RefreshProfileCard 刷新：Loose Moq 会返回 null 违反 GetSnapshot 非空契约
        profile.Setup(x => x.GetSnapshot()).Returns(ListenerProfileSnapshot.Empty);
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        var originalText = vm.ResetProfileButtonText;

        try
        {
            // 首次点击：武装确认态，不执行清除
            vm.ResetListenerProfileCommand.Execute().Subscribe();
            profile.Verify(x => x.Reset(), Times.Never);
            Assert.NotEqual(originalText, vm.ResetProfileButtonText);

            // 5 秒窗口内第二次点击：执行清除并回到初始文案。
            // 命令体必须是同步的：CreateFromTask 在 Task.Delay 期间会忽略后续 Execute，确认点击会失效
            vm.ResetListenerProfileCommand.Execute().Subscribe();
            profile.Verify(x => x.Reset(), Times.Once);
            Assert.Equal(originalText, vm.ResetProfileButtonText);
        }
        finally
        {
            vm.Dispose();
        }
    }

    private static ListenerProfileSnapshot CreatePopulatedSnapshot() => new()
    {
        TotalEventsIngested = 42,
        TopArtists = new[]
        {
            new ArtistAffinity { Artist = "周杰伦", Score = 8.5, PlayCount = 32, LastPlayedAt = DateTime.Now },
            new ArtistAffinity { Artist = "林俊杰", Score = 4.2, PlayCount = 0, LastPlayedAt = DateTime.Now },
        },
        AvoidArtists = new[] { new ArtistAffinity { Artist = "张三", Score = -2.1 } },
        DislikeBlacklist = new[] { new ProfileMusicRef("晴天", "周杰伦") },
        Digest = new TasteDigestData
        {
            Text = "偏爱华语流行与抒情摇滚",
            StyleTags = new List<string> { "华语流行", "抒情" },
            GeneratedAt = new DateTime(2026, 10, 21),
        },
        CompletionRate = 0.85,
        HasCompletionSample = true,
        NewArtistRatio = 0.2,
        HasExplorationSample = true,
        DominantTimeBand = "night",
        PersonaTraits = new[] { ListeningPersona.Completionist, ListeningPersona.NightOwl },
        Impression = new ListenerImpressionData
        {
            Text = "直接爽快，偏好点歌多于闲聊",
            Traits = new List<string> { "直接", "爱点歌" },
            GeneratedAt = new DateTime(2026, 10, 22),
        },
    };

    [Fact]
    public void ProfileCard_PopulatedSnapshot_MapsAllSections()
    {
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(CreatePopulatedSnapshot());
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            // 切页命令是页 4 的刷新入口之一（另见弹层打开/开关翻转/清除）
            vm.ShowProfileTabCommand.Execute().Subscribe();

            Assert.True(vm.ProfileHasCard);
            Assert.Equal(string.Empty, vm.ProfileEmptyHint);
            Assert.True(vm.ProfileHasDigest);
            Assert.Equal("偏爱华语流行与抒情摇滚", vm.ProfileDigestText);
            Assert.Equal("更新于 2026/10/21", vm.ProfileDigestMeta);
            Assert.True(vm.ProfileHasStyleTags);
            Assert.Equal(new List<string> { "华语流行", "抒情" }, vm.ProfileStyleTagItems);
            // 听歌习惯特质经 TraitText 按界面语言映射（快照存稳定 key）
            Assert.True(vm.ProfileHasPersonaTraits);
            Assert.Equal(new List<string> { "爱完整听完", "深夜听歌" }, vm.ProfilePersonaTraitItems);
            Assert.True(vm.ProfileHasImpression);
            Assert.Equal("直接爽快，偏好点歌多于闲聊", vm.ProfileImpressionText);
            Assert.Equal("更新于 2026/10/22", vm.ProfileImpressionMeta);
            Assert.True(vm.ProfileHasImpressionTraits);
            Assert.Equal(new List<string> { "直接", "爱点歌" }, vm.ProfileImpressionTraitItems);
            Assert.True(vm.ProfileHasTopArtists);
            // 行内只列歌手名（印象优先），收听次数收进 Tooltip 详情
            Assert.Equal("周杰伦、林俊杰", vm.ProfileTopArtistsText);
            Assert.Equal("周杰伦 ×32、林俊杰", vm.ProfileTopArtistsDetail);
            Assert.True(vm.ProfileHasAvoidArtists);
            Assert.Equal("张三", vm.ProfileAvoidArtistsText);
            Assert.True(vm.ProfileHasBlacklist);
            var item = Assert.Single(vm.ProfileBlacklistItems);
            Assert.Equal("晴天 - 周杰伦", item.DisplayText);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ProfileCard_DataWithoutDigest_ShowsCardWithPlaceholderState()
    {
        // 已过冷启动有歌手/黑名单数据但 digest 未生成（LLM 未配置/归纳中/退避中）：
        // 卡片照常显示、digest 段走占位态，不得回退「还不太了解你」的虚假归因
        var snapshot = CreatePopulatedSnapshot();
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(new ListenerProfileSnapshot
        {
            TotalEventsIngested = snapshot.TotalEventsIngested,
            TopArtists = snapshot.TopArtists,
            DislikeBlacklist = snapshot.DislikeBlacklist,
        });
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            vm.ShowProfileTabCommand.Execute().Subscribe();
            Assert.True(vm.ProfileHasCard);
            Assert.False(vm.ProfileHasDigest);
            Assert.Equal(string.Empty, vm.ProfileDigestText);
            Assert.True(vm.ProfileHasTopArtists);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ProfileCard_EmptySnapshot_ShowsColdStartHintAndRelocalizes()
    {
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(ListenerProfileSnapshot.Empty);
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            vm.ShowProfileTabCommand.Execute().Subscribe();
            Assert.False(vm.ProfileHasCard);
            Assert.Contains("还不太了解你", vm.ProfileEmptyHint);

            // _onLanguageChanged 重建 VM 拼串文案（「更新于」/四态提示同源）
            AppLanguage.Apply("en");
            Assert.Contains("keep listening", vm.ProfileEmptyHint);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply("zh");
        }
    }

    [Fact]
    public void ProfileCard_DisabledToggle_ShowsPausedHint()
    {
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(CreatePopulatedSnapshot());
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            // 先证明有数据时卡片可见
            vm.ShowProfileTabCommand.Execute().Subscribe();
            Assert.True(vm.ProfileHasCard);

            // 关开关：toggle 订阅推服务 Enabled 并刷新卡片（GetSnapshot 此时返回 Empty 的暂停态）
            vm.ListenerProfileEnabled = false;
            profile.VerifySet(x => x.Enabled = false, Times.Once);
            Assert.False(vm.ProfileHasCard);
            Assert.Contains("学习已暂停", vm.ProfileEmptyHint);

            // 重开开关恢复卡片（再次刷新）
            vm.ListenerProfileEnabled = true;
            Assert.True(vm.ProfileHasCard);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ProfileCard_NullService_CommandSafeAndCardHidden()
    {
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        try
        {
            vm.ShowProfileTabCommand.Execute().Subscribe();
            Assert.False(vm.ProfileHasCard);
            Assert.Equal(string.Empty, vm.ProfileEmptyHint);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ProfileCard_ResetClearsCardBackToColdStart()
    {
        var populated = CreatePopulatedSnapshot();
        var hasData = true;
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot())
            .Returns(() => hasData ? populated : ListenerProfileSnapshot.Empty);
        profile.Setup(x => x.Reset()).Callback(() => hasData = false);
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            vm.ShowProfileTabCommand.Execute().Subscribe();
            Assert.True(vm.ProfileHasCard);

            // 二次确认清除后刷新回落冷启动态
            vm.ResetListenerProfileCommand.Execute().Subscribe();
            vm.ResetListenerProfileCommand.Execute().Subscribe();
            Assert.False(vm.ProfileHasCard);
            Assert.Contains("还不太了解你", vm.ProfileEmptyHint);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RemoveBlacklistItem_TrueBranch_ConfirmsAndRefreshesList()
    {
        var hasItem = true;
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(() => hasItem
            ? new ListenerProfileSnapshot { DislikeBlacklist = new[] { new ProfileMusicRef("晴天", "周杰伦") } }
            : ListenerProfileSnapshot.Empty);
        profile.Setup(x => x.RemoveDislikeEvents("晴天", "周杰伦"))
            .Returns(true)
            .Callback(() => hasItem = false);
        var recommendations = new Mock<IRecommendationService>();
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object, recommendationService: recommendations.Object);
        try
        {
            vm.ShowProfileTabCommand.Execute().Subscribe();
            Assert.True(vm.ProfileHasBlacklist);

            await vm.RemoveBlacklistItemCommand.Execute(new ProfileBlacklistItem("晴天", "周杰伦", "晴天 - 周杰伦"));

            profile.Verify(x => x.RemoveDislikeEvents("晴天", "周杰伦"), Times.Once);
            recommendations.Verify(x => x.RemoveDislikeFeedback("晴天", "周杰伦"), Times.Once);
            Assert.False(vm.ProfileHasBlacklist);
            Assert.Contains("已移除", vm.StatusMessage);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RemoveBlacklistItem_FalseBranch_SilentRefreshWithoutConfirmation()
    {
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(new ListenerProfileSnapshot
        {
            DislikeBlacklist = new[] { new ProfileMusicRef("晴天", "周杰伦") },
        });
        profile.Setup(x => x.RemoveDislikeEvents(It.IsAny<string?>(), It.IsAny<string?>())).Returns(false);
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            await vm.RemoveBlacklistItemCommand.Execute(new ProfileBlacklistItem("晴天", "周杰伦", "晴天 - 周杰伦"));

            // false（陈旧条目滑出 180 天窗/无匹配）：仅刷新列表，不发「已移除」的假确认
            Assert.True(vm.ProfileHasBlacklist);
            Assert.DoesNotContain("已移除", vm.StatusMessage);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RegenerateDigest_PassesForce_ReportsSuccessWhenDigestChanged()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var digestText = "旧归纳";
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(() => new ListenerProfileSnapshot
        {
            Digest = new TasteDigestData { Text = digestText },
        });
        profile.Setup(x => x.RefreshDigestAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(async (CancellationToken cancellationToken, bool force) =>
            {
                await gate.Task;
                digestText = "新归纳";
            });
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            var run = vm.RegenerateDigestCommand.Execute();
            // Execute 把命令体调度到线程池：标志翻转与本测试续体无顺序保证，
            // 带预算轮询（同 TTS 事件轮询惯例），线程池饥饿不假红
            for (var i = 0; i < 200 && !vm.IsRegeneratingDigest; i++)
                await Task.Delay(10);
            Assert.True(vm.IsRegeneratingDigest);

            gate.SetResult();
            await run;

            Assert.False(vm.IsRegeneratingDigest);
            // force 实参锚定：漏传会静默落回自动触发语义，测试必须挡住
            profile.Verify(x => x.RefreshDigestAsync(It.IsAny<CancellationToken>(), true), Times.Once);
            Assert.Contains("已重新归纳", vm.StatusMessage);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RegenerateDigest_UnchangedDigest_ReportsNotCompleted()
    {
        // 服务全内吞（LLM 未配置/归纳失败/门槛未过/在途跳过）时 digest 不变：
        // 反馈必须诚实说「未完成」而不是假装成功
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(new ListenerProfileSnapshot
        {
            Digest = new TasteDigestData { Text = "归纳" },
        });
        profile.Setup(x => x.RefreshDigestAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            await vm.RegenerateDigestCommand.Execute();
            Assert.Contains("归纳未完成", vm.StatusMessage);
            Assert.False(vm.IsRegeneratingDigest);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RegenerateDigest_AlsoForcesImpressionRefresh()
    {
        // 「重新归纳」按钮统一管口味与印象两段：impression 也必须带 force 实参
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(new ListenerProfileSnapshot
        {
            Digest = new TasteDigestData { Text = "归纳" },
        });
        profile.Setup(x => x.RefreshDigestAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        profile.Setup(x => x.RefreshImpressionAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            await vm.RegenerateDigestCommand.Execute();
            profile.Verify(x => x.RefreshImpressionAsync(It.IsAny<CancellationToken>(), true), Times.Once);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task RegenerateDigest_ColdStartDigestNoOpImpressionChanged_ReportsSuccess()
    {
        // digest 的 force 不跳 30 事件冷启动门槛（服务内静默 no-op）、印象的 force 要求有聊天
        // 素材——只聊天不收听的用户点「重新归纳」时，印象变化也必须如实报成功，
        // 防止「归纳未完成」对成功路径说谎
        var impression = new ListenerImpressionData { Text = "旧印象" };
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(() => new ListenerProfileSnapshot
        {
            TotalEventsIngested = 3,
            Impression = impression,
        });
        profile.Setup(x => x.RefreshImpressionAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback(() => impression = new ListenerImpressionData
            {
                Text = "新印象",
                GeneratedAt = DateTime.Now,
            })
            .Returns(Task.CompletedTask);
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            await vm.RegenerateDigestCommand.Execute();
            Assert.Contains("已重新归纳", vm.StatusMessage);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ProfileCard_OnlyImpression_ShowsCard()
    {
        // 只聊天不收听的新用户：性格印象有（16 条消息水位）但口味三态全空（30 事件门槛未到）
        // ——卡片必须可见，否则印象段恰好在它的目标场景不可达
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(new ListenerProfileSnapshot
        {
            Impression = new ListenerImpressionData { Text = "喜欢在深夜点老歌" },
        });
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            listeningProfile: profile.Object);
        try
        {
            vm.ShowProfileTabCommand.Execute().Subscribe();
            Assert.True(vm.ProfileHasCard);
            Assert.True(vm.ProfileHasImpression);
            Assert.Equal("喜欢在深夜点老歌", vm.ProfileImpressionText);
            Assert.False(vm.ProfileHasDigest);
            Assert.False(vm.ProfileHasTopArtists);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task DiagnoseSources_RendersPerSourceClassifiedResults()
    {
        // 聚合服务双源：网易 code=0 → 登录态/代理失效；酷狗未登录 → 未登录分类文案
        var handler = new StaticResponseHandler("{\"code\":0}");
        using var client = new HttpClient(handler);
        var musicSearch = new MusicSourceBroker(client);
        var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile(),
            musicSearch: musicSearch);

        try
        {
            await vm.DiagnoseSourcesCommand.Execute();
            Assert.Contains("未登录", vm.SourceDiagnosticsText);
            Assert.Contains("登录态或本地服务异常", vm.SourceDiagnosticsText);
            Assert.False(vm.IsDiagnosingSources);

            vm.SelectedLanguage = "en";
            // 账号状态后缀仅存在于含实验性源（Node 代理）的构建；无 Node 核心构建下不拼接
            if (vm.HasExperimentalProviders)
            {
                Assert.Contains("NetEase account:", vm.SourceDiagnosticsText);
                Assert.DoesNotContain("网易云账号：", vm.SourceDiagnosticsText);
            }
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply("zh");
        }
    }

    private sealed class StaticResponseHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => send(request);
    }

    private static Task<HttpResponseMessage> RefusedResponse()
        => Task.FromException<HttpResponseMessage>(new HttpRequestException(
            "refused", new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.ConnectionRefused)));

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static async Task WaitForStatusAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(100);
        }
        Assert.True(condition(), "status did not reach the expected value in time");
    }

    // 1x1 PNG：CreateBitmap 解码失败会自吞并继续轮询，base64 本身合法即可
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task KugouQrLogin_LocalProxyUnavailable_ShowsServiceStatusAndStopsPolling()
    {
        var checkCalls = 0;
        var handler = new RoutingHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/login/qr/check"))
            {
                checkCalls++;
                return RefusedResponse();
            }

            return Task.FromResult(JsonResponse(
                $"{{\"data\":{{\"qrcode\":\"KEY\",\"qrcode_img\":\"data:image/png;base64,{TinyPngBase64}\"}}}}"));
        });
        using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object,
            CreateTempSettingsFile(), httpClient: new HttpClient(handler));

        vm.KugouQrLoginCommand.Execute().Subscribe();
        await WaitForStatusAsync(() =>
            vm.KugouAccountStatus.Contains("不可用") || vm.KugouAccountStatus.Contains("unavailable"));

        // 轮询在服务不可用后终止：等待两个轮询周期调用数不再增长
        var callsAfterFailure = checkCalls;
        await Task.Delay(3500);
        Assert.Equal(callsAfterFailure, checkCalls);
        Assert.True(callsAfterFailure <= 2, $"unexpected poll count: {callsAfterFailure}");
    }

    [Fact]
    public async Task NeteaseQrLogin_LocalProxyUnavailable_ShowsServiceStatusAndStopsPolling()
    {
        var checkCalls = 0;
        var handler = new RoutingHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/login/qr/check"))
            {
                checkCalls++;
                return RefusedResponse();
            }

            if (path.Contains("/login/qr/create"))
                return Task.FromResult(JsonResponse(
                    $"{{\"data\":{{\"qrimg\":\"data:image/png;base64,{TinyPngBase64}\"}}}}"));

            return Task.FromResult(JsonResponse("{\"data\":{\"unikey\":\"KEY\"}}"));
        });
        using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object,
            CreateTempSettingsFile(), httpClient: new HttpClient(handler));

        vm.NeteaseQrLoginCommand.Execute().Subscribe();
        await WaitForStatusAsync(() =>
            vm.NeteaseAccountStatus.Contains("不可用") || vm.NeteaseAccountStatus.Contains("unavailable"));

        var callsAfterFailure = checkCalls;
        await Task.Delay(3500);
        Assert.Equal(callsAfterFailure, checkCalls);
        Assert.True(callsAfterFailure <= 2, $"unexpected poll count: {callsAfterFailure}");
    }

    [Fact]
    public async Task SelectingLocalProvider_EnumeratesModelsIntoDropdown()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            _mockLlm.Setup(x => x.ListLocalModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<string> { "qwen3.5:9b", "qwen3.5:4b-16k" });
            using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);

            vm.SelectedProvider = "local";

            await WaitForStatusAsync(() => vm.LocalModels.Count == 2);
            // 下拉候选按名称排序，与 ollama list 观感一致
            Assert.Equal(new[] { "qwen3.5:4b-16k", "qwen3.5:9b" }, vm.LocalModels);

            // 切回云端清空候选：不能拿本地模型名建议云端用户（误选后以 Ollama 模型名调云端 API 必失败）
            vm.SelectedProvider = "anthropic";
            await WaitForStatusAsync(() => vm.LocalModels.Count == 0);
            _mockLlm.Verify(x => x.ListLocalModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task SelectingCloudProvider_DoesNotEnumerateLocalModels()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);

            vm.SelectedProvider = "anthropic";
            // 负向断言需要给 fire-and-forget 链一个窗口（仅此处允许短等待）
            await Task.Delay(200);

            _mockLlm.Verify(x => x.ListLocalModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task EditingLocalBaseUrl_DebouncesAndUsesLatestEndpoint()
    {
        var requestedUrls = new List<string?>();
        _mockLlm.Setup(x => x.ListLocalModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string?, CancellationToken>((url, _) => requestedUrls.Add(url))
            .ReturnsAsync((string? url, CancellationToken _) => new List<string> { url! });
        using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        vm.BaseUrl = "http://localhost:11434/v1";
        vm.SelectedProvider = "local";
        await WaitForStatusAsync(() => vm.LocalModels.Contains("http://localhost:11434/v1"));

        vm.BaseUrl = "http://localhost:199";
        vm.BaseUrl = "http://localhost:19999/v1";
        Assert.Empty(vm.LocalModels);
        await WaitForStatusAsync(() => vm.LocalModels.Contains("http://localhost:19999/v1"));

        Assert.Equal(new[] { "http://localhost:11434/v1", "http://localhost:19999/v1" }, requestedUrls);
        _mockLlm.Verify(x => x.Configure(It.IsAny<LLMConfig>()), Times.Never);
    }

    [Fact]
    public async Task LocalModels_OldEndpointResponseCannotOverwriteNewEndpoint()
    {
        var oldResponse = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLlm.Setup(x => x.ListLocalModelsAsync("http://old/v1", It.IsAny<CancellationToken>()))
            .Callback(() => oldStarted.TrySetResult())
            .Returns(oldResponse.Task);
        _mockLlm.Setup(x => x.ListLocalModelsAsync("http://new/v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "new-model" });
        using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        vm.BaseUrl = "http://old/v1";
        vm.SelectedProvider = "local";
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.BaseUrl = "http://new/v1";
        await WaitForStatusAsync(() => vm.LocalModels.Contains("new-model"));
        oldResponse.SetResult(new List<string> { "old-model" });
        // 让旧请求的 await 续体跑完，再确认迟到结果没有覆盖新端点候选。
        await Task.Delay(200);
        Assert.Equal(new[] { "new-model" }, vm.LocalModels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalModels_LeavingLocalOrDisposingRejectsPendingResult(bool dispose)
    {
        var response = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLlm.Setup(x => x.ListLocalModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => started.TrySetResult())
            .Returns(response.Task);
        using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, CreateTempSettingsFile());
        vm.SelectedProvider = "local";
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (dispose)
            vm.Dispose();
        else
            vm.SelectedProvider = "openai";
        response.SetResult(new List<string> { "late-model" });
        await Task.Delay(200);

        Assert.Empty(vm.LocalModels);
    }

    [Fact]
    public async Task LoadingWithLocalProvider_EnumeratesAfterBaseUrlFullyLoaded()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            // llm_provider 在 settings 里排在 llm_base_url 之前：若枚举被加载期赋值触发，
            // 会拿着尚未读取的自定义 BaseUrl（空）走默认端点——此处锚定补拉发生在加载完成后，
            // 且经入参拿到已读齐的 BaseUrl
            File.WriteAllText(settingsFile,
                """{"llm_provider":"local","llm_base_url":"http://127.0.0.1:19999/v1","llm_model":"m"}""");
            string? requestedBaseUrl = null;
            _mockLlm.Setup(x => x.ListLocalModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string?, CancellationToken>((baseUrl, _) => requestedBaseUrl = baseUrl)
                .ReturnsAsync(new List<string>());

            using (var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile))
            {
                await vm.LoadAsync();
                await WaitForStatusAsync(() => requestedBaseUrl != null);

                Assert.Equal("http://127.0.0.1:19999/v1", requestedBaseUrl);
                // 恰好一次：加载期被 _isLoading 屏蔽 + 加载完成后补拉一次
                _mockLlm.Verify(x => x.ListLocalModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            }
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task SelectingLocalProvider_DoesNotTouchLiveServiceConfig()
    {
        // 翻看 provider 下拉（不保存）不能改运行中服务的聊天配置：
        // 电台在播时改配置会把 DJ 链路打到错误端点，且切回云端不恢复
        var settingsFile = CreateTempSettingsFile();
        try
        {
            _mockLlm.Setup(x => x.ListLocalModelsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<string> { "m" });
            using var vm = new SettingsViewModel(_mockLlm.Object, _mockStorage.Object, settingsFile);

            vm.SelectedProvider = "local";
            await WaitForStatusAsync(() => vm.LocalModels.Count == 1);
            vm.SelectedProvider = "anthropic";
            await Task.Delay(200);

            _mockLlm.Verify(x => x.Configure(It.IsAny<LLMConfig>()), Times.Never);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }
}
