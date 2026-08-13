using System.Globalization;

namespace HaCue2.Core.Model;

/// <summary>What kind of MIDI message an action cue sends.</summary>
public enum MidiActionKind
{
    NoteOn,
    NoteOff,
    ControlChange,
    ProgramChange,
}

/// <summary>
/// One MIDI message an action cue sends, parsed from what the operator typed.
/// </summary>
/// <param name="Channel">1–16, as every desk in the world numbers them.</param>
public readonly record struct MidiAction(
    MidiActionKind Kind,
    int Channel,
    int Number,
    int Value);

/// <summary>
/// Reads an action cue's address and arguments as a MIDI message.
/// </summary>
/// <remarks>
/// <para>
/// <b>In <c>HaCue2.Core</c> on purpose.</b> The engine sends these, but the STATUS PASS has to be able
/// to tell an operator that Q42 will not send anything - before the show, on a laptop, with no MIDI
/// interface anywhere near it. A parser that lived in the engine could only report the failure at the
/// moment the cue was fired, which is the one moment nobody can act on it.
/// </para>
/// <para>
/// <b>Address and arguments are read as one stream of tokens.</b> The two boxes exist because OSC has
/// an address and arguments; MIDI has neither, and insisting on a split would make the operator guess
/// which half a channel number belongs in. So <c>cc 1 7</c> + <c>100</c> and <c>cc 1 7 100</c> + <c></c>
/// are the same message, which is what somebody filling in two boxes would expect.
/// </para>
/// <para>
/// Channels are 1–16. The wire is 0–15 and the conversion belongs at the wire, not here: an operator
/// reading "channel 1" off the back of a desk must be able to type 1.
/// </para>
/// </remarks>
public static class MidiActions
{
    /// <summary>The forms the address may take, for the inspector's hint and the error message.</summary>
    public const string Syntax = "note <ch> <note> [vel] · noteoff <ch> <note> · cc <ch> <cc> <value> · pc <ch> <program>";

    /// <summary>
    /// Parses a message, or explains what is wrong with it.
    /// </summary>
    /// <param name="message">The parsed message, when this returns null.</param>
    /// <returns>Null when it parsed; the reason otherwise.</returns>
    public static string? TryParse(string address, string arguments, out MidiAction message)
    {
        message = default;

        var tokens = $"{address} {arguments}"
            .Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
            return $"no message to send - {Syntax}";

        if (Kind(tokens[0]) is not { } kind)
            return $"“{tokens[0]}” is not a MIDI message - {Syntax}";

        // A program change carries no value, so it needs one fewer number than the others.
        var wanted = kind == MidiActionKind.ProgramChange ? 2 : 3;
        var numbers = new int[wanted];

        for (var index = 0; index < wanted; index++)
        {
            // The velocity of a note is the ONE number that may be left off: "note 1 60" is a
            // full-velocity press, which is what a cue firing a note almost always means.
            if (index + 1 >= tokens.Length)
            {
                if (kind == MidiActionKind.NoteOn && index == 2)
                {
                    numbers[2] = 127;
                    break;
                }

                if (kind == MidiActionKind.NoteOff && index == 2)
                {
                    numbers[2] = 0;
                    break;
                }

                return $"“{$"{address} {arguments}".Trim()}” is missing a number - {Syntax}";
            }

            if (!int.TryParse(
                    tokens[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return $"“{tokens[index + 1]}” is not a number - {Syntax}";

            numbers[index] = value;
        }

        if (numbers[0] is < 1 or > 16)
            return $"channel {numbers[0]} is outside 1–16";

        if (numbers[1] is < 0 or > 127)
            return $"{numbers[1]} is outside 0–127";

        if (wanted == 3 && numbers[2] is < 0 or > 127)
            return $"{numbers[2]} is outside 0–127";

        message = new MidiAction(
            kind, numbers[0], numbers[1], wanted == 3 ? numbers[2] : 0);

        return null;
    }

    private static MidiActionKind? Kind(string token) => token.ToLowerInvariant() switch
    {
        "note" or "noteon" or "on" => MidiActionKind.NoteOn,
        "noteoff" or "off" => MidiActionKind.NoteOff,
        "cc" or "control" or "controlchange" => MidiActionKind.ControlChange,
        "pc" or "program" or "programchange" => MidiActionKind.ProgramChange,
        _ => null,
    };
}
