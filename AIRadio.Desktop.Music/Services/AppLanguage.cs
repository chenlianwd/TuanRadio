using System;
using System.Collections.Generic;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 界面语言状态：严格跟随设置的 zh/en 选项。
/// VM 动态文案经 <see cref="T"/> 即时读取当前语言；持有常驻文案的 VM 订阅 Changed 重算；
/// XAML 静态文案由宿主（AIRadio.Desktop 的 AppLanguageUi）订阅 Changed 重填资源字典触发
/// DynamicResource 刷新——本类不依赖任何 UI 框架，可安全驻留类库。
/// 两份字符串表的键集奇偶由 AppLanguageTests.StringTables_HaveIdenticalKeySetsAndNonEmptyValues 校验。
/// </summary>
public static class AppLanguage
{
    private static volatile string _current = "zh";
    // UI 线程编组钩子：宿主 Attach 时注入（Dispatcher.Post）；null（单测/无 UI 宿主）时在调用线程直接广播
    private static Action<Action>? _uiMarshal;

    /// <summary>语言变化通知；宿主注入编组后经 UI 线程触发，否则在调用线程触发。</summary>
    public static event Action? Changed;

    public static string Current => _current;

    /// <summary>宿主启动时注入 UI 线程编组器；必须在主窗口创建之前调用。传 null 恢复无宿主语义。</summary>
    public static void SetUiMarshal(Action<Action>? marshal) => _uiMarshal = marshal;

    public static void Apply(string? language)
    {
        var next = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        if (next == _current)
            return;

        // 先落地当前语言：后台线程调用时 T() 立即生效
        _current = next;
        void Notify() => Changed?.Invoke();
        if (_uiMarshal != null)
            _uiMarshal(Notify);
        else
            Notify();
    }

    /// <summary>VM 动态文案：按当前语言返回对应文本；默认中文，单测零改动。</summary>
    public static string T(string zh, string en)
        => _current == "en" ? en : zh;

