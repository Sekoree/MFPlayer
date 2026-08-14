using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;
using HaCue2.Presentation;

namespace HaCue2.ViewModels;

/// <summary>
/// The ACTION pane: the endpoint, address and arguments an action cue sends, with the parser-verified hint. A per-kind editor over the shared <see cref="CueEditPlumbing"/> (review F-11).
/// </summary>
public sealed partial class ActionPaneEditor(CueEditPlumbing plumbing, IInspectorEditorContext context)
    : ObservableObject
{
    private ActionCueNode? Action => context.Cue as ActionCueNode;
    private HaCueProject Project => context.Project;

    /// <summary>Raises every projection off the current selection - called from the inspector's
    /// <c>Reload</c>.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(ActionEndpoints));
        OnPropertyChanged(nameof(ActionEndpointIndex));
        OnPropertyChanged(nameof(ActionAddressValue));
        OnPropertyChanged(nameof(ActionArgumentsValue));
        OnPropertyChanged(nameof(ActionHint));
    }

    public IReadOnlyList<string> ActionEndpoints =>
        Action is null
            ? []
            : ["- none -", .. Project.ActionEndpoints.Select(
                endpoint => $"{endpoint.Name} · {Describe(endpoint.Kind)}")];

    public int ActionEndpointIndex
    {
        get
        {
            if (Action?.EndpointId is not { } id)
                return 0;

            var at = Project.ActionEndpoints.FindIndex(endpoint => endpoint.Id == id);
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            if (Action is not { } action || value < 0)
                return;

            var chosen = value == 0 || value > Project.ActionEndpoints.Count
                ? (Guid?)null
                : Project.ActionEndpoints[value - 1].Id;

            if (chosen == action.EndpointId)
                return;

            var target = action;
            plumbing.EditEach(target, "actionEndpoint", "cues",
                cue => cue.EndpointId, (cue, id) => cue.EndpointId = id, chosen, "set action endpoint");
        }
    }

    public string ActionAddressValue
    {
        get => Action?.Address ?? "";
        set
        {
            if (Action is not { } action)
                return;

            var target = action;
            plumbing.EditEach(target, "actionAddress", "cues",
                cue => cue.Address, (cue, address) => cue.Address = address, value, "set action address");
        }
    }

    public string ActionArgumentsValue
    {
        get => Action?.Arguments ?? "";
        set
        {
            if (Action is not { } action)
                return;

            var target = action;
            plumbing.EditEach(target, "actionArguments", "cues",
                cue => cue.Arguments, (cue, args) => cue.Arguments = args, value, "set action arguments");
        }
    }

    /// <summary>
    /// What this action will actually do - or what is wrong with it.
    /// </summary>
    /// <remarks>
    /// For a MIDI endpoint this is the PARSER's own verdict, and the same check runs in the status
    /// pass, so the hint and "will this show run" can never disagree. Saying it HERE means the operator
    /// finds out while authoring rather than when the desk fails to respond.
    /// </remarks>
    public string ActionHint
    {
        get
        {
            if (Action is not { } action)
                return "";

            if (action.EndpointId is not { } id
                || Project.ActionEndpoints.FirstOrDefault(endpoint => endpoint.Id == id) is not { } endpoint)
                return "no endpoint - this cue will do nothing";

            if (endpoint.Kind == EndpointKind.MidiOut)
            {
                // The parser's own verdict rather than a description of the syntax: an operator who has
                // typed something wrong wants to know WHAT is wrong, and the same check runs in the
                // status pass, so the two can never disagree about whether this cue will send.
                return MidiActions.TryParse(action.Address, action.Arguments, out var message) is { } wrong
                    ? wrong
                    : $"sends {Describe(message)} · channels are 1–16, values 0–127";
            }

            return action.Address.Length == 0
                ? "no address - this cue will do nothing"
                : "arguments are whitespace-separated and typed by shape: 3 is an int, 3.0 a float";
        }
    }

    private static string Describe(EndpointKind kind) =>
        kind == EndpointKind.MidiOut ? "MIDI out" : "OSC out";

    /// <summary>A parsed MIDI message in the words a desk's manual uses.</summary>
    private static string Describe(MidiAction message) => message.Kind switch
    {
        MidiActionKind.ControlChange =>
            $"CC {message.Number} = {message.Value} on ch {message.Channel}",
        MidiActionKind.ProgramChange =>
            $"program {message.Number} on ch {message.Channel}",
        MidiActionKind.NoteOff =>
            $"note {message.Number} off on ch {message.Channel}",
        _ => $"note {message.Number} on ch {message.Channel} at velocity {message.Value}",
    };
}
