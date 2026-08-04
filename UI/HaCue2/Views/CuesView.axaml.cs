using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using HaCue2.ViewModels;
using Avalonia.Markup.Xaml;

namespace HaCue2.Views;

/// <summary>
/// Screens 02, 03 and 05.
/// </summary>
/// <remarks>
/// There is no selection handler here any more. The cue tree's <c>HierarchicalTreeDataGridSource</c>
/// owns its selection model and the view-model subscribes to it directly — one place decides what is
/// selected, rather than a control event and a bound property that can disagree.
/// </remarks>
public partial class CuesView : UserControl
{
    public CuesView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Picks media files and makes a cue of each.
    /// </summary>
    /// <remarks>
    /// This is how a show actually gets built, so it is a button of its own rather than one entry in
    /// a menu of cue kinds. Several files at once, in the order they were chosen, as ONE undo step.
    /// </remarks>
    private async void OnAddMedia(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues || TopLevel.GetTopLevel(this) is not { } top)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add media cues",
            AllowMultiple = true,
        });

        cues.AddMedia([.. files.Select(file => file.TryGetLocalPath()).OfType<string>()]);
    }

    private void OnAddCue(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CuesViewModel cues
            && (sender as Control)?.Tag as string is { } tag
            && Enum.TryParse<CueKind>(tag, out var kind))
            cues.AddCue(kind);
    }

    private void OnDuplicate(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.DuplicateSelected();

    private void OnRemove(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.RemoveSelected();

    // The transport. GO always works (register item 3) — with no session it moves the cursor, which
    // is the half that can be right without one.
    private void OnGo(object? sender, RoutedEventArgs e) => (DataContext as CuesViewModel)?.Go();

    private void OnPause(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.TogglePause();

    /// <summary>Bare STOP: the selected active cue, not the show.</summary>
    private void OnStop(object? sender, RoutedEventArgs e) => (DataContext as CuesViewModel)?.Stop();

    /// <summary>The × on one Active row: stops THAT cue, whatever the tree has selected.</summary>
    private void OnStopActive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CuesViewModel cues && (sender as Control)?.Tag is Guid cueId)
            cues.StopCue(cueId);
    }

    /// <summary>A drag on an Active row's bar moves that cue's playhead.</summary>
    private void OnSeekActive(object? sender, Controls.SeekEventArgs e)
    {
        if (DataContext is CuesViewModel cues && (sender as Control)?.Tag is Guid cueId)
            _ = cues.SeekActiveAsync(cueId, e.Fraction);
    }

    private void OnStopAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues)
            return;

        if (!cues.StopAllNeedsConfirmation)
        {
            cues.StopAll();
            return;
        }

        PromptWindow.Show(
            this,
            new PromptViewModel(
                "Stop all sounding cues?",
                "This stops every active cue in the show.",
                [],
                _ => cues.StopAll(),
                confirm: "STOP ALL"));
    }

    private void OnStandbyUp(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.StepStandby(-1);

    private void OnStandbyDown(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.StepStandby(1);

    private void OnFireSelected(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.FireSelected();

    /// <summary>Auditions the selected cue. Monitoring only — it never reaches the program mix.</summary>
    private void OnPreview(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.PreviewSelected();

    /// <summary>
    /// PANIC is HELD, not clicked.
    /// </summary>
    /// <remarks>
    /// It is the one control an operator reaches for without reading, so a mis-click must not take the
    /// show down — but it also must not be behind a confirmation dialog, because the moment somebody
    /// needs it is the moment they have no attention left for a second decision. Holding is the
    /// compromise: one gesture, unmistakably deliberate, and the button counts down while it happens.
    /// </remarks>
    private void OnPanicPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CuesViewModel cues)
            cues.BeginPanic();
    }

    private void OnPanicReleased(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.CancelPanic();

    private void OnOpenTimeline(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.OpenTimeline();

    private void OnStandbyHere(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.StandbyHere();

    private void OnToggleEnabled(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.ToggleEnabled();

    /// <summary>Renumbers the list the tree is scoped to, as one undo step.</summary>
    private void OnRenumber(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues)
            return;

        PromptWindow.Show(this, Dialogs.Renumber(cues.Journal, cues.ScopedList), cues.Refresh);
    }
}
