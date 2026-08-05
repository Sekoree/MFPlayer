using System.Globalization;
using S.Control;

namespace HaCue2.Engine;

/// <summary>One thing that physically arrived, reduced to what a binding can be matched against.</summary>
/// <param name="Address">The OSC address, or empty for MIDI.</param>
/// <param name="Value">The continuous value (a CC, a pitch bend, an OSC float), or null.</param>
public readonly record struct TriggerSignal(
    Guid SourceId,
    bool IsMidi,
    string Address,
    ControlMIDIMessageType MidiType,
    int Channel,
    int Number,
    double? Value,
    bool IsKeyboard = false)
{
    /// <summary>How the wire monitor shows it — the same text a binding is written in.</summary>
    public string Describe() => IsKeyboard
        ? Address
        : IsMidi
        ? MidiType switch
        {
            ControlMIDIMessageType.NoteOn => $"note {Number} ch {Channel}",
            ControlMIDIMessageType.NoteOff => $"note off {Number} ch {Channel}",
            ControlMIDIMessageType.ControlChange => $"cc {Number} ch {Channel}",
            ControlMIDIMessageType.ProgramChange => $"program {Number} ch {Channel}",
            _ => $"{MidiType.ToString().ToLowerInvariant()} {Number} ch {Channel}",
        }
        : Address;
}

/// <summary>
/// Matching what arrived against what a binding says it wants.
/// </summary>
/// <remarks>
/// <para>
/// Pure and separate from the device layer on purpose: this is the part with all the edge cases and
/// none of the hardware, so it can be tested exhaustively without a MIDI port. The device half is
/// <c>S.Control</c>'s, which already solved discovery, matching and hot-plug.
/// </para>
/// <para>
/// A binding's <c>Input</c> is written the way the wire monitor prints it — "note 3 ch 1",
/// "cc 7 ch 1", "/hacue/go". That is deliberate: an operator watching the monitor can copy what they
/// see into a binding and have it work, which is what makes learn-by-hand possible before a Learn
/// button exists.
/// </para>
/// </remarks>
public static class TriggerMatching
{
    /// <summary>Keyboard gestures are formatting-insensitive but otherwise exact.</summary>
    public static bool MatchesKeyboard(string gesture, string pattern) =>
        string.Equals(NormalizeKeyboard(gesture), NormalizeKeyboard(pattern), StringComparison.OrdinalIgnoreCase)
        && NormalizeKeyboard(gesture).Length > 0;

    private static string NormalizeKeyboard(string text) =>
        string.Join('+', (text ?? "")
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "Ctrl" : part));

    /// <summary>
    /// Whether a signal satisfies a binding's input pattern.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An omitted channel matches any channel.</b> "note 3" fires on note 3 from anywhere, which is
    /// what somebody means when they do not mention one; "note 3 ch 1" is the narrower form. Requiring
    /// a channel would make the commonest binding the fiddliest to write.
    /// </para>
    /// <para>
    /// <b>Note-off never matches a note-on pattern.</b> Every note produces both, so a binding written
    /// as "note 3" that fired on release as well would fire every cue twice.
    /// </para>
    /// <para>
    /// OSC matches on address, with a trailing <c>*</c> allowed — "/hacue/go*" catches "/hacue/go/2".
    /// Case-sensitive, because OSC addresses are.
    /// </para>
    /// </remarks>
    public static bool Matches(TriggerSignal signal, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        pattern = pattern.Trim();

        return signal.IsMidi ? MatchesMidi(signal, pattern) : MatchesOsc(signal.Address, pattern);
    }

    private static bool MatchesOsc(string address, string pattern)
    {
        if (address.Length == 0)
            return false;

        if (pattern.EndsWith('*'))
            return address.StartsWith(pattern[..^1], StringComparison.Ordinal);

        return string.Equals(address, pattern, StringComparison.Ordinal);
    }

    private static bool MatchesMidi(TriggerSignal signal, string pattern)
    {
        var words = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
            return false;

        var kind = words[0].ToLowerInvariant();

        // "note off 3 ch 1" — two words for the kind. Checked before the single-word forms so "note"
        // does not swallow it.
        var offset = 1;

        if (kind == "note" && words.Length > 1 && words[1].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            kind = "noteoff";
            offset = 2;
        }

        var wanted = kind switch
        {
            "note" => ControlMIDIMessageType.NoteOn,
            "noteoff" => ControlMIDIMessageType.NoteOff,
            "cc" or "control" => ControlMIDIMessageType.ControlChange,
            "program" or "pc" => ControlMIDIMessageType.ProgramChange,
            "bend" or "pitchbend" => ControlMIDIMessageType.PitchBend,
            _ => ControlMIDIMessageType.Unknown,
        };

        if (wanted == ControlMIDIMessageType.Unknown || signal.MidiType != wanted)
            return false;

        // Pitch bend has no number of its own, so a bare "bend ch 1" is the whole pattern.
        if (offset < words.Length && int.TryParse(
                words[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            if (number != signal.Number)
                return false;

            offset++;
        }

        // "ch N", or nothing — and nothing means any channel.
        if (offset + 1 < words.Length
            && words[offset].Equals("ch", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(words[offset + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
            return channel == signal.Channel;

        return true;
    }

    /// <summary>
    /// A signal's continuous value scaled into a binding's range.
    /// </summary>
    /// <remarks>
    /// MIDI arrives 0–127 (or 0–16383 for a bend); a parameter wants the operator's own range. Null
    /// when the signal carries no value, which is how a note-on binding to a parameter is refused
    /// rather than writing an arbitrary number into a master trim.
    /// </remarks>
    public static double? Scale(TriggerSignal signal, double rangeMin, double rangeMax)
    {
        if (signal.Value is not { } value)
            return null;

        var full = signal.MidiType == ControlMIDIMessageType.PitchBend ? 16_383d : 127d;
        var normalized = signal.IsMidi ? Math.Clamp(value / full, 0, 1) : Math.Clamp(value, 0, 1);

        return rangeMin + ((rangeMax - rangeMin) * normalized);
    }

    /// <summary>Turns what S.Control observed into the shape a binding is matched against.</summary>
    /// <remarks>
    /// Returns null for anything that is not an inbound message a trigger could act on — an outbound
    /// record, or one carrying no address and no MIDI shape. Feeding those to the matcher would make
    /// every binding's behaviour depend on what the app itself had just sent.
    /// </remarks>
    public static TriggerSignal? Read(ControlMonitorRecord record, Guid sourceId)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Direction != ControlMonitorDirection.Input)
            return null;

        if (record.Protocol == ControlMonitorProtocol.MIDI)
        {
            if (record.MIDIMessageType is not { } type)
                return null;

            return new TriggerSignal(
                sourceId,
                IsMidi: true,
                Address: "",
                MidiType: type,
                Channel: record.MIDIChannel ?? 0,
                Number: record.MIDINote ?? record.MIDIController ?? record.MIDIParameter ?? 0,
                Value: record.MIDIValue);
        }

        if (record.Address is not { Length: > 0 } address)
            return null;

        // The first numeric argument is the value; an OSC trigger with no arguments is a bang, which
        // is exactly what a cue binding wants and what a parameter binding must refuse.
        var value = record.OSCArguments
            .Select(argument => argument.FloatValue ?? argument.IntegerValue)
            .FirstOrDefault(number => number is not null);

        return new TriggerSignal(
            sourceId,
            IsMidi: false,
            Address: address,
            MidiType: ControlMIDIMessageType.Unknown,
            Channel: 0,
            Number: 0,
            Value: value);
    }
}
