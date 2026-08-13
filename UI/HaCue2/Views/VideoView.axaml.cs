using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Model;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class VideoView : UserControl
{
    public VideoView() => InitializeComponent();

    /// <summary>
    /// Picks the composition's holding slate.
    /// </summary>
    /// <remarks>
    /// The field stays typable beside it, for the same reason every other path field in the app does:
    /// pasting is faster than clicking through, and a path on the show machine cannot be picked from
    /// the laptop a show is authored on. A cancelled picker leaves what was typed alone.
    /// </remarks>
    private async void OnBrowseIdleImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VideoViewModel video || TopLevel.GetTopLevel(this) is not { } top)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose the idle image",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new Avalonia.Platform.Storage.FilePickerFileType("Images")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff"],
                    },
                ],
            });

        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;

        // The composition this button belongs to, not "the selected one". Each pane carries its own
        // editor now, so relying on the selection would put the picked image on whichever canvas was
        // last clicked — which is usually the right one and occasionally, silently, is not.
        if ((sender as Control)?.DataContext is CompositionPaneViewModel pane)
            pane.IdleImage = path;
        else
            video.CompositionIdleImage = path;
    }

    /// <summary>
    /// Hands the view-model the machine's real screen list.
    /// </summary>
    /// <remarks>
    /// From the window manager rather than invented: the picker used to offer three hardcoded
    /// resolutions that matched no rig it would ever be opened on, so choosing "2 · 1920×1080" told an
    /// operator nothing about where their projector feed would land.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is not VideoViewModel video || TopLevel.GetTopLevel(this)?.Screens is not { } screens)
            return;

        video.SetScreens(screens.All.Select((screen, index) =>
            $"{index + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}"
            + (screen.IsPrimary ? " · primary" : "")));
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Inserts a filename token from the pattern dropdown (register item 30).</summary>
    private void OnInsertToken(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoViewModel video && (sender as Control)?.Tag as string is { } token)
            video.Record.InsertToken(token);
    }

    /// <summary>
    /// Arms or disarms the selected recording.
    /// </summary>
    /// <remarks>
    /// A press, never a consequence of an edit: a recording that armed itself because somebody typed a
    /// pattern would fill a disk during rehearsal.
    /// </remarks>
    private async void OnToggleRecorder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VideoViewModel video
            || video.SelectedOutput is not { } row
            || this.FindAncestorOfType<ShellWindow>()?.DataContext is not ShellViewModel shell)
            return;

        var problem = await shell.ToggleRecorderAsync(row.Id);
        video.Record.RefreshRunning();

        if (problem is not null)
            video.Record.NoteProblem(problem);
    }

    /// <summary>
    /// Flashes the selected output's own name on it.
    /// </summary>
    /// <remarks>
    /// The one reliable way to answer "which of these three projectors is Projector A" without
    /// unplugging anything, which is how it otherwise gets answered at a get-in.
    /// </remarks>
    private async void OnIdentify(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VideoViewModel video
            || video.SelectedOutput is not { } row
            || this.FindAncestorOfType<ShellWindow>()?.DataContext is not ShellViewModel shell)
            return;

        if (await shell.IdentifyAsync(row.Id) is { } problem)
            video.NoteProblem(problem);
    }

    /// <summary>"Edit ›" beside an output's mapping toggle jumps to the Mapping tab already scoped to
    /// that output — mapping is always the mapping OF one output, never a global mode.</summary>
    /// <summary>Opens the mapping editor over the Outputs pane, on the output it belongs to.</summary>
    private void OnEditMapping(object? sender, RoutedEventArgs e) => Video?.OpenMapping();

    private void OnCloseMapping(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoViewModel video)
        {
            if (video.IsCalibrationOn)
                _ = SetCalibrationAsync(video, false);
            video.ShowMapping = false;
        }
    }

    private async void OnCalibration(object? sender, RoutedEventArgs e)
    {
        if (Video is { } video)
            await SetCalibrationAsync(video, !video.IsCalibrationOn);
    }

    private async Task SetCalibrationAsync(VideoViewModel video, bool enabled)
    {
        try
        {
            if (video.SelectedOutput is not { } output
                || this.FindAncestorOfType<ShellWindow>()?.DataContext is not ShellViewModel shell)
                return;
            if (await shell.SetCalibrationAsync(output.Id, enabled) is { } problem)
                video.NoteProblem(problem);
            else
                video.NoteCalibration(output.Id, enabled);
        }
        finally
        {
            // The button flipped ITSELF on the click, before the engine was asked anything. Every way
            // out of here — refused because no show is running, no output selected, or done — has to
            // put the latch back onto the truth, or CALIBRATION GRID sits lit over an output that is
            // showing no grid at all. Cheap, and idempotent on the path that already notified.
            video.ReassertCalibration();
        }
    }

    private void OnLayoutGesture(object? sender, PlacementGesture e) => Video?.ApplyLayoutGesture(e);

    private void OnResolutionLayout(object? sender, RoutedEventArgs e)
    {
        if (Video is { } video && PaneOf(sender) is { } compositionId)
            video.ApplyResolutionLayout(compositionId);
    }

    /// <summary>
    /// Selects the screen whose slice was clicked, on the canvas it was clicked on.
    /// </summary>
    /// <remarks>
    /// Through the canvas's own pane rather than the view-model's selection, because every composition
    /// draws its own layout now: an index alone would address whichever pane happened to be selected.
    /// This is also what selects the COMPOSITION when a box is clicked — the canvas handles the press,
    /// so it never reaches the pane's own <see cref="OnSelectComposition"/>.
    /// </remarks>
    private void OnLayoutOutputSelected(object? sender, int index) =>
        Video?.SelectScreen(PaneOf(sender), index);

    /// <summary>The composition a control inside a pane's template belongs to.</summary>
    private static Guid? PaneOf(object? sender) =>
        (sender as Control)?.DataContext is CompositionPaneViewModel pane ? pane.Id : null;

    /// <summary>
    /// Selects the composition whose pane was clicked.
    /// </summary>
    /// <remarks>
    /// The panes are canvases, not list rows, so selection is a click on the pane rather than a
    /// ListBox. It matters because the inspector beside them — size, rate, idle image, and which
    /// outputs the canvas feeds — is about ONE composition, and before this the second pane could only
    /// be reached by selecting an output that happened to be on it.
    /// </remarks>
    private void OnSelectComposition(object? sender, PointerPressedEventArgs e)
    {
        if (Video is not { } video || (sender as Control)?.DataContext is not CompositionPaneViewModel pane)
            return;

        video.SelectedCompositionId = pane.Id;
        video.Refresh();
    }

    /// <summary>Takes one output off the composition it shows. The output itself survives.</summary>
    private void OnUnassign(object? sender, RoutedEventArgs e)
    {
        if (Video is not { } video || (sender as Control)?.Tag is not Guid outputId)
            return;

        video.UnassignOutput(outputId);
    }

    private void OnAssign(object? sender, RoutedEventArgs e) => Video?.AssignSelectedOutput();

    /// <summary>
    /// Writes a picked size into whichever field its dropdown belongs to.
    /// </summary>
    /// <remarks>
    /// The picker sets the box rather than replacing it, so a preset is a shortcut and never a limit —
    /// an LED wall of 1408×768 is still typed straight in.
    /// </remarks>
    private void OnPickResolution(object? sender, RoutedEventArgs e)
    {
        if (Video is not { } video
            || sender is not Button { Content: string size, Tag: string field })
            return;

        switch (field)
        {
            case "composition":
                // Through the SELECTION here, unlike the other per-pane editors: these buttons live in
                // a flyout, so their DataContext is the size string rather than the pane. Opening that
                // flyout means clicking inside the pane, and the pane's own PointerPressed selects the
                // composition before the popup opens — so the selection is that pane by construction.
                video.CompositionSize = size;
                break;
            case "window":
                video.OutputWindowSize = size;
                break;
            case "raster":
                video.OutputRaster = size;
                break;
        }

        // The flyout stays open otherwise, over a field the operator has just finished with.
        (sender as Control)?.FindAncestorOfType<Popup>()?.Close();
    }

    // ── canvas gestures ───────────────────────────────────────────────────────────────────────
    // Handled here rather than bound to a command because a composition canvas lives inside a
    // DataTemplate whose DataContext is one pane, and the edit belongs to the view — the pane is a
    // projection, not the thing that owns the journal.

    private void OnMappingSourceGesture(object? sender, PlacementGesture e) =>
        Video?.ApplyMappingSourceGesture(e);

    private void OnMappingTargetGesture(object? sender, PlacementGesture e) =>
        Video?.ApplyMappingTargetGesture(e);

    private void OnGestureCompleted(object? sender, EventArgs e) => Video?.EndGesture();

    private void OnSectionSelected(object? sender, int index) => Video?.SelectSection(index);

    private void OnWarpNudge(object? sender, RoutedEventArgs e)
    {
        if (Video is not { } video || (sender as Control)?.Tag is not string direction)
            return;
        var (dx, dy) = direction switch
        {
            "left" => (-0.005, 0d),
            "right" => (0.005, 0d),
            "up" => (0d, -0.005),
            "down" => (0d, 0.005),
            _ => (0d, 0d),
        };
        video.NudgeWarp(dx, dy);
    }

    private void OnWarpPointMoved(object? sender, WarpPointGesture e) => Video?.MoveWarpPoint(e);
    private void OnWarpPointSelected(object? sender, int index)
    {
        if (Video is { } video)
            video.SelectedWarpPoint = index;
    }
    private void OnWarpGestureCompleted(object? sender, EventArgs e) => Video?.EndWarpGesture();

    /// <summary>
    /// Removes the selected output or composition on Delete, depending on which pane is open.
    /// </summary>
    /// <remarks>
    /// The key AND the context menu, because the two habits are different people: one reaches for
    /// Delete, the other right-clicks. Both land on the same confirmation, which is where the
    /// consequences are counted — the key must not be a faster way to skip the question.
    /// </remarks>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || Video is null)
            return;

        // Handled either way: an unhandled Delete on a list is how a keystroke meant for one pane ends
        // up acting on another.
        e.Handled = true;
        OnDialog(sender, e);
    }

    /// <summary>Opens whichever dialog the button asked for, by its Tag.</summary>
    private void OnDialog(object? sender, RoutedEventArgs e)
    {
        if (Video is not { } video || (sender as Control)?.Tag as string is not { } verb)
            return;

        var journal = video.Journal;

        // The mapping-section verbs are not prompts at all — they act on the selection directly,
        // because "duplicate this section" has nothing to ask.
        switch (verb)
        {
            case "section":
                video.AddSection();
                return;
            case "section:copy":
                video.DuplicateSection();
                return;
            case "section:delete":
                video.DeleteSection();
                return;
            case "section:up":
                video.MoveSection(-1);
                return;
            case "section:down":
                video.MoveSection(1);
                return;
            case "section:split":
                video.SplitIntoGrid();
                return;
            case "section:reset":
                video.ResetToIdentity();
                return;
            case "mesh:reset":
                video.ResetMesh();
                return;
        }

        var prompt = verb switch
        {
            "out:local" => Dialogs.AddVideoOutput(journal, VideoOutputKind.LocalScreen, video.Screens),
            "out:ndi" => Dialogs.AddVideoOutput(journal, VideoOutputKind.Ndi, video.Screens),
            "out:record" => Dialogs.AddVideoOutput(journal, VideoOutputKind.Record, video.Screens),
            "out:stream" => Dialogs.AddVideoOutput(journal, VideoOutputKind.Stream, video.Screens),
            "out:rename" => video.SelectedOutput is { } output
                ? Rename(journal, output.Id, output.Name)
                : null,
            "out:remove" => Dialogs.RemoveVideoOutput(journal, video.SelectedOutput?.Id),
            "composition" => Dialogs.AddComposition(journal),
            "composition:remove" => Dialogs.RemoveComposition(journal, video.SelectedCompositionId),
            _ => null,
        };

        PromptWindow.Show(this, prompt, video.Refresh);
    }

    /// <summary>Renames a video output through the journal, by id.</summary>
    private static PromptViewModel? Rename(
        HaCue2.Core.Journal.ProjectJournal journal, Guid outputId, string current)
    {
        if (journal.Project.VideoOutputs.FirstOrDefault(item => item.Id == outputId) is not { } output)
            return null;

        return Dialogs.RenameTo(
            journal, current, "video", () => output.Name, name => output.Name = name, outputId);
    }

    /// <summary>Right-clicking a row selects it first, so the menu acts on what was clicked.</summary>
    /// <remarks>
    /// Avalonia opens a ListBox's context menu without moving the selection, so without this the menu's
    /// REMOVE would delete whatever happened to be selected before — which is the one class of mistake
    /// a confirmation dialog does not catch, because the dialog names the wrong thing convincingly.
    /// </remarks>
    private void OnRowRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list
            || !e.GetCurrentPoint(list).Properties.IsRightButtonPressed
            || (e.Source as Control)?.DataContext is not { } item
            || list.ItemsSource is not System.Collections.IEnumerable items)
            return;

        foreach (var candidate in items)
        {
            if (!ReferenceEquals(candidate, item))
                continue;

            list.SelectedItem = item;
            return;
        }
    }

    private VideoViewModel? Video => DataContext as VideoViewModel;
}
