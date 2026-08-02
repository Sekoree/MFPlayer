using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace HaCue2.Views;

public partial class InspectorPane : UserControl
{
    public InspectorPane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The "✎" beside any curve picker opens the shared editor (register item 16) — one control for
    /// fades, crossfades and patch-cue ramps alike, so a curve authored in one place is editable in
    /// every other.
    /// </summary>
    private void OnEditCurve(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<Window>() is { } owner)
            new CurveEditorWindow().ShowDialog(owner);
    }
}
