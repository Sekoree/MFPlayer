using S.Control;

namespace HaCue2.Engine;

/// <summary>
/// Where the incoming MIDI timecode says the show is (register item 3, the chase arm).
/// </summary>
/// <remarks>
/// <para>
/// A thin adapter, deliberately. All of the difficulty in reading MTC - nibble assembly, the inherent
/// two-frame read lag, wall-clock interpolation between quarter-frames, stall and relocate detection -
/// is solved in <see cref="MidiTimecodeChaseClock"/>, and re-deriving any of it here would produce a
/// second, worse answer. What belongs in HaCue2 is the part S.Control cannot know: that timecode
/// arrives as monitor records on the same path bindings do, and that the transport chip needs one
/// short line of text out of it.
/// </para>
/// <para>
/// <b>Nothing fires from here.</b> The clock is read, not subscribed to. A cue that fires on a
/// timecode crossing is a scheduler over this position, and it is not built - so the readout says what
/// is arriving and claims nothing more.
/// </para>
/// </remarks>
public sealed class TimecodeChase
{
    private readonly MidiTimecodeChaseClock _clock = new();

    /// <summary>
    /// Feeds one arrived record, if it is timecode.
    /// </summary>
    /// <returns>Whether the record was timecode and was consumed.</returns>
    /// <remarks>
    /// Runs on the MIDI I/O thread, like everything else on this path, so it does no more than hand
    /// the bytes over: the chase clock does its own locking and a poll that blocks drops messages.
    /// </remarks>
    public bool Feed(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.RawBytes is not { Length: > 0 } bytes)
            return false;

        // Quarter-frames are the running case: eight of them carry one timecode, four times a frame.
        if (bytes[0] == 0xF1 && bytes.Length >= 2)
        {
            _clock.FeedQuarterFrame(bytes[1]);
            return true;
        }

        // A full-frame locate is SysEx, and it is how a deck says "I am parked here" - the decoder
        // refuses anything that is not one, so this can be handed every SysEx that arrives.
        if (bytes[0] == 0xF0)
            return _clock.FeedFullFrame(bytes) is not null;

        return false;
    }

    /// <summary>The interpolated position, for the UI sweep.</summary>
    public MidiTimecodeChaseState Read() => _clock.Read();

    /// <summary>
    /// Forgets the incoming stream.
    /// </summary>
    /// <remarks>
    /// Called when the ports close. Without it the chip would keep showing the last label a
    /// disconnected sender happened to reach, which reads as a live chase over a dead cable.
    /// </remarks>
    public void Reset() => _clock.Reset();
}
