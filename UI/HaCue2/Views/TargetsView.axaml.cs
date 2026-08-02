using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HaCue2.Views;

public partial class TargetsView : UserControl
{
    public TargetsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
