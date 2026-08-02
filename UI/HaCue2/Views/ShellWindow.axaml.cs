using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
        }

        base.OnKeyDown(e);
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

    private void OnSettings(object? sender, RoutedEventArgs e)
        => _settings = Reopen(_settings, () => new SettingsWindow { DataContext = new SettingsViewModel(Shell.Project) });

    private void OnDiagnostics(object? sender, RoutedEventArgs e)
        => _diagnostics = Reopen(_diagnostics, () => new DiagnosticsWindow { DataContext = new DiagnosticsViewModel(Shell.Runtime) });

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
