using S.Media.MIDI.Events;

namespace S.Media.MIDI.Types;

/// <summary>
/// Shared contract for MIDI input and output devices.
/// Both <see cref="S.Media.MIDI.Input.MIDIInput"/> and <see cref="S.Media.MIDI.Output.MIDIOutput"/>
/// implement this interface.
/// </summary>
public interface IMIDIDevice : IDisposable
{
    /// <summary>Device descriptor returned by <see cref="S.Media.MIDI.Runtime.MIDIEngine"/>.</summary>
    MIDIDeviceInfo Device { get; }

    /// <summary><see langword="true"/> while the device port is open and active.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Opens the device port. Safe to call when already open; returns
    /// <see cref="S.Media.Core.Errors.MediaResult.Success"/> without side effects.
    /// </summary>
    int Open();

    /// <summary>
    /// Closes the device port and releases its underlying stream handle.
    /// Safe to call when already closed.
    /// </summary>
    int Close();

    /// <summary>
    /// Raised when the connection status changes. Possible states:
    /// <see cref="MIDIConnectionStatus.Opening"/>, <see cref="MIDIConnectionStatus.Open"/>,
    /// <see cref="MIDIConnectionStatus.Closed"/>, <see cref="MIDIConnectionStatus.Disconnected"/>,
    /// <see cref="MIDIConnectionStatus.Reconnecting"/>, <see cref="MIDIConnectionStatus.ReconnectFailed"/>.
    /// </summary>
    event EventHandler<MIDIConnectionStatusEventArgs>? StatusChanged;
}

