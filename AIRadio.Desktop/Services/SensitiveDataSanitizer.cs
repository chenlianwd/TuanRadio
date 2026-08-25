using System.Text.RegularExpressions;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 日志与诊断文本脱敏：掩盖 Cookie、token、userid、dfid 与常见签名参数的值。
/// 用于逐源搜索报告和异常文本进入 UI/日志之前；脱敏只作用于文本，不改变业务行为。
/// </summary>
internal static class SensitiveDataSanitizer
{
    // 覆盖 query 参数（& 分隔）与 cookie 分号分隔两种 key=value 形态
    private static readonly Regex SensitivePairs = new(
        @"(?i)\b(token|userid|dfid|cookie|sign|signature|auth)\s*=\s*[^&;\s""']+",
        RegexOptions.Compiled);

    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return SensitivePairs.Replace(text, "$1=<redacted>");
    }
}
