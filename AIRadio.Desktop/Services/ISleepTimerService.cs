using System;
using System.ComponentModel;

namespace AIRadio.Desktop.Services;

public enum SleepTimerMode
{
    Disabled,
    Duration,
    EndOfTrack
}

public interface ISleepTimerService : INotifyPropertyChanged, IDisposable
{
    bool IsActive { get; }
    SleepTimerMode Mode { get; }
    TimeSpan TargetDuration { get; }
    TimeSpan RemainingTime { get; }
    bool CloseAppOnExpiry { get; set; }
    bool IsFadingOut { get; }
    string DisplayStatus { get; }

    event Action? RequestAppShutdown;

    void StartDurationTimer(TimeSpan duration);
    void StartEndOfTrackTimer();
    void CancelTimer();
    bool CheckAndConsumeEndOfTrack();
}
