using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class VoiceOption
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
}

public class SettingsViewModel : ViewModelBase, IDisposable
{
    private const string LlmCredentialService = "llm";
    private const string LegacyMinimaxCredentialService = "minimax";
    private readonly ILLMService _llmService;
    private readonly ISecureStorage _secureStorage;
    private readonly string _settingsDir;
    private readonly string _settingsFile;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly IDisposable _selectedCharacterSub;
    private readonly IDisposable _providerSub;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _isLoadingSettings;
    private int _disposed;
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    // Per-character overrides: character id → (voiceId, personalityPrompt)
    private readonly Dictionary<string, (string VoiceId, string Personality)> _overrides = new();

    [Reactive] public string ApiKey { get; set; } = string.Empty;
    [Reactive] public string SelectedProvider { get; set; } = "openai";
    [Reactive] public string BaseUrl { get; set; } = string.Empty;
    [Reactive] public string Model { get; set; } = string.Empty;
    [Reactive] public string StatusMessage { get; set; } = string.Empty;
    [Reactive] public bool IsTesting { get; set; }
    [Reactive] public string TestConnectionButtonText { get; set; } = "测试连接";
    [Reactive] public bool TtsEnabled { get; set; } = true;
    [Reactive] public bool IsDarkMode { get; set; } = true;
    [Reactive] public bool EnableStarfield { get; set; } = true;
    [Reactive] public string SpeechMixMode { get; set; } = "duck";
    [Reactive] public string SelectedLanguage { get; set; } = "zh"; // "zh" or "en"

    // Character customization
    public List<CharacterProfile> Characters { get; } = CharacterProfile.Presets;
    [Reactive] public CharacterProfile? SelectedCharacter { get; set; }
    [Reactive] public VoiceOption? CharacterVoice { get; set; }
    [Reactive] public string CharacterPersonality { get; set; } = string.Empty;

    public List<VoiceOption> Voices { get; } = new()
    {
        new() { Id = "male-qn-qingse", DisplayName = "青涩男声" },
        new() { Id = "male-qn-jingying", DisplayName = "精英男声" },
        new() { Id = "male-qn-badao", DisplayName = "霸道男声" },
        new() { Id = "female-shaonv", DisplayName = "少女音" },
        new() { Id = "female-yujie", DisplayName = "御姐音" },
        new() { Id = "female-chengshu", DisplayName = "成熟女声" },
    };

    public List<VoiceOption> Languages { get; } = new()
    {
        new() { Id = "zh", DisplayName = "中文" },
        new() { Id = "en", DisplayName = "English" },
    };

    public List<VoiceOption> LlmProviders { get; } = new()
    {
        new() { Id = "openai", DisplayName = "OpenAI 兼容格式" },
        new() { Id = "anthropic", DisplayName = "Anthropic 兼容格式" },
        new() { Id = "local", DisplayName = "本地模型" },
    };

    public List<VoiceOption> SpeechMixModes { get; } = new()
    {
        new() { Id = "duck", DisplayName = "说话时降低音乐音量" },
        new() { Id = "pause", DisplayName = "说话时暂停音乐" },
    };

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    // Notify MainWindow when character settings change so it can re-apply
    public event Action? CharacterSettingsChanged;

    public SettingsViewModel(ILLMService llmService, ISecureStorage secureStorage, string? settingsFile = null)
    {
        _llmService = llmService;
        _secureStorage = secureStorage;
        _settingsFile = settingsFile ?? SettingsFile;
        _settingsDir = Path.GetDirectoryName(_settingsFile) ?? SettingsDir;

        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);

        // When character selection changes, load its overrides
        _selectedCharacterSub = this.WhenAnyValue(x => x.SelectedCharacter)
            .Where(c => c != null)
            .Subscribe(c => LoadCharacterOverrides(c!));

        _providerSub = this.WhenAnyValue(x => x.SelectedProvider)
            .Skip(1)
            .Subscribe(_ =>
            {
                if (_isLoadingSettings)
                    return;

                ApiKey = string.Empty;
                BaseUrl = string.Empty;
                Model = string.Empty;
            });

