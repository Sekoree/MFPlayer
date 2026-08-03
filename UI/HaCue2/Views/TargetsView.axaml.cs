using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using HaCue2.Core.Model;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class TargetsView : UserControl
{
    public TargetsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Opens whichever dialog the button asked for, by its Tag.</summary>
    private void OnDialog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TargetsViewModel targets || (sender as Control)?.Tag as string is not { } verb)
            return;

        var journal = targets.Journal;

        var prompt = verb switch
        {
            "in:midi" => Dialogs.AddTriggerInput(journal, TriggerInputKind.MidiIn),
            "in:osc" => Dialogs.AddTriggerInput(journal, TriggerInputKind.OscIn),
            "end:osc" => Dialogs.AddEndpoint(journal, EndpointKind.OscOut),
            "end:midi" => Dialogs.AddEndpoint(journal, EndpointKind.MidiOut),
            _ => null,
        };

        PromptWindow.Show(this, prompt, targets.Refresh);
    }

    /// <summary>Starts listening for the next message on any enabled source.</summary>
    private void OnLearn(object? sender, RoutedEventArgs e) =>
        (DataContext as TargetsViewModel)?.BeginLearn();

    /// <summary>Creates the binding from what was caught. The only path that constructs one.</summary>
    private void OnBind(object? sender, RoutedEventArgs e) =>
        (DataContext as TargetsViewModel)?.Bind();

    private void OnRemoveBinding(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TargetsViewModel targets)
            targets.RemoveBinding(targets.SelectedBindingIndex);
    }

    /// <summary>
    /// Sends the selected endpoint's own configured test payload.
    /// </summary>
    /// <remarks>
    /// A sender of its own rather than the show's: testing an endpoint has to work while no session is
    /// running, which is exactly when somebody is setting the desk up.
    /// </remarks>
    private async void OnSendTest(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TargetsViewModel targets)
            return;

        using var probe = new HaCue2.Engine.ActionSender();
        await targets.SendTestAsync(probe);
    }
}
