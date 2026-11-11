using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using AIRadio.Desktop.Models;
using ReactiveUI;

namespace AIRadio.Desktop.Services;

public sealed class SleepTimerService : ReactiveObject, ISleepTimerService
{
    private readonly IAudioService _audioService;
    private readonly IScheduler _timeScheduler;
    private readonly IScheduler _uiScheduler;
    private readonly object _gate = new();

    private IDisposable? _timerSubscription;
    private readonly IDisposable? _trackEndedSubscription;
    private readonly IDisposable? _trackChangedSubscription;

    private SleepTimerMode _mode = SleepTimerMode.Disabled;
    private TimeSpan _targetDuration = TimeSpan.Zero;
    private TimeSpan _remainingTime = TimeSpan.Zero;
    private bool _closeAppOnExpiry;
    private bool _isFadingOut;
    private string _displayStatus = string.Empty;

    private float _capturedBaseVolume = 1f;
    private float _lastAlgorithmVolume = 1f;
    private bool _userVolumeInterrupted;
    private bool _isDisposed;

    public SleepTimerService(IAudioService audioService, IScheduler? scheduler = null, IScheduler? uiScheduler = null)
    {
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _timeScheduler = scheduler ?? DefaultScheduler.Instance;
        _uiScheduler = uiScheduler ?? (scheduler ?? RxApp.MainThreadScheduler);

        _trackEndedSubscription = _audioService.TrackEnded
            .ObserveOn(_uiScheduler)
            .Subscribe(_ =>
            {
                if (Mode == SleepTimerMode.EndOfTrack)
                {
                    CheckAndConsumeEndOfTrack();
                }
            });

        _trackChangedSubscription = _audioService.TrackChanged
            .ObserveOn(_uiScheduler)
            .Subscribe(track =>
            {
                if (Mode == SleepTimerMode.EndOfTrack && track?.IsLiveStream == true)
                {
                    CancelTimer();
                }
            });

        AppLanguage.Changed += UpdateDisplayStatus;
    }

    public bool IsActive => Mode != SleepTimerMode.Disabled;

    public SleepTimerMode Mode
    {
        get => _mode;
        private set
        {
            if (_mode != value)
            {
                this.RaiseAndSetIfChanged(ref _mode, value);
                this.RaisePropertyChanged(nameof(IsActive));
            }
        }
    }

    public TimeSpan TargetDuration
    {
        get => _targetDuration;
        private set => this.RaiseAndSetIfChanged(ref _targetDuration, value);
    }

    public TimeSpan RemainingTime
    {
        get => _remainingTime;
        private set => this.RaiseAndSetIfChanged(ref _remainingTime, value);
    }

    public bool CloseAppOnExpiry
    {
        get => _closeAppOnExpiry;
        set => this.RaiseAndSetIfChanged(ref _closeAppOnExpiry, value);
    }

    public bool IsFadingOut
    {
        get => _isFadingOut;
        private set => this.RaiseAndSetIfChanged(ref _isFadingOut, value);
    }

    public string DisplayStatus
    {
        get => _displayStatus;
        private set => this.RaiseAndSetIfChanged(ref _displayStatus, value);
    }

    public event Action? RequestAppShutdown;

    public void StartDurationTimer(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            CancelTimer();
            return;
        }

