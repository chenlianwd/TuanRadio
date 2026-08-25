using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.VisualTree;
using AIRadio.Desktop.ViewModels;
using System;
using System.Globalization;

namespace AIRadio.Desktop.Views;

public partial class SpectrumView : UserControl
{
    private SpectrumViewModel? _viewModel;

    public SpectrumView()
    {
        Resources["SpectrumBarConverter"] = new SpectrumBarHeightConverter();
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

public class SpectrumBarHeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is float f)
            return Math.Clamp(Math.Max(4, f * 118), 4, 118);
        return 2.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