    /// <summary>把音源内部名称转换为当前界面的显示名称，兼容历史中英文及简称。</summary>
    public static string MusicSourceName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        return source.Trim().ToLowerInvariant() switch
        {
            "netease" or "网易" or "网易云音乐" or "netease cloud music" => T("网易云音乐", "NetEase Cloud Music"),
            "kugou" or "酷狗" or "酷狗音乐" or "kugou music" => T("酷狗音乐", "Kugou Music"),
            "kuwo" or "酷我" or "酷我音乐" or "kuwo music" => T("酷我音乐", "Kuwo Music"),
            "migu" or "咪咕" or "咪咕音乐" or "migu music" => T("咪咕音乐", "Migu Music"),
            "youtube" or "youtube music" => "YouTube",
            "local" or "local library" or "本地曲库" => T("本地曲库", "Local library"),
            "opensubsonic" => "OpenSubsonic",
            "audius" => "Audius",
            "多平台聚合" or "multi-source" or "multi-source music" => T("多平台聚合", "Multi-source"),
            _ => source
        };
    }

    /// <summary>当前语言的 S_* 词条表只读视图（宿主资源字典填充用）。</summary>
    public static IReadOnlyDictionary<string, string> GetStringTable(string? language)
        => string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? EnStrings : ZhStrings;

    // ---- 字符串表（键集必须与 EnStrings 完全一致）----

    internal static readonly Dictionary<string, string> ZhStrings = new()
    {
        // 通用
        ["S_On"] = "开",
        ["S_Off"] = "关",
        // 聊天区
        ["S_TipCollapse"] = "收起提示",
        ["S_TipVoiceHint"] = "查看语音播放提示",
        ["S_ChatWatermark"] = "和主播说点什么…",
        ["S_ReleaseToTranscribe"] = "松开结束说话…",
        ["S_Transcribing"] = "正在识别语音…",
        ["S_Responding"] = "正在回复…",
        // 简洁播放器
        ["S_Favorite"] = "收藏（同时记入长期口味）",
        ["S_TopMost"] = "置顶",
        ["S_Minimize"] = "最小化",
        ["S_Close"] = "关闭",
        ["S_Expand"] = "展开",
        ["S_Prev"] = "上一首",
        ["S_PlayPause"] = "播放/暂停",
        ["S_Next"] = "下一首",
        ["S_Volume"] = "音量",
        ["S_CompactLyricsTip"] = "点击在歌词与歌曲信息之间切换",
        // 标题栏
        ["S_CompactMode"] = "简洁模式",
        // 播放控制区（心形收藏已并入"喜欢"信号，点击同时记入口味画像）
        ["S_FavCurrent"] = "收藏（同时记入长期口味）",
        ["S_Dislike"] = "不喜欢",
        ["S_Similar"] = "再来类似",
        ["S_Calmer"] = "换安静一点",
        ["S_Energetic"] = "换燃一点",
        ["S_SongStory"] = "讲讲这首歌",
        // 曲库抽屉
        ["S_SearchWatermark"] = "搜索歌曲…",
        ["S_Remove"] = "从播放列表移除",
        ["S_CloseLibrary"] = "关闭曲库",
        ["S_PlaylistTab"] = "播放列表",
        ["S_FavoritesTab"] = "收藏",
        ["S_SearchTab"] = "搜索",
        ["S_ProgramTab"] = "节目单",
        ["S_KugouPlaylistsTab"] = "酷狗歌单",
        ["S_PlayAll"] = "播放全部",
        ["S_ShufflePlay"] = "随机播放",
        ["S_PlayFromHere"] = "从此处播放",
        ["S_PlayNext"] = "下一首播放",
        ["S_AddToQueue"] = "加入队列末尾",
        ["S_SearchKugouPlaylist"] = "在当前酷狗歌单中搜索",
        ["S_LocalSyncedPlaylists"] = "已同步的本地歌单",
        ["S_ShowAllTracks"] = "全部歌曲",
        ["S_ProgramDescription"] = "DJ 根据当前收听风格临时编排的下一组候选歌曲",
        ["S_RefreshProgram"] = "重新编排",
        ["S_Refresh"] = "刷新",
        ["S_ImportPlaylist"] = "导入歌单",
        ["S_PlayProgramTrack"] = "播放候选歌曲",
        ["S_Import"] = "导入本地文件",
        ["S_ImportFolder"] = "导入本地音乐文件夹",
        ["S_RescanLocalLibrary"] = "重扫本地曲库",
        ["S_OpenSubsonicTitle"] = "私有曲库（OpenSubsonic / Navidrome）",
        ["S_OpenSubsonicUrl"] = "服务器地址，例如 https://music.example.com",
        ["S_OpenSubsonicUser"] = "用户名",
        ["S_OpenSubsonicPassword"] = "密码；留空沿用已保存的密码",
        ["S_OpenSubsonicConnect"] = "测试连接并保存",
        ["S_OpenSubsonicDisconnect"] = "断开",
        ["S_AudiusTitle"] = "Audius API Key（可选）",
        ["S_AudiusApiKey"] = "粘贴 API Key，请勿填写 Bearer Token",
        ["S_AudiusSave"] = "保存 API Key",
        ["S_AudiusClear"] = "清除 API Key",
        ["S_AudiusHint"] = "Key 用于搜索和曲目详情；播放地址不会包含 Key。保存后可运行音源连接诊断。",
        ["S_OpenSubsonicQuality"] = "私有曲库音质偏好",
        ["S_OpenSubsonicQualityHint"] = "原始音质请求原文件；省流量请求服务端最高 128 kbps。实际编码取决于服务器。",
        ["S_MusicSourcePolicy"] = "全局音源策略",
        ["S_MusicSourcePolicyHint"] = "影响跨源回退的尝试顺序与搜索结果的源偏好，不改变首选源（首选仍按上方列表排序）。",
        ["S_MusicProviderPriority"] = "音源启用与优先级",
        ["S_MusicProviderPriorityHint"] = "列表从上到下优先；停用的音源不会参与搜索或解析。",
        ["S_MusicProviderUp"] = "提高音源优先级",
        ["S_MusicProviderDown"] = "降低音源优先级",
        ["S_Play"] = "播放",
        ["S_Add"] = "加入",
        // 主窗口与聊天区
        ["S_Library"] = "曲库",
        ["S_HostPicker"] = "主播",
        ["S_Theme"] = "切换主题",
        ["S_CloseSettings"] = "关闭设置",
        ["S_HoldToTalk"] = "按住说话",
        ["S_Send"] = "发送",
        // 设置页
        ["S_Settings"] = "设置",
        ["S_AiService"] = "AI 服务",
        // 设置页侧边导航
        ["S_SettingsNavMusic"] = "音乐音源",
        ["S_SettingsNavHost"] = "主播与语音",
        ["S_SettingsNavAppearance"] = "外观与通用",
        ["S_ApiKeyWatermark"] = "输入你的 API Key",
        ["S_AutoSaveHint"] = "连接测试成功后会自动保存服务、模型、地址和 API Key",
        ["S_Accounts"] = "音源账号",
        ["S_MusicBehaviorSub"] = "源偏好与诊断",
        ["S_NeteaseTitle"] = "网易云音乐（扫码登录）",
        ["S_QrLogin"] = "扫码登录",
        ["S_Logout"] = "退出登录",
        ["S_NeteaseHint"] = "登录后免费账号仍只能试听 VIP 歌曲的片段，黑胶会员可完整播放",
        ["S_KugouTitle"] = "酷狗音乐（扫码登录）",
        ["S_KugouHint"] = "酷狗音源必须登录后才能播放；在官方 App 每日看广告领到的会员在这里同样有效",
        ["S_KugouVerify"] = "滑块验证",
        ["S_YtdlpTitle"] = "YouTube 音源（yt-dlp 登录态）",
        ["S_YtdlpHint"] = "YouTube 会拦截未登录请求（not a bot 风控）；选择已登录过 YouTube 的浏览器可复用其 Cookies",
        ["S_LanguageTitle"] = "界面与 AI 回复语言",
        ["S_LanguageHint"] = "选择界面显示与 AI 主播回复使用的语言，可用于英语听力练习",
        ["S_WeatherCityWatermark"] = "天气城市（如：上海），留空自动定位",
        ["S_WeatherCityHint"] = "天气仅显示在时钟舞台角落的图标，悬停查看详情；留空时按网络 IP 定位",
        ["S_TtsGroup"] = "语音播报",
        ["S_TtsLabel"] = "AI 回复自动语音播报",
        ["S_RadioSoundFx"] = "复古收音机调频音效",
        ["S_Starfield"] = "星光背景随音乐呼吸",
        ["S_SpectrumStyle"] = "频谱样式",
        ["S_AccentPalette"] = "配色主题",
        ["S_TopMostToggle"] = "简洁模式窗口置顶",
        ["S_CompactShowLyricsToggle"] = "简洁模式显示歌词",
        ["S_ProfileGroup"] = "收听画像",
        ["S_ProfileLearn"] = "学习我的口味（本地保存，用于推荐）",
        ["S_ProfileHint"] = "画像只记录歌曲名、歌手和收听行为，也会从聊天中取少量片段归纳你的性格，都保存在本机且不上传；片段归纳后会自动清除。关闭只暂停学习，清除后推荐从零重新学习",
        ["S_ProfileCardTitle"] = "DJ 对你的印象",
        ["S_ProfileDigestLabel"] = "口味归纳",
        ["S_ProfileDigestPending"] = "归纳还没生成——配好 AI 服务再听一阵子就有了",
        ["S_ProfilePersonaLabel"] = "听歌习惯",
        ["S_ProfileImpressionLabel"] = "性格印象",
        ["S_ProfileImpressionPending"] = "和 DJ 多聊几句，这里会慢慢形成对你的印象",
        ["S_ProfileTopArtistsLabel"] = "常听歌手",
        ["S_ProfileAvoidArtistsLabel"] = "回避歌手",
        ["S_ProfileBlacklistLabel"] = "不爱听的歌",
        ["S_ProfileBlacklistEmpty"] = "暂无",
        ["S_ProfileBlacklistExpiryHint"] = "标记后 180 天自动失效",
        ["S_ProfileRemoveItem"] = "移除",
        ["S_ProfileRegenerateDigest"] = "重新归纳",
        ["S_CharSettings"] = "主播设置",
        ["S_Voice"] = "音色",
        ["S_Personality"] = "人格提示词",
        ["S_Save"] = "保存设置",
    };

    internal static readonly Dictionary<string, string> EnStrings = new()
    {
        // Common
        ["S_On"] = "On",
        ["S_Off"] = "Off",
        // Chat area
        ["S_TipCollapse"] = "Hide tips",
        ["S_TipVoiceHint"] = "Show voice playback tips",
        ["S_ChatWatermark"] = "Say something to the DJ…",
        ["S_ReleaseToTranscribe"] = "Release to transcribe…",
        ["S_Transcribing"] = "Transcribing speech…",
        ["S_Responding"] = "Responding…",
        // Compact player
        ["S_Favorite"] = "Favorite (feeds your taste profile)",
        ["S_TopMost"] = "Always on top",
        ["S_Minimize"] = "Minimize",
        ["S_Close"] = "Close",
        ["S_Expand"] = "Expand",
        ["S_Prev"] = "Previous",
        ["S_PlayPause"] = "Play/Pause",
        ["S_Next"] = "Next",
        ["S_Volume"] = "Volume",
        ["S_CompactLyricsTip"] = "Click to toggle between lyrics and track info",
        // Title bar
        ["S_CompactMode"] = "Compact mode",
        // Player deck (heart favorite doubles as the "like" signal, feeding the taste profile)
        ["S_FavCurrent"] = "Favorite (feeds your taste profile)",
        ["S_Dislike"] = "Dislike",
        ["S_Similar"] = "More like this",
        ["S_Calmer"] = "Something calmer",
        ["S_Energetic"] = "More energy",
        ["S_SongStory"] = "About this song",
        // Library drawer
        ["S_SearchWatermark"] = "Search songs…",
        ["S_Remove"] = "Remove from playlist",
        ["S_CloseLibrary"] = "Close library",
        ["S_PlaylistTab"] = "Playlist",
        ["S_FavoritesTab"] = "Favorites",
        ["S_SearchTab"] = "Search",
        ["S_ProgramTab"] = "Program",
        ["S_KugouPlaylistsTab"] = "Kugou playlists",
        ["S_PlayAll"] = "Play all",
        ["S_ShufflePlay"] = "Shuffle play",
        ["S_PlayFromHere"] = "Play from here",
        ["S_PlayNext"] = "Play next",
        ["S_AddToQueue"] = "Add to queue",
        ["S_SearchKugouPlaylist"] = "Search this Kugou playlist",
        ["S_LocalSyncedPlaylists"] = "Synced local playlists",
        ["S_ShowAllTracks"] = "All tracks",
        ["S_ProgramDescription"] = "The next set of tracks curated by the DJ from your current listening style",
        ["S_RefreshProgram"] = "Refresh program",
        ["S_Refresh"] = "Refresh",
        ["S_ImportPlaylist"] = "Import list",
        ["S_PlayProgramTrack"] = "Play this candidate",
        ["S_Import"] = "Import files",
        ["S_ImportFolder"] = "Import music folder",
        ["S_RescanLocalLibrary"] = "Rescan local library",
        ["S_OpenSubsonicTitle"] = "Private library (OpenSubsonic / Navidrome)",
        ["S_OpenSubsonicUrl"] = "Server URL, e.g. https://music.example.com",
        ["S_OpenSubsonicUser"] = "Username",
        ["S_OpenSubsonicPassword"] = "Password; leave blank to keep the saved password",
        ["S_OpenSubsonicConnect"] = "Test connection and save",
        ["S_OpenSubsonicDisconnect"] = "Disconnect",
        ["S_AudiusTitle"] = "Audius API Key (optional)",
        ["S_AudiusApiKey"] = "Paste the API Key, not the Bearer Token",
        ["S_AudiusSave"] = "Save API Key",
        ["S_AudiusClear"] = "Clear API Key",
        ["S_AudiusHint"] = "The key is used for search and track details; playback URLs do not contain it. Run source diagnostics after saving.",
        ["S_OpenSubsonicQuality"] = "Private library quality",
        ["S_OpenSubsonicQualityHint"] = "Original requests the source file; data saver asks the server for up to 128 kbps. Actual encoding depends on the server.",
        ["S_MusicSourcePolicy"] = "Source strategy",
        ["S_MusicSourcePolicyHint"] = "Affects cross-source fallback order and source preference in search results; it does not change the preferred source (still the list order above).",
        ["S_MusicProviderPriority"] = "Music sources and priority",
        ["S_MusicProviderPriorityHint"] = "Sources higher in the list take priority; disabled sources are skipped for search and resolution.",
        ["S_MusicProviderUp"] = "Move source up",
        ["S_MusicProviderDown"] = "Move source down",
        ["S_Play"] = "Play",
        ["S_Add"] = "Add",
        // Main window & chat area
        ["S_Library"] = "Library",
        ["S_HostPicker"] = "Host",
        ["S_Theme"] = "Switch theme",
        ["S_CloseSettings"] = "Close settings",
        ["S_HoldToTalk"] = "Hold to talk",
        ["S_Send"] = "Send",
        // Settings
        ["S_Settings"] = "Settings",
        ["S_AiService"] = "AI service",
        // Settings side navigation
        ["S_SettingsNavMusic"] = "Music sources",
        ["S_SettingsNavHost"] = "Host & voice",
        ["S_SettingsNavAppearance"] = "Appearance & general",
        ["S_ApiKeyWatermark"] = "Enter your API key",
        ["S_AutoSaveHint"] = "Automatically saves provider, model, URL and API key after a successful connection test",
        ["S_Accounts"] = "Music accounts",
        ["S_MusicBehaviorSub"] = "Source preferences & diagnostics",
        ["S_NeteaseTitle"] = "NetEase Cloud Music (QR login)",
        ["S_QrLogin"] = "Scan QR code",
        ["S_Logout"] = "Log out",
        ["S_NeteaseHint"] = "Logged-in free accounts can still only preview VIP tracks; Vinyl members get full playback",
        ["S_KugouTitle"] = "Kugou Music (QR login)",
        ["S_KugouHint"] = "Kugou requires login for playback; membership earned from daily ads in the official app also works here",
        ["S_KugouVerify"] = "Captcha verify",
        ["S_YtdlpTitle"] = "YouTube source (yt-dlp cookies)",
        ["S_YtdlpHint"] = "YouTube blocks anonymous requests (bot check); pick a browser signed in to YouTube to reuse its cookies",
        ["S_LanguageTitle"] = "Interface & AI language",
        ["S_LanguageHint"] = "Sets the language of the interface and DJ replies — handy for listening practice",
        ["S_WeatherCityWatermark"] = "Weather city (e.g. Shanghai); leave empty to auto-locate",
        ["S_WeatherCityHint"] = "Weather shows as a corner icon on the clock stage; hover for details. Empty city locates by network IP",
        ["S_TtsGroup"] = "Voice",
        ["S_TtsLabel"] = "Auto speak AI replies",
        ["S_RadioSoundFx"] = "Retro radio tuning sound FX",
        ["S_Starfield"] = "Starfield breathes with the music",
        ["S_SpectrumStyle"] = "Spectrum style",
        ["S_AccentPalette"] = "Color theme",
        ["S_TopMostToggle"] = "Keep compact player on top",
        ["S_CompactShowLyricsToggle"] = "Show lyrics in compact mode",
        ["S_ProfileGroup"] = "Listening profile",
        ["S_ProfileLearn"] = "Learn my taste (stored locally, used for recommendations)",
        ["S_ProfileHint"] = "The profile records song titles, artists and listening behavior, and uses a few chat snippets to summarize your personality; everything stays on this device and is never uploaded. Snippets are cleared after summarizing. Turn off to pause learning; clear it to start over",
        ["S_ProfileCardTitle"] = "How the DJ sees you",
        ["S_ProfileDigestLabel"] = "Taste summary",
        ["S_ProfileDigestPending"] = "Not generated yet — set up an AI service and keep listening",
        ["S_ProfilePersonaLabel"] = "Listening style",
        ["S_ProfileImpressionLabel"] = "Personality",
        ["S_ProfileImpressionPending"] = "Chat with the DJ a few times and an impression will form here",
        ["S_ProfileTopArtistsLabel"] = "Most played",
        ["S_ProfileAvoidArtistsLabel"] = "Avoiding",
        ["S_ProfileBlacklistLabel"] = "Disliked songs",
        ["S_ProfileBlacklistEmpty"] = "None",
        ["S_ProfileBlacklistExpiryHint"] = "Auto-expires 180 days after marking",
        ["S_ProfileRemoveItem"] = "Remove",
        ["S_ProfileRegenerateDigest"] = "Regenerate",
        ["S_CharSettings"] = "Host settings",
        ["S_Voice"] = "Voice",
        ["S_Personality"] = "Personality prompt",
        ["S_Save"] = "Save settings",
    };
}