        lock (_gate)
        {
            _timerSubscription?.Dispose();
            _timerSubscription = null;

            if (IsFadingOut && !_userVolumeInterrupted)
            {
                _audioService.Volume = _capturedBaseVolume;
            }

            _userVolumeInterrupted = false;
            IsFadingOut = false;
            TargetDuration = duration;
            RemainingTime = duration;
            Mode = SleepTimerMode.Duration;
            UpdateDisplayStatus();

            var fadeDuration = duration <= TimeSpan.FromSeconds(20)
                ? TimeSpan.FromSeconds(Math.Max(1.0, duration.TotalSeconds / 2.0))
                : TimeSpan.FromSeconds(10.0);

            _timerSubscription = Observable.Interval(TimeSpan.FromSeconds(1), _timeScheduler)
                .ObserveOn(_uiScheduler)
                .Subscribe(_ => OnTick(fadeDuration));
        }
    }

    public void StartEndOfTrackTimer()
    {
        if (_audioService.CurrentTrack?.IsLiveStream == true)
        {
            return;
        }

        lock (_gate)
        {
            _timerSubscription?.Dispose();
            _timerSubscription = null;

            if (IsFadingOut && !_userVolumeInterrupted)
            {
                _audioService.Volume = _capturedBaseVolume;
            }

            _userVolumeInterrupted = false;
            IsFadingOut = false;
            TargetDuration = TimeSpan.Zero;
            RemainingTime = TimeSpan.Zero;
            Mode = SleepTimerMode.EndOfTrack;
            UpdateDisplayStatus();
        }
    }

    public void CancelTimer()
    {
        lock (_gate)
        {
            _timerSubscription?.Dispose();
            _timerSubscription = null;

            if (IsFadingOut && !_userVolumeInterrupted)
            {
                _audioService.Volume = _capturedBaseVolume;
            }

            _userVolumeInterrupted = false;
            IsFadingOut = false;
            Mode = SleepTimerMode.Disabled;
            RemainingTime = TimeSpan.Zero;
            TargetDuration = TimeSpan.Zero;
            UpdateDisplayStatus();
        }
    }

    public bool CheckAndConsumeEndOfTrack()
    {
        bool shouldTrigger;
        bool shouldClose;

        lock (_gate)
        {
            if (Mode != SleepTimerMode.EndOfTrack)
                return false;

            Mode = SleepTimerMode.Disabled;
            shouldClose = CloseAppOnExpiry;
            UpdateDisplayStatus();
            shouldTrigger = true;
        }

        if (shouldTrigger)
        {
            _audioService.Pause();
            if (shouldClose)
            {
                RequestAppShutdown?.Invoke();
            }
            return true;
        }

        return false;
    }

    private void OnTick(TimeSpan fadeDuration)
    {
        bool expired = false;
        bool shouldCloseApp = false;
        bool wasFading = false;
        float baseVolume = 1f;
        bool interrupted = false;

        lock (_gate)
        {
            if (_isDisposed || Mode != SleepTimerMode.Duration)
                return;

            RemainingTime = RemainingTime - TimeSpan.FromSeconds(1);
            if (RemainingTime < TimeSpan.Zero)
            {
                RemainingTime = TimeSpan.Zero;
            }

            UpdateDisplayStatus();

            // 淡出检测与音量衰减
            if (RemainingTime <= fadeDuration)
            {
                if (!IsFadingOut)
                {
                    IsFadingOut = true;
                    _capturedBaseVolume = _audioService.Volume;
                    _lastAlgorithmVolume = _capturedBaseVolume;
                }

                if (!_userVolumeInterrupted)
                {
                    // 检测用户是否手动调节了音量滑条
                    if (Math.Abs(_audioService.Volume - _lastAlgorithmVolume) > 0.05f)
                    {
                        _userVolumeInterrupted = true;
                    }
                    else
                    {
                        var ratio = (float)(RemainingTime.TotalSeconds / fadeDuration.TotalSeconds);
                        ratio = Math.Clamp(ratio, 0f, 1f);
                        var targetVol = _capturedBaseVolume * ratio;
                        _audioService.Volume = targetVol;
                        _lastAlgorithmVolume = targetVol;
                    }
                }
            }

            if (RemainingTime <= TimeSpan.Zero)
            {
                _timerSubscription?.Dispose();
                _timerSubscription = null;

                shouldCloseApp = CloseAppOnExpiry;
                wasFading = IsFadingOut;
                baseVolume = _capturedBaseVolume;
                interrupted = _userVolumeInterrupted;

                IsFadingOut = false;
                Mode = SleepTimerMode.Disabled;
                RemainingTime = TimeSpan.Zero;
                TargetDuration = TimeSpan.Zero;
                UpdateDisplayStatus();

                expired = true;
            }
        }

        if (expired)
        {
            _audioService.Pause();

            if (wasFading && !interrupted)
            {
                _audioService.Volume = baseVolume;
            }

            if (shouldCloseApp)
            {
                RequestAppShutdown?.Invoke();
            }
        }
    }

    private void UpdateDisplayStatus()
    {
        DisplayStatus = Mode switch
        {
            SleepTimerMode.Duration => FormatRemainingTime(RemainingTime),
            SleepTimerMode.EndOfTrack => AppLanguage.T("播完停播", "Stop at end"),
            _ => string.Empty
        };
    }

    private static string FormatRemainingTime(TimeSpan time)
    {
        if (time.TotalSeconds < 0)
            time = TimeSpan.Zero;

        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        }

        return $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            AppLanguage.Changed -= UpdateDisplayStatus;

            _timerSubscription?.Dispose();
            _timerSubscription = null;
            _trackEndedSubscription?.Dispose();
            _trackChangedSubscription?.Dispose();

            if (IsFadingOut && !_userVolumeInterrupted)
            {
                _audioService.Volume = _capturedBaseVolume;
            }

            IsFadingOut = false;
            Mode = SleepTimerMode.Disabled;
        }
    }
}
