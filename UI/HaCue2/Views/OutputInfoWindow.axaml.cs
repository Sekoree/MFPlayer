using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class OutputInfoWindow : Window
{
    public OutputInfoWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnProgramMeterPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as OutputInfoViewModel)?.ResetMeterClips();
        e.Handled = true;
    }
}
