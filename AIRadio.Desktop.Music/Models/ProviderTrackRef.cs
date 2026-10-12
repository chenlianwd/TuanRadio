using System;

namespace AIRadio.Desktop.Models;

/// <summary>
/// 稳定音源曲目身份：ProviderId 与 TrackId 显式拆分，替代到处切割 "source:id" 字符串。
/// Phase 0 供歌单持久化使用；Phase 1 的 Provider 契约复用同一模型，避免二次迁移。
/// </summary>
public sealed record ProviderTrackRef(string ProviderId, string TrackId)
{
    /// <summary>规范串接形态，与既有 Track.SourceId 的 "source:id" 约定一致。</summary>
    public string ToSourceId()
        => string.IsNullOrEmpty(ProviderId) ? TrackId : $"{ProviderId}:{TrackId}";

    /// <summary>解析旧 "source:id" 形态；缺少前缀分隔时返回 null，由调用方按原样处理。</summary>
    public static ProviderTrackRef? FromSourceId(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return null;

        var separator = sourceId.IndexOf(':');
        return separator > 0
            ? new ProviderTrackRef(sourceId[..separator], sourceId[(separator + 1)..])
            : null;
    }
}
