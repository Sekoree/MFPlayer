using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.Controls;
using HaCue2.Core.Model;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class VideoView : UserControl
{
    public VideoView() => InitializeComponent();

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

    /// <summary>"Edit ›" beside an output's mapping toggle jumps to the Mapping tab already scoped to
    /// that output — mapping is always the mapping OF one output, never a global mode.</summary>
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

    private void OnEditMapping(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoViewModel video)
            video.SelectedTab = VideoViewModel.MappingTab;
    }

    // ── canvas gestures ───────────────────────────────────────────────────────────────────────
    // Handled here rather than bound to a command because a composition canvas lives inside a
    // DataTemplate whose DataContext is one pane, and the edit belongs to the view — the pane is a
    // projection, not the thing that owns the journal.

    private void OnLayerGesture(object? sender, PlacementGesture e) => Video?.ApplyLayerGesture(e);

    private void OnMappingSourceGesture(object? sender, PlacementGesture e) =>
        Video?.ApplyMappingSourceGesture(e);

    private void OnMappingTargetGesture(object? sender, PlacementGesture e) =>
        Video?.ApplyMappingTargetGesture(e);

    private void OnGestureCompleted(object? sender, EventArgs e) => Video?.EndGesture();

    private void OnSectionSelected(object? sender, int index) => Video?.SelectSection(index);

    /// <summary>
    /// Pushes a typed section number into the document when the field loses focus.
    /// </summary>
    /// <remarks>
    /// The default binding commits on LostFocus already; this raises the properties back so a value the
    /// document clamped (a width past the edge, a negative opacity) is shown as what was actually
    /// stored rather than as what was typed.
    /// </remarks>
    private void OnSectionFieldCommitted(object? sender, RoutedEventArgs e) => Video?.Refresh();

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
        }

        var prompt = verb switch
        {
            "out:local" => Dialogs.AddVideoOutput(journal, VideoOutputKind.LocalScreen, video.Screens),
            "out:ndi" => Dialogs.AddVideoOutput(journal, VideoOutputKind.Ndi, video.Screens),
            "out:record" => Dialogs.AddVideoOutput(journal, VideoOutputKind.Record, video.Screens),
            "out:stream" => Dialogs.AddVideoOutput(journal, VideoOutputKind.Stream, video.Screens),
            "composition" => Dialogs.AddComposition(journal),
            _ => null,
        };

        PromptWindow.Show(this, prompt, video.Refresh);
    }

    private VideoViewModel? Video => DataContext as VideoViewModel;
}
