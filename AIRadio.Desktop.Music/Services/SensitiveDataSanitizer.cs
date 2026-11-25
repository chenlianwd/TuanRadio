using System;
using System.Text.RegularExpressions;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 日志与诊断文本脱敏：掩盖 Cookie、token、userid、dfid 与常见签名参数的值。
/// 用于逐源搜索报告和异常文本进入 UI/日志之前；脱敏只作用于文本，不改变业务行为。
/// </summary>
public static class SensitiveDataSanitizer
{
    // 覆盖 query 参数（& 分隔）与 cookie 分号分隔两种 key=value 形态。
    // 键名包含各音源真实 Cookie 键（MUSIC_U/__csrf/kg_mid 等）与常见 OAuth 变体；
    // 注意 accesstoken 无词边界可循，必须显式列出，\btoken 匹配不到它；
    // key 为网易二维码轮询的会话键——有效期内持 key 可直接换取完整登录 cookie。
    private static readonly Regex SensitivePairs = new(
        @"(?i)\b(token|userid|dfid|cookie|sign|signature|auth|key|t1|vip_token|access_token|accesstoken|refresh_token|authorization|music_u|music_a|__csrf|nmtid|kg_mid|kg_dfid|vip_uid|kugou_api_guid|kugou_api_mid|kugou_api_dev|kugou_api_webgl)\s*=\s*[^&;\s""']+",
        RegexOptions.Compiled);

    // Authorization: Bearer xxx / bearer token 文本形态
    private static readonly Regex BearerTokens = new(
        @"(?i)\b(bearer|basic)\s+[a-z0-9._~+/=-]+",
        RegexOptions.Compiled);

    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var masked = SensitivePairs.Replace(text, "$1=<redacted>");
        return BearerTokens.Replace(masked, "$1 <redacted>");
    }

    /// <summary>
    /// 用于日志输出的 URL 脱敏：仅保留 scheme://host[:port]/path，彻底剥离 userinfo、query string 与 fragment，
    /// 防止私密/付费订阅链接或带签名参数的音频直链把凭据写入日志文件。
    /// </summary>
    public static string SanitizeUrlForLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return SanitizeUrlForLog(uri);

        var q = url.IndexOfAny(['?', '#']);
        var truncated = q >= 0 ? url[..q] : url;
        return Sanitize(truncated) ?? string.Empty;
    }

    public static string SanitizeUrlForLog(Uri? uri)
    {
        if (uri == null)
            return string.Empty;

        if (!uri.IsAbsoluteUri)
            return SanitizeUrlForLog(uri.OriginalString);

        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{portPart}{uri.AbsolutePath}";
    }
}
