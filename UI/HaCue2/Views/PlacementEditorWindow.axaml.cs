using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HaCue2.Controls;
using HaCue2.ViewModels;

namespace HaCue2.Views;

/// <summary>A large, non-modal host for the same live placement editor used in the inspector.</summary>
public partial class PlacementEditorWindow : Window
{
    public PlacementEditorWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as InspectorViewModel)?.Video.EndPlacementGesture();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnPlacementGesture(object? sender, PlacementGesture gesture) =>
        (DataContext as InspectorViewModel)?.Video.ApplyPlacementGesture(gesture);

    private void OnPlacementGestureCompleted(object? sender, EventArgs e) =>
        (DataContext as InspectorViewModel)?.Video.EndPlacementGesture();
}
