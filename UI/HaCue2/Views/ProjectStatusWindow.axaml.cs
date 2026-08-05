using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class ProjectStatusWindow : Window
{
    public ProjectStatusWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Relinks everything missing by searching a new root.</summary>
    private void OnRelinkRoot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectStatusViewModel status)
            PromptWindow.Show(this, status.RelinkUnderRoot());
    }

    /// <summary>
    /// Points ONE missing reference at a file the operator chose.
    /// </summary>
    /// <remarks>
    /// The manual escape hatch, for the file a search cannot find because it was renamed. A file
    /// picker rather than a typed path: the operator is looking at the file, not remembering it.
    /// </remarks>
    private async void OnRelinkOne(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectStatusViewModel status || status.MissingPath.Length == 0)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Replacement for {System.IO.Path.GetFileName(status.MissingPath)}",
            AllowMultiple = false,
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } chosen)
            status.RelinkOne(status.MissingPath, chosen);
    }

    /// <summary>Runs the checks again — after fixing something outside the app, or plugging a device in.</summary>
    private void OnRerun(object? sender, RoutedEventArgs e) =>
        (DataContext as ProjectStatusViewModel)?.Rerun();

    /// <summary>
    /// Puts the report on the clipboard, as the same plain text <c>hacue2-check</c> prints.
    /// </summary>
    /// <remarks>
    /// The whole point of "copy report" is pasting it into a message to somebody who is not in the
    /// building, so it is the CLI's text rather than a screen-shaped rendering — one format, and the
    /// person reading it can run the same command and compare.
    /// </remarks>
    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectStatusViewModel status || Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(status.Report.ToText());
        status.NoteCopied();
    }

    /// <summary>
    /// Takes the operator to the screen that owns the thing this check is complaining about.
    /// </summary>
    /// <remarks>
    /// The status window is a collector: it can say an output is unpatched but the patch itself lives
    /// on the Audio screen, so the only useful thing a fix button can do is put that screen in front of
    /// the operator. The window stays open behind it — the list is a work queue, and closing it after
    /// one fix would mean re-running the checks to find the next.
    /// </remarks>
    private void OnFix(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string destination } || destination.Length == 0)
            return;

        if (Owner is ShellWindow shell)
            shell.Shell.SelectedView = destination;

        Owner?.Activate();
    }

    private void OnConsolidate(object? sender, RoutedEventArgs e) =>
        (DataContext as ProjectStatusViewModel)?.Consolidate();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