        // Default selection
        SelectedCharacter = Characters[0];
    }

    private void LoadCharacterOverrides(CharacterProfile character)
    {
        if (_overrides.TryGetValue(character.Id, out var ov))
        {
            CharacterVoice = Voices.Find(v => v.Id == ov.VoiceId) ?? Voices.Find(v => v.Id == character.VoiceId) ?? Voices[0];
            CharacterPersonality = ov.Personality;
        }
        else
        {
            CharacterVoice = Voices.Find(v => v.Id == character.VoiceId) ?? Voices[0];
            CharacterPersonality = character.PersonalityPrompt;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _isLoadingSettings = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await _secureStorage.GetApiKeyAsync(LlmCredentialService);
            if (!string.IsNullOrEmpty(key))
            {
                ApiKey = key;
            }

            if (File.Exists(_settingsFile))
            {
                var json = await File.ReadAllTextAsync(_settingsFile, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("llm_provider", out var provider))
                    SelectedProvider = NormalizeProvider(provider.GetString());

                if (root.TryGetProperty("llm_base_url", out var baseUrl))
                    BaseUrl = baseUrl.GetString() ?? string.Empty;

                if (root.TryGetProperty("llm_model", out var model))
                    Model = model.GetString() ?? string.Empty;

                if (root.TryGetProperty("language", out var lang))
                    SelectedLanguage = lang.GetString() ?? "zh";

                if (root.TryGetProperty("tts_enabled", out var tts))
                    TtsEnabled = tts.GetBoolean();

                if (root.TryGetProperty("is_dark_mode", out var dark))
                    IsDarkMode = dark.GetBoolean();

                if (root.TryGetProperty("enable_starfield", out var starfield))
                    EnableStarfield = starfield.GetBoolean();

                if (root.TryGetProperty("speech_mix_mode", out var speechMode))
                    SpeechMixMode = speechMode.GetString() == "pause" ? "pause" : "duck";

                if (root.TryGetProperty("character_overrides", out var ovElem))
                {
                    foreach (var prop in ovElem.EnumerateObject())
                    {
                        var voiceId = prop.Value.TryGetProperty("voice_id", out var v) ? v.GetString() ?? "" : "";
                        var personality = prop.Value.TryGetProperty("personality", out var p) ? p.GetString() ?? "" : "";
                        _overrides[prop.Name] = (voiceId, personality);
                    }
                }
            }

            // Apply first character
            if (SelectedCharacter != null)
                LoadCharacterOverrides(SelectedCharacter);

            ConfigureLlm();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings");
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    public (string VoiceId, string Personality)? GetOverride(string characterId)
    {
        return _overrides.TryGetValue(characterId, out var ov) ? ov : null;
    }

    private async Task TestConnectionAsync()
    {
        if (RequiresApiKey(SelectedProvider) && string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusMessage = "请先输入 API Key";
            return;
        }
        if (string.IsNullOrWhiteSpace(Model))
        {
            StatusMessage = "请先输入模型名称";
            return;
        }

        IsTesting = true;
        TestConnectionButtonText = "正在测试...";
        StatusMessage = "正在测试连接...";
        try
        {
            NormalizeLlmInputs();
            ConfigureLlm(ApiKey);
            var result = await _llmService.ChatAsync("你好，请用一句话回复", new List<ChatMessage>());
            StatusMessage = $"连接成功：{result[..Math.Min(50, result.Length)]}...";
        }
        catch (Exception ex)
        {
            var failure = ApiFailureInfo.FromException(ex);
            Log.Error(ex, "AI service API error");
            StatusMessage = $"连接失败：{failure.Title}。{failure.RecoveryHint}";
        }
        finally
        {
            IsTesting = false;
            TestConnectionButtonText = "测试连接";
        }
    }

    private async Task SaveAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var gateHeld = false;
        try
        {
            await _saveGate.WaitAsync(_lifetimeCts.Token);
            gateHeld = true;
            if (Volatile.Read(ref _disposed) != 0)
                return;

            NormalizeLlmInputs();

            // Save current character overrides
            if (SelectedCharacter != null && CharacterVoice != null)
            {
                _overrides[SelectedCharacter.Id] = (CharacterVoice.Id, CharacterPersonality);
            }

            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                await _secureStorage.SaveApiKeyAsync(LlmCredentialService, ApiKey);
            }
            else
            {
                _secureStorage.DeleteApiKey(LlmCredentialService);
            }
            _secureStorage.DeleteApiKey(LegacyMinimaxCredentialService);

            ConfigureLlm();

            Directory.CreateDirectory(_settingsDir);
            var overridesJson = new Dictionary<string, object>();
            foreach (var kv in _overrides)
            {
                overridesJson[kv.Key] = new { voice_id = kv.Value.VoiceId, personality = kv.Value.Personality };
            }

            var settingsData = new
            {
                llm_provider = SelectedProvider,
                llm_base_url = BaseUrl,
                llm_model = Model,
                tts_enabled = TtsEnabled,
                is_dark_mode = IsDarkMode,
                enable_starfield = EnableStarfield,
                speech_mix_mode = SpeechMixMode,
                language = SelectedLanguage,
                character_overrides = overridesJson
            };
            // Settings stored as plaintext JSON in %APPDATA%; API key is in Windows Credential Manager
            var json = JsonSerializer.Serialize(settingsData, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _settingsFile + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, _lifetimeCts.Token);
            File.Move(tempPath, _settingsFile, overwrite: true);

            CharacterSettingsChanged?.Invoke();
            StatusMessage = "设置已保存";
            Log.Information("Settings saved to {Path}", _settingsFile);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 关闭时取消排队保存，避免 Dispose 后继续写盘。
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally
        {
            if (gateHeld)
                _saveGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        _selectedCharacterSub.Dispose();
        _providerSub.Dispose();
    }

    private void ConfigureLlm(string? apiKeyOverride = null)
    {
        _llmService.Configure(new LLMConfig
        {
            Provider = NormalizeProvider(SelectedProvider),
            ApiKey = (apiKeyOverride ?? ApiKey ?? string.Empty).Trim(),
            BaseUrl = (BaseUrl ?? string.Empty).Trim(),
            Model = (Model ?? string.Empty).Trim()
        });
    }

    private void NormalizeLlmInputs()
    {
        ApiKey = (ApiKey ?? string.Empty).Trim();
        BaseUrl = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        Model = (Model ?? string.Empty).Trim();
    }

    private static string NormalizeProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        "claude" or "anthropic" => "anthropic",
        "ollama" or "local" => "local",
        _ => "openai"
    };

    private static bool RequiresApiKey(string provider)
        => !string.Equals(NormalizeProvider(provider), "local", StringComparison.OrdinalIgnoreCase);
}
