using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class AuditionPane : UserControl
{
    public AuditionPane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Re-reads the rig when a typed field loses focus.
    /// </summary>
    /// <remarks>
    /// The level is stored as a number and rendered back with a unit, so the box has to be refreshed
    /// from the document after a commit — otherwise it keeps showing exactly what was typed, including
    /// a value the parser rejected.
    /// </remarks>
    private void OnFieldCommitted(object? sender, RoutedEventArgs e) =>
        (DataContext as AuditionViewModel)?.Refresh();
}
