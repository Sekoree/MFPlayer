using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

/// <summary>"Duck under…" dialog for one timeline media block (timeline doc Phase D). Apply writes
/// the dip through the VM and closes with true; Cancel/Esc close with false and change nothing.</summary>
public partial class DuckUnderDialog : Window
{
    public DuckUnderDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
    }

    private void ApplyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DuckUnderDialogViewModel vm)
            return;
        Close(vm.Apply());
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
