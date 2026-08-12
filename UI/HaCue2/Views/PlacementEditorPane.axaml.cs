using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

/// <summary>The complete editor hosted by one video-placement expander.</summary>
public partial class PlacementEditorPane : UserControl
{
    public PlacementEditorPane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnRemovePlacement(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.RemovePlacement();
    }

    private void OnLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector && (sender as Control)?.Tag is string preset)
            inspector.ApplyLayout(preset);
    }

    private void OnCrop(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector && (sender as Control)?.Tag is string preset)
            inspector.ApplyCrop(preset);
    }

    private void OnAddAutomation(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector
            && (sender as Control)?.Tag is string propertyId)
            inspector.AddLane(propertyId);
    }

    private void OnAddEffect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector && (sender as Control)?.Tag is string typeId)
            inspector.AddLayerEffect(typeId);
    }

    private void OnToggleEffect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector && (sender as Control)?.Tag is Guid effectId)
            inspector.ToggleLayerEffect(effectId);
    }

    private void OnMoveEffectUp(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector && (sender as Control)?.Tag is Guid effectId)
            inspector.MoveLayerEffect(effectId, -1);
    }

    private void OnMoveEffectDown(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector && (sender as Control)?.Tag is Guid effectId)
            inspector.MoveLayerEffect(effectId, 1);
    }

    private void OnRemoveEffect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector && (sender as Control)?.Tag is Guid effectId)
            inspector.RemoveLayerEffect(effectId);
    }

    private void OnPlacementGesture(object? sender, Controls.PlacementGesture gesture)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.ApplyPlacementGesture(gesture);
    }

    private void OnPlacementGestureCompleted(object? sender, EventArgs e)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.EndPlacementGesture();
    }

    private void OnFieldCommitted(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InspectorViewModel inspector)
            inspector.EndEdit();
    }

    private void OnPopOut(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InspectorViewModel inspector)
            return;

        var window = new PlacementEditorWindow { DataContext = inspector };
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }
}
