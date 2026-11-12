using System;
using System.ComponentModel;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface ISceneModeService : INotifyPropertyChanged, IDisposable
{
    /// <summary>当前生效的电台场景模式。</summary>
    RadioSceneMode CurrentMode { get; }

    /// <summary>是否处于伴眠电台模式（快捷只读属性）。</summary>
    bool IsSleepMode { get; }

    /// <summary>场景模式变化的可观察流（严格在 UI 线程发射）。</summary>
    IObservable<RadioSceneMode> ModeChanged { get; }

    /// <summary>设置或清除手动场景覆盖（null 表示清除手动覆盖，跟随睡眠定时器等自动感知）。</summary>
    void SetManualMode(RadioSceneMode? mode);
}
