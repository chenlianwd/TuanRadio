using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Data;

namespace AIRadio.Desktop.Converters;

// Tab content visibility: visible when TabIndex equals parameter (0=列表, 1=收藏, 2=搜索, 3=节目单)
public class TabVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int tabIndex && parameter is string paramStr && int.TryParse(paramStr, out int param))
            return tabIndex == param;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
