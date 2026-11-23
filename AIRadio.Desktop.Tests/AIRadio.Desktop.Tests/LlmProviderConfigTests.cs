using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class LlmProviderConfigTests
{
    private static string CreateTempSettingsFile()
        => Path.Combine(Path.GetTempPath(), $"airadio-llm-cfg-{Guid.NewGuid():N}.json");

    private static SettingsViewModel CreateVm(
        out Mock<ILLMService> mockLlm,
        out Mock<ISecureStorage> mockStorage,
        string settingsFile)
    {
        mockLlm = new Mock<ILLMService>();
        mockLlm.Setup(x => x.ListModelsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        mockStorage = new Mock<ISecureStorage>();
        mockStorage.Setup(x => x.GetApiKeyAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        mockStorage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        return new SettingsViewModel(mockLlm.Object, mockStorage.Object, settingsFile);
    }

    [Fact]
    public void Catalog_AllPresetsAndEndpoints_AreValidAndComplete()
    {
        var presetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in LlmProviderCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Id));
            Assert.True(presetIds.Add(preset.Id), $"Duplicate preset id: {preset.Id}");
            Assert.False(string.IsNullOrWhiteSpace(preset.NameZh));
            Assert.False(string.IsNullOrWhiteSpace(preset.NameEn));

            if (preset.IsCustom)
            {
                Assert.Equal(LlmProviderCatalog.CustomId, preset.Id);
                Assert.Empty(preset.Endpoints);
                continue;
            }

            Assert.NotEmpty(preset.Endpoints);
            var endpointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in preset.Endpoints)
            {
                Assert.False(string.IsNullOrWhiteSpace(endpoint.Id));
                Assert.True(endpointIds.Add(endpoint.Id), $"Duplicate endpoint id {endpoint.Id} in {preset.Id}");
                Assert.False(string.IsNullOrWhiteSpace(endpoint.NameZh));
                Assert.False(string.IsNullOrWhiteSpace(endpoint.NameEn));
                Assert.Contains(endpoint.Protocol, new[] { "openai", "anthropic", "local" });
                Assert.True(
                    endpoint.BaseUrl.StartsWith("https://", StringComparison.Ordinal) ||
                    endpoint.BaseUrl.StartsWith("http://", StringComparison.Ordinal),
                    $"Invalid BaseUrl in {preset.Id}.{endpoint.Id}: {endpoint.BaseUrl}");
                Assert.False(endpoint.BaseUrl.EndsWith('/'), $"Trailing slash in {preset.Id}.{endpoint.Id}");
            }
        }

        // 国内多协议主流服务商必须同时具备 OpenAI 与 Anthropic 兼容端点
        foreach (var multiProtocolId in new[] { "deepseek", "zhipu", "kimi", "qwen", "minimax" })
        {
            var preset = LlmProviderCatalog.FindPreset(multiProtocolId);
            Assert.NotNull(preset);
            Assert.Contains(preset!.Endpoints, e => e.Protocol == "openai");
            Assert.Contains(preset.Endpoints, e => e.Protocol == "anthropic");
        }

        // Coding Plan 专属端点覆盖
        Assert.Contains(LlmProviderCatalog.FindPreset("zhipu")!.Endpoints, e => e.Id == "coding-openai");
        Assert.Contains(LlmProviderCatalog.FindPreset("kimi")!.Endpoints, e => e.Id == "coding-anthropic");
        var qwen = LlmProviderCatalog.FindPreset("qwen")!;
        Assert.Contains(qwen.Endpoints, e => e.Id == "coding-openai" && !string.IsNullOrEmpty(e.HintZh));
        Assert.Contains(qwen.Endpoints, e => e.Id == "coding-anthropic" && !string.IsNullOrEmpty(e.HintEn));
    }

    [Theory]
    [InlineData("openai", "https://api.deepseek.com/v1", "deepseek", "openai")]
    [InlineData("openai", "https://api.deepseek.com/v1/", "deepseek", "openai")]
    [InlineData("openai", "https://api.deepseek.com/v1/chat/completions", "deepseek", "openai")]
    [InlineData("openai", "https://api.deepseek.com", "deepseek", "openai")]
    [InlineData("anthropic", "https://api.deepseek.com/anthropic", "deepseek", "anthropic")]
    [InlineData("anthropic", "https://api.deepseek.com/anthropic/v1/messages", "deepseek", "anthropic")]
    [InlineData("openai", "https://open.bigmodel.cn/api/coding/paas/v4", "zhipu", "coding-openai")]
    [InlineData("anthropic", "https://open.bigmodel.cn/api/anthropic/v1", "zhipu", "anthropic")]
    [InlineData("anthropic", "https://api.kimi.com/coding/", "kimi", "coding-anthropic")]
    [InlineData("anthropic", "https://coding.dashscope.aliyuncs.com/apps/anthropic/v1/messages", "qwen", "coding-anthropic")]
    [InlineData("openai", "https://api.minimaxi.com/v1", "minimax", "openai")]
    [InlineData("anthropic", "https://api.anthropic.com", "anthropic", "anthropic")]
    [InlineData("openai", "https://api.openai.com", "openai", "openai")]
    [InlineData("openai", "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini", "openai")]
    [InlineData("local", "http://localhost:11434", "ollama", "ollama")]
    [InlineData("local", "http://127.0.0.1:11434", "ollama", "ollama")]
    [InlineData("local", "http://localhost:1234", "ollama", "lmstudio")]
    [InlineData("local", "http://localhost:1234/v1", "ollama", "lmstudio")]
    [InlineData("openai", "", "custom", "")]
    [InlineData("openai", "https://my-private-proxy.example/v1", "custom", "")]
    public void InferPresetFromConfig_ResolvesExpectedPresetAndEndpoint(
        string provider,
        string baseUrl,
        string expectedPresetId,
        string expectedEndpointId)
    {
        var (presetId, endpointId) = LlmProviderCatalog.InferPresetFromConfig(provider, baseUrl);
        Assert.Equal(expectedPresetId, presetId);
        Assert.Equal(expectedEndpointId, endpointId);
    }

    [Fact]
    public void SwitchingEndpoint_SharesKeyWithinSameDomain_AndIsolatesKeysAcrossCodingAndIntlDomains()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(out _, out _, settingsFile);
            vm.ApplyLlmPresetCommand.Execute("qwen").Subscribe();
            vm.ApiKey = "sk-qwen-standard";
            Assert.Equal("openai", vm.SelectedProvider);
            Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1", vm.BaseUrl);
            Assert.Equal("qwen-plus", vm.Model);
            Assert.False(vm.HasEndpointHint);

            // 同计费域（通用 OpenAI <-> 通用 Anthropic）共享同一 API Key
            vm.SelectedEndpointId = "anthropic";
            Assert.Equal("anthropic", vm.SelectedProvider);
            Assert.Equal("https://dashscope.aliyuncs.com/apps/anthropic", vm.BaseUrl);
            Assert.Equal("sk-qwen-standard", vm.ApiKey);

            // 切到 Coding Plan OpenAI 端点：独立计费域不串用通用 Key，默认模型自动切为兼容编码模型、提示可见
            vm.SelectedEndpointId = "coding-openai";
            Assert.Equal("openai", vm.SelectedProvider);
            Assert.Equal("https://coding.dashscope.aliyuncs.com/v1", vm.BaseUrl);
            Assert.Equal("qwen3-coder-plus", vm.Model);
            Assert.True(vm.HasEndpointHint);
            Assert.Contains("sk-sp-", vm.EndpointHint);
            Assert.Equal(string.Empty, vm.ApiKey);

            // 在 Coding Plan 域填入专属 Key，并在 Coding Plan 域内切换协议（coding-openai <-> coding-anthropic）共享该 Key
            vm.ApiKey = "sk-sp-coding-key";
            vm.Model = "my-custom-qwen-finetune";
            vm.SelectedEndpointId = "coding-anthropic";
            Assert.Equal("anthropic", vm.SelectedProvider);
            Assert.Equal("https://coding.dashscope.aliyuncs.com/apps/anthropic", vm.BaseUrl);
            Assert.Equal("my-custom-qwen-finetune", vm.Model);
            Assert.Equal("sk-sp-coding-key", vm.ApiKey);

            // 切回通用端点：自动恢复通用 Key，两套套餐 Key 互不覆盖
            vm.SelectedEndpointId = "openai";
            Assert.Equal("sk-qwen-standard", vm.ApiKey);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public void SwitchingProtocolOnPreset_MapsMatchingEndpointOrDetachesToCustom()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(out _, out _, settingsFile);

            // 通义千问在 Coding Plan 下切换协议：openai <-> anthropic 保持 Coding Plan 变体语义
            vm.ApplyLlmPresetCommand.Execute("qwen").Subscribe();
            vm.SelectedEndpointId = "coding-openai";
            vm.ApiKey = "sk-sp-coding";
            vm.SelectedProvider = "anthropic";
            Assert.Equal("qwen", vm.ActivePresetId);
            Assert.Equal("coding-anthropic", vm.SelectedEndpointId);
            Assert.Equal("https://coding.dashscope.aliyuncs.com/apps/anthropic", vm.BaseUrl);
            Assert.Equal("sk-sp-coding", vm.ApiKey);

            // Kimi 国际版切换协议：保持 -intl 区域语义
            vm.ApplyLlmPresetCommand.Execute("kimi").Subscribe();
            vm.SelectedEndpointId = "openai-intl";
            vm.SelectedProvider = "anthropic";
            Assert.Equal("kimi", vm.ActivePresetId);
            Assert.Equal("anthropic-intl", vm.SelectedEndpointId);
            Assert.Equal("https://api.moonshot.ai/anthropic", vm.BaseUrl);

            // 硅基流动不支持 local 协议：保存原预设档案并切回 custom 模式
            vm.ApplyLlmPresetCommand.Execute("siliconflow").Subscribe();
            vm.ApiKey = "sk-sf-123";
            vm.SelectedProvider = "local";
            Assert.Equal(LlmProviderCatalog.CustomId, vm.ActivePresetId);
            Assert.Equal("local", vm.SelectedProvider);
            Assert.False(vm.LlmNeedsApiKey);

            // 切回硅基流动时，之前的 Key 与地址仍然保留
            vm.ApplyLlmPresetCommand.Execute("siliconflow").Subscribe();
            Assert.Equal("sk-sf-123", vm.ApiKey);
            Assert.Equal("https://api.siliconflow.cn/v1", vm.BaseUrl);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task PresetProfiles_RoundTripThroughDiskAndDedicatedCredentialSlots_WithoutLeakingSecrets()
    {
        var settingsFile = CreateTempSettingsFile();
        var storedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using (var vm = CreateVm(out _, out var storage, settingsFile))
            {
                storage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
                    .Callback<string, string>((slot, key) => storedKeys[slot] = key)
                    .Returns(Task.CompletedTask);

                await vm.LoadAsync();
                vm.ApplyLlmPresetCommand.Execute("deepseek").Subscribe();
                vm.SelectedEndpointId = "anthropic";
                vm.Model = "deepseek-reasoner";
                vm.ApiKey = "sk-ds-secret";

                vm.ApplyLlmPresetCommand.Execute("zhipu").Subscribe();
                vm.ApiKey = "sk-zp-standard";
                vm.SelectedEndpointId = "coding-openai";
                vm.ApiKey = "sk-zp-secret";

                await vm.SaveCommand.Execute();
            }

            var json = await File.ReadAllTextAsync(settingsFile);
            Assert.DoesNotContain("sk-ds-secret", json);
            Assert.DoesNotContain("sk-zp-standard", json);
            Assert.DoesNotContain("sk-zp-secret", json);
            Assert.Equal("sk-ds-secret", storedKeys["llm.preset.deepseek"]);
            Assert.Equal("sk-zp-standard", storedKeys["llm.preset.zhipu"]);
            Assert.Equal("sk-zp-secret", storedKeys["llm.preset.zhipu.coding"]);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("zhipu", doc.RootElement.GetProperty("llm_active_preset").GetString());
            Assert.Equal("openai", doc.RootElement.GetProperty("llm_provider").GetString());
            Assert.Equal("https://open.bigmodel.cn/api/coding/paas/v4", doc.RootElement.GetProperty("llm_base_url").GetString());

            // 重载后恢复活动服务商及未激活服务商的配置与各自密钥
            using var reloaded = CreateVm(out _, out var reloadStorage, settingsFile);
            reloadStorage.Setup(x => x.GetApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync((string slot) => storedKeys.GetValueOrDefault(slot));
            await reloaded.LoadAsync();

            Assert.Equal("zhipu", reloaded.ActivePresetId);
            Assert.Equal("coding-openai", reloaded.SelectedEndpointId);
            Assert.Equal("sk-zp-secret", reloaded.ApiKey);

            reloaded.ApplyLlmPresetCommand.Execute("deepseek").Subscribe();
            for (var i = 0; i < 100 && reloaded.ApiKey != "sk-ds-secret"; i++)
                await Task.Delay(20);
            Assert.Equal("deepseek", reloaded.ActivePresetId);
            Assert.Equal("anthropic", reloaded.SelectedEndpointId);
            Assert.Equal("deepseek-reasoner", reloaded.Model);
            Assert.Equal("sk-ds-secret", reloaded.ApiKey);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task SaveUiStateCommand_PassesThroughDiskPresetProfiles_AndNonAsciiKeyInInactivePresetBlocksWrites()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            await File.WriteAllTextAsync(settingsFile, """
                {
                  "llm_provider": "openai",
                  "llm_base_url": "https://api.deepseek.com/v1",
                  "llm_model": "deepseek-chat",
                  "llm_active_preset": "deepseek",
                  "llm_preset_profiles": {
                    "zhipu": {
                      "endpoint_id": "coding-openai",
                      "provider": "openai",
                      "base_url": "https://open.bigmodel.cn/api/coding/paas/v4",
                      "model": "glm-4.7"
                    }
                  }
                }
                """);

            using var vm = CreateVm(out _, out var storage, settingsFile);
            await vm.LoadAsync();

            // 内存里切到 zhipu 并放入未保存的修改 + 坏 Key，先测 UI 自动保存透传磁盘原样
            vm.ApplyLlmPresetCommand.Execute("zhipu").Subscribe();
            vm.BaseUrl = "https://unsaved-dirty.example/v1";
            vm.ApiKey = "sk-含中文坏密钥";
            vm.IsDarkMode = false;
            await vm.SaveUiStateCommand.Execute();

            var afterUiSave = await File.ReadAllTextAsync(settingsFile);
            Assert.DoesNotContain("unsaved-dirty", afterUiSave);
            Assert.Contains("\"llm_active_preset\": \"deepseek\"", afterUiSave);
            storage.Verify(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

            // 切回 deepseek（坏 Key 留在非激活的 zhipu 档案中），显式保存必须整批拦截
            vm.ApplyLlmPresetCommand.Execute("deepseek").Subscribe();
            vm.ApiKey = "sk-good-ds";
            await vm.SaveCommand.Execute();

            Assert.Contains("非 ASCII", vm.StatusMessage);
            storage.Verify(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            var afterBlockedSave = await File.ReadAllTextAsync(settingsFile);
            Assert.DoesNotContain("unsaved-dirty", afterBlockedSave);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task LoadAsync_LegacySettingsWithoutActivePreset_InfersPresetAndMigratesKeyOnce()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            await File.WriteAllTextAsync(settingsFile, """
                {
                  "llm_provider": "openai",
                  "llm_base_url": "https://api.deepseek.com/v1",
                  "llm_model": "deepseek-chat"
                }
                """);

            using var vm = CreateVm(out _, out var storage, settingsFile);
            storage.Setup(x => x.GetApiKeyAsync("llm.preset.deepseek")).ReturnsAsync((string?)null);
            storage.Setup(x => x.GetApiKeyAsync("llm.openai")).ReturnsAsync("sk-legacy-ds");

            await vm.LoadAsync();

            Assert.Equal("deepseek", vm.ActivePresetId);
            Assert.Equal("openai", vm.SelectedEndpointId);
            Assert.Equal("sk-legacy-ds", vm.ApiKey);
            Assert.True(vm.HasMultiplePresetEndpoints);
            storage.Verify(x => x.SaveApiKeyAsync("llm.preset.deepseek", "sk-legacy-ds"), Times.Once);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task RefreshModelsAndSelectModel_MergesRemoteAndRecommendedWithoutDuplicates_AndLocalizesStatus()
    {
        var settingsFile = CreateTempSettingsFile();
        var originalLang = AppLanguage.Current;
        try
        {
            AppLanguage.Apply("zh");
            using var vm = CreateVm(out var mockLlm, out _, settingsFile);
            mockLlm.Setup(x => x.ListModelsAsync("openai", "https://api.deepseek.com/v1", "sk-ds", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<string> { "deepseek-reasoner", "deepseek-v4-custom" });

            vm.ApplyLlmPresetCommand.Execute("deepseek").Subscribe();

            // 缺少 Key 时主动点击拉取模型，提示用户输入 Key 且不发请求
            await vm.RefreshModelsCommand.Execute();
            Assert.True(vm.HasModelFetchStatus);
            Assert.Contains("API Key", vm.ModelFetchStatus);
            mockLlm.Verify(x => x.ListModelsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

            // 填入 Key 后主动拉取：合并远端模型与预设推荐模型并去重
            vm.ApiKey = "sk-ds";
            await vm.RefreshModelsCommand.Execute();
            Assert.Equal(new[] { "deepseek-reasoner", "deepseek-v4-custom", "deepseek-chat" }, vm.AvailableModels);
            Assert.Contains("2 个可用模型", vm.ModelFetchStatus);
            Assert.False(vm.IsFetchingModels);

            // 切换语言时拉取状态同步重算
            AppLanguage.Apply("en");
            Assert.Contains("Fetched 2 available models", vm.ModelFetchStatus);

            // 点选模型同步更新 Model 与芯片选中高亮
            vm.SelectModelCommand.Execute("deepseek-reasoner").Subscribe();
            Assert.Equal("deepseek-reasoner", vm.Model);
            Assert.True(vm.QuickModelOptions.First(m => m.ModelId == "deepseek-reasoner").IsSelected);
            Assert.False(vm.QuickModelOptions.First(m => m.ModelId == "deepseek-chat").IsSelected);
            Assert.Equal("Live", vm.AvailableModelOptions.First(m => m.ModelId == "deepseek-v4-custom").SourceLabel);
            Assert.Equal("Preset", vm.AvailableModelOptions.First(m => m.ModelId == "deepseek-chat").SourceLabel);
        }
        finally
        {
            AppLanguage.Apply(originalLang);
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task EmptyBaseUrlOnNamedPreset_FallsBackToActiveEndpointDefault_WithoutLeakingKeyToOfficialOpenAi()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(out var mockLlm, out _, settingsFile);
            LLMConfig? capturedConfig = null;
            mockLlm.Setup(x => x.Configure(It.IsAny<LLMConfig>()))
                .Callback<LLMConfig>(cfg => capturedConfig = cfg);
            mockLlm.Setup(x => x.ListModelsAsync("openai", "https://api.deepseek.com/v1", "sk-ds-key", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<string> { "deepseek-chat" });

            await vm.LoadAsync();
            vm.ApplyLlmPresetCommand.Execute("deepseek").Subscribe();
            vm.ApiKey = "sk-ds-key";
            // 用户清空输入框（依赖水印提示的"留空使用 https://api.deepseek.com/v1"）
            vm.BaseUrl = "   ";

            await vm.RefreshModelsCommand.Execute();
            mockLlm.Verify(
                x => x.ListModelsAsync("openai", "https://api.deepseek.com/v1", "sk-ds-key", It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);

            await vm.SaveCommand.Execute();
            Assert.NotNull(capturedConfig);
            Assert.Equal("https://api.deepseek.com/v1", capturedConfig!.BaseUrl);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public async Task SwitchingPresetDuringInFlightManualModelFetch_CancelsRequestAndReleasesIsFetchingModelsImmediately()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(out var mockLlm, out _, settingsFile);
            var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken capturedToken = default;

            mockLlm.Setup(x => x.ListModelsAsync("openai", "https://api.deepseek.com/v1", "sk-slow", It.IsAny<CancellationToken>()))
                .Returns(async (string? _, string? _, string? _, CancellationToken ct) =>
                {
                    capturedToken = ct;
                    fetchStarted.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return new List<string> { "stale-model" };
                });

            vm.ApplyLlmPresetCommand.Execute("deepseek").Subscribe();
            vm.ApiKey = "sk-slow";

            var manualFetchTask = vm.RefreshModelsCommand.Execute().ToTask();
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(vm.IsFetchingModels);

            // 在途拉取未完成时切换到智谱：旧请求应被立即取消且按钮立即恢复可点击
            vm.ApplyLlmPresetCommand.Execute("zhipu").Subscribe();
            Assert.False(vm.IsFetchingModels);
            Assert.True(capturedToken.IsCancellationRequested);

            await manualFetchTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(vm.IsFetchingModels);
            Assert.DoesNotContain("stale-model", vm.AvailableModels);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task LoadAsync_ContradictoryActivePresetAndProvider_SelfHealsToConsistentState()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            await File.WriteAllTextAsync(settingsFile, """
                {
                  "llm_provider": "local",
                  "llm_base_url": "http://127.0.0.1:9999/v1",
                  "llm_model": "my-local-gguf",
                  "llm_active_preset": "deepseek"
                }
                """);

            using var vm = CreateVm(out _, out _, settingsFile);
            await vm.LoadAsync();

            Assert.Equal(LlmProviderCatalog.CustomId, vm.ActivePresetId);
            Assert.Equal(string.Empty, vm.SelectedEndpointId);
            Assert.Equal("local", vm.SelectedProvider);
            Assert.False(vm.HasMultiplePresetEndpoints);
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }
}
