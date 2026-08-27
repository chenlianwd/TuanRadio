using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AIRadio.Desktop.ViewModels;

namespace AIRadio.Desktop.Views;

public partial class SpectrumView : UserControl
{
    private SpectrumViewModel? _viewModel;

    public SpectrumView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel(DataContext as SpectrumViewModel);
        AttachedToVisualTree += (_, _) => AttachViewModel(DataContext as SpectrumViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
    }

    private void AttachViewModel(SpectrumViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;
        if (_viewModel != null)
            _viewModel.SpectrumReceived -= OnSpectrumReceived;
        _viewModel = viewModel;
        if (_viewModel != null)
            _viewModel.SpectrumReceived += OnSpectrumReceived;
    }

    private void OnSpectrumReceived(float[] data) => Renderer.SetBands(data);
}
