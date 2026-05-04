using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using AIRadio.Desktop.ViewModels;
using System;
using System.Globalization;

namespace AIRadio.Desktop.Views;

public partial class PlayerView : UserControl
{
    public PlayerView()
    {
        Resources["BoolToAccentBg"] = new BoolToAccentBgConverter();
        Resources["BoolToAccentFg"] = new BoolToAccentFgConverter();
        Resources["RepeatToBg"] = new RepeatToBgConverter();
        Resources["RepeatToFg"] = new RepeatToFgConverter();
        Resources["RepeatIcon"] = new RepeatIconConverter();
        InitializeComponent();
    }

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is PlayerViewModel vm)
        {
            vm.SeekTo(slider.Value);
        }
    }
}

public class BoolToAccentBgConverter : IValueConverter
{
    private static readonly IBrush TrueBg = new SolidColorBrush(Color.Parse("#FF1ED760"));
    private static readonly IBrush FalseBg = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueBg : FalseBg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public class BoolToAccentFgConverter : IValueConverter
{
    private static readonly IBrush TrueFg = Brushes.Black;
    private static readonly IBrush FalseFg = new SolidColorBrush(Color.Parse("#FF808080"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueFg : FalseFg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public class RepeatToBgConverter : IValueConverter
{
    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.Parse("#FF1ED760"));
    private static readonly IBrush InactiveBg = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string mode && mode != "none" ? ActiveBg : InactiveBg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public class RepeatToFgConverter : IValueConverter
{
    private static readonly IBrush ActiveFg = Brushes.Black;
    private static readonly IBrush InactiveFg = new SolidColorBrush(Color.Parse("#FF808080"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string mode && mode != "none" ? ActiveFg : InactiveFg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public class RepeatIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            "list" => "🔁",
            "single" => "🔂",
            _ => "⏹"
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
