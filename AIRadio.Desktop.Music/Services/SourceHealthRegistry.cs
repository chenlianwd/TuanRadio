using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRadio.Desktop.Services;

internal enum SourceOperation { Search, Resolution }

public sealed record SourceHealthSnapshot(
    string SourceName,
    DateTimeOffset? LastSearchSuccessUtc,
    DateTimeOffset? LastResolutionSuccessUtc,
    int RecentRequestCount,
    int RecentSuccessCount,
    int ConsecutiveTransportFailures,
    TimeSpan CircuitRemaining);

/// <summary>只对传输/协议故障熔断；正常空结果和曲目权益限制不能惩罚整个音源。</summary>
internal sealed class SourceHealthRegistry
{
    internal const int FailureThreshold = 3;
    internal static readonly TimeSpan CircuitDuration = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Metrics> _metrics = new(StringComparer.OrdinalIgnoreCase);
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

    public void RecordSuccess(string sourceName, SourceOperation? operation = null)
    {
        lock (_gate)
        {
            _entries.Remove(sourceName);
            RecordMetric(sourceName, operation, success: true);
        }
    }

    public void RecordBusinessResponse(string sourceName)
    {
        lock (_gate)
        {
            _entries.Remove(sourceName);
            // 已收到正常业务响应，但不把版权/登录限制记作搜索或播放解析成功。
            RecordMetric(sourceName, operation: null, success: true);
        }
    }

    public void RecordTransportFailure(string sourceName, SourceOperation? operation = null)
    {
        lock (_gate)
        {
            _entries.TryGetValue(sourceName, out var entry);
            var failures = (entry?.ConsecutiveFailures ?? 0) + 1;
            _entries[sourceName] = new Entry(
                failures,
                failures >= FailureThreshold ? _clock() + CircuitDuration : DateTimeOffset.MinValue);
            RecordMetric(sourceName, operation, success: false);
        }
    }

    public SourceHealthSnapshot Snapshot(string sourceName)
    {
        lock (_gate)
        {
            _entries.TryGetValue(sourceName, out var entry);
            _metrics.TryGetValue(sourceName, out var metrics);
            var remaining = entry?.OpenUntil - _clock() ?? TimeSpan.Zero;
            return new SourceHealthSnapshot(sourceName,
                metrics?.LastSearchSuccessUtc, metrics?.LastResolutionSuccessUtc,
                metrics?.Recent.Count ?? 0, metrics?.Recent.Count(value => value) ?? 0,
                entry?.ConsecutiveFailures ?? 0,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    private void RecordMetric(string sourceName, SourceOperation? operation, bool success)
    {
        if (!_metrics.TryGetValue(sourceName, out var metrics))
            _metrics[sourceName] = metrics = new Metrics();
        metrics.Recent.Enqueue(success);
        if (metrics.Recent.Count > 20) metrics.Recent.Dequeue();
        if (!success) return;
        if (operation == SourceOperation.Search) metrics.LastSearchSuccessUtc = _clock();
        if (operation == SourceOperation.Resolution) metrics.LastResolutionSuccessUtc = _clock();
    }

    public void Reset(string sourceName)
    {
        lock (_gate)
            _entries.Remove(sourceName);
    }

    private sealed record Entry(int ConsecutiveFailures, DateTimeOffset OpenUntil);

    private sealed class Metrics
    {
        public Queue<bool> Recent { get; } = new();
        public DateTimeOffset? LastSearchSuccessUtc { get; set; }
        public DateTimeOffset? LastResolutionSuccessUtc { get; set; }
    }
}
