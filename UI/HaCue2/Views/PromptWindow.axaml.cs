using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class PromptWindow : Window
{
    public PromptWindow()
    {
        InitializeComponent();
        DataContext = new PromptViewModel();
    }

    public PromptWindow(PromptViewModel prompt)
    {
        InitializeComponent();
        DataContext = prompt;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Writes a preset into the field it belongs to, leaving it typable afterwards.</summary>
    private void OnPickSuggestion(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string suggestion, Tag: PromptField field })
            field.Value = suggestion;
    }

    /// <summary>
    /// Picks a folder or a file into the field beside the button.
    /// </summary>
    /// <remarks>
    /// Opens where the field already points, so browsing from a half-typed path continues from there
    /// rather than from the home directory. A cancelled picker leaves what was typed alone - the one
    /// thing worse than no browse button is one that clears the box.
    /// </remarks>
    /// <summary>Fires an Action field's caller-supplied verb (a probe, a test, a scan).</summary>
    private void OnAction(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is PromptField field)
            field.Invoke();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not PromptField field
            || TopLevel.GetTopLevel(this) is not { } top)
            return;

        var start = field.Value.Trim().Length > 0
            ? await top.StorageProvider.TryGetFolderFromPathAsync(field.Value.Trim())
                .ConfigureAwait(true)
            : null;

        if (field.IsFolder)
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Choose {field.Label.ToLowerInvariant()}",
                SuggestedStartLocation = start,
            }).ConfigureAwait(true);

            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder)
                field.Value = folder;

            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Choose {field.Label.ToLowerInvariant()}",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        }).ConfigureAwait(true);

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } file)
            field.Value = file;
    }

    /// <summary>
    /// Opens a prompt over whatever window the control lives in, and refreshes after.
    /// </summary>
    /// <remarks>
    /// Every caller wants the same three things - find the owner, show modally, re-read the document
    /// when it closes - so they say it once here rather than twenty times.
    /// </remarks>
    public static void Show(Control from, PromptViewModel? prompt, Action? afterClose = null)
    {
        if (prompt is null || TopLevel.GetTopLevel(from) is not Window owner)
            return;

        var window = new PromptWindow(prompt);

        if (afterClose is not null)
            window.Closed += (_, _) => afterClose();

        window.ShowDialog(owner);
    }

    /// <summary>Confirm runs the caller's edit; Cancel never does, so nothing was an edit.</summary>
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PromptViewModel { CanConfirm: true } prompt)
            prompt.Commit();

        Close();
    }

    /// <summary>The third answer. Distinct from confirm, and equally deliberate.</summary>
    private void OnAlternative(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PromptViewModel prompt)
            prompt.CommitAlternative();

        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
