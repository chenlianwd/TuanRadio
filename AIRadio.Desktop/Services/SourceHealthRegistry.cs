using System;
using System.Collections.Generic;

namespace AIRadio.Desktop.Services;

/// <summary>只对传输/协议故障熔断；正常空结果和曲目权益限制不能惩罚整个音源。</summary>
internal sealed class SourceHealthRegistry
{
    internal const int FailureThreshold = 3;
    internal static readonly TimeSpan CircuitDuration = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public SourceHealthRegistry(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool CanRequest(string sourceName, out TimeSpan remaining)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sourceName, out var entry) || entry.OpenUntil <= _clock())
            {
                remaining = TimeSpan.Zero;
                return true;
            }

            remaining = entry.OpenUntil - _clock();
            return false;
        }
    }

    public void RecordSuccess(string sourceName)
    {
        lock (_gate)
            _entries.Remove(sourceName);
    }

    public void RecordTransportFailure(string sourceName)
    {
        lock (_gate)
        {
            _entries.TryGetValue(sourceName, out var entry);
            var failures = (entry?.ConsecutiveFailures ?? 0) + 1;
            _entries[sourceName] = new Entry(
                failures,
                failures >= FailureThreshold ? _clock() + CircuitDuration : DateTimeOffset.MinValue);
        }
    }

    public void Reset(string sourceName)
    {
        lock (_gate)
            _entries.Remove(sourceName);
    }

    private sealed record Entry(int ConsecutiveFailures, DateTimeOffset OpenUntil);
}
