using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.Controls;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class CurveEditorWindow : Window
{
    public CurveEditorWindow()
    {
        InitializeComponent();
        DataContext = new CurveEditorViewModel();
    }

    /// <summary>Opens on a real curve — the route every "✎" beside a curve picker takes.</summary>
    public CurveEditorWindow(CurveEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCurveGesture(object? sender, CurveGesture e) => Editor?.Apply(e);

    private void OnCurveGestureCompleted(object? sender, EventArgs e) => Editor?.EndGesture();

    private void OnHoldToggled(object? sender, int index) => Editor?.ToggleHold(index);

    private void OnDone(object? sender, RoutedEventArgs e) => Close();

    private CurveEditorViewModel? Editor => DataContext as CurveEditorViewModel;
}
