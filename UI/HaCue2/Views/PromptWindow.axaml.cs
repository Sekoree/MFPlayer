using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class PromptWindow : Window
{
    public PromptWindow()
    {
        InitializeComponent();
        DataContext = new PromptViewModel();
    }

    public PromptWindow(PromptViewModel prompt)
    {
        InitializeComponent();
        DataContext = prompt;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Opens a prompt over whatever window the control lives in, and refreshes after.
    /// </summary>
    /// <remarks>
    /// Every caller wants the same three things — find the owner, show modally, re-read the document
    /// when it closes — so they say it once here rather than twenty times.
    /// </remarks>
    public static void Show(Control from, PromptViewModel? prompt, Action? afterClose = null)
    {
        if (prompt is null || TopLevel.GetTopLevel(from) is not Window owner)
            return;

        var window = new PromptWindow(prompt);

        if (afterClose is not null)
            window.Closed += (_, _) => afterClose();

        window.ShowDialog(owner);
    }

    /// <summary>Confirm runs the caller's edit; Cancel never does, so nothing was an edit.</summary>
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PromptViewModel { CanConfirm: true } prompt)
            prompt.Commit();

        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
