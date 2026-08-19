using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace AIRadio.Desktop.Views;

public partial class SpectrumView : UserControl
{
    public SpectrumView()
    {
        Resources["SpectrumBarConverter"] = new SpectrumBarHeightConverter();
        InitializeComponent();
    }
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
