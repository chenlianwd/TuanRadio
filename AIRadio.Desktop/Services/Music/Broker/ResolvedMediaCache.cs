using System;
using System.Collections.Generic;
using System.Linq;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 播放解析内存缓存（docs/plans 2026-09-20 §3.5）：key = 解析输入身份（ProviderTrackRef），
/// 只服务播放解析路径（点歌/续播重复解析同一曲目时命中），搜索列表不预解析。
/// TTL = ExpiresAt − 60s，无过期时间默认 10 分钟——各源现阶段不返回过期时间
/// （ExpiresAt 恒 null），正确性依赖"播放失败 → 恢复路径 forceRefresh 逐出/覆盖"自愈。
/// 陈旧 URL 防护：forceRefresh 跳过读取且解析前先逐出（失败不回写旧值）。
/// 仅进程内存：应用关闭随进程释放，含敏感头的条目不落盘、不进日志。
/// </summary>
internal sealed class ResolvedMediaCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<ProviderTrackRef, Entry> _entries = new();
    private readonly Func<DateTimeOffset>? _clock;

    private sealed record Entry(ResolveTrackResult Result, DateTimeOffset ExpiresAt);

    public ResolvedMediaCache(Func<DateTimeOffset>? clock = null)
        => _clock = clock;

    public bool TryGet(ProviderTrackRef key, out ResolveTrackResult? result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > Now)
                {
                    result = entry.Result;
                    return true;
                }
                _entries.Remove(key);
            }

            result = null;
            return false;
        }
    }

    public void Set(ProviderTrackRef key, ResolveTrackResult result, DateTimeOffset? expiresAt)
    {
        var now = Now;
        var expiry = expiresAt.HasValue ? expiresAt.Value - TimeSpan.FromSeconds(60) : now + DefaultTtl;
        if (expiry <= now)
            return; // 已过期/临期的结果不进缓存

        lock (_gate)
            _entries[key] = new Entry(result, expiry);
    }

    public void Evict(ProviderTrackRef key)
    {
        lock (_gate)
            _entries.Remove(key);
    }

    /// <summary>凭据变化失效：只清对应 Provider（大小写不敏感匹配 Descriptor.Id 与 SourceId 前缀）。</summary>
    public void ClearProvider(string providerId)
    {
        lock (_gate)
        {
            foreach (var key in _entries
                         .Keys
                         .Where(key => string.Equals(key.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                _entries.Remove(key);
        }
    }

    public void ClearAll()
    {
        lock (_gate)
            _entries.Clear();
    }

    private DateTimeOffset Now => _clock?.Invoke() ?? DateTimeOffset.UtcNow;
}
