using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class CurveEditorWindow : Window
{
    public CurveEditorWindow()
    {
        InitializeComponent();
        DataContext = new CurveEditorViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
