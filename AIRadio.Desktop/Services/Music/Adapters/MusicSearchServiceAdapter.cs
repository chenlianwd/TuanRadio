using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 把既有 IMusicSearchService 包装为 IMusicProvider（docs/plans 2026-09-20 §3.2：包装而非重写）。
/// Descriptor.Id 沿用原 FindSource 的类型名 Replace("MusicService","") 约定派生，
/// 兼容持久化 SourceId 前缀与既有测试 mock 命名（A3 修复锚点）；
/// DisplayName 转发内层 Name，熔断键与逐源报告因此与旧实现完全一致。
/// </summary>
public sealed class MusicSearchServiceAdapter : IMusicProvider
{
    private readonly IMusicSearchService _inner;
    private readonly PlaybackHeaders? _playbackHeaders;

    public MusicProviderDescriptor Descriptor { get; }

    public MusicSearchServiceAdapter(
        IMusicSearchService inner,
        ProviderNetworkScope networkScope = ProviderNetworkScope.PublicInternet,
        bool isExperimental = false,
        PlaybackHeaders? playbackHeaders = null)
    {
        _inner = inner;
        _playbackHeaders = playbackHeaders;
        Descriptor = new MusicProviderDescriptor(
            Id: DeriveProviderId(inner.GetType().Name),
            DisplayName: inner.Name,
            NetworkScope: networkScope,
            IsSlowSource: inner.IsSlowSource,
            IsExperimental: isExperimental);
    }

    /// <summary>
    /// 按 PlaybackHeaderProfiles 的源档案构造（档案缺失即 null 头，网易/酷狗签名 URL 鉴权无档案）。
    /// 宿主侧统一走此工厂，避免逐处散落档案查表。
    /// </summary>
    internal static MusicSearchServiceAdapter CreateWithPlaybackHeaderProfile(
        IMusicSearchService inner,
        ProviderNetworkScope networkScope = ProviderNetworkScope.PublicInternet,
        bool isExperimental = false)
    {
        _ = PlaybackHeaderProfiles.TryGet(DeriveProviderId(inner.GetType().Name), out var headers);
        return new MusicSearchServiceAdapter(inner, networkScope, isExperimental, playbackHeaders: headers);
    }

    public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
        => _inner.SearchAsync(keyword, limit, cancellationToken);

    public async Task<MediaResolutionResult> ResolveAsync(
        ProviderTrackRef track,
        IReadOnlyDictionary<string, string>? providerMetadata,
        CancellationToken cancellationToken)
    {
        // carrier.Id 用裸 TrackId（不带前缀），与旧聚合器 CreateProviderTrack 的传参口径一致：
        // 各音源实现按无前缀 id 拼上游请求。必须走 OnlineTrack 重载并携带 ProviderMetadata——
        // 酷狗多 hash/album 参数解析链依赖它，退到字符串重载会整链丢失。
        var carrier = new OnlineTrack
        {
            Id = track.TrackId,
            ProviderMetadata = providerMetadata is { Count: > 0 }
                ? new Dictionary<string, string>(providerMetadata, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        try
        {
            var url = await _inner.GetPlayUrlAsync(carrier, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
                return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.TransportRejected);
            // 播放头经 Sanitized 剔除不安全项后随 ResolvedMedia 透传（null 即不带）
            return MediaResolutionResult.Playable(new ResolvedMedia(track, uri, Headers: _playbackHeaders?.Sanitized()));
        }
        catch (MusicSourceBusinessException ex)
        {
            var failure = ex.Kind switch
            {
                MusicSourceFailureKind.NotSignedIn or MusicSourceFailureKind.AuthExpired => PlaybackFailureKind.AuthRequired,
                MusicSourceFailureKind.RiskControl => PlaybackFailureKind.RiskVerificationRequired,
                MusicSourceFailureKind.PreviewOnly => PlaybackFailureKind.PreviewOnly,
                MusicSourceFailureKind.ApiBroken => PlaybackFailureKind.SourceUnavailable,
                _ => PlaybackFailureKind.SourceUnavailable
            };
            return MediaResolutionResult.Failed(track.ProviderId, failure,
                retryable: failure == PlaybackFailureKind.SourceUnavailable);
        }
    }

    /// <summary>与原 FindSource 路由规则逐字一致的 ProviderId 派生（A3 回归锚点）。</summary>
    internal static string DeriveProviderId(string serviceTypeName)
        => serviceTypeName.Replace("MusicService", "", StringComparison.OrdinalIgnoreCase);
}
