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
    private readonly KugouVerificationService? _kugouVerification;
    private bool _kugouVerifyRunning;
    private bool _neteaseQrRunning;
    private bool _kugouQrRunning;
    private bool _loadingYtdlpBrowser;
    private bool _ytdlpCookieNoticeShown;
    private readonly IDisposable _selectedLanguageSub;
    // 常驻文案/选项列表随语言切换重建；静态事件必须持委托在 Dispose 退订
    private readonly Action _onLanguageChanged;
    private string? _lastCharacterSignature;
    private bool _lastSaveSucceeded;
    private Func<string>? _statusMessageFactory;
    private Func<string>? _neteaseAccountStatusFactory;
    private Func<string>? _kugouAccountStatusFactory;
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
    [Reactive] public string TestConnectionButtonText { get; set; } = AppLanguage.T("测试连接", "Test");
    [Reactive] public bool TtsEnabled { get; set; } = true;
    [Reactive] public bool IsDarkMode { get; set; } = true;
    [Reactive] public bool EnableStarfield { get; set; } = true;
    [Reactive] public string SelectedSpectrumStyle { get; set; } = "bars";
    [Reactive] public bool CompactModeTopmost { get; set; } = true;
    [Reactive] public bool StartInCompactMode { get; set; }
    [Reactive] public string SpeechMixMode { get; set; } = "duck";
    [Reactive] public string SelectedLanguage { get; set; } = "zh"; // "zh" or "en"

    // 音源账号（网易扫码/酷狗扫码/yt-dlp cookies）
    [Reactive] public string NeteaseAccountStatus { get; set; } = AppLanguage.T("未登录", "Not signed in");
    [Reactive] public IImage? NeteaseQrImage { get; set; }
    [Reactive] public bool IsNeteaseQrVisible { get; set; }
    [Reactive] public string KugouAccountStatus { get; set; } = AppLanguage.T("未登录", "Not signed in");
    [Reactive] public IImage? KugouQrImage { get; set; }
    [Reactive] public bool IsKugouQrVisible { get; set; }
    [Reactive] public VoiceOption? SelectedYtdlpBrowser { get; set; }
    // 首次启用浏览器 Cookies 的隐私提示（每次会话提示一次）
    [Reactive] public string YtdlpCookieNotice { get; set; } = string.Empty;
    [Reactive] public bool IsYtdlpCookieNoticeVisible { get; set; }

    // Character customization
    public List<CharacterProfile> Characters { get; } = CharacterProfile.Presets;
    [Reactive] public CharacterProfile? SelectedCharacter { get; set; }
    [Reactive] public VoiceOption? CharacterVoice { get; set; }
    [Reactive] public string CharacterPersonality { get; set; } = string.Empty;

    // 依赖语言的选项列表：语言切换时整体换新实例。
    // 不能就地 Clear+AddRange：绑定重读到同一 List 实例会被 Avalonia 按相等性去重，
    // ItemsSource 不会重新赋值，下拉项将停留旧语言。
    [Reactive] public List<VoiceOption> Voices { get; set; } = new();

    // 语言名按 i18n 惯例保留母语写法，不随界面语言重建
    public List<VoiceOption> Languages { get; } = new()
    {
        new() { Id = "zh", DisplayName = "中文" },
        new() { Id = "en", DisplayName = "English" },
    };

    [Reactive] public List<VoiceOption> LlmProviders { get; set; } = new();

    [Reactive] public List<VoiceOption> SpeechMixModes { get; set; } = new();

    [Reactive] public List<VoiceOption> SpectrumStyles { get; set; } = new();

    [Reactive] public List<VoiceOption> YtdlpBrowsers { get; set; } = new();

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> NeteaseQrLoginCommand { get; }
    public ReactiveCommand<Unit, Unit> NeteaseLogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> KugouQrLoginCommand { get; }
    public ReactiveCommand<Unit, Unit> KugouLogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> KugouVerifyCommand { get; }

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
        System.Net.Http.HttpClient? httpClient = null,
        KugouVerificationService? kugouVerification = null)
    {
        _llmService = llmService;
        _secureStorage = secureStorage;
        _settingsFile = settingsFile;
        _settingsDir = Path.GetDirectoryName(_settingsFile) ?? SettingsDir;
        _accounts = accountStore ?? new MusicAccountStore(secureStorage);
        var http = httpClient ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _neteaseAccount = new NeteaseAccountService(http);
        _kugouAccount = new KugouAccountService(http);
        _kugouVerification = kugouVerification;
        SetNeteaseAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));
        SetKugouAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));

        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(() => SaveAsync());

        // 主题/简洁模式等无关 UI 状态的自动保存：不写 LLM 配置、不动凭据，
        // 磁盘上已有的 llm_* 字段原样保留
        SaveUiStateCommand = ReactiveCommand.CreateFromTask(() => SaveAsync(persistLlmFields: false));
        NeteaseQrLoginCommand = ReactiveCommand.CreateFromTask(() => RunNeteaseQrLoginAsync());
        NeteaseLogoutCommand = ReactiveCommand.CreateFromTask(() => LogoutNeteaseAsync());
        KugouQrLoginCommand = ReactiveCommand.CreateFromTask(() => RunKugouQrLoginAsync());
        KugouLogoutCommand = ReactiveCommand.CreateFromTask(() => LogoutKugouAsync());
        KugouVerifyCommand = ReactiveCommand.CreateFromTask(() => RunKugouVerifyAsync());

        // When character selection changes, load its overrides
        _selectedCharacterSub = this.WhenAnyValue(x => x.SelectedCharacter)
            .Where(c => c != null)
            .Subscribe(c => LoadCharacterOverrides(c!));

        // 初始填充选项列表（依赖当前语言），必须在默认选中赋值之前完成
        CharacterProfile.RefreshLocalizedPresets();
        RebuildLocalizedOptionLists();

        // 默认选中必须在订阅之前完成，否则构造即触发一次无意义（且有副作用）的自动保存
        SelectedYtdlpBrowser = YtdlpBrowsers[0];
        // Skip(1)：WhenAnyValue 订阅时会立刻发射当前值，跳过它避免构造期触发自动保存
        _selectedYtdlpBrowserSub = this.WhenAnyValue(x => x.SelectedYtdlpBrowser)
            .Skip(1)
            .Where(b => b != null)
            .Subscribe(b =>
            {
                _accounts.YtdlpCookieBrowser = b!.Id;
                UpdateYtdlpCookieNotice(b.Id);
                if (_loadingYtdlpBrowser)
                    return;
                // 用户切换浏览器时跟随保存，不触碰 LLM 字段与凭据
                _ = SaveUiStateCommand.Execute();
            });

        // 界面显示语言严格跟随本选项：加载读到旧值与用户切换时都经 Apply 生效
        _selectedLanguageSub = this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1)
            .Subscribe(language => AppLanguage.Apply(language));

        // 语言切换时刷新常驻文案与选项列表显示名（静态事件，Dispose 退订）
        _onLanguageChanged = () =>
        {
            TestConnectionButtonText = AppLanguage.T("测试连接", "Test");
            CharacterProfile.RefreshLocalizedPresets();
            RelocalizeCharacterPersonality();
            if (_statusMessageFactory != null)
                StatusMessage = _statusMessageFactory();
            if (_neteaseAccountStatusFactory != null)
                NeteaseAccountStatus = _neteaseAccountStatusFactory();
            if (_kugouAccountStatusFactory != null)
                KugouAccountStatus = _kugouAccountStatusFactory();
            if (IsYtdlpCookieNoticeVisible && SelectedYtdlpBrowser is { Id.Length: > 0 } browser)
                YtdlpCookieNotice = BuildYtdlpCookieNotice(browser.Id);
            RebuildLocalizedOptionLists();
        };
        AppLanguage.Changed += _onLanguageChanged;

        // Default selection
        SelectedCharacter = Characters[0];
    }

    /// <summary>重建依赖语言的选项列表；按 Id 保留既有选择，浏览器选择重建不触发自动保存。</summary>
    private void RebuildLocalizedOptionLists()
    {
        var voiceId = CharacterVoice?.Id;
        Voices = new List<VoiceOption>
        {
            new() { Id = "male-qn-qingse", DisplayName = AppLanguage.T("青涩男声", "Soft male") },
            new() { Id = "male-qn-jingying", DisplayName = AppLanguage.T("精英男声", "Elite male") },
            new() { Id = "male-qn-badao", DisplayName = AppLanguage.T("霸道男声", "Bold male") },
            new() { Id = "female-shaonv", DisplayName = AppLanguage.T("少女音", "Girl voice") },
            new() { Id = "female-yujie", DisplayName = AppLanguage.T("御姐音", "Mature female") },
            new() { Id = "female-chengshu", DisplayName = AppLanguage.T("成熟女声", "Grown female") },
        };
        if (voiceId != null)
            CharacterVoice = Voices.FirstOrDefault(v => v.Id == voiceId) ?? Voices[0];

        LlmProviders = new List<VoiceOption>
        {
            new() { Id = "openai", DisplayName = AppLanguage.T("OpenAI 兼容格式", "OpenAI-compatible") },
            new() { Id = "anthropic", DisplayName = AppLanguage.T("Anthropic 兼容格式", "Anthropic-compatible") },
            new() { Id = "local", DisplayName = AppLanguage.T("本地模型", "Local model") },
        };

        SpeechMixModes = new List<VoiceOption>
        {
            new() { Id = "duck", DisplayName = AppLanguage.T("说话时降低音乐音量", "Duck volume while speaking") },
            new() { Id = "pause", DisplayName = AppLanguage.T("说话时暂停音乐", "Pause music while speaking") },
        };

        SpectrumStyles = new List<VoiceOption>
        {
            new() { Id = "bars", DisplayName = AppLanguage.T("经典柱状", "Classic bars") },
            new() { Id = "mirror", DisplayName = AppLanguage.T("镜像脉冲", "Mirrored pulse") },
            new() { Id = "wave", DisplayName = AppLanguage.T("霓虹波形", "Neon wave") },
            new() { Id = "particles", DisplayName = AppLanguage.T("星点粒子", "Star particles") },
        };

        var browserId = SelectedYtdlpBrowser?.Id;
        _loadingYtdlpBrowser = true;
        try
        {
            YtdlpBrowsers = new List<VoiceOption>
            {
                new() { Id = "", DisplayName = AppLanguage.T("不使用", "Don't use") },
                new() { Id = "chrome", DisplayName = "Chrome" },
                new() { Id = "edge", DisplayName = "Edge" },
                new() { Id = "firefox", DisplayName = "Firefox" },
                new() { Id = "brave", DisplayName = "Brave" },
                new() { Id = "chromium", DisplayName = "Chromium" },
                new() { Id = "opera", DisplayName = "Opera" },
                new() { Id = "vivaldi", DisplayName = "Vivaldi" },
            };
            SelectedYtdlpBrowser = YtdlpBrowsers.FirstOrDefault(b => b.Id == browserId) ?? YtdlpBrowsers[0];
        }
        finally
        {
            _loadingYtdlpBrowser = false;
        }
    }

    /// <summary>
    /// 首次（每次会话一次）从"不使用"切到具体浏览器时展示隐私提示：
    /// 明确浏览器 Cookie 只在本机用于 yt-dlp 播放请求，不写入日志。
    /// </summary>
    private void UpdateYtdlpCookieNotice(string browserId)
    {
        if (string.IsNullOrEmpty(browserId))
        {
            YtdlpCookieNotice = string.Empty;
            IsYtdlpCookieNoticeVisible = false;
            return;
        }

        if (_ytdlpCookieNoticeShown)
            return;

        _ytdlpCookieNoticeShown = true;
        YtdlpCookieNotice = BuildYtdlpCookieNotice(browserId);
        IsYtdlpCookieNoticeVisible = true;
    }

    private static string BuildYtdlpCookieNotice(string browserId)
        => AppLanguage.T(
            $"隐私提示：已启用 {browserId} 浏览器 Cookies。yt-dlp 仅在本机读取该浏览器的 YouTube 登录态用于播放请求，Cookies 不会上传、记录或写入日志。",
            $"Privacy notice: {browserId} browser cookies enabled. yt-dlp only reads this browser's YouTube sign-in locally for playback requests; cookies are never uploaded, logged or stored in logs.");

    private void RelocalizeCharacterPersonality()
    {
        CharacterPersonality = CharacterProfile.LocalizeBuiltInPersonality(CharacterPersonality);
        if (SelectedCharacter != null && _overrides.TryGetValue(SelectedCharacter.Id, out var current))
        {
            _overrides[SelectedCharacter.Id] = (
                current.VoiceId,
                CharacterProfile.LocalizeBuiltInPersonality(current.Personality));
        }
    }

    private void SetStatusMessage(Func<string> messageFactory)
    {
        _statusMessageFactory = messageFactory;
        StatusMessage = messageFactory();
    }

    private void SetNeteaseAccountStatus(Func<string> messageFactory)
    {
        _neteaseAccountStatusFactory = messageFactory;
        NeteaseAccountStatus = messageFactory();
    }

    private void SetKugouAccountStatus(Func<string> messageFactory)
    {
        _kugouAccountStatusFactory = messageFactory;
        KugouAccountStatus = messageFactory();
    }

    private void LoadCharacterOverrides(CharacterProfile character)
    {
        if (_overrides.TryGetValue(character.Id, out var ov))
        {
            CharacterVoice = Voices.Find(v => v.Id == ov.VoiceId) ?? Voices.Find(v => v.Id == character.VoiceId) ?? Voices[0];
            var personality = CharacterProfile.LocalizeBuiltInPersonality(ov.Personality);
            _overrides[character.Id] = (ov.VoiceId, personality);
            CharacterPersonality = personality;
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

                if (root.TryGetProperty("spectrum_style", out var spectrumStyle))
                    SelectedSpectrumStyle = NormalizeSpectrumStyle(spectrumStyle.GetString());

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
                        _overrides[prop.Name] = (voiceId, CharacterProfile.LocalizeBuiltInPersonality(personality));
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
                SetNeteaseAccountStatus(() => AppLanguage.T("已登录", "Signed in"));
            if (!string.IsNullOrEmpty(_accounts.KugouCookie))
                SetKugouAccountStatus(() => AppLanguage.T("已登录", "Signed in"));

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
            SetStatusMessage(() => AppLanguage.T("请先输入 API Key", "Enter your API key first"));
            return;
        }
        if (string.IsNullOrWhiteSpace(Model))
        {
            SetStatusMessage(() => AppLanguage.T("请先输入模型名称", "Enter a model name first"));
            return;
        }

        IsTesting = true;
        TestConnectionButtonText = AppLanguage.T("正在测试...", "Testing...");
        SetStatusMessage(() => AppLanguage.T("正在测试连接...", "Testing connection..."));
        try
        {
            NormalizeLlmInputs();
            ConfigureLlm(ApiKey);
            var result = await _llmService.ChatAsync(AppLanguage.T("你好，请用一句话回复", "Hello, reply in one sentence"), new List<ChatMessage>());
            var resultPreview = result[..Math.Min(50, result.Length)];
            await SaveAsync();
            if (_lastSaveSucceeded)
                SetStatusMessage(() => AppLanguage.T($"连接成功并已保存：{resultPreview}...", $"Connected and saved: {resultPreview}..."));
        }
        catch (Exception ex)
        {
            var failure = ApiFailureLocalization.ForCurrentLanguage(ApiFailureInfo.FromException(ex));
            Log.Error(ex, "AI service API error");
            SetStatusMessage(() =>
            {
                var localized = ApiFailureLocalization.ForCurrentLanguage(failure);
                return AppLanguage.T($"连接失败：{localized.Title}。{localized.RecoveryHint}", $"Connection failed: {localized.Title}. {localized.RecoveryHint}");
            });
        }
        finally
        {
            IsTesting = false;
            TestConnectionButtonText = AppLanguage.T("测试连接", "Test");
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
                SetNeteaseAccountStatus(() => nickname != null
                    ? AppLanguage.T($"已登录：{nickname}", $"Signed in: {nickname}")
                    : AppLanguage.T("已登录（昵称获取失败，登录态可能过期）", "Signed in (nickname unavailable; login may have expired)"));
            }

            if (!string.IsNullOrEmpty(_accounts.KugouCookie))
            {
                var baseline = _accounts.KugouCookie!;
                var refreshed = await _kugouAccount.RefreshCredentialAsync(
                    baseline,
                    forceSessionRefresh: true,
                    cancellationToken: _lifetimeCts.Token) ?? baseline;
                if (!string.Equals(baseline, refreshed, StringComparison.Ordinal))
                    await _accounts.SetKugouCookieAsync(refreshed);

                var snapshot = await _kugouAccount.GetAccountSnapshotAsync(refreshed, _lifetimeCts.Token);
                SetKugouAccountStatus(() => BuildKugouAccountStatus(snapshot));
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
                SetNeteaseAccountStatus(() => AppLanguage.T("二维码创建失败：本地音乐服务未就绪，请稍后重试", "Failed to create QR code: local music service not ready, try again later"));
                return;
            }

            NeteaseQrImage = CreateBitmap(session.QrPng);
            IsNeteaseQrVisible = true;
            SetNeteaseAccountStatus(() => AppLanguage.T("请用网易云音乐 App 扫码", "Scan with the NetEase Cloud Music app"));

            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(1500, _lifetimeCts.Token);
                var result = await _neteaseAccount.CheckQrAsync(session.Key, _lifetimeCts.Token);
                switch (result.State)
                {
                    case QrState.Waiting:
                        break;
                    case QrState.Scanned:
                        SetNeteaseAccountStatus(() => AppLanguage.T("已扫码，请在手机上确认", "Scanned; confirm on your phone"));
                        break;
                    case QrState.Confirmed when !string.IsNullOrEmpty(result.Cookie):
                        await _accounts.SetNeteaseCookieAsync(result.Cookie!);
                        IsNeteaseQrVisible = false;
                        NeteaseQrImage = null;
                        var nickname = await _neteaseAccount.GetNicknameAsync(result.Cookie!, _lifetimeCts.Token);
                        SetNeteaseAccountStatus(() => AppLanguage.T($"已登录：{nickname ?? "未知昵称"}", $"Signed in: {nickname ?? "unknown"}"));
                        return;
                    case QrState.Expired:
                        SetNeteaseAccountStatus(() => AppLanguage.T("二维码已过期，请重新扫码", "QR code expired; scan again"));
                        return;
                    default:
                        SetNeteaseAccountStatus(() => AppLanguage.T("登录失败：接口返回异常，请重试", "Login failed: unexpected API response, try again"));
                        return;
                }
            }
            SetNeteaseAccountStatus(() => AppLanguage.T("等待扫码超时，请重试", "Timed out waiting for the scan; try again"));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease QR login failed");
            SetNeteaseAccountStatus(() => AppLanguage.T($"登录失败：{ex.Message}", $"Login failed: {ex.Message}"));
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
                SetKugouAccountStatus(() => AppLanguage.T("二维码创建失败：本地酷狗服务未就绪，请稍后重试", "Failed to create QR code: local Kugou service not ready, try again later"));
                return;
            }

            KugouQrImage = CreateBitmap(session.QrPng);
            IsKugouQrVisible = true;
            SetKugouAccountStatus(() => AppLanguage.T("请用酷狗音乐 App 扫码", "Scan with the Kugou Music app"));

            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(1500, _lifetimeCts.Token);
                var result = await _kugouAccount.CheckQrAsync(session.Key, _lifetimeCts.Token);
                switch (result.State)
                {
                    case QrState.Waiting:
                        break;
                    case QrState.Scanned:
                        SetKugouAccountStatus(() => AppLanguage.T("已扫码，请在手机上确认", "Scanned; confirm on your phone"));
                        break;
                    case QrState.Confirmed when !string.IsNullOrEmpty(result.Cookie):
                        await _accounts.SetKugouCookieAsync(result.Cookie!);
                        IsKugouQrVisible = false;
                        KugouQrImage = null;
                        var snapshot = await _kugouAccount.GetAccountSnapshotAsync(
                            result.Cookie!, _lifetimeCts.Token);
                        SetKugouAccountStatus(() => BuildKugouAccountStatus(snapshot));
                        return;
                    case QrState.Expired:
                        SetKugouAccountStatus(() => AppLanguage.T("二维码已过期，请重新扫码", "QR code expired; scan again"));
                        return;
                    default:
                        SetKugouAccountStatus(() => AppLanguage.T("登录失败：接口返回异常，请重试", "Login failed: unexpected API response, try again"));
                        return;
                }
            }
            SetKugouAccountStatus(() => AppLanguage.T("等待扫码超时，请重试", "Timed out waiting for the scan; try again"));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kugou QR login failed");
            SetKugouAccountStatus(() => AppLanguage.T($"登录失败：{ex.Message}", $"Login failed: {ex.Message}"));
        }
        finally
        {
            _kugouQrRunning = false;
        }
    }

    private static string BuildKugouAccountStatus(KugouAccountSnapshot snapshot)
    {
        var account = snapshot.Nickname ?? AppLanguage.T("未知昵称", "unknown");
        if (snapshot.Nickname == null)
        {
            return AppLanguage.T(
                "登录态验证失败，请重新扫码或检查酷狗代理",
                "Sign-in validation failed; scan again or check the Kugou proxy");
        }

        if (!snapshot.HasAuth)
        {
            return AppLanguage.T(
                $"账号有效：{account}（播放授权未就绪，请稍后重试或重新扫码）",
                $"Account valid: {account} (playback authorization is not ready; retry later or scan again)");
        }

        if (snapshot.VipType > 0)
        {
            var diagnostic = snapshot.VipEndpointAvailable
                ? AppLanguage.T("会员接口可用", "membership endpoint available")
                : AppLanguage.T("会员详情暂不可用", "membership details unavailable");
            return AppLanguage.T(
                $"已登录：{account}（会员类型 {snapshot.VipType}，{diagnostic}；播放权益以实际检测为准）",
                $"Signed in: {account} (membership type {snapshot.VipType}, {diagnostic}; playback rights are verified per track)");
        }

        return AppLanguage.T(
            $"已登录：{account}（未识别到付费会员；免费听活动以实际播放为准）",
            $"Signed in: {account} (no paid membership detected; promotional access is verified per track)");
    }

    private async Task LogoutNeteaseAsync()
    {
        await _accounts.SetNeteaseCookieAsync(null);
        IsNeteaseQrVisible = false;
        NeteaseQrImage = null;
        SetNeteaseAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));
    }

    /// <summary>
    /// 酷狗滑块验证（手动入口）：先校验登录态（token 过期时滑块解决不了），
    /// 再探测风控状态；命中挑战则经会话桥打开浏览器验证页并轮询等待恢复。
    /// </summary>
    private async Task RunKugouVerifyAsync()
    {
        var verification = _kugouVerification;
        if (verification == null || _kugouVerifyRunning)
            return;
        _kugouVerifyRunning = true;
        try
        {
            var cookie = _accounts.KugouCookie;
            if (string.IsNullOrEmpty(cookie))
            {
                SetKugouAccountStatus(() => AppLanguage.T("酷狗未登录，请先扫码登录", "Kugou is not signed in; scan the QR code first"));
                return;
            }

            if (!verification.TryBeginManual())
            {
                SetKugouAccountStatus(() => AppLanguage.T("已有验证进行中，请稍候", "A verification is already running; wait a moment"));
                return;
            }

            try
            {
                // token 有效性预检：过期（如 20018）时昵称拿不到，滑块也无法恢复播放
                var nickname = await _kugouAccount.GetNicknameAsync(cookie, _lifetimeCts.Token);
                if (string.IsNullOrEmpty(nickname))
                {
                    SetKugouAccountStatus(() => AppLanguage.T(
                        "登录态校验失败，请重新扫码登录",
                        "Sign-in check failed; please scan the QR code again"));
                    return;
                }

                var probeHash = verification.LastChallenge?.Hash;
                var probe = await verification.DetectChallengeAsync(cookie, probeHash, _lifetimeCts.Token);
                if (probe.Status == KugouProbeStatus.Playable)
                {
                    SetKugouAccountStatus(() => AppLanguage.T("无需验证，酷狗当前可正常获取播放地址", "No verification needed; Kugou playback URLs work"));
                    return;
                }
                if (probe.Challenge?.EventId == null)
                {
                    SetKugouAccountStatus(() => AppLanguage.T(
                        "未能获取验证会话（可能需重新登录或稍后再试）",
                        "Could not obtain a verification session; re-login or retry later"));
                    return;
                }

                var outcome = await verification.RunVerificationAsync(
                    cookie,
                    probe.Challenge.Hash,
                    _lifetimeCts.Token,
                    () => SetKugouAccountStatus(() => AppLanguage.T(
                        "已打开验证页面，请在浏览器完成滑块验证…",
                        "Verification page opened; complete the slider in your browser...")));

                SetKugouAccountStatus(outcome switch
                {
                    KugouVerifyOutcome.Verified => () => AppLanguage.T("验证成功，酷狗播放已恢复", "Verified; Kugou playback recovered"),
                    KugouVerifyOutcome.VerifiedButUnavailable => () => AppLanguage.T(
                        "验证已完成；若歌曲仍不可播，可能需要 VIP 或重新登录",
                        "Verification done; if tracks stay unplayable, VIP or re-login may be required"),
                    KugouVerifyOutcome.NeedsRelogin => () => AppLanguage.T(
                        "酷狗要求重新确认登录身份，请重新扫码登录",
                        "Kugou asks to re-confirm your identity; please scan the QR code again"),
                    KugouVerifyOutcome.Timeout => () => AppLanguage.T("等待验证超时，可重试", "Timed out waiting for verification; retry"),
                    _ => () => AppLanguage.T("验证未完成，可重试", "Verification not completed; retry"),
                });
            }
            finally
            {
                verification.EndVerification();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kugou verification failed");
            SetKugouAccountStatus(() => AppLanguage.T($"验证失败：{ex.Message}", $"Verification failed: {ex.Message}"));
        }
        finally
        {
            _kugouVerifyRunning = false;
        }
    }

    private async Task LogoutKugouAsync()
    {
        await _accounts.SetKugouCookieAsync(null);
        IsKugouQrVisible = false;
        KugouQrImage = null;
        SetKugouAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));
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
                spectrum_style = NormalizeSpectrumStyle(SelectedSpectrumStyle),
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
            SetStatusMessage(() => AppLanguage.T("设置已保存", "Settings saved"));
            Log.Information("Settings saved to {Path}", _settingsFile);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // 关闭时取消排队保存，避免 Dispose 后继续写盘。
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            SetStatusMessage(() => AppLanguage.T($"保存失败：{ex.Message}", $"Save failed: {ex.Message}"));
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
        _selectedLanguageSub.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
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

    private static string NormalizeSpectrumStyle(string? style) => style?.ToLowerInvariant() switch
    {
        "mirror" => "mirror",
        "wave" => "wave",
        "particles" => "particles",
        _ => "bars"
    };

    private static bool RequiresApiKey(string provider)
        => !string.Equals(NormalizeProvider(provider), "local", StringComparison.OrdinalIgnoreCase);
}
