using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class CuesView : UserControl
{
    public CuesView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Feeds the whole selection to the inspector, not just the lead cue.
    /// </summary>
    /// <remarks>
    /// Selection is read here rather than bound because <c>SelectedItems</c> is a mutable list the
    /// control owns: a two-way binding to it means the view-model holds a reference to the control's
    /// state and has to guess when it settled. A SelectionChanged handler hands over a snapshot,
    /// which is what the inspector actually needs.
    /// </remarks>
    private void OnCueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not CuesViewModel cues)
            return;

        cues.Inspector.Show([.. list.SelectedItems?.OfType<CueRow>() ?? []]);
    }
}
