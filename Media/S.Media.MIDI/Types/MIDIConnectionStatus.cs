namespace S.Media.MIDI.Types;

public enum MIDIConnectionStatus
{
    Closed = 0,
    Opening = 1,
    Open = 2,
    Disconnected = 3,
    Reconnecting = 4,
    ReconnectFailed = 5,
}

