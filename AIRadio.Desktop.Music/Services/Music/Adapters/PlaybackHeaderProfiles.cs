using System;
using System.Collections.Generic;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 按源静态播放头档案（宿主侧配置，Provider 不自声明——与 NetworkScope 同一治理原则，
/// 见演进计划 §3.2/:212）。档案取自各源 API 请求实测的防盗链头组合；加错方向无害
/// （CDN 忽略多余头），缺头方向可能被防盗链拒绝。
///
/// 投递能力（二进制验证：VLC 3.0.23.1 win-x64，2026-10-12）：libvlccore 内置 HTTP
/// 客户端支持 :http-user-agent / :http-cookies；不支持 :http-referrer（仅旧
/// libhttp_plugin 支持，默认不接管媒体播放）——Referer 档案为尽力投递，核心路径为 no-op。
/// </summary>
public static class PlaybackHeaderProfiles
{
    /// <summary>googlevideo 直链惯例的桌面浏览器 UA；也用于其它源需要浏览器身份的场景。</summary>
    public const string DesktopBrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private static readonly IReadOnlyDictionary<string, PlaybackHeaders> Profiles =
        new Dictionary<string, PlaybackHeaders>
        {
            // 酷我 API 防盗链要求 Referer www.kuwo.cn（KuwoMusicService 搜索/取链同源值）；
            // 直链是否校验未证实，Referer 尽力投递
            ["kuwo"] = new PlaybackHeaders(Referer: new Uri("https://www.kuwo.cn/")),
            // 咪咕 API 侧组合为 UA（桌面/移动 Mozilla）+ Referer m.music.migu.cn；
            // UA 走可靠路径，Referer 尽力投递
            ["migu"] = new PlaybackHeaders(
                UserAgent: DesktopBrowserUserAgent,
                Referer: new Uri("https://m.music.migu.cn/")),
            // googlevideo 直链可能按 UA 节流/拒绝非浏览器客户端；yt-dlp 取流时自带 UA，
            // LibVLC 播放同一 URL 时此前是裸 VLC UA
            ["youtube"] = new PlaybackHeaders(UserAgent: DesktopBrowserUserAgent),
        };

    /// <summary>按 Descriptor.Id 查播放头档案；网易/酷狗（签名 URL 鉴权）无档案返回 false。</summary>
    public static bool TryGet(string providerId, out PlaybackHeaders headers)
    {
        if (Profiles.TryGetValue(providerId, out var profile))
        {
            headers = profile;
            return true;
        }
        headers = null!;
        return false;
    }
}
