using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class CueListSettingsDialog : Window
{
    public CueListSettingsDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CueListSettingsDialogViewModel vm) return;
        Close(vm.ToResult());
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
