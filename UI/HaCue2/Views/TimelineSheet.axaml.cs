using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class TimelineSheet : UserControl
{
    public TimelineSheet() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Closing the sheet is the Cues view's state, not the sheet's — the sheet is only ever
    /// a projection of "is the timeline open".</summary>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<CuesView>()?.DataContext is CuesViewModel cues)
            cues.IsTimelineOpen = false;
    }
}
