using System.Linq;

namespace AIRadio.Desktop.Services;

/// <summary>在 UI 边界按当前语言重建常见 API 错误，避免底层较早生成的中文文案泄漏到英文界面。</summary>
public static class ApiFailureLocalization
{
    public static ApiFailureInfo ForCurrentLanguage(ApiFailureInfo failure)
    {
        var containsChinese = ContainsChinese(failure.Title) ||
                              ContainsChinese(failure.Detail) ||
                              ContainsChinese(failure.RecoveryHint);
        if ((AppLanguage.Current == "zh" && containsChinese) ||
            (AppLanguage.Current == "en" && !containsChinese))
        {
            return failure;
        }

        return failure.Kind switch
    {
        ApiFailureKind.MissingApiKey => new(
            failure.Kind,
            AppLanguage.T("未配置 AI 服务 API Key", "AI service API key is not configured"),
            AppLanguage.T("当前没有可用于 AI 回复和语音合成的 API Key。", "No API key is available for AI replies and speech generation."),
            AppLanguage.T("打开设置页填写 API Key，保存后再测试连接。", "Enter the API key in Settings, save it, and test the connection.")),
        ApiFailureKind.Authentication => new(
            failure.Kind,
            AppLanguage.T("API Key 无效或未授权", "Invalid or unauthorized API key"),
            AppLanguage.T("AI 服务拒绝了请求，请检查 API Key 与账号接口权限。", "The AI service rejected the request. Check the API key and account permissions."),
            AppLanguage.T("重新填写并保存 API Key，然后测试连接。", "Enter and save the API key again, then test the connection.")),
        ApiFailureKind.Timeout => new(
            failure.Kind,
            AppLanguage.T("AI 响应超时", "AI response timed out"),
            AppLanguage.T("AI 服务未在限定时间内返回。", "The AI service did not respond in time."),
            AppLanguage.T("稍后重试，或检查网络代理和防火墙设置。", "Try again later, or check your proxy and firewall settings.")),
        ApiFailureKind.Network => new(
            failure.Kind,
            AppLanguage.T("无法连接 AI 服务", "Can't connect to the AI service"),
            AppLanguage.T("请求未能到达 AI 服务，请检查网络、DNS、代理或 TLS 设置。", "The request could not reach the AI service. Check the network, DNS, proxy or TLS settings."),
            AppLanguage.T("检查网络后重试。", "Check your connection and try again.")),
        ApiFailureKind.RateLimited => new(
            failure.Kind,
            AppLanguage.T("AI 服务请求过于频繁", "AI request rate limited"),
            AppLanguage.T("请求过多或账号额度不足。", "There were too many requests, or the account quota is insufficient."),
            AppLanguage.T("稍等一会儿再试，或检查账号额度。", "Wait a moment and try again, or check the account quota.")),
        ApiFailureKind.Server => new(
            failure.Kind,
            AppLanguage.T("AI 服务暂时不可用", "AI service temporarily unavailable"),
            AppLanguage.T("服务端没有正常处理请求。", "The server did not process the request successfully."),
            AppLanguage.T("稍后重试。", "Try again later.")),
        ApiFailureKind.InvalidResponse => new(
            failure.Kind,
            AppLanguage.T("AI 服务返回异常", "Invalid AI service response"),
            AppLanguage.T("服务返回了无法处理的内容。", "The service returned content that could not be processed."),
            AppLanguage.T("检查设置和日志后重试。", "Check Settings and the logs, then try again.")),
        ApiFailureKind.Unknown => new(
            failure.Kind,
            AppLanguage.T("AI 服务异常", "AI service error"),
            AppLanguage.Current == "en" && ContainsChinese(failure.Detail)
                ? "An unexpected error occurred."
                : failure.Detail,
            AppLanguage.T("稍后重试，或查看日志获取详细原因。", "Try again later, or check the logs for details.")),
        _ => failure
        };
    }

    private static bool ContainsChinese(string value)
        => value.Any(character => character is >= '\u4e00' and <= '\u9fff');
}
