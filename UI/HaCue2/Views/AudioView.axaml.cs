using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HaCue2.Views;

public partial class AudioView : UserControl
{
    public AudioView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
