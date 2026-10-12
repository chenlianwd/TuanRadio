using System;
using System.Collections.Generic;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>有限传输头白名单：只存在于内存，不参与歌单序列化、普通日志或诊断报告。</summary>
public sealed record PlaybackHeaders(
    string? UserAgent = null,
    Uri? Referer = null,
    string? Cookie = null)
{
    /// <summary>单头值长度上限：正常 UA/Referer/Cookie 远小于此，防病态输入。</summary>
    public const int MaxHeaderValueLength = 2048;

    /// <summary>
    /// 头值安全校验：拒绝 CR/LF 与全部控制字符（&lt;0x20、DEL），防 HTTP 头拆分与
    /// LibVLC 选项注入（libvlc 每条 AddOption 即一个选项、值域到串尾，控制字符是唯一
    /// 可行的注入向量）。null/空串视为安全（表示不携带该头）。
    /// </summary>
    public static bool IsSafeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        if (value.Length > MaxHeaderValueLength)
            return false;
        foreach (var ch in value)
        {
            // \u 定长转义：\x 变长转义在字符常量会被后续十六进制字符吞并
            if (ch < ' ' || ch == '\u007F')
                return false;
        }
        return true;
    }

    /// <summary>
    /// 剔除不安全头值后的副本（Cookie 额外约束为可打印 ASCII，分号是 VLC 选项内的
    /// cookie 分隔语义）；全部项被剔除时返回 null（等价于不带播放头）。
    /// </summary>
    public PlaybackHeaders? Sanitized()
    {
        var userAgent = IsSafeHeaderValue(UserAgent) ? UserAgent : null;
        var referer = Referer != null && IsSafeHeaderValue(Referer.OriginalString) ? Referer : null;
        var cookie = IsSafeHeaderValue(Cookie) && IsPrintableAscii(Cookie) ? Cookie : null;
        if (userAgent == null && referer == null && cookie == null)
            return null;
        return new PlaybackHeaders(userAgent, referer, cookie);
    }

    private static bool IsPrintableAscii(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        foreach (var ch in value)
        {
            if (ch > '~')
                return false;
        }
        return true;
    }
}

/// <summary>
/// 解析出的可播放媒体（docs/plans 2026-09-20 §3.1）：Uri 交给播放器前必须经 MediaUriPolicy 校验。
/// 交给播放器时用 <see cref="RawUrl"/>（OriginalString 逐字保留解析所得字符串），
/// 不得用 Uri.ToString()——后者会改变转义形态。
/// </summary>
public sealed record ResolvedMedia(
    ProviderTrackRef Track,
    Uri Uri,
    DateTimeOffset? ExpiresAt = null,
    PlaybackHeaders? Headers = null,
    bool IsPreview = false,
    string? Codec = null,
    string? Container = null,
    int? BitrateKbps = null)
{
    /// <summary>解析得到的原始 URL 字符串，与音源返回逐字一致。</summary>
    public string RawUrl => Uri.OriginalString;
}

/// <summary>
/// Broker 级曲目解析结果（docs/plans §3.1 两级解析入口的 Broker 层）：
/// 跨源回退成功时携带实际生效的身份与元数据。现状跨源回退由聚合器直接回写
/// 共享 track 实例（调用方依赖该行为），Broker 保持回写的同时把结果显式返回。
/// Codec/BitrateKbps/IsPreview 为音源声明的媒体信息（仅首选源命中时透传，回退路径无声明值），
/// 仅作展示兜底；实际播放编码/码率以播放器解析为准。
/// </summary>
public sealed record ResolveTrackResult(
    string Url,
    ProviderTrackRef Track,
    string SourceName,
    IReadOnlyDictionary<string, string> ProviderMetadata,
    bool FellBack = false,
    string? Codec = null,
    int? BitrateKbps = null,
    bool IsPreview = false,
    PlaybackHeaders? Headers = null);

/// <summary>Broker 聚合解析的最终状态；预检只消费分类，不保存播放 URL。</summary>
public sealed record PlaybackResolutionOutcome(
    ResolveTrackResult? Track,
    PlaybackFailureKind Failure);
