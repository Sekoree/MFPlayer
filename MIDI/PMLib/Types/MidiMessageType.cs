namespace PMLib.Types;

/// <summary>
/// Identifies the MIDI message type.
/// The underlying value matches the MIDI status byte for system messages, and the
/// upper-nibble status byte (without channel) for channel voice messages.
/// </summary>
public enum MidiMessageType : byte
{
    // ── Channel voice ─────────────────────────────────────────────────────────
    NoteOff              = 0x80,
    NoteOn               = 0x90,
    PolyphonicAftertouch = 0xA0,
    ControlChange        = 0xB0,
    ProgramChange        = 0xC0,
    ChannelAftertouch    = 0xD0,
    PitchBend            = 0xE0,

    // ── System common ─────────────────────────────────────────────────────────
    SysEx                = 0xF0,
    MidiTimeCode         = 0xF1,
    SongPosition         = 0xF2,
    SongSelect           = 0xF3,
    TuneRequest          = 0xF6,

    // ── System real-time ──────────────────────────────────────────────────────
    TimingClock          = 0xF8,
    Start                = 0xFA,
    Continue             = 0xFB,
    Stop                 = 0xFC,
    ActiveSensing        = 0xFE,
    Reset                = 0xFF,
}

