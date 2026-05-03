using Avalonia.Controls;
using Avalonia.Input;
using ReactiveUI;

namespace AIRadio.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.IsSettingsOpen = false;
        }
    }
}
