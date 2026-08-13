using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.Controls;
using HaCue2.ViewModels;

namespace HaCue2.Views;

/// <summary>
/// Screen 04b - the clip editor.
/// </summary>
/// <remarks>
/// A window rather than a sheet inside the Cues view, because trimming half an hour off a recording is
/// a job somebody sits down to do: it wants the width, and it wants to be closable without disturbing
/// where they were in the show.
/// </remarks>
public partial class ClipEditorWindow : Window
{
    public ClipEditorWindow()
    {
        InitializeComponent();
        DataContext = new ClipEditorViewModel();
    }

    public ClipEditorWindow(ClipEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;

        // The scan starts when the window does, not when the view-model is built: opening a file is
        // slow and can fail, and a view-model that did it on construction is one no preview could make.
        Opened += (_, _) => editor.Begin();
        Closed += (_, _) => editor.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnTrimGesture(object? sender, TrimGesture e) => Editor?.Apply(e.Handle, e.At);

    private void OnTrimGestureCompleted(object? sender, EventArgs e) => Editor?.EndGesture();

    /// <summary>
    /// Commits a typed time on blur, not on every keystroke.
    /// </summary>
    /// <remarks>
    /// "30:0" is a prefix of "30:00" and would otherwise be committed on the way through - moving the
    /// trim point twice, the second time to somewhere nobody asked for.
    /// </remarks>
    private void OnTrimInCommitted(object? sender, RoutedEventArgs e)
    {
        if (Editor is { } editor && sender is TextBox box)
            editor.NoteProblem(editor.SetTrimIn(box.Text ?? ""));
    }

    private void OnTrimOutCommitted(object? sender, RoutedEventArgs e)
    {
        if (Editor is { } editor && sender is TextBox box)
            editor.NoteProblem(editor.SetTrimOut(box.Text ?? ""));
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();

    private ClipEditorViewModel? Editor => DataContext as ClipEditorViewModel;
}
