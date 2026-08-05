using Avalonia.Controls;
using Avalonia.Input;
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
            "in:key" => Dialogs.AddTriggerInput(journal, TriggerInputKind.Keyboard),
            "in:clock" => Dialogs.AddTriggerInput(journal, TriggerInputKind.Schedule),
            "in:mtc" => Dialogs.AddTriggerInput(journal, TriggerInputKind.Timecode),
            "in:edit" => Dialogs.EditTriggerInput(journal, targets.SelectedSource?.Id),
            "in:remove" => Dialogs.RemoveTriggerInput(journal, targets.SelectedSource?.Id),
            "end:osc" => Dialogs.AddEndpoint(journal, EndpointKind.OscOut),
            "end:midi" => Dialogs.AddEndpoint(journal, EndpointKind.MidiOut),
            "end:edit" => Dialogs.EditEndpoint(journal, targets.SelectedEndpoint?.Id),
            "end:remove" => Dialogs.RemoveEndpoint(journal, targets.SelectedEndpoint?.Id),
            _ => null,
        };

        PromptWindow.Show(this, prompt, targets.Refresh);
    }

    /// <summary>
    /// Delete on a list removes what is selected, through the same confirmation the menu opens.
    /// </summary>
    /// <remarks>
    /// The key AND the context menu, because the two habits are different people. Handled either way,
    /// so an unhandled Delete cannot travel up and act on a different pane.
    /// </remarks>
    private void OnEndpointsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || DataContext is not TargetsViewModel targets)
            return;

        e.Handled = true;
        PromptWindow.Show(
            this, Dialogs.RemoveEndpoint(targets.Journal, targets.SelectedEndpoint?.Id), targets.Refresh);
    }

    private void OnSourcesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || DataContext is not TargetsViewModel targets)
            return;

        e.Handled = true;
        PromptWindow.Show(
            this, Dialogs.RemoveTriggerInput(targets.Journal, targets.SelectedSource?.Id), targets.Refresh);
    }

    /// <summary>A binding is removed outright: it is one line, and undo takes it straight back.</summary>
    private void OnBindingsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || DataContext is not TargetsViewModel targets)
            return;

        e.Handled = true;
        targets.RemoveBinding(targets.SelectedBindingIndex);
    }

    /// <summary>Right-clicking a row selects it first, so the menu acts on what was clicked.</summary>
    /// <remarks>
    /// Avalonia opens a ListBox's context menu without moving the selection, so without this the menu's
    /// REMOVE would act on whatever was selected before — the one class of mistake a confirmation
    /// dialog does not catch, because the dialog names the wrong thing convincingly.
    /// </remarks>
    private void OnRowRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list
            || !e.GetCurrentPoint(list).Properties.IsRightButtonPressed
            || (e.Source as Control)?.DataContext is not { } item
            || list.ItemsSource is not System.Collections.IEnumerable items)
            return;

        foreach (var candidate in items)
        {
            if (!ReferenceEquals(candidate, item))
                continue;

            list.SelectedItem = item;
            return;
        }
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
