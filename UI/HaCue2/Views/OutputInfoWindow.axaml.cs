using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HaCue2.Views;

public partial class OutputInfoWindow : Window
{
    public OutputInfoWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
