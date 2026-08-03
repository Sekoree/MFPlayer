using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Puts the whole bay on the clipboard as text.
    /// </summary>
    /// <remarks>
    /// The same rendering the framework's own report writer produces, so what gets pasted into a
    /// message is what somebody else can reproduce — rather than a screen-shaped transcription that
    /// drifts from it.
    /// </remarks>
    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsViewModel diagnostics || Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(diagnostics.Report());
    }

    private void OnReset(object? sender, RoutedEventArgs e) =>
        (DataContext as DiagnosticsViewModel)?.ResetCounters();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
