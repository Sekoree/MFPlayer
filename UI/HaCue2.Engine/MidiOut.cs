using HaCue2.Core.Model;
using S.Control;

namespace HaCue2.Engine;

/// <summary>
/// The MIDI output ports action cues send to.
/// </summary>
/// <remarks>
/// <para>
/// S.Control owns the port catalogue, the name matching and the message encoding. What lives here is
/// the HaCue2 half: an <see cref="ActionEndpoint"/> is a device NAME HINT, exactly like an audio line's
/// - ports are not stable across reboots, let alone across machines - and turning the document's list
/// of endpoints into the device config that layer wants.
/// </para>
/// <para>
/// <b>Ports open on first send, not at start-up.</b> An app holding a MIDI port it never uses is a
/// port another program cannot open, which is a rude thing to do to somebody's rig during a get-in.
/// It also means a desk plugged in after the show started still works on the next fire.
/// </para>
/// </remarks>
public sealed class MidiOut : IDisposable
{
    private readonly Lock _gate = new();
    private ControlSystemMIDIDeviceSessionManager? _sender;
    private string _configured = "";
    private bool _disposed;

    /// <summary>
    /// Adopts the project's MIDI endpoints.
    /// </summary>
    /// <remarks>
    /// Compared before rebuilding, like the trigger inputs are: a reload happens on every edit, and
    /// closing an open output port to reopen the identical one would drop whatever a cue was sending
    /// through it at that moment.
    /// </remarks>
    public void Adopt(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var endpoints = project.ActionEndpoints
            .Where(endpoint => endpoint.Kind == EndpointKind.MidiOut)
            .ToList();

        var describe = string.Join(
            "|", endpoints.OrderBy(endpoint => endpoint.Id).Select(endpoint => $"{endpoint.Id}:{endpoint.Host}"));

        ControlSystemMIDIDeviceSessionManager? retired;

        lock (_gate)
        {
            if (_disposed || describe == _configured)
                return;

            _configured = describe;
            retired = _sender;

            _sender = endpoints.Count == 0
                ? null
                : new ControlSystemMIDIDeviceSessionManager(new ControlSystemConfig
                {
                    IsArmed = true,
                    Devices =
                    [
                        .. endpoints.Select(endpoint => new ControlDeviceInstanceConfig
                        {
                            Id = endpoint.Id,
                            Name = endpoint.Name,
                            Protocol = ControlDeviceProtocol.MIDI,
                            IsEnabled = true,
                            Binding = new ControlDeviceBindingConfig
                            {
                                MIDIOutputDeviceName =
                                    endpoint.Host.Length > 0 ? endpoint.Host : endpoint.Name,
                            },
                        }),
                    ],
                });
        }

        retired?.Dispose();
    }

    /// <summary>
    /// Sends one action cue's message, or explains why it could not.
    /// </summary>
    /// <returns>Null on success; the reason otherwise.</returns>
    public string? Send(ActionCueNode cue, ActionEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(cue);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (MidiActions.TryParse(cue.Address, cue.Arguments, out var message) is { } wrong)
            return $"“{cue.Label}” - {wrong}";

        ControlSystemMIDIDeviceSessionManager? sender;

        lock (_gate)
            sender = _sender;

        if (sender is null)
            return $"“{endpoint.Name}” is not among this show's MIDI endpoints";

        try
        {
            // Fire-and-forget on purpose: every one of these completes synchronously (the manager
            // writes to the port and returns a completed task), and awaiting inside a cue fire would
            // put the transport behind a native write.
            _ = message.Kind switch
            {
                MidiActionKind.ControlChange => sender.SendControlChangeAsync(
                    endpoint.Id, message.Channel, message.Number, message.Value,
                    highResolution14Bit: false),
                MidiActionKind.ProgramChange => sender.SendProgramChangeAsync(
                    endpoint.Id, message.Channel, message.Number),
                _ => sender.SendNoteAsync(
                    endpoint.Id, message.Channel, message.Number, message.Value,
                    isNoteOn: message.Kind == MidiActionKind.NoteOn),
            };

            return null;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A port another application holds, a device that has been unplugged, a name that matches
            // nothing on this machine. All of them are the operator's to fix and none of them may take
            // the show down.
            return $"“{cue.Label}” → {endpoint.Name} failed - {failure.Message}";
        }
    }

    public void Dispose()
    {
        ControlSystemMIDIDeviceSessionManager? sender;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            sender = _sender;
            _sender = null;
        }

        sender?.Dispose();
    }
}
