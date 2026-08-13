using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HaCue2.Controls;

/// <summary>
/// What a list says when there is nothing in it.
/// </summary>
/// <remarks>
/// A blank panel is indistinguishable from one that failed to load, and the difference matters most
/// on a machine somebody is setting up for the first time - which is exactly when every list in the
/// app is empty at once. The detail line always names the way OUT, because "nothing here" without a
/// next step is just a dead end with better typography.
/// </remarks>
public partial class EmptyState : UserControl
{
    public static readonly StyledProperty<string> HeadlineProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Headline), "Nothing here");

    public static readonly StyledProperty<string> DetailProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Detail), "");

    public EmptyState() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public string Headline
    {
        get => GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }

    /// <summary>The way out. Never left empty - a dead end with better typography is still a dead end.</summary>
    public string Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }
}
