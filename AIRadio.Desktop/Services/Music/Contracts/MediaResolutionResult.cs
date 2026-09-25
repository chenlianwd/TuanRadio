namespace AIRadio.Desktop.Services.Music;

/// <summary>播放解析的可诊断结果。诊断码只能是本地稳定分类，不保存上游响应或带签名地址。</summary>
public enum PlaybackFailureKind
{
    None,
    NotFound,
    PreviewOnly,
    AuthRequired,
    SubscriptionRequired,
    PurchaseRequired,
    RegionRestricted,
    RiskVerificationRequired,
    SourceUnavailable,
    Timeout,
    TransportRejected
}

public sealed record MediaResolutionResult(
    ResolvedMedia? Media,
    PlaybackFailureKind Failure,
    string ProviderId,
    string? DiagnosticCode,
    bool IsRetryable)
{
    public static MediaResolutionResult Playable(ResolvedMedia media)
        => new(media, PlaybackFailureKind.None, media.Track.ProviderId, null, false);

    public static MediaResolutionResult Failed(string providerId, PlaybackFailureKind failure,
        bool retryable = false)
        => new(null, failure, providerId, failure.ToString(), retryable);
}
