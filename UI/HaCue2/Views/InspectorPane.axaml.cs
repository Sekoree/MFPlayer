using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class InspectorPane : UserControl
{
    public InspectorPane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Ends the coalescing group when a field loses focus.
    /// </summary>
    /// <remarks>
    /// The journal cannot know when a gesture ended — only the UI can. Without this boundary two
    /// separate edits of the same field merge into one undo step, and an edit made after a save merges
    /// into the command that was on top when the save happened.
    /// </remarks>
    private void OnFieldCommitted(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.EndEdit();
    }

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
