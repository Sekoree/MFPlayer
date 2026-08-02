using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HaCue2.Views;

public partial class AuditionPane : UserControl
{
    public AuditionPane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
