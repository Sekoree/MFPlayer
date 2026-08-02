using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HaCue2.Views;

public partial class ScopePane : UserControl
{
    public ScopePane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
