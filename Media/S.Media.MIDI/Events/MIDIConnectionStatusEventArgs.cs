using S.Media.MIDI.Types;

namespace S.Media.MIDI.Events;

public sealed class MIDIConnectionStatusEventArgs : EventArgs
{
    public MIDIConnectionStatusEventArgs(MIDIConnectionStatus status, MIDIDeviceInfo device, DateTimeOffset changedAtUtc, int? errorCode = null)
    {
        Status = status;
        Device = device;
        ChangedAtUtc = changedAtUtc;
        ErrorCode = errorCode;
    }

    public MIDIConnectionStatus Status { get; }

    public MIDIDeviceInfo Device { get; }

    public DateTimeOffset ChangedAtUtc { get; }

    public int? ErrorCode { get; }
}
