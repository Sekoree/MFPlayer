namespace S.Media.MIDI.Types;

public readonly record struct MIDIDeviceInfo(
    int DeviceId,
    string Name,
    bool IsInput,
    bool IsOutput,
    bool IsNative = true);

