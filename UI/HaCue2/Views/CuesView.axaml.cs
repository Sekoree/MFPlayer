using Avalonia.Controls;
using Avalonia.Interactivity;
using HaCue2.ViewModels;
using Avalonia.Markup.Xaml;

namespace HaCue2.Views;

/// <summary>
/// Screens 02, 03 and 05.
/// </summary>
/// <remarks>
/// There is no selection handler here any more. The cue tree's <c>HierarchicalTreeDataGridSource</c>
/// owns its selection model and the view-model subscribes to it directly — one place decides what is
/// selected, rather than a control event and a bound property that can disagree.
/// </remarks>
public partial class CuesView : UserControl
{
    public CuesView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Renumbers the list the tree is scoped to, as one undo step.</summary>
    private void OnRenumber(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CuesViewModel cues)
            return;

        PromptWindow.Show(this, Dialogs.Renumber(cues.Journal, cues.ScopedList), cues.Refresh);
    }
}
