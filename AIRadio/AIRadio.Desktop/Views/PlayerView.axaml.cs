using Avalonia.Controls;
using Avalonia.Input;
using AIRadio.Desktop.ViewModels;

namespace AIRadio.Desktop.Views;

public partial class PlayerView : UserControl
{
    public PlayerView()
    {
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
