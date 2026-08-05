using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

/// <summary>The YouTube add/edit window. Opens over the shell and closes once the cue exists.</summary>
public partial class YouTubeCueWindow : Window
{
    public YouTubeCueWindow()
    {
        InitializeComponent();
        DataContext = new YouTubeCueViewModel();
    }

    public YouTubeCueWindow(YouTubeCueViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        model.Finished += Close;

        // Closing cancels only manifest lookup. A queued download belongs to the application-level
        // background queue and deliberately survives this authoring window.
        Closed += (_, _) => model.Cancel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Opens over whatever window the control lives in, and refreshes after.</summary>
    public static void Show(Control from, YouTubeCueViewModel? model, Action? afterClose = null)
    {
        if (model is null || TopLevel.GetTopLevel(from) is not Window owner)
            return;

        var window = new YouTubeCueWindow(model);

        if (afterClose is not null)
            window.Closed += (_, _) => afterClose();

        window.ShowDialog(owner);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
