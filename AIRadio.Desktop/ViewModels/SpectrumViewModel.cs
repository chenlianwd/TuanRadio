using ReactiveUI;
using ReactiveUI.SourceGenerators;
using AIRadio.Desktop.Services;
using System;
using System.Reactive.Linq;

namespace AIRadio.Desktop.ViewModels;

public partial class SpectrumViewModel : ViewModelBase, IDisposable
{
    [Reactive] public partial string SelectedStyle { get; set; } = "bars";
    private readonly IDisposable _spectrumSub;

    public event Action<float[]>? SpectrumReceived;

    public SpectrumViewModel(IAudioService audioService)
    {
        // 频谱渲染全部走 SpectrumReceived 事件（SpectrumView/CompactPlayer/ClockStage
        // 直连 Renderer），不经 ObservableCollection——30Hz×32 的集合通知在 UI 线程
        // 空转无消费者
        _spectrumSub = audioService.SpectrumData
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(data => SpectrumReceived?.Invoke(data));
    }

    public void Dispose()
    {
        _spectrumSub?.Dispose();
    }
}
