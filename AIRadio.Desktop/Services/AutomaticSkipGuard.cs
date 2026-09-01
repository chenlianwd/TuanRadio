using System;
using System.Collections.Generic;

namespace AIRadio.Desktop.Services;

public sealed record PlaybackRecoveryNotice(
    bool IsBlocked,
    int ConsecutiveFailures,
    string Message,
    IReadOnlyList<string> RecentFailures);

internal sealed class AutomaticSkipGuard
{
    internal const int FailureLimit = 3;
    private readonly object _gate = new();
    private readonly Queue<string> _recentFailures = new();
    private int _consecutiveFailures;

    public PlaybackRecoveryNotice Record(string trackTitle, string reason)
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            _recentFailures.Enqueue($"{trackTitle}: {reason}");
            while (_recentFailures.Count > FailureLimit)
                _recentFailures.Dequeue();

            var blocked = _consecutiveFailures >= FailureLimit;
            return new PlaybackRecoveryNotice(
                blocked,
                _consecutiveFailures,
                blocked
                    ? AppLanguage.T(
                        "连续 3 首歌曲无法播放，已暂停自动切歌。请检查音源账号或重新选择歌曲。",
                        "Three consecutive tracks failed. Automatic skipping has paused; check the source account or choose another track.")
                    : AppLanguage.T(
                        $"当前歌曲无法播放，正在尝试下一首（{_consecutiveFailures}/{FailureLimit}）",
                        $"This track could not play; trying the next ({_consecutiveFailures}/{FailureLimit})"),
                _recentFailures.ToArray());
        }
    }

    public bool Reset()
    {
        lock (_gate)
        {
            if (_consecutiveFailures == 0)
                return false;
            _consecutiveFailures = 0;
            _recentFailures.Clear();
            return true;
        }
    }
}
