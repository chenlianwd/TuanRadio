using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Avalonia.Threading;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class VoiceOption
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class SettingsViewModel : ViewModelBase
{
    private readonly IMinimaxService _minimaxService;
    private readonly IDJService _djService;
    private readonly ISecureStorage _secureStorage;
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    // Per-character overrides: character id → (voiceId, personalityPrompt)
    private readonly Dictionary<string, (string VoiceId, string Personality)> _overrides = new();

    [Reactive] public string ApiKey { get; set; } = string.Empty;
    [Reactive] public string StatusMessage { get; set; } = string.Empty;
    [Reactive] public bool IsTesting { get; set; }
    [Reactive] public bool TtsEnabled { get; set; } = true;
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

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    // Notify MainWindow when character settings change so it can re-apply
    public event Action? CharacterSettingsChanged;

    public SettingsViewModel(IMinimaxService minimaxService, IDJService djService, ISecureStorage secureStorage)
    {
        _minimaxService = minimaxService;
        _djService = djService;
        _secureStorage = secureStorage;

        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);

        // When character selection changes, load its overrides
        this.WhenAnyValue(x => x.SelectedCharacter)
            .Where(c => c != null)
            .Subscribe(c => LoadCharacterOverrides(c!));

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

    public async Task LoadAsync()
    {
        try
        {
            var key = await _secureStorage.GetApiKeyAsync("minimax");
            if (!string.IsNullOrEmpty(key))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApiKey = key);
                _minimaxService.SetApiKey(key);
            }

            if (File.Exists(SettingsFile))
            {
                var json = await File.ReadAllTextAsync(SettingsFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("language", out var lang))
                    SelectedLanguage = lang.GetString() ?? "zh";

                if (root.TryGetProperty("tts_enabled", out var tts))
                    TtsEnabled = tts.GetBoolean();

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
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings");
        }
    }

    public (string VoiceId, string Personality)? GetOverride(string characterId)
    {
        return _overrides.TryGetValue(characterId, out var ov) ? ov : null;
    }

    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "请先输入 API Key");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsTesting = true;
            StatusMessage = "正在测试连接...";
        });
        try
        {
            _minimaxService.SetApiKey(ApiKey);
            var result = await _minimaxService.ChatAsync("你好，请用一句话回复", new List<ChatMessage>());
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"连接成功：{result[..Math.Min(50, result.Length)]}...");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Minimax API error");
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"连接失败：{ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsTesting = false);
        }
    }

    private async Task SaveAsync()
    {
        // Save current character overrides
        if (SelectedCharacter != null && CharacterVoice != null)
        {
            _overrides[SelectedCharacter.Id] = (CharacterVoice.Id, CharacterPersonality);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                await _secureStorage.SaveApiKeyAsync("minimax", ApiKey);
                _minimaxService.SetApiKey(ApiKey);
            }

            Directory.CreateDirectory(SettingsDir);
            var overridesJson = new Dictionary<string, object>();
            foreach (var kv in _overrides)
            {
                overridesJson[kv.Key] = new { voice_id = kv.Value.VoiceId, personality = kv.Value.Personality };
            }

            var settingsData = new
            {
                tts_enabled = TtsEnabled,
                language = SelectedLanguage,
                character_overrides = overridesJson
            };
            var json = JsonSerializer.Serialize(settingsData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(SettingsFile, json);

            CharacterSettingsChanged?.Invoke();
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "设置已保存");
            Log.Information("Settings saved to {Path}", SettingsFile);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"保存失败：{ex.Message}");
        }
    }
}
