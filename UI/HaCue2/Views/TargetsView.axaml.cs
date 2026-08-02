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
}
