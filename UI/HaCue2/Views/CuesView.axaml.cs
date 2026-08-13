using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HaCue2.Core.Media;
using HaCue2.Engine;
using HaCue2.Machine;
using HaCue2.ViewModels;
using Avalonia.Markup.Xaml;

namespace HaCue2.Views;

/// <summary>
/// Screens 02, 03 and 05.
/// </summary>
/// <remarks>
/// There is no selection handler here any more. The cue tree's <c>HierarchicalTreeDataGridSource</c>
/// owns its selection model and the view-model subscribes to it directly - one place decides what is
/// selected, rather than a control event and a bound property that can disagree.
/// </remarks>
public partial class CuesView : UserControl
{
    private bool _collapsedForWidth;

    /// <summary>What a row drag picked up, captured when it started - the drop args do not carry it.</summary>
    private IReadOnlyList<Guid> _draggedCues = [];

    public CuesView()
    {
        InitializeComponent();

        // Reordering by dragging a row. The grid raises both halves; the drop is answered rather than
        // left to its built-in move. See OnCueRowDrop.
        if (this.FindControl<TreeDataGrid>("CueTree") is { } tree)
        {
            tree.RowDragStarted += OnCueRowDragStarted;
            tree.RowDrop += OnCueRowDrop;
        }

        // Files dragged in from a file manager become media cues - the same import + MEDIA… runs, which
        // is how a show gets built. handledEventsToo, because this has to see a drag the tree's own
        // row-reorder handling has already looked at.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFilesDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnFilesDrop, RoutingStrategies.Bubble, handledEventsToo: true);

        // PANIC is the one control in the app that is HELD rather than clicked, so it needs the raw
        // pointer edges - and Button marks PointerPressed and PointerReleased HANDLED in its own class
        // handler, which runs before any instance handler on the same control. Declared in markup, the
        // hold handlers were therefore never called and the button did nothing whatsoever.
        //
        // handledEventsToo is what gets them delivered. It is deliberately narrow: only this button,
        // only these three events, so nothing else in the view starts seeing handled input.
        if (this.FindControl<Button>("PanicButton") is { } panic)
        {
            panic.AddHandler(PointerPressedEvent, OnPanicPressed, handledEventsToo: true);
            panic.AddHandler(PointerReleasedEvent, OnPanicReleased, handledEventsToo: true);
            panic.AddHandler(PointerCaptureLostEvent, OnPanicCaptureLost, handledEventsToo: true);
        }
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (DataContext is not CuesViewModel cues)
            return;
        if (Bounds.Width < 1_040 && cues.IsRightPanelOpen)
        {
            cues.IsRightPanelOpen = false;
            _collapsedForWidth = true;
        }
        else if (Bounds.Width >= 1_180 && _collapsedForWidth)
        {
            cues.IsRightPanelOpen = true;
            _collapsedForWidth = false;
        }
    }

