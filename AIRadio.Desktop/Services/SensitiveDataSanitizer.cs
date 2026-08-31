using System.Text.RegularExpressions;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 日志与诊断文本脱敏：掩盖 Cookie、token、userid、dfid 与常见签名参数的值。
/// 用于逐源搜索报告和异常文本进入 UI/日志之前；脱敏只作用于文本，不改变业务行为。
/// </summary>
internal static class SensitiveDataSanitizer
{
    // 覆盖 query 参数（& 分隔）与 cookie 分号分隔两种 key=value 形态。
    // 键名包含各音源真实 Cookie 键（MUSIC_U/__csrf/kg_mid 等）与常见 OAuth 变体；
    // 注意 accesstoken 无词边界可循，必须显式列出，\btoken 匹配不到它。
    private static readonly Regex SensitivePairs = new(
        @"(?i)\b(token|userid|dfid|cookie|sign|signature|auth|access_token|accesstoken|refresh_token|authorization|music_u|music_a|__csrf|nmtid|kg_mid|kg_dfid|vip_uid)\s*=\s*[^&;\s""']+",
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
}
