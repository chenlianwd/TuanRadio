using System;
using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using ReactiveUI;

namespace AIRadio.Desktop.Services;

public sealed class SceneModeService : ReactiveObject, ISceneModeService
{
    private readonly ISleepTimerService _sleepTimer;
    private readonly IScheduler _uiScheduler;
    private readonly CompositeDisposable _disposables = new();
    private readonly Subject<RadioSceneMode> _modeChangedSubject = new();

    private RadioSceneMode? _manualMode;
    private RadioSceneMode _currentMode = RadioSceneMode.Default;
    private bool _isDisposed;

    public SceneModeService(
        ISleepTimerService sleepTimer,
        IScheduler? uiScheduler = null)
    {
        _sleepTimer = sleepTimer ?? throw new ArgumentNullException(nameof(sleepTimer));
        _uiScheduler = uiScheduler ?? RxApp.MainThreadScheduler;

        // 订阅睡眠定时器属性变化（IsActive 驱动伴眠模式）
        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => _sleepTimer.PropertyChanged += h,
                h => _sleepTimer.PropertyChanged -= h)
            .Where(e => string.IsNullOrEmpty(e.EventArgs.PropertyName) || e.EventArgs.PropertyName == nameof(ISleepTimerService.IsActive))
            .ObserveOn(_uiScheduler)
            .Subscribe(_ => EvaluateMode())
            .DisposeWith(_disposables);

        EvaluateMode();
    }

    public RadioSceneMode CurrentMode
    {
        get => _currentMode;
        private set
        {
            if (_currentMode != value)
            {
                this.RaiseAndSetIfChanged(ref _currentMode, value);
                this.RaisePropertyChanged(nameof(IsSleepMode));
                _modeChangedSubject.OnNext(value);
            }
        }
    }

    public bool IsSleepMode => CurrentMode == RadioSceneMode.Sleep;

    public IObservable<RadioSceneMode> ModeChanged => _modeChangedSubject.AsObservable();

    public void SetManualMode(RadioSceneMode? mode)
    {
        if (_isDisposed) return;
        _manualMode = mode;
        EvaluateMode();
    }

    private void EvaluateMode()
    {
        if (_isDisposed) return;

        RadioSceneMode calculated;
        if (_manualMode.HasValue)
        {
            calculated = _manualMode.Value;
        }
        else if (_sleepTimer.IsActive)
        {
            calculated = RadioSceneMode.Sleep;
        }
        else
        {
            calculated = RadioSceneMode.Default;
        }

        CurrentMode = calculated;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _disposables.Dispose();
        _modeChangedSubject.OnCompleted();
        _modeChangedSubject.Dispose();
    }
}
