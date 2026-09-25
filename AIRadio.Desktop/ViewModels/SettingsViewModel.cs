using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
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

public sealed class MusicProviderOption : ReactiveObject
{
    private bool _enabled;
    public string Id { get; }
    public string DisplayName => AppLanguage.MusicSourceName(Id);
    public bool Enabled
    {
        get => _enabled;
        set => this.RaiseAndSetIfChanged(ref _enabled, value);
    }

    public MusicProviderOption(string id, bool enabled)
    {
        Id = id;
        _enabled = enabled;
    }

    public void RefreshLanguage() => this.RaisePropertyChanged(nameof(DisplayName));
}

public class SettingsViewModel : ViewModelBase, IDisposable
{
#if TUANRADIO_SLIM_CORE
    public bool HasExperimentalProviders => false;
#else
    public bool HasExperimentalProviders => true;
#endif
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
    private readonly IListeningProfileService? _listeningProfile;
    private readonly IMusicSearchService? _musicSearch;
    private readonly OpenSubsonicProvider? _openSubsonic;
    private Func<string>? _openSubsonicStatusFactory;
    private readonly IDisposable _listenerProfileToggleSub;
    private bool _loadingProfileToggle;
    private int _listenerProfileResetArmed;
    private int _resetArmVersion;
    private Func<string>? _sourceDiagnosticsFactory;
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
    [Reactive] public bool CompactShowLyrics { get; set; } = true;
    [Reactive] public bool RadioSoundFxEnabled { get; set; } = true;
    [Reactive] public bool StartInCompactMode { get; set; }
    [Reactive] public bool ShowLyricsInStage { get; set; }
    [Reactive] public bool ListenerProfileEnabled { get; set; } = true;
    // 天气城市：留空时按 IP 自动定位（见 WeatherService）
    [Reactive] public string WeatherCity { get; set; } = string.Empty;
    [Reactive] public string OpenSubsonicServerUrl { get; set; } = string.Empty;
    [Reactive] public string OpenSubsonicUsername { get; set; } = string.Empty;
    [Reactive] public string OpenSubsonicPassword { get; set; } = string.Empty;
    [Reactive] public string OpenSubsonicStatus { get; set; } = string.Empty;
    [Reactive] public bool IsConnectingOpenSubsonic { get; set; }
    // 清除画像的二次确认态：首次点击进入确认，5 秒内再点执行
    [Reactive] public string ResetProfileButtonText { get; set; } = AppLanguage.T("清除收听画像", "Clear listening profile");
    // 音源逐源连接诊断结果（随语言切换重建）
    [Reactive] public string SourceDiagnosticsText { get; set; } = string.Empty;
    [Reactive] public bool IsDiagnosingSources { get; set; }
    [Reactive] public string DiagnoseSourcesButtonText { get; set; } = AppLanguage.T("音源连接诊断", "Diagnose music sources");
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
    [Reactive] public List<MusicProviderOption> MusicProviders { get; set; } = new();

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetListenerProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> DiagnoseSourcesCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectOpenSubsonicCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectOpenSubsonicCommand { get; }
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
        KugouVerificationService? kugouVerification = null,
        IListeningProfileService? listeningProfile = null,
        IMusicSearchService? musicSearch = null,
        OpenSubsonicProvider? openSubsonic = null)
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
        _listeningProfile = listeningProfile;
        _musicSearch = musicSearch;
        _openSubsonic = openSubsonic;
        SetNeteaseAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));
        SetKugouAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));

        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(() => SaveAsync());
        ResetListenerProfileCommand = ReactiveCommand.Create(ResetListenerProfile);
        DiagnoseSourcesCommand = ReactiveCommand.CreateFromTask(
            DiagnoseSourcesAsync,
            this.WhenAnyValue(x => x.IsDiagnosingSources).Select(running => !running));
        ConnectOpenSubsonicCommand = ReactiveCommand.CreateFromTask(ConnectOpenSubsonicAsync);
        DisconnectOpenSubsonicCommand = ReactiveCommand.Create(DisconnectOpenSubsonic);

        // 主题/简洁模式等无关 UI 状态的自动保存：不写 LLM 配置、不动凭据，
        // 磁盘上已有的 llm_* 字段原样保留
        SaveUiStateCommand = ReactiveCommand.CreateFromTask(() => SaveAsync(persistLlmFields: false));

        // Skip(1)：避免构造/加载赋值触发自动保存；加载期赋值由 _loadingProfileToggle 屏蔽
        _listenerProfileToggleSub = this.WhenAnyValue(x => x.ListenerProfileEnabled)
            .Skip(1)
            .Subscribe(enabled =>
            {
                if (_loadingProfileToggle)
                    return;
                if (_listeningProfile != null)
                    _listeningProfile.Enabled = enabled;
                _ = SaveUiStateCommand.Execute().Subscribe();
            });
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
                // （Subscribe 与其余调用点对齐：不订阅则 IsExecuting 不翻转、异常无人观察）
                _ = SaveUiStateCommand.Execute().Subscribe();
            });

        // 界面显示语言严格跟随本选项：加载读到旧值与用户切换时都经 Apply 生效
        _selectedLanguageSub = this.WhenAnyValue(x => x.SelectedLanguage)
            .Skip(1)
            .Subscribe(language => AppLanguage.Apply(language));

        // 语言切换时刷新常驻文案与选项列表显示名（静态事件，Dispose 退订）
        _onLanguageChanged = () =>
        {
            TestConnectionButtonText = AppLanguage.T("测试连接", "Test");
            DiagnoseSourcesButtonText = AppLanguage.T("音源连接诊断", "Diagnose music sources");
            ResetProfileButtonText = Volatile.Read(ref _listenerProfileResetArmed) != 0
                ? AppLanguage.T("再次点击确认清除", "Click again to confirm")
                : AppLanguage.T("清除收听画像", "Clear listening profile");
            CharacterProfile.RefreshLocalizedPresets();
            RelocalizeCharacterPersonality();
            if (_statusMessageFactory != null)
                StatusMessage = _statusMessageFactory();
            if (_neteaseAccountStatusFactory != null)
                NeteaseAccountStatus = _neteaseAccountStatusFactory();
            if (_kugouAccountStatusFactory != null)
                KugouAccountStatus = _kugouAccountStatusFactory();
            if (_sourceDiagnosticsFactory != null)
                SourceDiagnosticsText = _sourceDiagnosticsFactory();
            if (_openSubsonicStatusFactory != null)
                OpenSubsonicStatus = _openSubsonicStatusFactory();
            if (IsYtdlpCookieNoticeVisible && SelectedYtdlpBrowser is { Id.Length: > 0 } browser)
                YtdlpCookieNotice = BuildYtdlpCookieNotice(browser.Id);
            RebuildLocalizedOptionLists();
            foreach (var option in MusicProviders) option.RefreshLanguage();
        };
        AppLanguage.Changed += _onLanguageChanged;

        // Default selection
        SelectedCharacter = Characters[0];
        RebuildMusicProviders(Array.Empty<string>(), Array.Empty<string>());
    }

    private void RebuildMusicProviders(IReadOnlyList<string> orderedIds, IReadOnlyCollection<string> disabledIds)
    {
        if (_musicSearch is not IMusicSourceBroker broker) return;
        var order = orderedIds.Select((id, index) => (id, index))
            .Where(item => !string.IsNullOrWhiteSpace(item.id))
            .GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(disabledIds, StringComparer.OrdinalIgnoreCase);
        MusicProviders = broker.GetProviderDescriptors()
            .Select((descriptor, index) => (descriptor, index))
            .OrderBy(item => order.TryGetValue(item.descriptor.Id, out var rank) ? rank : int.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => new MusicProviderOption(item.descriptor.Id, !disabled.Contains(item.descriptor.Id)))
            .ToList();
        broker.ConfigureProviders(MusicProviders.Select(item => item.Id).ToArray(),
            MusicProviders.Where(item => !item.Enabled).Select(item => item.Id).ToArray());
    }

    public async Task ApplyMusicProviderOptionsAsync()
    {
        if (_musicSearch is not IMusicSourceBroker broker) return;
        broker.ConfigureProviders(MusicProviders.Select(item => item.Id).ToArray(),
            MusicProviders.Where(item => !item.Enabled).Select(item => item.Id).ToArray());
        await SaveAsync(persistLlmFields: false);
    }

    public async Task MoveMusicProviderAsync(string id, int direction)
    {
        var index = MusicProviders.FindIndex(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var target = index + direction;
        if (index < 0 || target < 0 || target >= MusicProviders.Count) return;
        var reordered = MusicProviders.ToList();
        (reordered[index], reordered[target]) = (reordered[target], reordered[index]);
        MusicProviders = reordered;
        await ApplyMusicProviderOptionsAsync();
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

        // 与 SaveAsync 同门互斥：加载的多个 await 之间属性逐步赋值，而窗口在加载完成前
        // 已可交互——此期间的自动保存（切主题/浏览器/退出简洁模式）会把半加载状态连同
        // 尚未读入的角色覆盖整体写盘，角色语音/人设配置丢失
        await _saveGate.WaitAsync(_lifetimeCts.Token);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await _secureStorage.GetApiKeyAsync(LlmCredentialService);
            if (!string.IsNullOrEmpty(key))
            {
                ApiKey = key;
            }

            string[] savedMusicOrder = Array.Empty<string>();
            string[] disabledMusicProviders = Array.Empty<string>();
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

                if (root.TryGetProperty("compact_show_lyrics", out var compactLyrics))
                    CompactShowLyrics = compactLyrics.GetBoolean();

                if (root.TryGetProperty("radio_sound_fx_enabled", out var radioFx))
                    RadioSoundFxEnabled = radioFx.GetBoolean();

                if (root.TryGetProperty("start_in_compact_mode", out var startCompact))
                    StartInCompactMode = startCompact.GetBoolean();

                if (root.TryGetProperty("show_lyrics_in_stage", out var showLyrics))
                    ShowLyricsInStage = showLyrics.GetBoolean();

                if (root.TryGetProperty("listener_profile_enabled", out var listenerProfile))
                {
                    // 加载期赋值只同步内存态，不触发跟随保存
                    _loadingProfileToggle = true;
                    ListenerProfileEnabled = listenerProfile.GetBoolean();
                    _loadingProfileToggle = false;
                }

                if (root.TryGetProperty("weather_city", out var weatherCity))
                    WeatherCity = weatherCity.GetString() ?? string.Empty;

                if (root.TryGetProperty("music_provider_order", out var musicOrder) &&
                    musicOrder.ValueKind == JsonValueKind.Array)
                    savedMusicOrder = musicOrder.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? string.Empty).ToArray();
                if (root.TryGetProperty("music_provider_disabled", out var disabledSources) &&
                    disabledSources.ValueKind == JsonValueKind.Array)
                    disabledMusicProviders = disabledSources.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? string.Empty).ToArray();

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

            RebuildMusicProviders(savedMusicOrder, disabledMusicProviders);

            // 画像开关初值在设置加载完成后同步给服务（主窗口加载路径也会再同步一次）
            if (_listeningProfile != null)
                _listeningProfile.Enabled = ListenerProfileEnabled;

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

            if (_openSubsonic != null)
            {
                try
                {
                    await _openSubsonic.LoadAsync(cancellationToken);
                    OpenSubsonicServerUrl = _openSubsonic.ServerUrl;
                    OpenSubsonicUsername = _openSubsonic.Username;
                    if (_openSubsonic.IsConfigured)
                        SetOpenSubsonicStatus(() => AppLanguage.T("已配置私有曲库", "Private library configured"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "OpenSubsonic configuration could not be loaded");
                    SetOpenSubsonicStatus(() => AppLanguage.T("私有曲库配置读取失败", "Could not load private library configuration"));
                }
            }

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

    private async Task ConnectOpenSubsonicAsync()
    {
        if (_openSubsonic == null) return;
        IsConnectingOpenSubsonic = true;
        SetOpenSubsonicStatus(() => AppLanguage.T("正在测试私有曲库连接…", "Testing private library connection…"));
        try
        {
            await _openSubsonic.ConnectAsync(OpenSubsonicServerUrl, OpenSubsonicUsername,
                OpenSubsonicPassword, _lifetimeCts.Token);
            OpenSubsonicPassword = string.Empty;
            SetOpenSubsonicStatus(() => AppLanguage.T("连接成功，配置已安全保存", "Connected; configuration saved securely"));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning("OpenSubsonic connection test failed: {Type}", ex.GetType().Name);
            SetOpenSubsonicStatus(() => AppLanguage.T("连接失败，请检查地址、账号和网络", "Connection failed; check server, credentials and network"));
        }
        finally { IsConnectingOpenSubsonic = false; }
    }

    private void DisconnectOpenSubsonic()
    {
        if (_openSubsonic == null) return;
        try
        {
            _openSubsonic.Disconnect();
            OpenSubsonicPassword = string.Empty;
            SetOpenSubsonicStatus(() => AppLanguage.T("已断开私有曲库", "Private library disconnected"));
        }
        catch (Exception ex)
        {
            Log.Warning("OpenSubsonic disconnect failed: {Type}", ex.GetType().Name);
            SetOpenSubsonicStatus(() => AppLanguage.T("断开失败，请重试", "Could not disconnect; try again"));
        }
    }

    private void SetOpenSubsonicStatus(Func<string> factory)
    {
        _openSubsonicStatusFactory = factory;
        OpenSubsonicStatus = factory();
    }

    /// <summary>
    /// 音源逐源连接诊断：聚合服务对每个源做 limit 1 轻量探测，结果按结构化分类渲染
    /// （复用搜索状态行的 FormatSourceStatus，含未登录/风控/接口失效等恢复建议）。
    /// </summary>
    private async Task DiagnoseSourcesAsync()
    {
        if (_musicSearch is not IMusicSourceBroker multi)
        {
            SetSourceDiagnostics(() => AppLanguage.T(
                "聚合音源服务不可用。", "The aggregated music service is unavailable."));
            return;
        }

        IsDiagnosingSources = true;
        try
        {
            if (HasExperimentalProviders)
                await RefreshAccountStatusAsync();
            var report = (await multi.DiagnoseAsync(_lifetimeCts.Token)).ToList();
            var health = multi.GetHealthSnapshots()
                .ToDictionary(item => item.SourceName, StringComparer.OrdinalIgnoreCase);
            // 文案在工厂里现算：语言切换时经 _onLanguageChanged 重建成当前语言
            SetSourceDiagnostics(() =>
            {
                var sourceLines = report.Count == 0
                    ? AppLanguage.T("没有可诊断的音源。", "No music sources to diagnose.")
                    : string.Join("\n", report.Select(item =>
                        PlaylistViewModel.FormatSourceStatus(item) +
                        (health.TryGetValue(item.Name, out var snapshot)
                            ? "\n  " + FormatSourceHealth(snapshot) : string.Empty)));
                if (!HasExperimentalProviders) return sourceLines;
                return sourceLines + "\n" + AppLanguage.T("网易云账号：", "NetEase account: ") +
                    NeteaseAccountStatus + "\n" +
                    AppLanguage.T("酷狗账号：", "Kugou account: ") + KugouAccountStatus;
            });
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Source diagnostics failed");
            SetSourceDiagnostics(() => AppLanguage.T("诊断失败，请稍后重试。", "Diagnostics failed; try again later."));
        }
        finally
        {
            IsDiagnosingSources = false;
        }
    }

    private void SetSourceDiagnostics(Func<string> factory)
    {
        _sourceDiagnosticsFactory = factory;
        SourceDiagnosticsText = factory();
    }

    private static string FormatSourceHealth(SourceHealthSnapshot snapshot)
    {
        var search = snapshot.LastSearchSuccessUtc?.ToLocalTime().ToString("g") ??
            AppLanguage.T("无记录", "none");
        var resolution = snapshot.LastResolutionSuccessUtc?.ToLocalTime().ToString("g") ??
            AppLanguage.T("无记录", "none");
        var circuit = snapshot.CircuitRemaining > TimeSpan.Zero
            ? AppLanguage.T($"熔断 {Math.Ceiling(snapshot.CircuitRemaining.TotalSeconds):0} 秒",
                $"circuit open {Math.Ceiling(snapshot.CircuitRemaining.TotalSeconds):0}s")
            : AppLanguage.T("熔断关闭", "circuit closed");
        return AppLanguage.T(
            $"近 20 条记录 {snapshot.RecentSuccessCount}/{snapshot.RecentRequestCount} 次接口正常响应；搜索成功 {search}；播放解析成功 {resolution}；{circuit}",
            $"Valid API responses {snapshot.RecentSuccessCount}/{snapshot.RecentRequestCount} in recent 20 records; search success {search}; media resolution success {resolution}; {circuit}");
    }

    /// <summary>
    /// 清除收听画像：二次确认（首次点击武装、5 秒内再点执行），武装态经按钮文案表达。
    /// 命令体保持同步、延时解除武装走 UI 调度器上的 Timer：CreateFromTask 执行期间的
    /// 再次 Execute 会被 ReactiveCommand 忽略，确认点击将失效。
    /// </summary>
    private void ResetListenerProfile()
    {
        if (_listeningProfile == null)
        {
            SetStatusMessage(() => AppLanguage.T("收听画像服务不可用", "Listening profile service unavailable"));
            return;
        }

        if (Interlocked.CompareExchange(ref _listenerProfileResetArmed, 1, 0) != 0)
        {
            // 已武装：第二次点击执行清除
            Interlocked.Increment(ref _resetArmVersion);
            Interlocked.Exchange(ref _listenerProfileResetArmed, 0);
            _listeningProfile.Reset();
            ResetProfileButtonText = AppLanguage.T("清除收听画像", "Clear listening profile");
            SetStatusMessage(() => AppLanguage.T("收听画像已清除", "Listening profile cleared"));
            return;
        }

        ResetProfileButtonText = AppLanguage.T("再次点击确认清除", "Click again to confirm");
        SetStatusMessage(() => AppLanguage.T("5 秒内再次点击以确认清除收听画像", "Click again within 5 seconds to confirm"));
        var version = Interlocked.Increment(ref _resetArmVersion);
        // 计时用默认调度器（MainThreadScheduler 在测试环境可能被替换为 Immediate，
        // Observable.Timer 的 dueTime 会被忽略而立即解除武装），回调再切回 UI 线程
        Observable.Timer(TimeSpan.FromSeconds(5))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (Volatile.Read(ref _resetArmVersion) == version && Volatile.Read(ref _listenerProfileResetArmed) != 0)
                {
                    Interlocked.Exchange(ref _listenerProfileResetArmed, 0);
                    ResetProfileButtonText = AppLanguage.T("清除收听画像", "Clear listening profile");
                }
            });
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
                // 刷新期间（数秒级网络往返）用户可能登出/重新扫码：store 值一旦变化就
                // 不再回写旧会话，否则会把登出/新登录静默覆盖（与播放路径同口径）
                var latest = _accounts.KugouCookie;
                if (!string.Equals(latest, baseline, StringComparison.Ordinal))
                {
                    Log.Information("Kugou credential changed during status refresh; keeping the newer stored value");
                    if (latest == null)
                    {
                        // 刷新期间用户登出：按登出口径显示，不拿旧会话再查快照
                        SetKugouAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));
                        return;
                    }
                    refreshed = latest;
                }
                else if (!string.Equals(baseline, refreshed, StringComparison.Ordinal))
                {
                    await _accounts.SetKugouCookieAsync(refreshed);
                }

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

            SetNeteaseQrImage(CreateBitmap(session.QrPng));
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
                        SetNeteaseQrImage(null);
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
            // 异常消息可能携带含 QR key/userid 的请求 URL：进 UI 前统一脱敏
            var detail = Services.SensitiveDataSanitizer.Sanitize(ex.Message);
            SetNeteaseAccountStatus(() => AppLanguage.T($"登录失败：{detail}", $"Login failed: {detail}"));
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

            SetKugouQrImage(CreateBitmap(session.QrPng));
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
                        SetKugouQrImage(null);
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
            var detail = Services.SensitiveDataSanitizer.Sanitize(ex.Message);
            SetKugouAccountStatus(() => AppLanguage.T($"登录失败：{detail}", $"Login failed: {detail}"));
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
        SetNeteaseQrImage(null);
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
            var detail = Services.SensitiveDataSanitizer.Sanitize(ex.Message);
            SetKugouAccountStatus(() => AppLanguage.T($"验证失败：{detail}", $"Verification failed: {detail}"));
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
        SetKugouQrImage(null);
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

    private void SetNeteaseQrImage(IImage? image)
    {
        // Bitmap 持非托管内存：覆盖/置空前 Dispose 旧图，仅靠终结器回收会随反复扫码累积
        (NeteaseQrImage as IDisposable)?.Dispose();
        NeteaseQrImage = image;
    }

    private void SetKugouQrImage(IImage? image)
    {
        (KugouQrImage as IDisposable)?.Dispose();
        KugouQrImage = image;
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
                compact_show_lyrics = CompactShowLyrics,
                radio_sound_fx_enabled = RadioSoundFxEnabled,
                start_in_compact_mode = StartInCompactMode,
                show_lyrics_in_stage = ShowLyricsInStage,
                listener_profile_enabled = ListenerProfileEnabled,
                weather_city = WeatherCity,
                speech_mix_mode = SpeechMixMode,
                language = SelectedLanguage,
                ytdlp_cookie_browser = _accounts.YtdlpCookieBrowser ?? "",
                music_provider_order = MusicProviders.Select(item => item.Id).ToArray(),
                music_provider_disabled = MusicProviders.Where(item => !item.Enabled)
                    .Select(item => item.Id).ToArray(),
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
        _listenerProfileToggleSub.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
        // 给在途 SaveAsync 一个短窗口退出：改完设置立即关窗时最后一次保存
        // 可能停在 gate/写盘的 await 上，被取消吞掉后静默丢变更
        try
        {
            if (!_saveGate.Wait(TimeSpan.FromMilliseconds(500)))
                Log.Warning("Settings save still in flight during shutdown; last change may be lost");
        }
        catch (ObjectDisposedException)
        {
        }
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
