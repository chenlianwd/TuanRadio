using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 界面语言状态：严格跟随设置的 zh/en 选项。
/// XAML 静态文案经 <see cref="Attach"/> 挂载的资源字典（DynamicResource）切换：
/// 切换时原地清空重填同一 ResourceDictionary 实例，触发所有 DynamicResource 刷新；
/// VM 动态文案经 <see cref="T"/> 即时读取当前语言；持有常驻文案的 VM 订阅 Changed 重算。
/// 两份字符串表的键集奇偶由 StringResourceParityTests 校验。
/// </summary>
public static class AppLanguage
{
    private static volatile string _current = "zh";
    private static ResourceDictionary? _stringsHost;

    /// <summary>语言变化通知；UI 线程触发（无 Avalonia 应用时在调用线程触发）。</summary>
    public static event Action? Changed;

    public static string Current => _current;

    /// <summary>App 启动时挂载字符串宿主字典；必须在主窗口创建之前调用。</summary>
    public static void Attach(Application app)
    {
        _stringsHost = new ResourceDictionary();
        app.Resources.MergedDictionaries.Add(_stringsHost);
        FillStrings(_current);
    }

    public static void Apply(string? language)
    {
        var next = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        if (next == _current)
            return;

        // 先落地当前语言：后台线程调用时 T() 立即生效
        _current = next;

        var app = Application.Current;
        if (app == null)
        {
            // 单测环境（未 Attach）：仅通知 VM 层
            Changed?.Invoke();
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            FillStrings(next);
            Changed?.Invoke();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                FillStrings(next);
                Changed?.Invoke();
            });
        }
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
            "多平台聚合" or "multi-source" or "multi-source music" => T("多平台聚合", "Multi-source"),
            _ => source
        };
    }

    private static void FillStrings(string language)
    {
        if (_stringsHost == null)
            return;

        var source = language == "en" ? EnStrings : ZhStrings;
        _stringsHost.Clear();
        foreach (var pair in source)
            _stringsHost[pair.Key] = pair.Value;
    }

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
        // 简洁播放器
        ["S_Favorite"] = "收藏",
        ["S_TopMost"] = "置顶",
        ["S_Minimize"] = "最小化",
        ["S_Close"] = "关闭",
        ["S_Expand"] = "展开",
        ["S_Prev"] = "上一首",
        ["S_PlayPause"] = "播放/暂停",
        ["S_Next"] = "下一首",
        ["S_Volume"] = "音量",
        // 标题栏
        ["S_CompactMode"] = "简洁模式",
        // 播放控制区
        ["S_FavCurrent"] = "收藏当前歌曲",
        ["S_Like"] = "喜欢",
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
        ["S_Play"] = "播放",
        ["S_Add"] = "加入",
        // 主窗口与聊天区
        ["S_Library"] = "曲库",
        ["S_HostPicker"] = "数字人",
        ["S_Theme"] = "切换主题",
        ["S_CloseSettings"] = "关闭设置",
        ["S_HoldToTalk"] = "按住说话",
        ["S_Send"] = "发送",
        // 设置页
        ["S_Settings"] = "设置",
        ["S_AiService"] = "AI 服务",
        ["S_ApiKeyWatermark"] = "输入你的 API Key",
        ["S_BaseUrlWatermark"] = "Base URL；留空使用该格式默认地址",
        ["S_ModelWatermark"] = "输入模型名称，例如 gpt-4o-mini / deepseek-chat",
        ["S_AutoSaveHint"] = "连接测试成功后会自动保存服务、模型、地址和 API Key",
        ["S_Accounts"] = "音源账号",
        ["S_NeteaseTitle"] = "网易云音乐（扫码登录）",
        ["S_QrLogin"] = "扫码登录",
        ["S_Logout"] = "退出登录",
        ["S_NeteaseHint"] = "登录后免费账号仍只能试听 VIP 歌曲的片段，黑胶会员可完整播放",
        ["S_KugouTitle"] = "酷狗音乐（扫码登录）",
        ["S_KugouHint"] = "酷狗音源必须登录后才能播放；在官方 App 每日看广告领到的会员在这里同样有效",
        ["S_YtdlpTitle"] = "YouTube 音源（yt-dlp 登录态）",
        ["S_YtdlpHint"] = "YouTube 会拦截未登录请求（not a bot 风控）；选择已登录过 YouTube 的浏览器可复用其 Cookies",
        ["S_LanguageTitle"] = "界面与 AI 回复语言",
        ["S_LanguageHint"] = "选择界面显示与 AI 主播回复使用的语言，可用于英语听力练习",
        ["S_TtsGroup"] = "语音播报",
        ["S_TtsLabel"] = "AI 回复自动语音播报",
        ["S_Visuals"] = "视觉效果",
        ["S_Starfield"] = "星光背景随音乐呼吸",
        ["S_SpectrumStyle"] = "频谱样式",
        ["S_TopMostToggle"] = "简洁模式窗口置顶",
        ["S_CharSettings"] = "数字人设置",
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
        // Compact player
        ["S_Favorite"] = "Favorite",
        ["S_TopMost"] = "Always on top",
        ["S_Minimize"] = "Minimize",
        ["S_Close"] = "Close",
        ["S_Expand"] = "Expand",
        ["S_Prev"] = "Previous",
        ["S_PlayPause"] = "Play/Pause",
        ["S_Next"] = "Next",
        ["S_Volume"] = "Volume",
        // Title bar
        ["S_CompactMode"] = "Compact mode",
        // Player deck
        ["S_FavCurrent"] = "Favorite this track",
        ["S_Like"] = "Like",
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
        ["S_ImportPlaylist"] = "Import playlist",
        ["S_PlayProgramTrack"] = "Play this candidate",
        ["S_Import"] = "Import files",
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
        ["S_ApiKeyWatermark"] = "Enter your API key",
        ["S_BaseUrlWatermark"] = "Base URL; leave empty for the provider default",
        ["S_ModelWatermark"] = "Model name, e.g. gpt-4o-mini / deepseek-chat",
        ["S_AutoSaveHint"] = "Automatically saves provider, model, URL and API key after a successful connection test",
        ["S_Accounts"] = "Music accounts",
        ["S_NeteaseTitle"] = "NetEase Cloud Music (QR login)",
        ["S_QrLogin"] = "Scan QR code",
        ["S_Logout"] = "Log out",
        ["S_NeteaseHint"] = "Logged-in free accounts can still only preview VIP tracks; Vinyl members get full playback",
        ["S_KugouTitle"] = "Kugou Music (QR login)",
        ["S_KugouHint"] = "Kugou requires login for playback; membership earned from daily ads in the official app also works here",
        ["S_YtdlpTitle"] = "YouTube source (yt-dlp cookies)",
        ["S_YtdlpHint"] = "YouTube blocks anonymous requests (bot check); pick a browser signed in to YouTube to reuse its cookies",
        ["S_LanguageTitle"] = "Interface & AI language",
        ["S_LanguageHint"] = "Sets the language of the interface and DJ replies — handy for listening practice",
        ["S_TtsGroup"] = "Voice",
        ["S_TtsLabel"] = "Auto speak AI replies",
        ["S_Visuals"] = "Visuals",
        ["S_Starfield"] = "Starfield breathes with the music",
        ["S_SpectrumStyle"] = "Spectrum style",
        ["S_TopMostToggle"] = "Keep compact player on top",
        ["S_CharSettings"] = "Host settings",
        ["S_Voice"] = "Voice",
        ["S_Personality"] = "Personality prompt",
        ["S_Save"] = "Save settings",
    };
}
