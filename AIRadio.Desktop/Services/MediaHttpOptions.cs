using System;
using System.Collections.Generic;
using AIRadio.Desktop.Services.Music;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 播放请求头 → LibVLC 媒体选项的映射（演进计划 §5.1"头映射与注入拒绝测试"的落地侧）。
/// 投递能力以 VLC 3.0.23.1 win-x64 二进制验证为准：libvlccore 内置 HTTP 客户端支持
/// :http-user-agent / :http-cookies（分号分隔 name=value）；:http-referrer 仅旧
/// libhttp_plugin 支持、默认不接管媒体播放，属尽力投递。
/// 消费点兜底：任一值不安全（控制字符/超长）即跳过该头，不影响其余头与播放本身。
/// </summary>
public static class MediaHttpOptions
{
    public static IReadOnlyList<string> Build(PlaybackHeaders? headers)
    {
        if (headers == null)
            return Array.Empty<string>();

        var options = new List<string>(3);
        if (!string.IsNullOrEmpty(headers.UserAgent) &&
            PlaybackHeaders.IsSafeHeaderValue(headers.UserAgent))
        {
            options.Add($":http-user-agent={headers.UserAgent}");
        }

        if (headers.Referer != null &&
            PlaybackHeaders.IsSafeHeaderValue(headers.Referer.OriginalString))
        {
            options.Add($":http-referrer={headers.Referer.OriginalString}");
        }

        if (!string.IsNullOrEmpty(headers.Cookie) &&
            PlaybackHeaders.IsSafeHeaderValue(headers.Cookie))
        {
            // VLC 选项内的 cookie 分隔符即分号，值原样透传不做格式转换
            options.Add($":http-cookies={headers.Cookie}");
        }

        return options;
    }
}
