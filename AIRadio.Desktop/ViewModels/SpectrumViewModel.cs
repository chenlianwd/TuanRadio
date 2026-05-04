using ReactiveUI;
using AIRadio.Desktop.Services;
using System;
using System.Collections.ObjectModel;
using System.Reactive.Linq;

namespace AIRadio.Desktop.ViewModels;

public class SpectrumViewModel : ViewModelBase, IDisposable
{
    public ObservableCollection<float> Bands { get; } = new();
    private readonly IDisposable _spectrumSub;

    public event Action<float[]>? SpectrumReceived;

    private const int BandCount = 16;

    public SpectrumViewModel(IAudioService audioService)
    {
        for (int i = 0; i < BandCount; i++)
            Bands.Add(0f);

        _spectrumSub = audioService.SpectrumData
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(data =>
            {
                var count = Math.Min(data.Length, BandCount);
                for (int i = 0; i < count; i++)
                {
                    Bands[i] = data[i];
                }
                SpectrumReceived?.Invoke(data);
            });
    }

    public void Dispose()
    {
        _spectrumSub?.Dispose();
    }
}
