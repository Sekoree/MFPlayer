using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.Core.Model;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class InspectorPane : UserControl
{
    public InspectorPane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>A level change on a patch cue: one logical output moved to a level.</summary>
    private void OnAddLevelChange(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InspectorViewModel inspector)
            return;

        PromptWindow.Show(
            this,
            Dialogs.AddLevelChange(inspector.Journal, inspector.Cue as PatchCueNode),
            inspector.Reload);
    }

    /// <summary>
    /// Opens the timeline sheet on the selected group.
    /// </summary>
    /// <remarks>
    /// The sheet had no way in at all: it existed and drew the first timeline group in the show, but
    /// nothing opened it and nothing pointed it at a group the operator had chosen.
    /// </remarks>
    /// <summary>
    /// Auditions the selected cue - the same verb the cue context menu and Ctrl+P use.
    /// </summary>
    /// <remarks>
    /// Reached through the CUE view rather than the inspector's own journal, because auditioning is a
    /// transport action and the transport lives there. An inspector that grew its own preview path
    /// would be a second answer to "what does previewing mean".
    /// </remarks>
    private void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<CuesView>()?.DataContext is CuesViewModel cues)
            cues.PreviewSelected();
    }

    /// <summary>Adds a lane of the kind the menu item names.</summary>
    private void OnAddLane(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector
            && (sender as Control)?.Tag is string propertyId)
            inspector.AddLane(propertyId);
    }

    private void OnRemoveLane(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector
            && (sender as Control)?.Tag is int index)
            inspector.RemoveLane(index);
    }

    private void OnConfigureLane(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector
            && (sender as Control)?.Tag is int index)
            PromptWindow.Show(this, inspector.ConfigureLane(index), inspector.Reload);
    }

    /// <summary>Opens the shared curve editor over one lane's points.</summary>
    private void OnEditLane(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InspectorViewModel inspector
            || (sender as Control)?.Tag is not int index
            || inspector.LaneEditor(index) is not { } editor)
            return;

        if (this.FindAncestorOfType<Window>() is not { } owner)
            return;

        var window = new AutomationEditorWindow(editor);
        window.Closed += (_, _) => inspector.Reload();
        window.ShowDialog(owner);
    }

    /// <summary>
    /// Opens the clip editor on the selected media cue.
    /// </summary>
    /// <remarks>
    /// Not a dialog: it is a window somebody sits with while they find a cut, and the show behind it
    /// stays visible and usable. It writes through the same journal, so an undo made afterwards in the
    /// cue list undoes the trim.
    /// </remarks>
    private void OnEditClip(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InspectorViewModel inspector
            || inspector.ClipEditor() is not { } editor
            || this.FindAncestorOfType<Window>() is not { } owner)
            return;

        var window = new ClipEditorWindow(editor);
        window.Closed += (_, _) => inspector.Reload();
        window.Show(owner);
    }

    private void OnOpenTimeline(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<CuesView>()?.DataContext is CuesViewModel cues)
            cues.OpenTimeline();
    }

    private void OnChooseSubtitles(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<Window>() is not { } owner
            || DataContext is not InspectorViewModel inspector
            || inspector.SubtitlePicker is not { } picker)
            return;

        var window = new SubtitlePickerWindow(picker);
        window.Closed += (_, _) => inspector.Reload();
        window.ShowDialog(owner);
    }

    private void OnPlace(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.PlaceOnComposition();
    }

    /// <summary>Opening a placement's expander selects the placement its nested editor projects.</summary>
    private void OnPlacementExpanding(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector
            && sender is Expander { Tag: int index })
            inspector.ExpandPlacement(index);
    }

    private void OnSendGesture(object? sender, Controls.MatrixGesture gesture)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.Audio.ApplySendGesture(gesture);
    }

    private void OnSendGestureEnded(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.Audio.EndSendGesture();
    }

    /// <summary>One of the four send presets the PRESETS strip names.</summary>
    private void OnSendPreset(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector && (sender as Control)?.Tag is string preset)
            inspector.Audio.ApplySendPreset(preset);
    }

    /// <summary>
    /// Ends the coalescing group when a field loses focus.
    /// </summary>
    /// <remarks>
    /// The journal cannot know when a gesture ended - only the UI can. Without this boundary two
    /// separate edits of the same field merge into one undo step, and an edit made after a save merges
    /// into the command that was on top when the save happened.
    /// </remarks>
    private void OnFieldCommitted(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.EndEdit();
    }

    /// <summary>
    /// The "✎" beside any curve picker opens the shared editor (register item 16) - one control for
    /// fades, crossfades and patch-cue ramps alike, so a curve authored in one place is editable in
    /// every other.
    /// </summary>
    private void OnEditCurve(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<Window>() is not { } owner
            || DataContext is not InspectorViewModel inspector
            || (sender as Control)?.Tag as string is not { } which)
            return;

        // No editor for a cue that has no curve of that name: opening one on a stand-in would let an
        // operator draw a shape that goes nowhere.
        if (inspector.CurveEditor(which) is not { } editor)
            return;

        var window = new CurveEditorWindow(editor);
        window.Closed += (_, _) => inspector.Reload();
        window.ShowDialog(owner);
    }
}