    private void OnToggleRightPanel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CuesViewModel cues)
        {
            cues.IsRightPanelOpen = !cues.IsRightPanelOpen;
            _collapsedForWidth = false;
        }
    }

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

    /// <summary>
    /// Adds a cue that watches an NDI sender.
    /// </summary>
    /// <remarks>
    /// The network scan happens on a worker so the transport and active-cue controls stay responsive
    /// during the discovery window - and the add row says it is running, because two seconds of a
    /// click doing nothing visible is indistinguishable from a click that missed. The latch also stops
    /// a second click starting a second scan and stacking a second dialog on top of the first.
    /// </remarks>
    private async void OnAddNdi(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues)
            return;

        if (await DiscoverAsync(cues) is { } scan && ReferenceEquals(DataContext, cues))
            PromptWindow.Show(this, Dialogs.NdiSourceCue(cues, scan), cues.Refresh);
    }

    /// <summary>Scans for NDI senders under the busy latch, or nothing when one is already running.</summary>
    private static async Task<NdiSources.Scan?> DiscoverAsync(CuesViewModel cues)
    {
        if (cues.IsScanningSources)
            return null;

        cues.IsScanningSources = true;

        try
        {
            return await Task.Run(() => NdiSources.Discover());
        }
        finally
        {
            cues.IsScanningSources = false;
        }
    }

    private void OnAddCapture(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues)
            return;

        PromptWindow.Show(this, Dialogs.CaptureSourceCue(cues, App.Machine.Devices), cues.Refresh);
    }

    private void OnAddYouTube(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues)
            return;

        YouTubeCueWindow.Show(
            this,
            new YouTubeCueViewModel(
                cues, YouTubeRuntime.Gateway, YouTubeRuntime.Preparer, YouTubeRuntime.Downloads),
            cues.Refresh);
    }

    /// <summary>Reopens the dialog that made this cue, on the cue it made.</summary>
    private async void OnEditSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues || cues.SelectedSourceCue is not { } cue)
            return;

        switch (cues.SelectedSourceKind)
        {
            case SourceKind.Ndi:
                if (await DiscoverAsync(cues) is { } scan && ReferenceEquals(DataContext, cues))
                    PromptWindow.Show(this, Dialogs.NdiSourceCue(cues, scan, cue), cues.Refresh);
                break;

            case SourceKind.Capture:
                PromptWindow.Show(this, Dialogs.CaptureSourceCue(cues, App.Machine.Devices, cue), cues.Refresh);
                break;

            case SourceKind.YouTube:
                YouTubeCueWindow.Show(
                    this,
                    new YouTubeCueViewModel(
                        cues, YouTubeRuntime.Gateway, YouTubeRuntime.Preparer, YouTubeRuntime.Downloads, cue),
                    cues.Refresh);
                break;
        }
    }

    /// <summary>
    /// Remembers what the drag picked up, and refuses one that may not happen.
    /// </summary>
    /// <remarks>
    /// The drop event carries the target but not the dragged rows, so they are captured here - and the
    /// grid only starts a drag at all when this leaves an effect on the args.
    /// </remarks>
    private void OnCueRowDragStarted(object? sender, TreeDataGridRowDragStartedEventArgs e)
    {
        _draggedCues = DataContext is CuesViewModel { CanEditDocument: true }
            ? [.. e.Models.OfType<CueRow>().Select(row => row.Id)]
            : [];

        e.AllowedEffects = _draggedCues.Count > 0 ? DragDropEffects.Move : DragDropEffects.None;
    }

    /// <summary>
    /// Performs the reorder the operator just dropped.
    /// </summary>
    /// <remarks>
    /// Marked handled unconditionally, which is what stops the grid doing the move itself: its
    /// built-in reorder mutates the ROW collection, and those rows are a projection rebuilt from the
    /// document on the next refresh - a move made that way would be gone a moment later and would
    /// never have reached the journal, so it could not be undone either.
    /// </remarks>
    private void OnCueRowDrop(object? sender, TreeDataGridRowDragEventArgs e)
    {
        e.Handled = true;
        var dragged = _draggedCues;
        _draggedCues = [];

        if (DataContext is not CuesViewModel cues
            || dragged.Count == 0
            || e.Position == TreeDataGridRowDropPosition.None
            || e.TargetRow?.DataContext is not CueRow target)
            return;

        cues.MoveCues(dragged, target.Id, e.Position switch
        {
            TreeDataGridRowDropPosition.Before => CueDrop.Before,
            TreeDataGridRowDropPosition.Inside => CueDrop.Inside,
            _ => CueDrop.After,
        });
    }

    /// <summary>
    /// Says a file drag would be accepted, and ONLY a file drag.
    /// </summary>
    /// <remarks>
    /// The narrowness is the point. This runs after the tree's own row-drag handling, and answering
    /// every drag would answer that one too - with <c>None</c>, which stops the platform ever
    /// dispatching the drop, so a row reorder would show its indicator and then do nothing.
    /// </remarks>
    private void OnFilesDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File) || !IsOverCueList(e))
            return;

        // Importing is authoring, so it is behind the same lock as everything else that writes cues.
        e.DragEffects = DataContext is CuesViewModel { CanEditDocument: true }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Whether a drag is over the cue list rather than somewhere else on the screen.
    /// </summary>
    /// <remarks>
    /// The whole view accepts drops so the handlers see the drag at all; only this panel ACTS on one.
    /// Dropping an album on the inspector or the Active panel and having cues appear elsewhere is a
    /// gesture nobody made. The panel rather than the tree, because an empty list hides the tree and
    /// shows its no-cues state instead - which is precisely when somebody drags files in.
    /// </remarks>
    private bool IsOverCueList(DragEventArgs e)
    {
        if (this.FindControl<Panel>("CueListSurface") is not { } surface)
            return true;

        var at = e.GetPosition(surface);
        return at.X >= 0 && at.Y >= 0 && at.X <= surface.Bounds.Width && at.Y <= surface.Bounds.Height;
    }

    /// <summary>
    /// Makes a media cue of each dropped file, where they were dropped.
    /// </summary>
    /// <remarks>
    /// The row under the pointer is the anchor, so files land where the operator aimed rather than at
    /// the end of the list - dropped on a group they go inside it, dropped on a cue they follow it, and
    /// dropped on empty space they append. One undo step for the whole drop, like + MEDIA….
    /// </remarks>
    private void OnFilesDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CuesViewModel { CanEditDocument: true } cues
            || !IsOverCueList(e)
            || e.DataTransfer.TryGetFiles() is not { } files)
            return;

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .OfType<string>()
            .Where(path => path.Length > 0)
            .ToList();

        if (paths.Count == 0)
            return;

        e.Handled = true;
        cues.AddMedia(paths, AnchorAt(e));
    }

    /// <summary>The cue a drop landed on, or nothing when it landed past the last row.</summary>
    private static Guid? AnchorAt(DragEventArgs e)
    {
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: CueRow row })
                return row.Id;
        }

        return null;
    }

    private void OnDuplicate(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.DuplicateSelected();

    private void OnRemove(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.RemoveSelected();

    // The transport. GO always works (register item 3) - with no session it moves the cursor, which
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

    /// <summary>The × on a group header: everything the group is holding, in one press.</summary>
    private void OnStopGroup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CuesViewModel cues && (sender as Control)?.Tag is Guid groupId)
            cues.StopGroup(groupId);
    }

    /// <summary>A drag on an Active row's bar moves that cue's playhead.</summary>
    private void OnSeekActive(object? sender, Controls.SeekEventArgs e)
    {
        if (DataContext is CuesViewModel cues && (sender as Control)?.Tag is Guid cueId)
            _ = cues.SeekActiveAsync(cueId, e.Fraction);
    }

    /// <summary>A drag on a group header's bar moves every sounding child to the same absolute time.</summary>
    private void OnSeekGroup(object? sender, Controls.SeekEventArgs e)
    {
        if (DataContext is CuesViewModel cues && (sender as Control)?.Tag is Guid groupId)
            _ = cues.SeekGroupAsync(groupId, e.Fraction);
    }

    /// <summary>Double-tap on a group header toggles its expander - a larger target than the ▾.</summary>
    private void OnToggleGroupRow(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ActiveGroupRow row)
            row.IsExpanded = !row.IsExpanded;
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

    /// <summary>The list readout is also the list picker, so multi-list GO is visible and reachable.</summary>
    private void OnSelectList(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues || sender is not Control anchor)
            return;

        var flyout = new MenuFlyout();
        foreach (var scope in cues.CueLists)
        {
            var item = new MenuItem { Header = scope.Name };
            item.Click += (_, _) => cues.SelectTransportListCommand.Execute(scope);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }

    private void OnFireSelected(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.FireSelected();

    /// <summary>Auditions the selected cue. Monitoring only - it never reaches the program mix.</summary>
    private void OnPreview(object? sender, RoutedEventArgs e) =>
        (DataContext as CuesViewModel)?.PreviewSelected();

    /// <summary>
    /// PANIC is HELD, not clicked.
    /// </summary>
    /// <remarks>
    /// It is the one control an operator reaches for without reading, so a mis-click must not take the
    /// show down - but it also must not be behind a confirmation dialog, because the moment somebody
    /// needs it is the moment they have no attention left for a second decision. Holding is the
    /// compromise: one gesture, unmistakably deliberate, and the button reads HOLD… while it happens
    /// so a press that is working looks like one.
    /// </remarks>
    private void OnPanicPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CuesViewModel cues)
            cues.BeginPanic();
    }

    private void OnPanicReleased(object? sender, PointerReleasedEventArgs e) =>
        (DataContext as CuesViewModel)?.CancelPanic();

    /// <summary>A pointer that left the button abandons the hold - the mis-click escape hatch.</summary>
    private void OnPanicCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
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
