using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
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
/// owns its selection model and the view-model subscribes to it directly — one place decides what is
/// selected, rather than a control event and a bound property that can disagree.
/// </remarks>
public partial class CuesView : UserControl
{
    public CuesView()
    {
        InitializeComponent();

        // PANIC is the one control in the app that is HELD rather than clicked, so it needs the raw
        // pointer edges — and Button marks PointerPressed and PointerReleased HANDLED in its own class
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
    /// during the discovery window — and the add row says it is running, because two seconds of a
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
            new YouTubeCueViewModel(cues, YouTubeRuntime.Gateway, YouTubeRuntime.Preparer),
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
                    new YouTubeCueViewModel(cues, YouTubeRuntime.Gateway, YouTubeRuntime.Preparer, cue),
                    cues.Refresh);
                break;
        }
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

    /// <summary>A pointer that left the button abandons the hold — the mis-click escape hatch.</summary>
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
