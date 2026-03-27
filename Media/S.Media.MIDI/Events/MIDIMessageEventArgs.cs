using S.Media.MIDI.Types;

namespace S.Media.MIDI.Events;

public sealed class MIDIMessageEventArgs : EventArgs
{
    public MIDIMessageEventArgs(MIDIMessage message, MIDIDeviceInfo sourceDevice, DateTimeOffset receivedAtUtc, long? backendTimestamp)
    {
        Message = message;
        SourceDevice = sourceDevice;
        ReceivedAtUtc = receivedAtUtc;
        BackendTimestamp = backendTimestamp;
    }

    public MIDIMessage Message { get; }

    public MIDIDeviceInfo SourceDevice { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public long? BackendTimestamp { get; }
}
