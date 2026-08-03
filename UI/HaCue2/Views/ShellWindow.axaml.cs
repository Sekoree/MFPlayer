using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HaCue2.Session;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class ShellWindow : Window
{
    private DiagnosticsWindow? _diagnostics;
    private SettingsWindow? _settings;
    private ProjectStatusWindow? _projectStatus;
    private OutputInfoWindow? _outputInfo;

    public ShellWindow() => InitializeComponent();

    private ShellViewModel Shell =>
        DataContext as ShellViewModel ?? throw new InvalidOperationException("The shell has no view-model.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// F9 summons the Output info drawer (register item 4). It is a window-level key because the
    /// drawer is shell chrome, not part of whichever view happens to be focused.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            base.OnKeyDown(e);
            return;
        }

        // Undo is a WINDOW-level key with ONE journal behind it, across every view. That is the whole
        // point of a single journal: ⌘Z means the same thing whether the last edit was a cue label or
        // a patch cell, and the toast says which surface it changed so an undo cannot silently alter a
        // screen the operator is not looking at.
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            // Register item 3: GO always works. It is deliberately NOT gated on focus being anywhere
            // in particular, and deliberately not swallowed while a text box has focus either — see
            // the guard below, which is the one exception.
            case Key.Space when !control && !IsTyping():
                shell.Cues.Go();
                e.Handled = true;
                return;

            case Key.Escape:
                shell.Cues.StandbyHere();
                e.Handled = true;
                return;

            case Key.Up when !control && !IsTyping():
                shell.Cues.StepStandby(-1);
                e.Handled = true;
                return;

            case Key.Down when !control && !IsTyping():
                shell.Cues.StepStandby(1);
                e.Handled = true;
                return;

            case Key.F9:
                shell.IsOutputInfoOpen = !shell.IsOutputInfoOpen;
                e.Handled = true;
                return;

            case Key.Z when control && !shift:
                shell.Undo();
                e.Handled = true;
                return;

            case Key.Z when control && shift:
            case Key.Y when control:
                shell.Redo();
                e.Handled = true;
                return;

            // Save is a window key for the same reason undo is: it is about the DOCUMENT, and which
            // view happens to be focused has nothing to do with it.
            case Key.S when control && !shift:
                _ = SaveAsync(shell);
                e.Handled = true;
                return;

            case Key.S when control && shift:
                _ = SaveAsAsync(shell);
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Whether the focus is in something that eats keys.
    /// </summary>
    /// <remarks>
    /// Space in a cue LABEL has to be a space. This is the one place a transport key gives way, and it
    /// gives way to typing rather than to focus being "somewhere else" — every other control in the
    /// app can have focus and Space still fires the show.
    /// </remarks>
    private bool IsTyping() => FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>
    /// Saves, asking where only when there is nowhere yet.
    /// </summary>
    /// <remarks>
    /// The fallback matters: a Ctrl+S on a project that has never been saved must open Save As rather
    /// than appearing to work and writing nothing, which is the worst outcome this code can produce.
    /// </remarks>
    private async Task SaveAsync(ShellViewModel shell)
    {
        if (!await shell.SaveAsync())
            await SaveAsAsync(shell);
    }

    private async Task SaveAsAsync(ShellViewModel shell)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = shell.Project.Title,
            DefaultExtension = ProjectFiles.Extension.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType("HaCue2 project") { Patterns = [$"*{ProjectFiles.Extension}"] },
            ],
        });

        if (file?.TryGetLocalPath() is { } path)
            await shell.SaveToAsync(path);
    }

    private void OnHideDrawer(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
            shell.IsOutputInfoOpen = false;
    }

    /// <summary>Pop the drawer out for a second monitor, and close the in-shell copy behind it —
    /// two live copies of the same meters is a way to misread one of them.</summary>
    private void OnPopOutDrawer(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
            return;

        shell.IsOutputInfoOpen = false;
        _outputInfo = Reopen(_outputInfo, () => new OutputInfoWindow { DataContext = shell.OutputInfo });
    }

    /// <summary>
    /// Opens Settings with the journal behind it.
    /// </summary>
    /// <remarks>
    /// The journal is what makes the project half of this screen real: without it every project-scope
    /// setting is read once and written nowhere, so the pane describes the document rather than editing
    /// it (register items 26 and 28 — project settings are journaled and travel in the file).
    /// </remarks>
    private void OnSettings(object? sender, RoutedEventArgs e)
        => _settings = Reopen(
            _settings,
            () => new SettingsWindow { DataContext = new SettingsViewModel(Shell.Project, Shell.Journal) });

    private void OnDiagnostics(object? sender, RoutedEventArgs e)
        => _diagnostics = Reopen(_diagnostics, () => new DiagnosticsWindow { DataContext = Shell.OpenDiagnostics() });

    private void OnProjectStatus(object? sender, RoutedEventArgs e)
        => _projectStatus = Reopen(_projectStatus, () => new ProjectStatusWindow { DataContext = new ProjectStatusViewModel(Shell.Project, Shell.Environment, Shell.Journal) });

    /// <summary>
    /// Bring an already-open auxiliary window forward instead of stacking a second copy. Diagnostics
    /// in particular is meant to sit on a second monitor for a whole show, so pressing its button
    /// again must find it, not clone it.
    /// </summary>
    private T Reopen<T>(T? existing, Func<T> create) where T : Window
    {
        if (existing is { } window && window.IsVisible)
        {
            window.Activate();
            return window;
        }

        var created = create();
        created.Show(this);
        return created;
    }
}
