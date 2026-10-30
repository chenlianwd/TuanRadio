using ReactiveUI;
using ReactiveUI.SourceGenerators;
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
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

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

/// <summary>「不爱听的歌」展示条目（Title/Artist 供移除回传，DisplayText 供列表渲染）。</summary>
public sealed record ProfileBlacklistItem(string Title, string Artist, string DisplayText);

public partial class SettingsViewModel : ViewModelBase, IDisposable
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
    private readonly IDisposable _openSubsonicQualitySub;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _isLoading;
    private readonly MusicAccountStore _accounts;
    private readonly NeteaseAccountService _neteaseAccount;
    private readonly KugouAccountService _kugouAccount;
    private readonly KugouVerificationService? _kugouVerification;
    private readonly IListeningProfileService? _listeningProfile;
    private readonly IRecommendationService? _recommendationService;
    private readonly IMusicSearchService? _musicSearch;
    private readonly OpenSubsonicProvider? _openSubsonic;
    private readonly AudiusProvider? _audius;
    private readonly IWeatherService _weather;
    // 音色试听：合成与播放都复用生产链（null 时试听按钮静默无动作）
    private readonly ITtsService? _tts;
    private readonly IAudioService? _audio;
    private readonly IDisposable _weatherCitySearchSub;
    // 已落定地点的标准名（加载/点选/定位写入）：手输文本与它一致视为确认态，
    // 不弹联想不清坐标；改写为不同文本才触发搜索（节流回调晚于程序赋值，
    // 标志位拦不住，只能按内容判断）
    private string? _selectedWeatherPlaceName;
    // 联想请求代次：迟到的旧结果不覆盖新一轮输入
    private int _weatherSearchEpoch;
    private Func<string>? _audiusStatusFactory;
    private Func<string>? _openSubsonicStatusFactory;
    private readonly IDisposable _listenerProfileToggleSub;
    private readonly IDisposable _localModelSettingsSub;
    private readonly IDisposable _llmProfileSwapSub;
    private readonly IDisposable _llmWatermarkSub;
    private int _localModelsEpoch;
    private bool _loadingProfileToggle;
    private int _listenerProfileResetArmed;
    private int _resetArmVersion;
    private Func<string>? _sourceDiagnosticsFactory;
    private bool _kugouVerifyRunning;
    private bool _neteaseQrRunning;
    private bool _kugouQrRunning;
    private bool _loadingYtdlpBrowser;
    private bool _loadingOpenSubsonicQuality;
    private bool _loadingMusicSourcePolicy;
    private readonly IDisposable _musicSourcePolicySub;
    private bool _loadingAccentPalette;
    private readonly IDisposable _accentPaletteSub;
    private bool _ytdlpCookieNoticeShown;
    private readonly IDisposable _selectedLanguageSub;
    // 常驻文案/选项列表随语言切换重建；静态事件必须持委托在 Dispose 退订
    private readonly Action _onLanguageChanged;
    private string? _lastCharacterSignature;
    private bool _lastSaveSucceeded;
    // 本会话是否完整跑完过一次设置加载（LoadCoreAsync try 尾部置位，异常中断会跳过）：
    // SaveAsync 据此拒绝在"从未加载/加载异常"状态下把 VM 默认值整体覆盖到盘上已有配置
    private bool _settingsStateLoaded;
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

    [Reactive] public partial string ApiKey { get; set; } = string.Empty;
    [Reactive] public partial string SelectedProvider { get; set; } = "openai";
    [Reactive] public partial string BaseUrl { get; set; } = string.Empty;
    /// <summary>Base URL 输入框水印：显示当前服务的内置默认地址（留空即用它）。</summary>
    [Reactive] public partial string BaseUrlWatermark { get; private set; } = string.Empty;
    /// <summary>模型名输入框水印：按服务给示例；本地模型提示输入任意字符可弹出列表（纯点击不弹）。</summary>
    [Reactive] public partial string ModelWatermark { get; private set; } = string.Empty;
    [Reactive] public partial string Model { get; set; } = string.Empty;
    /// <summary>本地模型不需要 API Key：选中时隐藏 Key 输入框（判定与 RequiresApiKey 同源）。</summary>
    [Reactive] public partial bool LlmNeedsApiKey { get; set; } = true;

    /// <summary>单个 LLM 服务的独立配置档案；ApiKey 是会话缓存，跨会话权威来源=凭据管理器专槽。</summary>
    private sealed class LlmProfile
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }

    // llm_profiles 键白名单（服务 Id 与下拉三选项一致；未知键丢弃防 NormalizeProvider
    // 兜底把手编/未来版本的键并进 openai 档案）
    private static readonly string[] LlmProfileKeys = { "openai", "anthropic", "local" };
    // WindowsSecureStorage blob 上限 2560 字节（Unicode 编码即 2 字节/字符）
    private const int MaxLlmKeyLength = 1280;
    // 每服务档案：不可变字典整体换新（COW）——切换处理器在 _saveGate 外修改、保存序列化
    // 在 Task.Run 内枚举，引用整体替换避免并发枚举异常；ApiKey 不进 json 只进凭据专槽
    private Dictionary<string, LlmProfile> _llmProfiles = new();
    // 当前字段内容归属的服务（切换处理器与 LoadCoreAsync 尾部双点维护）
    private string _lastProvider = "openai";

    /// <summary>模型候选（本地 Ollama /api/tags、云端 /models 按 Key 鉴权），供模型输入框下拉选择；拉取失败为空、手填兜底。</summary>
    [Reactive] public partial List<string> LocalModels { get; set; } = new();
    [Reactive] public partial string StatusMessage { get; set; } = string.Empty;
    [Reactive] public partial bool IsTesting { get; set; }
    [Reactive] public partial string TestConnectionButtonText { get; set; } = AppLanguage.T("测试连接", "Test");
    [Reactive] public partial bool TtsEnabled { get; set; } = true;
    [Reactive] public partial bool IsDarkMode { get; set; } = true;
    [Reactive] public partial bool EnableStarfield { get; set; } = true;
    [Reactive] public partial string SelectedSpectrumStyle { get; set; } = "bars";
    [Reactive] public partial bool CompactModeTopmost { get; set; } = true;
    [Reactive] public partial bool CompactShowLyrics { get; set; } = true;
    [Reactive] public partial bool RadioSoundFxEnabled { get; set; } = true;
    [Reactive] public partial bool StartInCompactMode { get; set; }
    [Reactive] public partial bool ShowLyricsInStage { get; set; }
    [Reactive] public partial bool ListenerProfileEnabled { get; set; } = true;
    // 天气城市：留空时按 IP 自动定位（见 WeatherService）
    [Reactive] public partial string WeatherCity { get; set; } = string.Empty;
    /// <summary>已选地点纬度：点选候选/定位写入，手输城市名清空（回退 geocoding）。</summary>
    [Reactive] public partial double? WeatherLatitude { get; private set; }
    /// <summary>已选地点经度：与 WeatherLatitude 成对。</summary>
    [Reactive] public partial double? WeatherLongitude { get; private set; }
    /// <summary>城市搜索联想候选（手输触发，点选后清空）。</summary>
    [Reactive] public partial IReadOnlyList<GeoPlace> WeatherCitySuggestions { get; private set; }
        = Array.Empty<GeoPlace>();
    /// <summary>手输搜索完成且零匹配（文本非空、未落定）。</summary>
    [Reactive] public partial bool WeatherCityNoMatch { get; private set; }
    /// <summary>城市搜索网络失败（可重试）。</summary>
    [Reactive] public partial bool WeatherCitySearchFailed { get; private set; }
    /// <summary>定位按钮运行态（防重入）。</summary>
    [Reactive] public partial bool IsWeatherLocating { get; private set; }
    [Reactive] public partial string OpenSubsonicServerUrl { get; set; } = string.Empty;
    [Reactive] public partial string OpenSubsonicUsername { get; set; } = string.Empty;
    [Reactive] public partial string OpenSubsonicPassword { get; set; } = string.Empty;
    [Reactive] public partial string OpenSubsonicStatus { get; set; } = string.Empty;
    [Reactive] public partial string SelectedOpenSubsonicQuality { get; set; } = "auto";
    [Reactive] public partial string SelectedMusicSourcePolicy { get; set; } = "coverage";
    [Reactive] public partial string SelectedAccentPalette { get; set; } = AccentPalettes.MintId;
    [Reactive] public partial bool IsConnectingOpenSubsonic { get; set; }
    [Reactive] public partial string AudiusApiKeyInput { get; set; } = string.Empty;
    [Reactive] public partial string AudiusStatus { get; set; } = string.Empty;
    // 清除画像的二次确认态：首次点击进入确认，5 秒内再点执行
    [Reactive] public partial string ResetProfileButtonText { get; set; } = AppLanguage.T("清除收听画像", "Clear listening profile");
    // 「DJ 对你的印象」展示卡（数据源 GetSnapshot 现算，四态见 RefreshProfileCard）
    [Reactive] public partial bool ProfileHasCard { get; set; }
    [Reactive] public partial string ProfileEmptyHint { get; set; } = string.Empty;
    [Reactive] public partial bool ProfileHasDigest { get; set; }
    [Reactive] public partial string ProfileDigestText { get; set; } = string.Empty;
    [Reactive] public partial string ProfileDigestMeta { get; set; } = string.Empty;
    [Reactive] public partial bool ProfileHasStyleTags { get; set; }
    // 标签/特质列表必须整体换新 List 实例（同黑名单条目：就地 Clear+AddRange 绑定不刷新）
    [Reactive] public partial List<string> ProfileStyleTagItems { get; set; } = new();
    [Reactive] public partial bool ProfileHasPersonaTraits { get; set; }
    [Reactive] public partial List<string> ProfilePersonaTraitItems { get; set; } = new();
    [Reactive] public partial bool ProfileHasImpression { get; set; }
    [Reactive] public partial string ProfileImpressionText { get; set; } = string.Empty;
    [Reactive] public partial string ProfileImpressionMeta { get; set; } = string.Empty;
    [Reactive] public partial bool ProfileHasImpressionTraits { get; set; }
    [Reactive] public partial List<string> ProfileImpressionTraitItems { get; set; } = new();
    [Reactive] public partial bool ProfileHasTopArtists { get; set; }
    [Reactive] public partial string ProfileTopArtistsText { get; set; } = string.Empty;
    // 行内只列歌手名，收听次数收进 Tooltip（印象优先：卡片不是数据报表）
    [Reactive] public partial string ProfileTopArtistsDetail { get; set; } = string.Empty;
    [Reactive] public partial bool ProfileHasAvoidArtists { get; set; }
    [Reactive] public partial string ProfileAvoidArtistsText { get; set; } = string.Empty;
    [Reactive] public partial bool ProfileHasBlacklist { get; set; }
    // 重新归纳执行中（禁用按钮，canExecute 与 IsEnabled 双保险同 DiagnoseSources）
    [Reactive] public partial bool IsRegeneratingDigest { get; set; }
    // 黑名单条目必须整体换新 List 实例（同 MusicProviders：就地 Clear+AddRange 绑定不刷新）
    [Reactive] public partial List<ProfileBlacklistItem> ProfileBlacklistItems { get; set; } = new();
    // 音源逐源连接诊断结果（随语言切换重建）
    [Reactive] public partial string SourceDiagnosticsText { get; set; } = string.Empty;
    [Reactive] public partial bool IsDiagnosingSources { get; set; }
    [Reactive] public partial string DiagnoseSourcesButtonText { get; set; } = AppLanguage.T("音源连接诊断", "Diagnose music sources");
    [Reactive] public partial string SpeechMixMode { get; set; } = "duck";
    [Reactive] public partial string SelectedLanguage { get; set; } = "zh"; // "zh" or "en"

    // 设置页侧边导航当前页：0=AI 服务, 1=音乐音源, 2=主播与语音, 3=外观与通用, 4=收听画像。
    // 瞬态 UI 状态，不持久化到 settings.json。
    [Reactive] public partial int TabIndex { get; set; }

    // 音源账号（网易扫码/酷狗扫码/yt-dlp cookies）
    [Reactive] public partial string NeteaseAccountStatus { get; set; } = AppLanguage.T("未登录", "Not signed in");
    [Reactive] public partial IImage? NeteaseQrImage { get; set; }
    [Reactive] public partial bool IsNeteaseQrVisible { get; set; }
    [Reactive] public partial string KugouAccountStatus { get; set; } = AppLanguage.T("未登录", "Not signed in");
    [Reactive] public partial IImage? KugouQrImage { get; set; }
    [Reactive] public partial bool IsKugouQrVisible { get; set; }
    [Reactive] public partial VoiceOption? SelectedYtdlpBrowser { get; set; }
    // 首次启用浏览器 Cookies 的隐私提示（每次会话提示一次）
    [Reactive] public partial string YtdlpCookieNotice { get; set; } = string.Empty;
    [Reactive] public partial bool IsYtdlpCookieNoticeVisible { get; set; }

    // Character customization
    public List<CharacterProfile> Characters { get; } = CharacterProfile.Presets;
    [Reactive] public partial CharacterProfile? SelectedCharacter { get; set; }
    [Reactive] public partial VoiceOption? CharacterVoice { get; set; }
    [Reactive] public partial string CharacterPersonality { get; set; } = string.Empty;

    // 依赖语言的选项列表：语言切换时整体换新实例。
    // 不能就地 Clear+AddRange：绑定重读到同一 List 实例会被 Avalonia 按相等性去重，
    // ItemsSource 不会重新赋值，下拉项将停留旧语言。
    [Reactive] public partial List<VoiceOption> Voices { get; set; } = new();

    /// <summary>音色试听运行态（防重入，canExecute 联动禁用按钮）。</summary>
    [Reactive] public partial bool IsPreviewingVoice { get; private set; }
    /// <summary>音色试听失败提示；成功或未试听为空。空文本的 TextBlock 仍占一行字高
    /// （Avalonia 空串建空文本行），与同页音源诊断状态行先例一致。</summary>
    [Reactive] public partial string VoicePreviewStatus { get; private set; } = string.Empty;

    // 语言名按 i18n 惯例保留母语写法，不随界面语言重建
    public List<VoiceOption> Languages { get; } = new()
    {
        new() { Id = "zh", DisplayName = "中文" },
        new() { Id = "en", DisplayName = "English" },
    };

    [Reactive] public partial List<VoiceOption> LlmProviders { get; set; } = new();

    [Reactive] public partial List<VoiceOption> SpeechMixModes { get; set; } = new();

    [Reactive] public partial List<VoiceOption> SpectrumStyles { get; set; } = new();

    [Reactive] public partial List<VoiceOption> YtdlpBrowsers { get; set; } = new();
    [Reactive] public partial List<MusicProviderOption> MusicProviders { get; set; } = new();
    [Reactive] public partial List<VoiceOption> OpenSubsonicQualities { get; set; } = new();
    [Reactive] public partial List<VoiceOption> MusicSourcePolicies { get; set; } = new();
    [Reactive] public partial List<VoiceOption> AccentPaletteOptions { get; set; } = new();

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    // 音色试听：合成走与 DJ 开口同链的风格 Id 映射，播放走真实 TTS 通道（听到即所得）
    public ReactiveCommand<Unit, Unit> PreviewVoiceCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetListenerProfileCommand { get; }
    public ReactiveCommand<ProfileBlacklistItem, Unit> RemoveBlacklistItemCommand { get; }
    public ReactiveCommand<Unit, Unit> RegenerateDigestCommand { get; }
    public ReactiveCommand<Unit, Unit> DiagnoseSourcesCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectOpenSubsonicCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectOpenSubsonicCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAudiusApiKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAudiusApiKeyCommand { get; }
    public ReactiveCommand<GeoPlace, Unit> SelectWeatherCityCommand { get; }
    public ReactiveCommand<Unit, Unit> LocateWeatherCityCommand { get; }
    public ReactiveCommand<Unit, Unit> NeteaseQrLoginCommand { get; }
    public ReactiveCommand<Unit, Unit> NeteaseLogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> KugouQrLoginCommand { get; }
    public ReactiveCommand<Unit, Unit> KugouLogoutCommand { get; }
    public ReactiveCommand<Unit, Unit> KugouVerifyCommand { get; }

    // 设置页侧边导航切页命令（同 PlaylistViewModel.ShowPlaylistCommand 族的无参切 tab 模式）
    public ReactiveCommand<Unit, Unit> ShowAiTabCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowMusicTabCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHostTabCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowAppearanceTabCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowProfileTabCommand { get; }

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
        OpenSubsonicProvider? openSubsonic = null,
        AudiusProvider? audius = null,
        IRecommendationService? recommendationService = null,
        IWeatherService? weatherService = null,
        ITtsService? ttsService = null,
        IAudioService? audioService = null)
    {
        _llmService = llmService;
        _secureStorage = secureStorage;
        _settingsFile = settingsFile;
        _settingsDir = Path.GetDirectoryName(_settingsFile) ?? SettingsDir;
        _accounts = accountStore ?? new MusicAccountStore(secureStorage);
        var http = httpClient ?? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _weather = weatherService ?? new WeatherService(http);
        _neteaseAccount = new NeteaseAccountService(http);
        _kugouAccount = new KugouAccountService(http);
        _kugouVerification = kugouVerification;
        _listeningProfile = listeningProfile;
        _recommendationService = recommendationService;
        _musicSearch = musicSearch;
        _openSubsonic = openSubsonic;
        _audius = audius;
        _tts = ttsService;
        _audio = audioService;
        SetNeteaseAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));
        SetKugouAccountStatus(() => AppLanguage.T("未登录", "Not signed in"));

        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        PreviewVoiceCommand = ReactiveCommand.CreateFromTask(
            PreviewVoiceAsync,
            this.WhenAnyValue(x => x.IsPreviewingVoice).Select(running => !running));
        SaveCommand = ReactiveCommand.CreateFromTask(() => SaveAsync());
        ResetListenerProfileCommand = ReactiveCommand.Create(ResetListenerProfile);
        RemoveBlacklistItemCommand = ReactiveCommand.Create<ProfileBlacklistItem>(RemoveBlacklistItem);
        RegenerateDigestCommand = ReactiveCommand.CreateFromTask(
            RegenerateDigestAsync,
            this.WhenAnyValue(x => x.IsRegeneratingDigest).Select(running => !running));
        DiagnoseSourcesCommand = ReactiveCommand.CreateFromTask(
            DiagnoseSourcesAsync,
            this.WhenAnyValue(x => x.IsDiagnosingSources).Select(running => !running));
        ConnectOpenSubsonicCommand = ReactiveCommand.CreateFromTask(ConnectOpenSubsonicAsync);
        DisconnectOpenSubsonicCommand = ReactiveCommand.Create(DisconnectOpenSubsonic);
        SaveAudiusApiKeyCommand = ReactiveCommand.CreateFromTask(SaveAudiusApiKeyAsync);
        ClearAudiusApiKeyCommand = ReactiveCommand.Create(ClearAudiusApiKey);
        SelectWeatherCityCommand = ReactiveCommand.Create<GeoPlace>(SelectWeatherCity);
        LocateWeatherCityCommand = ReactiveCommand.CreateFromTask(
            LocateWeatherCityAsync,
            this.WhenAnyValue(x => x.IsWeatherLocating).Select(running => !running));

        // 天气城市联想：手输节流后搜 geocoding 候选；程序赋值（点选/定位/加载）靠
        // _selectedWeatherPlaceName 内容守卫兜住（节流回调晚于程序赋值，标志位拦不住）
        _weatherCitySearchSub = this.WhenAnyValue(x => x.WeatherCity)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(300), RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(text => _ = HandleWeatherCityTypedAsync(text));

        // 切换服务=换档案：当前字段存回旧服务、载入新服务自己的 Key/地址/模型。
        // 只改表单字段不触碰运行中服务配置（ConfigureLlm 仅发生在保存/测试连接，
        // 电台可能正在播）；加载期赋值被 _isLoading 屏蔽，归属对齐由 LoadCoreAsync
        // 尾部和解负责。订阅顺序相对 _localModelSettingsSub 无约束：本地模型枚举的
        // 代次入口预递增（RefreshLocalModelsAsync 首行）会作废切换引发的旧端点枚举。
        _llmProfileSwapSub = this.WhenAnyValue(x => x.SelectedProvider)
            .Skip(1)
            .Subscribe(provider =>
            {
                if (_isLoading)
                    return;
                var to = NormalizeProvider(provider);
                if (to == _lastProvider)
                    return;
                SwapLlmProfile(_lastProvider, to);
                _lastProvider = to;
            });

        // provider/地址/Key 任一变化即清空旧候选并作废在途结果（云端模型列表经
        // Bearer/x-api-key 鉴权，Key 从档案回填晚到也要重拉）；防抖，避免逐字输入
        // 地址/Key 时打出一串 HTTP 请求。加载期赋值被屏蔽，LoadCoreAsync 尾部显式补拉一次。
        _localModelSettingsSub = this.WhenAnyValue(x => x.SelectedProvider, x => x.BaseUrl, x => x.ApiKey)
            .Skip(1)
            .Subscribe(settings =>
            {
                if (_isLoading)
                    return;
                LocalModels = new();
                _ = RefreshLocalModelsAsync(debounce: true);
            });

        // 水印纯展示、只随服务走：无加载守卫（幂等赋值），语言切换由
        // RebuildLocalizedOptionLists 内的重算兜底
        _llmWatermarkSub = this.WhenAnyValue(x => x.SelectedProvider)
            .Subscribe(_ =>
            {
                UpdateBaseUrlWatermark();
                UpdateModelWatermark();
            });

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
                RefreshProfileCard();
                _ = SaveUiStateCommand.Execute().Subscribe();
            });
        NeteaseQrLoginCommand = ReactiveCommand.CreateFromTask(() => RunNeteaseQrLoginAsync());
        NeteaseLogoutCommand = ReactiveCommand.CreateFromTask(() => LogoutNeteaseAsync());
        ShowAiTabCommand = ReactiveCommand.Create(() => { TabIndex = 0; });
        ShowMusicTabCommand = ReactiveCommand.Create(() => { TabIndex = 1; });
        ShowHostTabCommand = ReactiveCommand.Create(() => { TabIndex = 2; });
        ShowAppearanceTabCommand = ReactiveCommand.Create(() => { TabIndex = 3; });
        ShowProfileTabCommand = ReactiveCommand.Create(() => { TabIndex = 4; RefreshProfileCard(); });
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

        _openSubsonicQualitySub = this.WhenAnyValue(x => x.SelectedOpenSubsonicQuality)
            .Skip(1)
            .Subscribe(id =>
            {
                if (id is not ("auto" or "original" or "data-saver"))
                    return;
                if (_openSubsonic != null)
                    _openSubsonic.Quality = ParseOpenSubsonicQuality(id);
                if (!_loadingOpenSubsonicQuality)
                    _ = SaveUiStateCommand.Execute().Subscribe();
            });
        _musicSourcePolicySub = this.WhenAnyValue(x => x.SelectedMusicSourcePolicy)
            .Skip(1)
            .Subscribe(id =>
            {
                if (id is not ("coverage" or "stability" or "quality"))
                    return;
                if (_musicSearch is IMusicSourceBroker broker)
                    broker.SourcePolicy = ParseMusicSourcePolicy(id);
                if (!_loadingMusicSourcePolicy)
                    _ = SaveUiStateCommand.Execute().Subscribe();
            });
        // 配色主题：切换即生效并跟随保存（加载期赋值由 _loadingAccentPalette 屏蔽，
        // 启动恢复由 MainWindowViewModel.LoadLocalStateAsync 统一 Apply）
        _accentPaletteSub = this.WhenAnyValue(x => x.SelectedAccentPalette)
            .Skip(1)
            .Subscribe(id =>
            {
                if (_loadingAccentPalette)
                    return;
                AccentPalettes.Apply(Avalonia.Application.Current, id);
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
            // 展示卡的「更新于」与四态提示是 VM 拼串，随语言重建
            RefreshProfileCard();
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
            if (_audiusStatusFactory != null)
                AudiusStatus = _audiusStatusFactory();
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

    private static string NormalizeOpenSubsonicQuality(string? id)
        => id is "original" or "data-saver" ? id : "auto";

    private static string NormalizeMusicSourcePolicy(string? id)
        => id is "stability" or "quality" ? id : "coverage";

    private static MusicSourcePolicy ParseMusicSourcePolicy(string? id)
        => id switch
        {
            "stability" => MusicSourcePolicy.Stability,
            "quality" => MusicSourcePolicy.Quality,
            _ => MusicSourcePolicy.Coverage
        };

    private static OpenSubsonicQuality ParseOpenSubsonicQuality(string? id)
        => id switch
        {
            "original" => OpenSubsonicQuality.Original,
            "data-saver" => OpenSubsonicQuality.DataSaver,
            _ => OpenSubsonicQuality.Automatic
        };

    /// <summary>按当前服务与语言重算 Base URL 水印（显示该服务的内置默认地址）。</summary>
    private void UpdateBaseUrlWatermark()
    {
        var url = LLMService.GetDefaultBaseUrl(SelectedProvider);
        BaseUrlWatermark = AppLanguage.T($"Base URL；留空使用 {url}", $"Base URL; leave empty for {url}");
    }

    /// <summary>按当前服务与语言重算模型名水印（示例文案；本地模型教下拉手势）。</summary>
    private void UpdateModelWatermark()
    {
        ModelWatermark = NormalizeProvider(SelectedProvider) switch
        {
            "anthropic" => AppLanguage.T("模型名称，例如 claude-sonnet-4-5", "Model name, e.g. claude-sonnet-4-5"),
            "local" => AppLanguage.T("模型名称，输入任意字符列出本机模型", "Model name; type any character to list local models"),
            _ => AppLanguage.T("模型名称，例如 gpt-4o-mini / deepseek-chat", "Model name, e.g. gpt-4o-mini / deepseek-chat"),
        };
    }

    /// <summary>重建依赖语言的选项列表；按 Id 保留既有选择，浏览器选择重建不触发自动保存。</summary>
    private void RebuildLocalizedOptionLists()
    {
        UpdateBaseUrlWatermark();
        UpdateModelWatermark();
        var voiceId = CharacterVoice?.Id;
        Voices = new List<VoiceOption>
        {
            new() { Id = "male-qn-qingse", DisplayName = AppLanguage.T("青涩男声", "Soft male") },
            new() { Id = "male-qn-jingying", DisplayName = AppLanguage.T("精英男声", "Elite male") },
            new() { Id = "male-qn-badao", DisplayName = AppLanguage.T("霸道男声", "Bold male") },
            new() { Id = "female-shaonv", DisplayName = AppLanguage.T("少女音", "Girl voice") },
            new() { Id = "female-yujie", DisplayName = AppLanguage.T("知性女声", "Refined female") },
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

        OpenSubsonicQualities = new List<VoiceOption>
        {
            new() { Id = "auto", DisplayName = AppLanguage.T("自动", "Automatic") },
            new() { Id = "original", DisplayName = AppLanguage.T("原始音质", "Original quality") },
            new() { Id = "data-saver", DisplayName = AppLanguage.T("省流量（最高 128 kbps）", "Data saver (up to 128 kbps)") },
        };
        MusicSourcePolicies = new List<VoiceOption>
        {
            new() { Id = "coverage", DisplayName = AppLanguage.T("覆盖优先", "Coverage first") },
            new() { Id = "stability", DisplayName = AppLanguage.T("稳定优先", "Stability first") },
            new() { Id = "quality", DisplayName = AppLanguage.T("音质优先", "Quality first") },
        };
        AccentPaletteOptions = AccentPalettes.AllIds.Select(id => new VoiceOption
        {
            Id = id,
            DisplayName = id switch
            {
                AccentPalettes.AmberId => AppLanguage.T("暖琥珀", "Amber"),
                AccentPalettes.SynthwaveId => AppLanguage.T("蒸汽波紫", "Synthwave"),
                AccentPalettes.IceId => AppLanguage.T("冰蓝", "Ice Blue"),
                _ => AppLanguage.T("薄荷绿", "Mint"),
            },
        }).ToList();
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
        _isLoading = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 旧单槽 Key：仅作为 active 服务专槽为空时的迁移回退来源（见下方档案装配）
            var legacyKey = await _secureStorage.GetApiKeyAsync(LlmCredentialService);

            string[] savedMusicOrder = Array.Empty<string>();
            string[] disabledMusicProviders = Array.Empty<string>();
            Dictionary<string, LlmProfile>? parsedProfiles = null;
            var diskActiveProvider = NormalizeProvider(SelectedProvider);
            using var doc = await OpenSettingsDocumentAsync(cancellationToken);
            if (doc != null)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("llm_provider", out var provider))
                    SelectedProvider = NormalizeProvider(provider.GetString());
                // 紧跟赋值捕获盘上 active：后续 await 间隙的用户下拉切换不改变字段内容的归属基准
                diskActiveProvider = NormalizeProvider(SelectedProvider);

                if (root.TryGetProperty("llm_base_url", out var baseUrl))
                    BaseUrl = baseUrl.GetString() ?? string.Empty;

                if (root.TryGetProperty("llm_model", out var model))
                    Model = model.GetString() ?? string.Empty;

                // 其余服务的档案（active 服务以扁平字段为权威，兼容旧版/手编 json）；
                // 键白名单+逐项 String 过滤：坏档案只丢自己，不拖垮其余字段加载
                parsedProfiles = ParseLlmProfiles(root);

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

                if (root.TryGetProperty("accent_palette", out var accentPalette))
                {
                    _loadingAccentPalette = true;
                    SelectedAccentPalette = AccentPalettes.Normalize(accentPalette.GetString());
                    _loadingAccentPalette = false;
                }

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
                {
                    WeatherCity = weatherCity.GetString() ?? string.Empty;
                    // 城市名即落定态：手输同名文本不弹联想；坐标键存在则取数时直用（旧配置缺坐标回退 geocoding）
                    _selectedWeatherPlaceName = WeatherCity;
                }
                if (root.TryGetProperty("weather_latitude", out var weatherLat) && weatherLat.ValueKind == JsonValueKind.Number)
                    WeatherLatitude = weatherLat.GetDouble();
                if (root.TryGetProperty("weather_longitude", out var weatherLon) && weatherLon.ValueKind == JsonValueKind.Number)
                    WeatherLongitude = weatherLon.GetDouble();

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

                if (root.TryGetProperty("open_subsonic_quality", out var quality) &&
                    quality.ValueKind == JsonValueKind.String)
                {
                    _loadingOpenSubsonicQuality = true;
                    SelectedOpenSubsonicQuality = NormalizeOpenSubsonicQuality(quality.GetString());
                    _loadingOpenSubsonicQuality = false;
                }
                if (root.TryGetProperty("music_source_policy", out var sourcePolicy) &&
                    sourcePolicy.ValueKind == JsonValueKind.String)
                {
                    _loadingMusicSourcePolicy = true;
                    SelectedMusicSourcePolicy = NormalizeMusicSourcePolicy(sourcePolicy.GetString());
                    if (_musicSearch is IMusicSourceBroker policyBroker)
                        policyBroker.SourcePolicy = ParseMusicSourcePolicy(SelectedMusicSourcePolicy);
                    _loadingMusicSourcePolicy = false;
                }
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

            // active 服务档案装配：扁平字段为权威；Key 专槽优先、旧单槽回退（唯一回退点）。
            // 回退命中即同步镜像写专槽完成一次性迁移——同步 await 在 _saveGate 内顺序化，
            // 消除与用户随后保存写同槽的竞态
            var activeProfile = new LlmProfile { BaseUrl = BaseUrl, Model = Model };
            var activeKey = await _secureStorage.GetApiKeyAsync(LlmCredentialSlot(diskActiveProvider));
            if (string.IsNullOrEmpty(activeKey) && !string.IsNullOrEmpty(legacyKey))
            {
                activeKey = legacyKey;
                try
                {
                    await _secureStorage.SaveApiKeyAsync(LlmCredentialSlot(diskActiveProvider), activeKey);
                }
                catch (Exception migrateEx)
                {
                    Log.Debug(migrateEx, "Legacy LLM key slot migration failed");
                }
            }
            if (!string.IsNullOrEmpty(activeKey))
            {
                activeProfile.ApiKey = activeKey;
                ApiKey = activeKey;
            }
            var profiles = parsedProfiles ?? new Dictionary<string, LlmProfile>();
            profiles[diskActiveProvider] = activeProfile;
            _llmProfiles = profiles;
            _lastProvider = diskActiveProvider;

            ConfigureLlm();

            // 账号昵称查询依赖本地音乐代理，就绪后由 RefreshAccountStatusAsync 刷新；
            // 加载期只按 cookie 有无恢复基础状态，避免误显示"未登录"，也不发网络请求
            if (!string.IsNullOrEmpty(_accounts.NeteaseCookie))
                SetNeteaseAccountStatus(() => AppLanguage.T("已登录", "Signed in"));
            if (!string.IsNullOrEmpty(_accounts.KugouCookie))
                SetKugouAccountStatus(() => AppLanguage.T("已登录", "Signed in"));

            if (_audius != null)
            {
                try
                {
                    await _audius.LoadAsync(cancellationToken);
                    SetAudiusStatus(_audius.HasApiKey
                        ? () => AppLanguage.T("API Key 已安全保存", "API Key saved securely")
                        : () => AppLanguage.T("公开无 Key 模式", "Public mode without an API Key"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning("Audius API Key could not be loaded: {Type}", ex.GetType().Name);
                    SetAudiusStatus(() => AppLanguage.T("API Key 读取失败", "Could not load API Key"));
                }
            }

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

            // 字段内容归属对齐：加载 await 窗口被用户切过下拉时（切换订阅被 _isLoading
            // 屏蔽、字段仍是盘上 active 的值），以盘上 active 为存回归属基准重放一次换档，
            // 治愈"下拉指着新服务、字段还是旧服务配置"的错位——归属基准必须是盘上值而非
            // SelectedProvider 现值，否则旧服务配置会被写进新服务档案并随保存持久化
            var tailProvider = NormalizeProvider(SelectedProvider);
            if (tailProvider != _lastProvider)
                SwapLlmProfile(_lastProvider, tailProvider);
            _lastProvider = tailProvider;
            LlmNeedsApiKey = RequiresApiKey(tailProvider);

            // 此时 llm_* 全部字段已读取完，补拉模型候选（加载期赋值被 _isLoading 屏蔽；
            // 云端 Key 从凭据槽回填晚到时由 ApiKey 触发源自动重拉）
            _ = RefreshLocalModelsAsync();

            // 必须是 try 的最后一条语句：加载流程完整跑完才放行保存；中途任何异常都会
            // 跳过本语句，让 SaveAsync 的加载守卫把停留在半加载/全默认态的 VM 挡在写盘
            // 之外。盘上文件缺失/损坏读到 null 也算跑完（损坏时无数据可保）；已知缺口：
            // 文件被杀软/同步工具瞬时锁住读不出时同样算跑完，守卫不会拦下随后的保存
            _settingsStateLoaded = true;
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
            _isLoading = false;
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

    private async Task SaveAudiusApiKeyAsync()
    {
        if (_audius == null) return;
        if (string.IsNullOrWhiteSpace(AudiusApiKeyInput))
        {
            SetAudiusStatus(() => AppLanguage.T("请输入 Audius API Key", "Enter an Audius API Key"));
            return;
        }
        try
        {
            await _audius.SaveApiKeyAsync(AudiusApiKeyInput, _lifetimeCts.Token);
            AudiusApiKeyInput = string.Empty;
            SetAudiusStatus(() => AppLanguage.T("API Key 已安全保存", "API Key saved securely"));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning("Audius API Key save failed: {Type}", ex.GetType().Name);
            SetAudiusStatus(() => AppLanguage.T("保存失败，请检查 Key 并重试", "Could not save the API Key; check it and retry"));
        }
    }

    private void ClearAudiusApiKey()
    {
        if (_audius == null) return;
        try
        {
            _audius.ClearApiKey();
            AudiusApiKeyInput = string.Empty;
            SetAudiusStatus(() => AppLanguage.T("已切换到公开无 Key 模式", "Switched to public mode without an API Key"));
        }
        catch (Exception ex)
        {
            Log.Warning("Audius API Key clear failed: {Type}", ex.GetType().Name);
            SetAudiusStatus(() => AppLanguage.T("清除失败，请重试", "Could not clear the API Key; try again"));
        }
    }

    /// <summary>手输城市文本（节流后）：弃坐标回到自由文本路径并弹联想候选；空文本回 IP 定位。直测入口。</summary>
    internal async Task HandleWeatherCityTypedAsync(string? text)
    {
        var query = text?.Trim() ?? string.Empty;
        WeatherCitySuggestions = Array.Empty<GeoPlace>();
        WeatherCityNoMatch = false;
        WeatherCitySearchFailed = false;

        if (query.Length == 0)
        {
            // 清空回到 IP 自动定位；bump 代次作废在途搜索，防迟到结果在清空后弹候选
            _ = Interlocked.Increment(ref _weatherSearchEpoch);
            _selectedWeatherPlaceName = null;
            WeatherLatitude = null;
            WeatherLongitude = null;
            return;
        }
        // 加载/点选/定位落定的标准名原样保留：确认态不弹联想；同样作废在途搜索
        if (string.Equals(query, _selectedWeatherPlaceName, StringComparison.Ordinal))
        {
            _ = Interlocked.Increment(ref _weatherSearchEpoch);
            return;
        }

        // 改写为不同文本：坐标作废（自由文本经 geocoding 解析），由用户点选候选升级回坐标直取
        WeatherLatitude = null;
        WeatherLongitude = null;

        var epoch = ++_weatherSearchEpoch;
        IReadOnlyList<GeoPlace>? places;
        try
        {
            places = await _weather.SearchPlacesAsync(query, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        if (_disposed != 0 || epoch != Volatile.Read(ref _weatherSearchEpoch))
            return;
        if (places == null)
        {
            WeatherCitySearchFailed = true;
            return;
        }
        if (places.Count == 0)
        {
            WeatherCityNoMatch = true;
            return;
        }
        WeatherCitySuggestions = places;
    }

    /// <summary>点选联想候选：落定标准名 + 坐标并跟随保存（同主题切换即存语义）。</summary>
    private void SelectWeatherCity(GeoPlace place) => ApplyWeatherPlace(place);

    private async Task LocateWeatherCityAsync()
    {
        if (IsWeatherLocating)
            return;
        IsWeatherLocating = true;
        try
        {
            GeoPlace? place = null;
            try
            {
                place = await _weather.LocateByIpAsync(_lifetimeCts.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                return;
            }
            if (place == null)
            {
                SetStatusMessage(() => AppLanguage.T("定位失败，请直接搜索城市名", "Could not locate; search for a city instead"));
                return;
            }
            var located = place;
            ApplyWeatherPlace(located);
            SetStatusMessage(() => AppLanguage.T($"已定位：{located.Name}", $"Located: {located.Name}"));
        }
        catch (Exception ex)
        {
            Log.Warning("Weather IP locate failed: {Type}", ex.GetType().Name);
            SetStatusMessage(() => AppLanguage.T("定位失败，请直接搜索城市名", "Could not locate; search for a city instead"));
        }
        finally
        {
            IsWeatherLocating = false;
        }
    }

    private void ApplyWeatherPlace(GeoPlace place)
    {
        WeatherCity = place.Name;
        _selectedWeatherPlaceName = place.Name;
        WeatherLatitude = place.Latitude;
        WeatherLongitude = place.Longitude;
        WeatherCitySuggestions = Array.Empty<GeoPlace>();
        WeatherCityNoMatch = false;
        WeatherCitySearchFailed = false;
        _ = SaveUiStateCommand.Execute().Subscribe();
    }

    private void SetAudiusStatus(Func<string> factory)
    {
        _audiusStatusFactory = factory;
        AudiusStatus = factory();
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
    /// 刷新「DJ 对你的印象」展示卡。四态：服务未注入 → 整卡隐藏；学习暂停（开关关，GetSnapshot
    /// 此时返回 Empty）→ 暂停提示；快照无可见内容 → 冷启动提示；有任一内容 → 卡片（digest 段空时
    /// 由占位词条兜底，不给「还不太了解你」的虚假归因）。GetSnapshot 为 ≤1000 事件的现算聚合，
    /// 同步开销可忽略。
    /// </summary>
    public void RefreshProfileCard()
    {
        if (_listeningProfile == null || !ListenerProfileEnabled)
        {
            ProfileHasCard = false;
            ProfileEmptyHint = _listeningProfile == null
                ? string.Empty
                : AppLanguage.T("学习已暂停，画像保留在本机", "Learning paused; profile kept on this device");
            ClearProfileCardSections();
            return;
        }

        var snapshot = _listeningProfile.GetSnapshot();
        var hasDigest = !string.IsNullOrWhiteSpace(snapshot.Digest?.Text);
        var hasTopArtists = snapshot.TopArtists.Count > 0;
        var hasBlacklist = snapshot.DislikeBlacklist.Count > 0;
        // 性格印象与口味是独立水位（16 条消息 vs 30 事件）：只聊天不收听的新用户
        // 攒出印象时卡片也必须可见，否则印象段恰好在它的目标场景不可达
        var hasImpression = !string.IsNullOrWhiteSpace(snapshot.Impression?.Text);
        if (!hasDigest && !hasTopArtists && !hasBlacklist && !hasImpression)
        {
            ProfileHasCard = false;
            ProfileEmptyHint = AppLanguage.T(
                "还不太了解你——多听几首、多聊几句，DJ 慢慢就懂了",
                "The DJ doesn't know you well yet — keep listening and chatting");
            ClearProfileCardSections();
            return;
        }

        ProfileHasCard = true;
        ProfileEmptyHint = string.Empty;
        ProfileHasDigest = hasDigest;
        ProfileDigestText = hasDigest ? snapshot.Digest!.Text : string.Empty;
        ProfileDigestMeta = hasDigest
            ? AppLanguage.T(
                $"更新于 {snapshot.Digest!.GeneratedAt:yyyy/M/d}",
                $"Updated {snapshot.Digest.GeneratedAt:yyyy/M/d}")
            : string.Empty;
        var styleTags = snapshot.Digest?.StyleTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList() ?? new List<string>();
        ProfileHasStyleTags = styleTags.Count > 0;
        ProfileStyleTagItems = styleTags;

        // 听歌习惯特质文案跟随界面语言（DJ 注入侧走人设语言，两语言域是既有分工）
        var isUiEn = AppLanguage.Current == "en";
        var personaTraits = snapshot.PersonaTraits
            .Select(t => ListeningPersona.TraitText(t, isUiEn))
            .ToList();
        ProfileHasPersonaTraits = personaTraits.Count > 0;
        ProfilePersonaTraitItems = personaTraits;

        ProfileHasImpression = hasImpression;
        ProfileImpressionText = hasImpression ? snapshot.Impression!.Text : string.Empty;
        ProfileImpressionMeta = hasImpression
            ? AppLanguage.T(
                $"更新于 {snapshot.Impression!.GeneratedAt:yyyy/M/d}",
                $"Updated {snapshot.Impression.GeneratedAt:yyyy/M/d}")
            : string.Empty;
        var impressionTraits = hasImpression
            ? snapshot.Impression!.Traits.Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            : new List<string>();
        ProfileHasImpressionTraits = impressionTraits.Count > 0;
        ProfileImpressionTraitItems = impressionTraits;

        ProfileHasTopArtists = hasTopArtists;
        ProfileTopArtistsText = string.Join("、", snapshot.TopArtists.Take(5).Select(a => a.Artist));
        ProfileTopArtistsDetail = string.Join("、", snapshot.TopArtists.Take(5)
            .Select(a => a.PlayCount > 0 ? $"{a.Artist} ×{a.PlayCount}" : a.Artist));
        ProfileHasAvoidArtists = snapshot.AvoidArtists.Count > 0;
        ProfileAvoidArtistsText = string.Join("、", snapshot.AvoidArtists.Select(a => a.Artist));
        ProfileHasBlacklist = hasBlacklist;
        // 黑名单条目必有非空歌手（构建条件），空标题（合法黑名单键）显示歌手即可
        ProfileBlacklistItems = snapshot.DislikeBlacklist
            .Select(x => new ProfileBlacklistItem(
                x.Title, x.Artist,
                string.IsNullOrWhiteSpace(x.Title) ? x.Artist : $"{x.Title} - {x.Artist}"))
            .ToList();
    }

    /// <summary>复位卡片分区展示态：早退分支不留陈旧标志（隐藏态也与快照一致）。</summary>
    private void ClearProfileCardSections()
    {
        ProfileHasDigest = false;
        ProfileDigestText = string.Empty;
        ProfileDigestMeta = string.Empty;
        ProfileHasStyleTags = false;
        ProfileStyleTagItems = new();
        ProfileHasPersonaTraits = false;
        ProfilePersonaTraitItems = new();
        ProfileHasImpression = false;
        ProfileImpressionText = string.Empty;
        ProfileImpressionMeta = string.Empty;
        ProfileHasImpressionTraits = false;
        ProfileImpressionTraitItems = new();
        ProfileHasTopArtists = false;
        ProfileTopArtistsText = string.Empty;
        ProfileTopArtistsDetail = string.Empty;
        ProfileHasAvoidArtists = false;
        ProfileAvoidArtistsText = string.Empty;
        ProfileHasBlacklist = false;
        ProfileBlacklistItems = new();
    }

    private void RemoveBlacklistItem(ProfileBlacklistItem item)
    {
        if (_listeningProfile == null || item is null)
            return;
        var removed = _listeningProfile.RemoveDislikeEvents(item.Title, item.Artist);
        var sessionRemoved = _recommendationService?.RemoveDislikeFeedback(item.Title, item.Artist) == true;
        RefreshProfileCard();
        // 两处都无匹配（陈旧条目）时仅刷新，不发「已移除」的假确认。
        if (removed || sessionRemoved)
            SetStatusMessage(() => AppLanguage.T("已移除，DJ 不再回避它", "Removed — the DJ won't avoid it anymore"));
    }

    private async Task RegenerateDigestAsync()
    {
        if (_listeningProfile == null)
            return;
        IsRegeneratingDigest = true;
        try
        {
            var before = _listeningProfile.GetSnapshot();
            var beforeDigestText = before.Digest?.Text;
            var beforeDigestAt = before.Digest?.GeneratedAt;
            var beforeImpressionText = before.Impression?.Text;
            var beforeImpressionAt = before.Impression?.GeneratedAt;
            // 双归纳（口味 digest + 性格印象）串行 force：按钮统一管两段
            await _listeningProfile.RefreshDigestAsync(_lifetimeCts.Token, force: true);
            await _listeningProfile.RefreshImpressionAsync(_lifetimeCts.Token, force: true);
            var after = _listeningProfile.GetSnapshot();
            RefreshProfileCard();
            // 两段各自独立判定（Text+GeneratedAt 双字段对比，消除「输出逐字相同」的假阴性）：
            // digest 的 force 不跳 30 事件门槛、印象的 force 要求有聊天素材——冷启动用户
            // 的 digest 静默 no-op 时，印象变化也算成功，防「归纳未完成」对成功路径说谎
            var digestChanged = !string.Equals(beforeDigestText, after.Digest?.Text, StringComparison.Ordinal) ||
                                beforeDigestAt != after.Digest?.GeneratedAt;
            var impressionChanged = !string.Equals(beforeImpressionText, after.Impression?.Text, StringComparison.Ordinal) ||
                                    beforeImpressionAt != after.Impression?.GeneratedAt;
            SetStatusMessage(() =>
                digestChanged || impressionChanged
                    ? AppLanguage.T("已重新归纳", "Regenerated")
                    : AppLanguage.T("归纳未完成——稍后再试或检查 AI 服务", "Summary not updated — try again later or check the AI service"));
        }
        finally
        {
            IsRegeneratingDigest = false;
        }
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
            RefreshProfileCard();
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
                if (Volatile.Read(ref _disposed) != 0)
                    return; // Dispose 后的一次性计时到期：不再触碰已释放 VM 的状态
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

    /// <summary>
    /// 枚举模型候选填充下拉（本地 Ollama /api/tags、云端 /models 按 Key 鉴权）；
    /// 失败静默保持空列表，手填模型名兜底。
    /// </summary>
    private async Task RefreshLocalModelsAsync(bool debounce = false)
    {
        // 代次守卫：快速来回切换 provider 时两次枚举并发，HTTP 完成序不定，
        // 旧结果晚到不得覆盖新结果（同歌词 generation 卫兵惯例）
        var epoch = ++_localModelsEpoch;
        // 三字段同点快照保证单次请求的字段组合一致；防抖窗口内的新触发不发旧组合
        // 请求靠的是上面 epoch 预递增 + 延迟后的代次检查
        var provider = SelectedProvider;
        var baseUrl = BaseUrl;
        var apiKey = ApiKey;
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350), _lifetimeCts.Token);
                if (epoch != _localModelsEpoch)
                    return;
            }
            // 端点从 VM 字段显式传入，不在此 ConfigureLlm——
            // 翻看 provider 下拉不能改变运行中服务的聊天配置（电台可能正在播）
            var models = await _llmService.ListModelsAsync(provider, baseUrl, apiKey, _lifetimeCts.Token);
            if (epoch != _localModelsEpoch || _lifetimeCts.IsCancellationRequested)
                return;
            LocalModels = (models ?? Enumerable.Empty<string>())
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Model enumeration failed in settings");
        }
    }

    private async Task TestConnectionAsync()
    {
        if (RequiresApiKey(SelectedProvider) && string.IsNullOrWhiteSpace(ApiKey))
        {
            SetStatusMessage(() => AppLanguage.T("请先输入 API Key", "Enter your API key first"));
            return;
        }
        // Trim 先行：尾缀全角空格（U+3000）属可修剪空白，不应误判为非法字符；
        // NormalizeLlmInputs 在 try 内才执行，此处的校验必须自带 Trim
        if (!string.IsNullOrWhiteSpace(ApiKey) && !IsPrintableAscii(ApiKey.Trim()))
        {
            SetStatusMessage(() => AppLanguage.T(
                "API Key 含非 ASCII 字符（复制时可能混入中文或全角符号），请重新粘贴",
                "The API key contains non-ASCII characters (likely pasted Chinese or full-width symbols); please re-paste it"));
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

    /// <summary>
    /// 试听当前选中音色：风格 Id 直接进 SynthesizeAsync，与 DJ 开口同链
    /// （内部映射真实 Edge 音色并叠加基线韵律）；播放走真实 TTS 通道，
    /// 音乐压低/说话辉光/调频音效全部联动——听到即所得。
    /// </summary>
    private async Task PreviewVoiceAsync()
    {
        var voice = CharacterVoice;
        if (voice == null || _tts == null || _audio == null)
            return;

        VoicePreviewStatus = string.Empty;
        try
        {
            IsPreviewingVoice = true;
            // 短预算取消透传进 SynthesizeAsync（内部链接令牌），最坏不占 Edge WS 锁长挂
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var audio = await _tts.SynthesizeAsync(GetVoicePreviewText(voice.Id), voice.Id, "neutral", cts.Token);
            if (audio.Length <= 0)
            {
                VoicePreviewStatus = AppLanguage.T(
                    "试听失败：语音服务没有返回音频。",
                    "Preview failed: the voice service returned no audio.");
            }
            else
            {
                // 播放启动失败不外抛（PlayTtsAudio 内部消化、只经 TtsError 通道广播，
                // 常规展示 UI 在设置弹层之下被遮住）：PlayTtsAudio 全同步，失败即已
                // 同步 OnNext——播放窗口内订阅一次捕获，落回试听状态行
                string? playbackError = null;
                var errorSub = _audio.TtsError.Take(1).Subscribe(message => playbackError = message);
                try
                {
                    _audio.PlayTtsAudio(audio);
                }
                finally
                {
                    errorSub.Dispose();
                }
                if (playbackError != null)
                    VoicePreviewStatus = playbackError;
            }
        }
        catch (OperationCanceledException)
        {
            VoicePreviewStatus = AppLanguage.T(
                "试听超时或已取消，请稍后重试。",
                "Preview timed out or was canceled; try again later.");
        }
        catch (Exception ex)
        {
            var failure = ApiFailureLocalization.ForCurrentLanguage(ApiFailureInfo.FromException(ex));
            VoicePreviewStatus = AppLanguage.T($"试听失败：{failure.Title}", $"Preview failed: {failure.Title}");
        }
        finally
        {
            IsPreviewingVoice = false;
        }
    }

    /// <summary>
    /// 每档音色一句专属介绍语：展示各自声线气质、时间中立不预设时段，
    /// 经 AppLanguage.T 双语；未知 Id 回落通用句。
    /// </summary>
    internal static string GetVoicePreviewText(string voiceId) => voiceId switch
    {
        "female-shaonv" => AppLanguage.T(
            "哇，欢迎回来！我就是你的电台 DJ，准备好把心情调到最亮了吗？",
            "Welcome back! I'm your radio DJ — ready to turn the mood all the way up?"),
        "female-yujie" => AppLanguage.T(
            "欢迎回来，我是你的电台 DJ。世界有点吵，我们把音量调到刚刚好。",
            "Welcome back, I'm your radio DJ. The world is a bit loud — let's set the volume just right."),
        "female-chengshu" => AppLanguage.T(
            "好久不见，我是你的电台 DJ。这一首歌，送给认真生活的你。",
            "Good to have you back. I'm your radio DJ, and this one goes out to you."),
        "male-qn-qingse" => AppLanguage.T(
            "嗨，你好呀！我是新来的实习 DJ，第一期节目，请多多关照。",
            "Hi there! I'm the new intern DJ — go easy on me, it's my first show!"),
        "male-qn-jingying" => AppLanguage.T(
            "欢迎收听，我是你的电台 DJ。节奏由我掌控，你只管交给耳朵。",
            "Welcome. I'm your radio DJ — I set the pace, you just lend me your ears."),
        "male-qn-badao" => AppLanguage.T(
            "别换台，这个频道我说了算。我是你的电台 DJ，现在，听我的。",
            "Don't touch that dial — this channel plays by my rules. I'm your radio DJ. Now, listen."),
        _ => AppLanguage.T(
            "你好，我是你的电台 DJ，很高兴在这里遇见你。",
            "Hi, I'm your radio DJ — great to have you here."),
    };

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
                    case QrState.ServiceUnavailable:
                        SetNeteaseAccountStatus(() => AppLanguage.T(
                            "本地音乐服务不可用，请稍后重试或重启应用",
                            "Local music service is unavailable; retry later or restart the app"));
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
                    case QrState.ServiceUnavailable:
                        SetKugouAccountStatus(() => AppLanguage.T(
                            "本地酷狗服务不可用，请稍后重试或重启应用",
                            "Local Kugou service is unavailable; retry later or restart the app"));
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

            // 加载守卫：本会话从未完整加载过设置（从未调用/加载异常中断）而盘上已有
            // 配置时，VM 里是默认值——全量覆盖会把用户配置整体抹掉。自动保存静默
            // 跳过保住盘上文件；显式保存额外给出状态提示。全新安装（盘上无文件）
            // 与加载完成不受影响（盘上损坏读到 null 亦放行——无数据可保）。
            if (!_settingsStateLoaded && File.Exists(_settingsFile))
            {
                Log.Warning("Settings save skipped: settings were never loaded this session; keeping on-disk copy");
                if (persistLlmFields)
                    SetStatusMessage(() => AppLanguage.T(
                        "设置尚未从磁盘加载，已阻止保存以防覆盖现有配置，请重启应用",
                        "Settings were never loaded from disk; save blocked to protect existing config. Please restart the app"));
                return;
            }

            if (persistLlmFields)
                NormalizeLlmInputs();

            // Save current character overrides
            if (SelectedCharacter != null && CharacterVoice != null)
            {
                _overrides[SelectedCharacter.Id] = (CharacterVoice.Id, CharacterPersonality);
            }

            string providerToWrite, baseUrlToWrite, modelToWrite;
            Dictionary<string, object>? profilesToWrite;
            if (persistLlmFields)
            {
                providerToWrite = SelectedProvider;
                baseUrlToWrite = BaseUrl;
                modelToWrite = Model;
                // active 服务以归一化后的字段值覆盖快照（字典本体不回写——切换处理器是
                // 唯一写者，档案与扁平的暂时性不一致由"加载扁平权威+切走存回自愈"兜底）
                profilesToWrite = BuildLlmProfilesSnapshot(
                    NormalizeProvider(providerToWrite), baseUrlToWrite, modelToWrite);

                // Key 预检（须在 NormalizeLlmInputs 之后：全角空格尾缀已被 Trim）：
                // 当前服务的字段值与其余服务的档案缓存统一过 ASCII/长度校验，全部通过
                // 后才统一写槽——坏 Key 一旦入存，运行期 LLM 调用会以 HttpRequestException
                //（"Request headers must contain only ASCII characters"）失败且被归类成
                // 网络问题误导排查；超长 Key 会让 CredWrite 中途抛异常留下半写状态
                var activeProvider = NormalizeProvider(SelectedProvider);
                var slotWrites = new List<(string Slot, string Key)>();
                foreach (var providerId in LlmProfileKeys)
                {
                    var key = (providerId == activeProvider ? ApiKey : GetProfile(providerId).ApiKey) ?? string.Empty;
                    key = key.Trim();
                    if (key.Length == 0)
                        continue;
                    if (!IsPrintableAscii(key))
                    {
                        SetStatusMessage(() => AppLanguage.T(
                            "API Key 含非 ASCII 字符（复制时可能混入中文或全角符号），请重新粘贴",
                            "The API key contains non-ASCII characters (likely pasted Chinese or full-width symbols); please re-paste it"));
                        return;
                    }
                    if (key.Length > MaxLlmKeyLength)
                    {
                        SetStatusMessage(() => AppLanguage.T(
                            $"API Key 过长（上限 {MaxLlmKeyLength} 字符），请检查是否粘贴了错误内容",
                            $"The API key is too long (limit {MaxLlmKeyLength} characters); please check the pasted content"));
                        return;
                    }
                    // local 服务不需要 Key，不建专槽（空档案天然跳过）
                    if (providerId != "local")
                        slotWrites.Add((LlmCredentialSlot(providerId), key));
                }
                // 旧单槽镜像写当前服务的 Key：回滚到旧版仍可用，也是存量测试锚定的契约
                if (!string.IsNullOrWhiteSpace(ApiKey))
                    slotWrites.Add((LlmCredentialService, ApiKey.Trim()));

                foreach (var (slot, key) in slotWrites)
                    await _secureStorage.SaveApiKeyAsync(slot, key);
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
                // 盘上 llm_profiles 原样透传，不从内存档案重建（内存含未保存的切换存回态）
                profilesToWrite = persisted?.Profiles;
                if (profilesToWrite == null)
                    profilesToWrite = BuildLlmProfilesSnapshot(
                        NormalizeProvider(providerToWrite), baseUrlToWrite, modelToWrite);
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
                llm_profiles = profilesToWrite,
                tts_enabled = TtsEnabled,
                is_dark_mode = IsDarkMode,
                enable_starfield = EnableStarfield,
                spectrum_style = NormalizeSpectrumStyle(SelectedSpectrumStyle),
                accent_palette = AccentPalettes.Normalize(SelectedAccentPalette),
                compact_mode_topmost = CompactModeTopmost,
                compact_show_lyrics = CompactShowLyrics,
                radio_sound_fx_enabled = RadioSoundFxEnabled,
                start_in_compact_mode = StartInCompactMode,
                show_lyrics_in_stage = ShowLyricsInStage,
                listener_profile_enabled = ListenerProfileEnabled,
                weather_city = WeatherCity,
                weather_latitude = WeatherLatitude,
                weather_longitude = WeatherLongitude,
                speech_mix_mode = SpeechMixMode,
                language = SelectedLanguage,
                ytdlp_cookie_browser = _accounts.YtdlpCookieBrowser ?? "",
                open_subsonic_quality = NormalizeOpenSubsonicQuality(SelectedOpenSubsonicQuality),
                music_source_policy = NormalizeMusicSourcePolicy(SelectedMusicSourcePolicy),
                music_provider_order = MusicProviders.Select(item => item.Id).ToArray(),
                music_provider_disabled = MusicProviders.Where(item => !item.Enabled)
                    .Select(item => item.Id).ToArray(),
                character_overrides = overridesJson
            };
            // Settings stored as plaintext JSON in %APPDATA%; API key is in Windows Credential Manager.
            // 序列化与写盘移出 UI 线程：主题切换等 UI 事件触发的保存不得挂住界面
            var settingsFile = _settingsFile;
            await Task.Run(() =>
            {
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(settingsData, jsonOptions);
                json = MergeWithUnknownDiskKeys(settingsFile, json, jsonOptions);
                var tempPath = settingsFile + ".tmp";
                File.WriteAllText(tempPath, json);
                // 旧配置轮转为 .bak：settings.json 被外部进程覆盖或写坏时留有恢复途径；
                // .bak 被占用等备份失败只降级为直接覆盖，不能阻塞保存
                if (File.Exists(settingsFile))
                {
                    try
                    {
                        File.Replace(tempPath, settingsFile, settingsFile + ".bak");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warning(ex, "Failed to rotate settings backup, overwriting directly");
                        File.Move(tempPath, settingsFile, overwrite: true);
                    }
                }
                else
                    File.Move(tempPath, settingsFile);
            }, _lifetimeCts.Token);

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

    /// <summary>
    /// 把本次保存负载与盘上配置合并：盘上存在而负载缺失的键原样带回。
    /// 保存是全量覆盖写，构建只写它认识的键——运行过更新的构建后回退时，本构建
    /// 的全量保存会把新版本写入的键整个删除，回新版本后即以默认值固化。合并让
    /// 携带本逻辑的构建在作为旧方降级运行时保住未知键；更旧构建（无合并逻辑）
    /// 已造成的删键不在盘上，无从回填、不在此列。盘上内容不可解析时按整体覆盖
    /// 处理（加载侧 OpenSettingsDocumentAsync 已就损坏告警）。
    /// </summary>
    private static string MergeWithUnknownDiskKeys(string settingsFile, string payloadJson, JsonSerializerOptions options)
    {
        try
        {
            if (!File.Exists(settingsFile))
                return payloadJson;
            if (JsonNode.Parse(File.ReadAllText(settingsFile)) is not JsonObject existing ||
                JsonNode.Parse(payloadJson) is not JsonObject fresh)
                return payloadJson;
            foreach (var key in existing.Select(kv => kv.Key).ToArray())
            {
                if (!fresh.ContainsKey(key))
                    fresh[key] = existing[key]?.DeepClone();
            }
            return fresh.ToJsonString(options);
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }

    /// <summary>盘上 LLM 字段的透传载体；Profiles 为 null 表示盘上无 llm_profiles。</summary>
    private sealed record PersistedLlmState(
        string Provider, string BaseUrl, string Model, Dictionary<string, object>? Profiles);

    private async Task<PersistedLlmState?> ReadPersistedLlmFieldsAsync()
    {
        try
        {
            if (!File.Exists(_settingsFile))
                return null;

            var json = await File.ReadAllTextAsync(_settingsFile, _lifetimeCts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Dictionary<string, object>? profiles = null;
            // 与 ParseLlmProfiles 同套白名单/过滤规则；值必须在此作用域内提取成纯
            // 字符串（JsonElement 不能随 using doc 销毁跨作用域使用）
            if (root.TryGetProperty("llm_profiles", out var profilesEl) &&
                profilesEl.ValueKind == JsonValueKind.Object)
            {
                profiles = new Dictionary<string, object>();
                foreach (var entry in profilesEl.EnumerateObject())
                {
                    if (!LlmProfileKeys.Contains(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    profiles[entry.Name] = new
                    {
                        base_url = entry.Value.TryGetProperty("base_url", out var baseUrl) &&
                                   baseUrl.ValueKind == JsonValueKind.String ? baseUrl.GetString() ?? "" : "",
                        model = entry.Value.TryGetProperty("model", out var model) &&
                                model.ValueKind == JsonValueKind.String ? model.GetString() ?? "" : ""
                    };
                }
            }
            return new PersistedLlmState(
                root.TryGetProperty("llm_provider", out var p) ? NormalizeProvider(p.GetString()) : "openai",
                root.TryGetProperty("llm_base_url", out var b) ? b.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("llm_model", out var m) ? m.GetString() ?? string.Empty : string.Empty,
                profiles);
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
        _openSubsonicQualitySub.Dispose();
        _musicSourcePolicySub.Dispose();
        _accentPaletteSub.Dispose();
        _weatherCitySearchSub.Dispose();
        _selectedLanguageSub.Dispose();
        _listenerProfileToggleSub.Dispose();
        _localModelSettingsSub.Dispose();
        _llmProfileSwapSub.Dispose();
        _llmWatermarkSub.Dispose();
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

    private static string LlmCredentialSlot(string provider) => $"{LlmCredentialService}.{provider}";

    /// <summary>
    /// 构建 llm_profiles 的序列化快照（键名小写下划线：序列化无命名策略，
    /// PascalCase 会与加载白名单键分裂致档案静默清空）。快照是独立新对象，
    /// 不回写字典本体（切换处理器保持唯一写者）。
    /// </summary>
    private Dictionary<string, object> BuildLlmProfilesSnapshot(string activeProvider, string baseUrl, string model)
    {
        var snapshot = new Dictionary<string, object>();
        foreach (var kv in _llmProfiles)
            snapshot[kv.Key] = new { base_url = kv.Value.BaseUrl ?? string.Empty, model = kv.Value.Model ?? string.Empty };
        snapshot[activeProvider] = new { base_url = baseUrl ?? string.Empty, model = model ?? string.Empty };
        return snapshot;
    }

    /// <summary>解析盘上 llm_profiles：键白名单+逐项 String 过滤，坏档案只丢自己。</summary>
    private static Dictionary<string, LlmProfile>? ParseLlmProfiles(JsonElement root)
    {
        if (!root.TryGetProperty("llm_profiles", out var profilesEl) ||
            profilesEl.ValueKind != JsonValueKind.Object)
            return null;
        var profiles = new Dictionary<string, LlmProfile>();
        foreach (var entry in profilesEl.EnumerateObject())
        {
            if (!LlmProfileKeys.Contains(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                continue;
            profiles[entry.Name] = new LlmProfile
            {
                BaseUrl = entry.Value.TryGetProperty("base_url", out var baseUrl) &&
                          baseUrl.ValueKind == JsonValueKind.String ? baseUrl.GetString() ?? "" : "",
                Model = entry.Value.TryGetProperty("model", out var model) &&
                        model.ValueKind == JsonValueKind.String ? model.GetString() ?? "" : ""
            };
        }
        return profiles;
    }

    private LlmProfile GetProfile(string provider) =>
        _llmProfiles.TryGetValue(provider, out var profile) ? profile : new LlmProfile();

    // 档案字典整体换新（COW）：旧字典实例不被修改，保存序列化线程枚举的快照永不撕裂
    private void UpdateProfile(string provider, LlmProfile profile) =>
        _llmProfiles = new Dictionary<string, LlmProfile>(_llmProfiles) { [provider] = profile };

    /// <summary>
    /// 切换 LLM 服务档案：当前字段存回旧服务、载入新服务自己的 Key/地址/模型。
    /// 只改表单字段，不触碰运行中服务配置（ConfigureLlm 仅发生在保存/测试连接）。
    /// </summary>
    private void SwapLlmProfile(string from, string to)
    {
        UpdateProfile(from, new LlmProfile
        {
            BaseUrl = BaseUrl ?? string.Empty,
            Model = Model ?? string.Empty,
            ApiKey = ApiKey ?? string.Empty
        });
        var target = GetProfile(to);
        BaseUrl = target.BaseUrl;
        Model = target.Model;
        if (!string.IsNullOrEmpty(target.ApiKey))
        {
            ApiKey = target.ApiKey;
        }
        else
        {
            // 本会话首次切到该服务且缓存为空：从凭据专槽回填（不回退旧 llm 单槽——
            // 那里镜像的是上次保存时 active 服务的 Key，回退会把 A 服务的 Key 串进 B 服务）
            ApiKey = string.Empty;
            _ = LoadProfileKeyFromSlotAsync(to);
        }
        LlmNeedsApiKey = RequiresApiKey(to);
    }

    /// <summary>
    /// 从凭据专槽回填表单 Key 字段。双守卫：回填时 SelectedProvider 仍是发起服务
    /// 且用户尚未手输才应用；ISecureStorage 无取消令牌，Dispose 后靠 _disposed 早退。
    /// </summary>
    private async Task LoadProfileKeyFromSlotAsync(string provider)
    {
        try
        {
            var key = await _secureStorage.GetApiKeyAsync(LlmCredentialSlot(provider));
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (!string.Equals(NormalizeProvider(SelectedProvider), provider, StringComparison.Ordinal))
                return;
            if (string.IsNullOrEmpty(key) || !string.IsNullOrEmpty(ApiKey))
                return;
            var cached = GetProfile(provider);
            if (string.IsNullOrEmpty(cached.ApiKey))
                UpdateProfile(provider, new LlmProfile
                {
                    BaseUrl = cached.BaseUrl,
                    Model = cached.Model,
                    ApiKey = key
                });
            ApiKey = key;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to load per-provider LLM key from credential slot");
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

    /// <summary>
    /// API Key 会原样进入 HTTP 请求头（Authorization/x-api-key），HttpClient 拒绝
    /// 非 ASCII 头；可打印 ASCII 0x20–0x7E 之外（含中文、全角符号、控制字符）一律拦截。
    /// </summary>
    private static bool IsPrintableAscii(string value) =>
        value.All(ch => ch >= ' ' && ch <= '~');

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
