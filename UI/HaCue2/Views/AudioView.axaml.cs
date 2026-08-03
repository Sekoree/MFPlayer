using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.Core.Model;
using HaCue2.Controls;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class AudioView : UserControl
{
    public AudioView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Opens whichever dialog the button asked for, by its Tag.
    /// </summary>
    /// <remarks>
    /// One handler rather than ten, because every one of these does the same thing: build a prompt,
    /// show it over this window, re-read the document afterwards. The Tag names the verb, so adding a
    /// button is a line of markup and a case rather than another handler nobody will find.
    /// </remarks>
    private void OnDialog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AudioViewModel audio || (sender as Control)?.Tag as string is not { } verb)
            return;

        var journal = audio.Journal;

        var prompt = verb switch
        {
            "line:device" => Dialogs.AddAudioLine(journal, AudioLineKind.PortAudio),
            "line:ndi" => Dialogs.AddAudioLine(journal, AudioLineKind.Ndi),
            "line:record" => Dialogs.AddAudioLine(journal, AudioLineKind.FileRecord),
            "line:stream" => Dialogs.AddAudioLine(journal, AudioLineKind.Stream),
            "output" => Dialogs.AddLogicalOutput(journal),
            "pair" => Dialogs.AddStereoPair(journal),
            "reorder" => Dialogs.Reorder(journal, audio.SelectedOutput?.Id),
            "group" => Dialogs.AddOutputGroup(journal, audio.SelectedOutputIds),
            "rename" => audio.SelectedOutput is { } row ? Dialogs.Rename(journal, row.Id, row.Name, "audio") : null,
            "snapshot" => Dialogs.SaveSnapshot(journal),
            "patch" => audio.PatchSelectedToDevice(),
            "relink" => audio.RelinkAbsentLines(),
            _ => null,
        };

        PromptWindow.Show(this, prompt, audio.Refresh);
    }

    /// <summary>
    /// Solos the selected logical output to the audition monitor, or clears it.
    /// </summary>
    /// <remarks>
    /// Monitoring, so it is allowed whenever a show is running: it rewrites the MONITOR line's own
    /// patch row and nothing else. "Why can I not hear Lobby" is the question it answers, and hearing
    /// Lobby alone is the answer.
    /// </remarks>
    private void OnSolo(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AudioViewModel audio
            || audio.SelectedOutput is not { } row
            || this.FindAncestorOfType<ShellWindow>()?.DataContext is not ShellViewModel shell)
            return;

        var problem = shell.SoloToMonitor(row.Id);
        audio.NoteSolo(shell.SoloedChannelId, problem);
    }

    /// <summary>
    /// Restarts the audio engine so a new mix rate or clock master takes effect.
    /// </summary>
    /// <remarks>
    /// The bus width and rate are fixed when the bay opens, so this is a genuine stop and start rather
    /// than a live change — which is exactly why it is a button the operator presses rather than
    /// something that happens under a running show when a combo box changes.
    /// </remarks>
    private async void OnRestartAudio(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<ShellWindow>()?.DataContext is not ShellViewModel shell)
            return;

        await shell.RestartAudioAsync();
    }

    /// <summary>
    /// Inserts a filename token from the pattern dropdown (register item 30).
    /// </summary>
    /// <remarks>
    /// The tokens are unguessable, which is the whole reason the dropdown exists — an operator who has
    /// to already know that <c>{n}</c> is the counter has not been helped by anything.
    /// </remarks>
    private void OnInsertToken(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AudioViewModel audio && (sender as Control)?.Tag as string is { } token)
            audio.Record.InsertToken(token);
    }

    /// <summary>
    /// Arms or disarms the selected recording.
    /// </summary>
    /// <remarks>
    /// A press, never a consequence of an edit: opening a file and starting an encoder is something an
    /// operator decides to do, and a recording that armed itself because somebody typed in a pattern
    /// would fill a disk during rehearsal.
    /// </remarks>
    private async void OnToggleRecorder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AudioViewModel audio
            || audio.SelectedLine is not { } row
            || this.FindAncestorOfType<ShellWindow>()?.DataContext is not ShellViewModel shell)
            return;

        var problem = await shell.ToggleRecorderAsync(row.Id);
        audio.Record.RefreshRunning();

        if (problem is not null)
            audio.Record.NoteProblem(problem);
    }

    private void OnRecall(object? sender, RoutedEventArgs e) =>
        (DataContext as AudioViewModel)?.RecallSelected();

    private void OnUpdateSnapshot(object? sender, RoutedEventArgs e) =>
        (DataContext as AudioViewModel)?.UpdateSelected();

    private void OnPatchGesture(object? sender, MatrixGesture gesture)
    {
        if (DataContext is AudioViewModel audio)
            audio.ApplyPatchGesture(gesture);
    }

    /// <summary>
    /// Closes the drag's coalescing group when the pointer comes up, so the whole drag is one undo
    /// step and the next drag on the same cell starts a new one.
    /// </summary>
    private void OnPatchGestureEnded(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is AudioViewModel audio)
            audio.EndPatchGesture();
    }
}
