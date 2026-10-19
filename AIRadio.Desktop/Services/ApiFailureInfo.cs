using System;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services;

public enum ApiFailureKind
{
    None,
    MissingApiKey,
    Authentication,
    Timeout,
    Network,
    RateLimited,
    Server,
    InvalidResponse,
    Unknown
}

public sealed record ApiFailureInfo(
    ApiFailureKind Kind,
    string Title,
    string Detail,
    string RecoveryHint)
{
    public static ApiFailureInfo FromException(Exception ex)
    {
        if (ex is LlmApiException apiEx)
            return apiEx.Failure;

        if (ex is TaskCanceledException or TimeoutException)
        {
            return new ApiFailureInfo(
                ApiFailureKind.Timeout,
                "AI 响应超时",
                "AI 服务在限定时间内没有返回，可能是网络较慢或服务繁忙。",
                "稍后重试，或检查网络代理和防火墙设置。");
        }

        if (ex is HttpRequestException)
        {
            return new ApiFailureInfo(
                ApiFailureKind.Network,
                "无法连接 AI 服务",
                "请求没有成功发送到 AI 服务，通常是网络、DNS、代理或 TLS 连接问题。",
                "检查网络后重试。");
        }

        // 只有 Edge TTS 走 ClientWebSocket，WebSocket 异常必为语音服务失败；
        // 不落 Unknown 兜底（会显示"AI 服务异常"+英文原文，误导排查方向）。
        // 文案经 AppLanguage.T 自带双语，ForCurrentLanguage 对已匹配当前语言的
        // 文案直接放行，不会被 Kind 级英文模板覆盖
        if (ex is WebSocketException)
        {
            return new ApiFailureInfo(
                ApiFailureKind.Network,
                AppLanguage.T("语音服务连接失败", "Voice service connection failed"),
                AppLanguage.T("与在线语音合成服务的连接没有建立成功（握手被拒绝或网络中断）。",
                    "The connection to the online voice service could not be established (handshake rejected or interrupted)."),
                AppLanguage.T("检查系统时间、网络与代理后重试；持续失败可在设置里暂时关闭语音播报。",
                    "Check the system clock, network and proxy, then retry. If it keeps failing, disable voice playback in Settings for now."));
        }

        return new ApiFailureInfo(
            ApiFailureKind.Unknown,
            "AI 服务异常",
            string.IsNullOrWhiteSpace(ex.Message) ? "发生未知错误。" : ex.Message,
            "稍后重试，或查看日志获取详细原因。");
    }

    public static ApiFailureInfo FromStatusCode(HttpStatusCode statusCode, string responseBody)
    {
        var body = string.IsNullOrWhiteSpace(responseBody) ? "无响应内容" : responseBody.Trim();
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ApiFailureInfo(
                ApiFailureKind.Authentication,
                "API Key 无效或未授权",
                "AI 服务拒绝了请求，通常是 API Key 缺失、填错、过期，或账号没有对应接口权限。",
                "打开设置页重新填写并保存 API Key，然后点击测试连接。"),
            (HttpStatusCode)429 => new ApiFailureInfo(
                ApiFailureKind.RateLimited,
                "AI 服务请求过于频繁",
                "AI 服务返回限流，短时间内请求太多或额度不足。",
                "稍等一会儿再试，或检查账号额度。"),
            var code when (int)code >= 500 => new ApiFailureInfo(
                ApiFailureKind.Server,
                "AI 服务暂时不可用",
                $"AI 服务返回 {(int)statusCode}，服务端没有正常处理请求。",
                "稍后重试。"),
            _ => new ApiFailureInfo(
                ApiFailureKind.InvalidResponse,
                $"AI 请求失败 ({(int)statusCode})",
                $"服务返回错误：{body}",
                "检查设置和日志后重试。")
        };
    }

    public static ApiFailureInfo MissingApiKey() => new(
        ApiFailureKind.MissingApiKey,
        "未配置 AI 服务 API Key",
        "当前没有可用于 AI 回复和语音合成的 API Key。",
        "打开设置页填写 API Key，保存后再测试连接。");
}

public sealed class LlmApiException : Exception
{
    public ApiFailureInfo Failure { get; }

    public LlmApiException(ApiFailureInfo failure)
        : base($"{failure.Title}: {failure.Detail}")
    {
        Failure = failure;
    }

    public LlmApiException(ApiFailureInfo failure, Exception innerException)
        : base($"{failure.Title}: {failure.Detail}", innerException)
    {
        Failure = failure;
    }
}
