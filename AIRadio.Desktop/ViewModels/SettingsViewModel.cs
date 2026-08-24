using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Avalonia.Media;
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
    private readonly IDisposable _selectedYtdlpBrowserSub;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly MusicAccountStore _accounts;
    private readonly NeteaseAccountService _neteaseAccount;
    private readonly KugouAccountService _kugouAccount;
    private bool _neteaseQrRunning;
    private bool _kugouQrRunning;
    private bool _loadingYtdlpBrowser;
    private string? _lastCharacterSignature;
    private bool _lastSaveSucceeded;
    private int _disposed;
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");

    /// <summary>应用真实使用的用户配置路径；仅生产组合根允许落在这里，测试必须显式传临时路径。</summary>
    public static readonly string DefaultSettingsFile = Path.Combine(SettingsDir, "settings.json");

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
    [Reactive] public bool CompactModeTopmost { get; set; } = true;
    [Reactive] public bool StartInCompactMode { get; set; }
    [Reactive] public string SpeechMixMode { get; set; } = "duck";
    [Reactive] public string SelectedLanguage { get; set; } = "zh"; // "zh" or "en"

    // 音源账号（网易扫码/酷狗扫码/yt-dlp cookies）
    [Reactive] public string NeteaseAccountStatus { get; set; } = "未登录";
    [Reactive] public IImage? NeteaseQrImage { get; set; }
    [Reactive] public bool IsNeteaseQrVisible { get; set; }
    [Reactive] public string KugouAccountStatus { get; set; } = "未登录";
    [Reactive] public IImage? KugouQrImage { get; set; }
    [Reactive] public bool IsKugouQrVisible { get; set; }
    [Reactive] public VoiceOption? SelectedYtdlpBrowser { get; set; }

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

    public List<VoiceOption> YtdlpBrowsers { get; } = new()
    {
        new() { Id = "", DisplayName = "不使用" },
        new() { Id = "chrome", DisplayName = "Chrome" },
        new() { Id = "edge", DisplayName = "Edge" },
        new() { Id = "firefox", DisplayName = "Firefox" },
        new() { Id = "brave", DisplayName = "Brave" },
        new() { Id = "chromium", DisplayName = "Chromium" },
        new() { Id = "opera", DisplayName = "Opera" },
        new() { Id = "vivaldi", DisplayName = "Vivaldi" },
    };

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> NeteaseQrLoginCommand { get; }
    public ReactiveCommand<Unit, Unit> NeteaseLogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> KugouQrLoginCommand { get; }
    public ReactiveCommand<Unit, Unit> KugouLogoutCommand { get; }

    // 主题/简洁模式等无关 UI 状态的自动保存：不写 LLM 配置、不动凭据，
    // 磁盘上已有的 llm_* 字段原样保留
    public ReactiveCommand<Unit, Unit> SaveUiStateCommand { get; }

    // Notify MainWindow when character settings change so it can re-apply
    public event Action? CharacterSettingsChanged;

    public SettingsViewModel(
        ILLMService llmService,
        ISecureStorage secureStorage,
        string settingsFile,
        MusicAccountStore? accountStore = null,
        System.Net.Http.HttpClient? httpClient = null)
    {
        _llmService = llmService;
        _secureStorage = secureStorage;
        _settingsFile = settingsFile;
        _settingsDir = Path.GetDirectoryName(_settingsFile) ?? SettingsDir;
        _accounts = accountStore ?? new MusicAccountStore(secureStorage);
        var http = httpClient ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _neteaseAccount = new NeteaseAccountService(http);
        _kugouAccount = new KugouAccountService(http);

        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(() => SaveAsync());

        // 主题/简洁模式等无关 UI 状态的自动保存：不写 LLM 配置、不动凭据，
        // 磁盘上已有的 llm_* 字段原样保留
        SaveUiStateCommand = ReactiveCommand.CreateFromTask(() => SaveAsync(persistLlmFields: false));
        NeteaseQrLoginCommand = ReactiveCommand.CreateFromTask(() => RunNeteaseQrLoginAsync());
        NeteaseLogoutCommand = ReactiveCommand.CreateFromTask(() => LogoutNeteaseAsync());
        KugouQrLoginCommand = ReactiveCommand.CreateFromTask(() => RunKugouQrLoginAsync());
        KugouLogoutCommand = ReactiveCommand.CreateFromTask(() => LogoutKugouAsync());

        // When character selection changes, load its overrides
        _selectedCharacterSub = this.WhenAnyValue(x => x.SelectedCharacter)
            .Where(c => c != null)
            .Subscribe(c => LoadCharacterOverrides(c!));

        // 默认选中必须在订阅之前完成，否则构造即触发一次无意义（且有副作用）的自动保存
        SelectedYtdlpBrowser = YtdlpBrowsers[0];
        // Skip(1)：WhenAnyValue 订阅时会立刻发射当前值，跳过它避免构造期触发自动保存
        _selectedYtdlpBrowserSub = this.WhenAnyValue(x => x.SelectedYtdlpBrowser)
            .Skip(1)
            .Where(b => b != null)
            .Subscribe(b =>
            {
                _accounts.YtdlpCookieBrowser = b!.Id;
                if (_loadingYtdlpBrowser)
                    return;
                // 用户切换浏览器时跟随保存，不触碰 LLM 字段与凭据
                _ = SaveUiStateCommand.Execute();
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

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await _secureStorage.GetApiKeyAsync(LlmCredentialService);
            if (!string.IsNullOrEmpty(key))
            {
                ApiKey = key;
            }

            using var doc = await OpenSettingsDocumentAsync(cancellationToken);
            if (doc != null)
            {
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

                if (root.TryGetProperty("compact_mode_topmost", out var compactTopmost))
                    CompactModeTopmost = compactTopmost.GetBoolean();

                if (root.TryGetProperty("start_in_compact_mode", out var startCompact))
                    StartInCompactMode = startCompact.GetBoolean();

                if (root.TryGetProperty("speech_mix_mode", out var speechMode))
                    SpeechMixMode = speechMode.GetString() == "pause" ? "pause" : "duck";

                if (root.TryGetProperty("ytdlp_cookie_browser", out var browserEl))
                {
                    var browserId = browserEl.GetString() ?? "";
                    // 加载期赋值只同步内存态，不触发跟随保存
                    _loadingYtdlpBrowser = true;
                    SelectedYtdlpBrowser = YtdlpBrowsers.Find(b => b.Id == browserId) ?? YtdlpBrowsers[0];
                    _loadingYtdlpBrowser = false;
                }

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

            // 账号昵称查询依赖本地音乐代理，就绪后由 RefreshAccountStatusAsync 刷新；
            // 加载期只按 cookie 有无恢复基础状态，避免误显示"未登录"，也不发网络请求
            if (!string.IsNullOrEmpty(_accounts.NeteaseCookie))
                NeteaseAccountStatus = "已登录";
            if (!string.IsNullOrEmpty(_accounts.KugouCookie))
                KugouAccountStatus = "已登录";

            // 加载完成即建立角色签名基线：启动后的第一次无关保存（主题/简洁模式）不会误触发事件
            _lastCharacterSignature = BuildCharacterSignature();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    /// <summary>优先读 settings.json，损坏或读不了时回退 .bak；两者都不可用返回 null，调用方按默认值运行。</summary>
    private async Task<JsonDocument?> OpenSettingsDocumentAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { _settingsFile, _settingsFile + ".bak" })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                return JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "Settings file {Path} is corrupt", path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Settings file {Path} is unreadable", path);
            }
        }
        return null;
    }

    private string BuildCharacterSignature()
        => JsonSerializer.Serialize(new
        {
            language = SelectedLanguage,
            tts = TtsEnabled,
            overrides = _overrides
                .OrderBy(kv => kv.Key)
                .Select(kv => new { id = kv.Key, kv.Value.VoiceId, kv.Value.Personality })
        });

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
            var successMessage = $"连接成功并已保存：{result[..Math.Min(50, result.Length)]}...";
            await SaveAsync();
            if (_lastSaveSucceeded)
                StatusMessage = successMessage;
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

    /// <summary>刷新音源账号昵称状态；依赖本地音乐代理已就绪，代理启动完成后调用。</summary>
    public async Task RefreshAccountStatusAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_accounts.NeteaseCookie))
            {
                var nickname = await _neteaseAccount.GetNicknameAsync(_accounts.NeteaseCookie!, _lifetimeCts.Token);
                NeteaseAccountStatus = nickname != null
                    ? $"已登录：{nickname}"
                    : "已登录（昵称获取失败，登录态可能过期）";
            }

            if (!string.IsNullOrEmpty(_accounts.KugouCookie))
            {
                var nickname = await _kugouAccount.GetNicknameAsync(_accounts.KugouCookie!, _lifetimeCts.Token);
                KugouAccountStatus = nickname != null ? $"已登录：{nickname}" : "已登录";
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Account status refresh failed");
        }
    }

    private async Task RunNeteaseQrLoginAsync()
    {
        if (_neteaseQrRunning)
            return;
        _neteaseQrRunning = true;
        try
        {
            var session = await _neteaseAccount.CreateQrSessionAsync(_lifetimeCts.Token);
            if (session == null)
            {
                NeteaseAccountStatus = "二维码创建失败：本地音乐服务未就绪，请稍后重试";
                return;
            }

            NeteaseQrImage = CreateBitmap(session.QrPng);
            IsNeteaseQrVisible = true;
            NeteaseAccountStatus = "请用网易云音乐 App 扫码";

            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(1500, _lifetimeCts.Token);
                var result = await _neteaseAccount.CheckQrAsync(session.Key, _lifetimeCts.Token);
                switch (result.State)
                {
                    case QrState.Waiting:
                        break;
                    case QrState.Scanned:
                        NeteaseAccountStatus = "已扫码，请在手机上确认";
                        break;
                    case QrState.Confirmed when !string.IsNullOrEmpty(result.Cookie):
                        await _accounts.SetNeteaseCookieAsync(result.Cookie!);
                        IsNeteaseQrVisible = false;
                        NeteaseQrImage = null;
                        var nickname = await _neteaseAccount.GetNicknameAsync(result.Cookie!, _lifetimeCts.Token);
                        NeteaseAccountStatus = $"已登录：{nickname ?? "未知昵称"}";
                        return;
                    case QrState.Expired:
                        NeteaseAccountStatus = "二维码已过期，请重新扫码";
                        return;
                    default:
                        NeteaseAccountStatus = "登录失败：接口返回异常，请重试";
                        return;
                }
            }
            NeteaseAccountStatus = "等待扫码超时，请重试";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease QR login failed");
            NeteaseAccountStatus = $"登录失败：{ex.Message}";
        }
        finally
        {
            _neteaseQrRunning = false;
        }
    }

    private async Task RunKugouQrLoginAsync()
    {
        if (_kugouQrRunning)
            return;
        _kugouQrRunning = true;
        try
        {
            var session = await _kugouAccount.CreateQrSessionAsync(_lifetimeCts.Token);
            if (session == null)
            {
                KugouAccountStatus = "二维码创建失败：本地酷狗服务未就绪，请稍后重试";
                return;
            }

            KugouQrImage = CreateBitmap(session.QrPng);
            IsKugouQrVisible = true;
            KugouAccountStatus = "请用酷狗音乐 App 扫码";

            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(1500, _lifetimeCts.Token);
                var result = await _kugouAccount.CheckQrAsync(session.Key, _lifetimeCts.Token);
                switch (result.State)
                {
                    case QrState.Waiting:
                        break;
                    case QrState.Scanned:
                        KugouAccountStatus = "已扫码，请在手机上确认";
                        break;
                    case QrState.Confirmed when !string.IsNullOrEmpty(result.Cookie):
                        await _accounts.SetKugouCookieAsync(result.Cookie!);
                        IsKugouQrVisible = false;
                        KugouQrImage = null;
                        var nickname = await _kugouAccount.GetNicknameAsync(result.Cookie!, _lifetimeCts.Token);
                        KugouAccountStatus = $"已登录：{nickname ?? "未知昵称"}";
                        return;
                    case QrState.Expired:
                        KugouAccountStatus = "二维码已过期，请重新扫码";
                        return;
                    default:
                        KugouAccountStatus = "登录失败：接口返回异常，请重试";
                        return;
                }
            }
            KugouAccountStatus = "等待扫码超时，请重试";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kugou QR login failed");
            KugouAccountStatus = $"登录失败：{ex.Message}";
        }
        finally
        {
            _kugouQrRunning = false;
        }
    }

    private async Task LogoutNeteaseAsync()
    {
        await _accounts.SetNeteaseCookieAsync(null);
        IsNeteaseQrVisible = false;
        NeteaseQrImage = null;
        NeteaseAccountStatus = "未登录";
    }

    private async Task LogoutKugouAsync()
    {
        await _accounts.SetKugouCookieAsync(null);
        IsKugouQrVisible = false;
        KugouQrImage = null;
        KugouAccountStatus = "未登录";
    }

    private static IImage? CreateBitmap(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to decode QR image");
            return null;
        }
    }

    private async Task SaveAsync(bool persistLlmFields = true)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _lastSaveSucceeded = false;
        var gateHeld = false;
        try
        {
            await _saveGate.WaitAsync(_lifetimeCts.Token);
            gateHeld = true;
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (persistLlmFields)
                NormalizeLlmInputs();

            // Save current character overrides
            if (SelectedCharacter != null && CharacterVoice != null)
            {
                _overrides[SelectedCharacter.Id] = (CharacterVoice.Id, CharacterPersonality);
            }

            string providerToWrite, baseUrlToWrite, modelToWrite;
            if (persistLlmFields)
            {
                providerToWrite = SelectedProvider;
                baseUrlToWrite = BaseUrl;
                modelToWrite = Model;

                if (!string.IsNullOrWhiteSpace(ApiKey))
                {
                    await _secureStorage.SaveApiKeyAsync(LlmCredentialService, ApiKey);
                }
                // ApiKey 为空时保留凭据管理器里的旧 key：自动保存路径随时可能触发保存，
                // 把空值视为"未填写"而不是"要清除"
                _secureStorage.DeleteApiKey(LegacyMinimaxCredentialService);
                ConfigureLlm();
            }
            else
            {
                var persisted = await ReadPersistedLlmFieldsAsync();
                providerToWrite = persisted?.Provider ?? SelectedProvider;
                baseUrlToWrite = persisted?.BaseUrl ?? BaseUrl;
                modelToWrite = persisted?.Model ?? Model;
            }

            Directory.CreateDirectory(_settingsDir);
            var overridesJson = new Dictionary<string, object>();
            foreach (var kv in _overrides)
            {
                overridesJson[kv.Key] = new { voice_id = kv.Value.VoiceId, personality = kv.Value.Personality };
            }

            // 角色相关设置（语言/TTS/覆盖项）未变化时不触发 CharacterSettingsChanged：
            // 该事件会让 MainWindowViewModel 重新 Initialize DJ 并清空聊天历史，
            // 主题/简洁模式这类无关保存不应带来这个副作用
            var characterSignature = BuildCharacterSignature();
            var characterSettingsChanged = characterSignature != _lastCharacterSignature;
            _lastCharacterSignature = characterSignature;

            var settingsData = new
            {
                llm_provider = providerToWrite,
                llm_base_url = baseUrlToWrite,
                llm_model = modelToWrite,
                tts_enabled = TtsEnabled,
                is_dark_mode = IsDarkMode,
                enable_starfield = EnableStarfield,
                compact_mode_topmost = CompactModeTopmost,
                start_in_compact_mode = StartInCompactMode,
                speech_mix_mode = SpeechMixMode,
                language = SelectedLanguage,
                ytdlp_cookie_browser = _accounts.YtdlpCookieBrowser ?? "",
                character_overrides = overridesJson
            };
            // Settings stored as plaintext JSON in %APPDATA%; API key is in Windows Credential Manager
            var json = JsonSerializer.Serialize(settingsData, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _settingsFile + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, _lifetimeCts.Token);
            // 旧配置轮转为 .bak：settings.json 被外部进程覆盖或写坏时留有恢复途径；
            // .bak 被占用等备份失败只降级为直接覆盖，不能阻塞保存
            if (File.Exists(_settingsFile))
            {
                try
                {
                    File.Replace(tempPath, _settingsFile, _settingsFile + ".bak");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Failed to rotate settings backup, overwriting directly");
                    File.Move(tempPath, _settingsFile, overwrite: true);
                }
            }
            else
                File.Move(tempPath, _settingsFile);

            if (characterSettingsChanged)
                CharacterSettingsChanged?.Invoke();
            _lastSaveSucceeded = true;
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

    private async Task<(string Provider, string BaseUrl, string Model)?> ReadPersistedLlmFieldsAsync()
    {
        try
        {
            if (!File.Exists(_settingsFile))
                return null;

            var json = await File.ReadAllTextAsync(_settingsFile, _lifetimeCts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("llm_provider", out var p) ? NormalizeProvider(p.GetString()) : "openai",
                root.TryGetProperty("llm_base_url", out var b) ? b.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("llm_model", out var m) ? m.GetString() ?? string.Empty : string.Empty);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read persisted LLM fields, falling back to current values");
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        _selectedCharacterSub.Dispose();
        _selectedYtdlpBrowserSub.Dispose();
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
