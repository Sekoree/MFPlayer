using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
            "group" => Dialogs.AddOutputGroup(journal, audio.SelectedOutputIds),
            "rename" => audio.SelectedOutput is { } row ? Dialogs.Rename(journal, row.Id, row.Name, "audio") : null,
            "snapshot" => Dialogs.SaveSnapshot(journal),
            "patch" => audio.PatchSelectedToDevice(),
            "relink" => audio.RelinkAbsentLines(),
            _ => null,
        };

        PromptWindow.Show(this, prompt, audio.Refresh);
    }

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
