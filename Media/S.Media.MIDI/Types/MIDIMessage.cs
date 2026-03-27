using PMLib.Types;

namespace S.Media.MIDI.Types;

public readonly record struct MIDIMessage(
    uint RawMessage,
    int Timestamp,
    byte Status,
    byte Data1,
    byte Data2)
{
    public static MIDIMessage FromPmEvent(PmEvent value)
    {
        return new MIDIMessage(
            RawMessage: value.Message,
            Timestamp: value.Timestamp,
            Status: PmEvent.GetStatus(value.Message),
            Data1: PmEvent.GetData1(value.Message),
            Data2: PmEvent.GetData2(value.Message));
    }

    public static MIDIMessage Create(byte status, byte data1, byte data2, int timestamp = 0)
    {
        return new MIDIMessage(
            RawMessage: PmEvent.CreateMessage(status, data1, data2),
            Timestamp: timestamp,
            Status: status,
            Data1: data1,
            Data2: data2);
    }
}
