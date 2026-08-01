using System.Diagnostics;
using System.Globalization;

namespace S.Control;

/// <summary>
/// MIDI Time Code frame rate. The values are the WIRE encoding of the rate bits carried in
/// quarter-frame piece 7 (and in the full-frame hour byte), so <c>(MidiTimecodeRate)((nibble &gt;&gt; 1) &amp; 3)</c>
/// is a direct cast.
/// </summary>
public enum MidiTimecodeRate
{
    /// <summary>24 fps (film).</summary>
    Fps24 = 0,

    /// <summary>25 fps (EBU / PAL).</summary>
    Fps25 = 1,

    /// <summary>29.97 fps drop-frame (NTSC). Frame NUMBERS run at 30/s, but frames 00 and 01 of every
    /// minute except every tenth are skipped, so the label tracks real time to ~1 frame per 10 min.</summary>
    Fps2997Drop = 2,

    /// <summary>30 fps non-drop.</summary>
    Fps30 = 3,
}

/// <summary>Frame-rate arithmetic shared by the decoder, the chase clock and callers converting an
/// authored <c>hh:mm:ss:ff</c> target into seconds.</summary>
public static class MidiTimecodeRates
{
    /// <summary>Frames per labelled second - the modulus of the <c>ff</c> field. 29.97 drop-frame
    /// COUNTS at 30 (it drops labels, not frames), so this is 30 for it too.</summary>
    public static int FramesPerSecond(MidiTimecodeRate rate) => rate switch
    {
        MidiTimecodeRate.Fps24 => 24,
        MidiTimecodeRate.Fps25 => 25,
        _ => 30,
    };

    /// <summary>Real-time duration of one frame. Only 29.97 drop-frame is not the reciprocal of
    /// <see cref="FramesPerSecond"/> - its frames are 1001/30000 s long.</summary>
    public static double SecondsPerFrame(MidiTimecodeRate rate) => rate switch
    {
        MidiTimecodeRate.Fps24 => 1.0 / 24.0,
        MidiTimecodeRate.Fps25 => 1.0 / 25.0,
        MidiTimecodeRate.Fps2997Drop => 1001.0 / 30000.0,
        _ => 1.0 / 30.0,
    };

    /// <summary>Frame numbers in one full 24-hour wrap of the label space (the wire's 5-bit hour field).
    /// 29.97 drop-frame is 2 589 408 - 107 892 per hour, not 108 000 - so it needs the same
    /// drop-aware arithmetic <see cref="MidiTimecodeValue.FrameNumber"/> uses.</summary>
    public static long FramesPerDay(MidiTimecodeRate rate) => rate switch
    {
        MidiTimecodeRate.Fps24 => 24L * 86_400,
        MidiTimecodeRate.Fps25 => 25L * 86_400,
        MidiTimecodeRate.Fps2997Drop => 2_589_408,
        _ => 30L * 86_400,
    };

    /// <summary>Short numeric label for operator display ("25", "29.97 DF"). Not translatable prose -
    /// frame rates are numbers everywhere in the industry.</summary>
    public static string Label(MidiTimecodeRate rate) => rate switch
    {
        MidiTimecodeRate.Fps24 => "24",
        MidiTimecodeRate.Fps25 => "25",
        MidiTimecodeRate.Fps2997Drop => "29.97 DF",
        _ => "30",
    };
}

/// <summary>
/// One <c>hh:mm:ss:ff</c> timecode label plus the rate its frame field is counted in. A value type so
/// the chase clock can hand a snapshot to the UI thread without allocating.
/// </summary>
public readonly record struct MidiTimecodeValue(
    int Hours,
    int Minutes,
    int Seconds,
    int Frames,
    MidiTimecodeRate Rate)
{
    /// <summary>Absolute frame number counted from 00:00:00:00 - drop-frame aware, so it is a dense,
    /// monotone index even across the minute boundaries whose 00/01 labels do not exist.</summary>
    public long FrameNumber
    {
        get
        {
            var totalMinutes = (long)Hours * 60 + Minutes;
            if (Rate != MidiTimecodeRate.Fps2997Drop)
                return ((totalMinutes * 60) + Seconds) * MidiTimecodeRates.FramesPerSecond(Rate) + Frames;
            // Drop-frame: two labels are skipped at the top of every minute except every tenth.
            return ((totalMinutes * 60) + Seconds) * 30 + Frames - 2 * (totalMinutes - totalMinutes / 10);
        }
    }

    /// <summary>Real-time position of this label, in seconds from 00:00:00:00.</summary>
    public double TotalSeconds => FrameNumber * MidiTimecodeRates.SecondsPerFrame(Rate);

    /// <summary>Rebuilds a label from an absolute frame number (the inverse of <see cref="FrameNumber"/>).
    /// Hours wrap at 24, matching the wire format's 5-bit hour field.</summary>
    public static MidiTimecodeValue FromFrameNumber(long frameNumber, MidiTimecodeRate rate)
    {
        if (frameNumber < 0)
            frameNumber = 0;

        if (rate == MidiTimecodeRate.Fps2997Drop)
        {
            // Re-insert the dropped labels: 18 per 10-minute block, then 2 per whole minute inside it.
            const long framesPerTenMinutes = 17982;
            const long framesPerMinute = 1798;
            var blocks = frameNumber / framesPerTenMinutes;
            var withinBlock = frameNumber % framesPerTenMinutes;
            var reinserted = 18 * blocks + (withinBlock < 2 ? 0 : 2 * ((withinBlock - 2) / framesPerMinute));
            frameNumber += reinserted;
            return new MidiTimecodeValue(
                (int)(frameNumber / 108000 % 24),
                (int)(frameNumber / 1800 % 60),
                (int)(frameNumber / 30 % 60),
                (int)(frameNumber % 30),
                rate);
        }

        long fps = MidiTimecodeRates.FramesPerSecond(rate);
        return new MidiTimecodeValue(
            (int)(frameNumber / (fps * 3600) % 24),
            (int)(frameNumber / (fps * 60) % 60),
            (int)(frameNumber / fps % 60),
            (int)(frameNumber % fps),
            rate);
    }

    /// <summary>Rebuilds a label from a real-time position by selecting the frame containing that instant
    /// (floor, so an interpolated chase display never shows a frame the sender has not reached yet).</summary>
    public static MidiTimecodeValue FromSeconds(double seconds, MidiTimecodeRate rate)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        var frames = (long)Math.Floor(seconds / MidiTimecodeRates.SecondsPerFrame(rate) + 1e-6);
        return FromFrameNumber(frames, rate);
    }

    /// <summary>True when every field is inside the range its rate allows.</summary>
    public bool IsValid =>
        Rate is >= MidiTimecodeRate.Fps24 and <= MidiTimecodeRate.Fps30
        && Hours is >= 0 and < 24
        && Minutes is >= 0 and < 60
        && Seconds is >= 0 and < 60
        && Frames >= 0 && Frames < MidiTimecodeRates.FramesPerSecond(Rate)
        // 29.97 DF omits labels :00 and :01 at the top of every minute except each tenth.
        // Accepting one maps it onto a frame that already has a valid label in the prior second, so an
        // authored schedule can fire at a different time than the operator entered.
        && (Rate != MidiTimecodeRate.Fps2997Drop
            || Seconds != 0
            || Minutes % 10 == 0
            || Frames >= 2);

    /// <summary>Parses <c>hh:mm:ss:ff</c> (';' and '.' are also accepted before the frame field, the
    /// usual drop-frame notations). Returns false on anything out of range for <paramref name="rate"/>,
    /// so the caller can leave the operator's last valid text in place.</summary>
    public static bool TryParse(string? text, MidiTimecodeRate rate, out MidiTimecodeValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        Span<int> fields = stackalloc int[4];
        var field = 0;
        var digits = 0;
        var current = 0;
        foreach (var c in text.AsSpan().Trim())
        {
            if (char.IsAsciiDigit(c))
            {
                if (++digits > 2)
                    return false;
                current = current * 10 + (c - '0');
                continue;
            }

            if (c is not (':' or ';' or '.'))
                return false;
            if (digits == 0 || field >= 3)
                return false;
            fields[field++] = current;
            current = 0;
            digits = 0;
        }

        if (field != 3 || digits == 0)
            return false;
        fields[3] = current;

        var candidate = new MidiTimecodeValue(fields[0], fields[1], fields[2], fields[3], rate);
        if (!candidate.IsValid)
            return false;
        value = candidate;
        return true;
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}:{Frames:D2}");
}

/// <summary>How an assembled timecode relates to the one before it.</summary>
public enum MidiTimecodeUpdateKind
{
    /// <summary>The assembled value matches where the previous anchor predicted the sender would be -
    /// the normal free-running case.</summary>
    Continued,

    /// <summary>First lock, a rate change, or a resume after the sender was gone long enough that the
    /// prediction no longer holds. A new chase run.</summary>
    Resynced,

    /// <summary>The sender relocated: the assembled value is discontinuous with the prediction.</summary>
    Jumped,

    /// <summary>A full-frame SysEx locate. Always a new run, and the sender is PARKED (a full frame is
    /// what a deck emits while locating/stopped, not while rolling).</summary>
    Located,
}

/// <summary>One decoded timecode, with the instant it describes.</summary>
/// <param name="Kind">Continuity relative to the previous update.</param>
/// <param name="Timecode">The assembled label.</param>
/// <param name="TimestampTicks">The caller-supplied tick at which the sender was AT
/// <paramref name="Timecode"/> - for quarter-frames this is the timestamp of piece 0, not of the
/// piece that completed the assembly (see <see cref="MidiTimecodeDecoder"/>).</param>
/// <param name="IsRunning">False for a full-frame locate (parked sender), true for quarter-frames.</param>
public readonly record struct MidiTimecodeUpdate(
    MidiTimecodeUpdateKind Kind,
    MidiTimecodeValue Timecode,
    long TimestampTicks,
    bool IsRunning);

/// <summary>
/// Pure MIDI Time Code decoder: byte sequences in, assembled timecodes out. No timers, no clock of its
/// own - every entry point takes the tick at which the message arrived, so the whole thing is
/// deterministically unit-testable. Not thread-safe; <see cref="MidiTimecodeChaseClock"/> owns the
/// locking.
/// </summary>
/// <remarks>
/// <para><strong>Quarter-frames (status 0xF1).</strong> Eight messages carry one timecode, one nibble
/// each: data byte = <c>piece &lt;&lt; 4 | nibble</c> with piece 0..7 = frame LSN, frame MSN, second LSN,
/// second MSN, minute LSN, minute MSN, hour LSN, then hour MSN + the 2 rate bits. They are emitted 4×
/// per frame, so one full timecode spans exactly 2 frames.</para>
/// <para><strong>The inherent 2-frame lag.</strong> Because the assembly only completes on piece 7, the
/// value is already <em>2 frames stale by the time it can be read</em> - the classic MTC receiver
/// caveat. Rather than paper over it with the customary "+2 frames" fudge, the decoder reports the
/// value against the timestamp of piece 0 (<see cref="MidiTimecodeUpdate.TimestampTicks"/>): that IS the
/// instant the sender was at that label. <see cref="MidiTimecodeChaseClock"/> then interpolates forward
/// from that anchor with wall time, so the position it reports is current instead of 2 frames behind.
/// The lag never disappears - it becomes prediction error rather than constant offset.</para>
/// <para><strong>Sequence discipline.</strong> Pieces must arrive 0,1,…,7. Anything else drops the
/// partial assembly and waits for the next piece 0, so dropped or duplicated messages cost one timecode
/// (2 frames) rather than producing a corrupt label. Reverse (descending) chase is deliberately NOT
/// decoded - it resyncs on each piece 0 instead, which is a stall from the scheduler's point of view.</para>
/// <para><strong>Nibble splices, and why sequence discipline alone is not enough.</strong> A dropout of
/// exactly a WHOLE MULTIPLE OF 8 quarter-frames resumes on the piece index the assembler is waiting for,
/// so the ordering check passes and pieces 0..3 of one window (frame + second nibbles) get merged with
/// pieces 4..7 of a later one (minute + hour nibbles). Within a minute that splice is harmless - the
/// low half already IS the label the window started on, and the update is stamped with that window's
/// piece 0 - but across a minute (or hour) rollover the merged label is a whole minute wrong.
/// <br/>A time bound on the window cannot separate the two cases: an 8-message dropout (~83 ms) and a
/// poll thread starved mid-window look identical on arrival timestamps. What DOES separate them is the
/// splice's arithmetic fingerprint. Because the low half is the true label at the window start, the
/// merged label is exactly the wall-time PREDICTION shifted by a whole number of minutes - never by an
/// arbitrary amount, and never backwards. So an assembled label that is discontinuous with the previous
/// anchor by (within tolerance) a positive whole number of minutes is treated as UNCONFIRMED: it is not
/// reported at all, it does not move the anchor, and it is only believed if the NEXT independent
/// assembly lands where it predicts (which a genuine relocate does within one timecode, and a spliced
/// label never does). A relocate that happens to trip the fingerprint therefore costs 2 frames of
/// reporting latency; a splice costs nothing and never escapes.
/// <br/>Residual case, deliberately accepted: a splice in the very FIRST assembly after a reset has no
/// anchor to be inconsistent with, so it is reported. It self-corrects on the next assembly (which
/// fails the prediction and re-anchors with a new generation), i.e. it costs one 2-frame window rather
/// than a persistently wrong label.</para>
/// <para><strong>Full frames (SysEx).</strong> <c>F0 7F &lt;dev&gt; 01 01 hh mm ss ff F7</c> - the locate
/// message, whose hour byte packs the rate as <c>0rrhhhhh</c>. Always reported as
/// <see cref="MidiTimecodeUpdateKind.Located"/> with <c>IsRunning=false</c>.</para>
/// </remarks>
public sealed class MidiTimecodeDecoder
{
    /// <summary>Quarter-frame messages per complete timecode (= 2 frames of sender time).</summary>
    public const int QuarterFramesPerTimecode = 8;

    private const byte QuarterFrameStatus = 0xF1;
    private const byte SysExStart = 0xF0;
    private const byte SysExEnd = 0xF7;
    private const byte UniversalRealtimeId = 0x7F;
    private const byte MidiTimecodeSubId1 = 0x01;
    private const byte FullMessageSubId2 = 0x01;

    /// <summary>Bytes of a well-formed full-frame message, start and end status included.</summary>
    public const int FullFrameLength = 10;

    private readonly long _ticksPerSecond;
    private readonly int[] _nibbles = new int[QuarterFramesPerTimecode];

    /// <summary>Piece index expected next; 0 means "no partial assembly, waiting for a fresh piece 0".</summary>
    private int _expectedPiece;
    private long _windowStartTicks;

    private bool _haveAnchor;
    private long _anchorTicks;
    private double _anchorSeconds;
    private MidiTimecodeRate _anchorRate;

    // An assembled label that carried the nibble-splice fingerprint: held unreported until a second,
    // independent assembly corroborates it (see the splice note in the class remarks).
    private bool _havePendingJump;
    private long _pendingJumpTicks;
    private double _pendingJumpSeconds;
    private MidiTimecodeRate _pendingJumpRate;

    /// <param name="ticksPerSecond">Frequency of the tick domain the caller timestamps with.
    /// Defaults to <see cref="Stopwatch.Frequency"/>.</param>
    public MidiTimecodeDecoder(long ticksPerSecond = 0) =>
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency;

    /// <summary>True when the last quarter-frame fed EXTENDED an in-order assembly - it landed on the
    /// piece index the assembler was waiting for (1..7) - rather than merely (re)starting a window on a
    /// piece 0 or being discarded as out of order.
    /// <para>This is the only evidence a receiver has that the sender is moving FORWARD at real time
    /// between assemblies, and <see cref="MidiTimecodeChaseClock"/> uses it as its liveness signal. Mere
    /// arrival proves nothing: a reverse/shuttle chase emits pieces 7..0, and every one of its piece 0s
    /// is "accepted" (a fresh window always restarts cleanly) while nothing after it ever fits - so a
    /// liveness stamp on arrival kept the clock "chasing" and let it extrapolate the position forward
    /// while the deck ran backwards.</para></summary>
    public bool LastQuarterFrameExtendedAssembly { get; private set; }

    /// <summary>True when <paramref name="message"/> is a MIDI Time Code full-frame SysEx. Cheap enough
    /// to run on the I/O thread as a filter before deciding to decode.</summary>
    public static bool IsFullFrame(ReadOnlySpan<byte> message) =>
        message.Length == FullFrameLength
        && message[0] == SysExStart
        && message[1] == UniversalRealtimeId
        && message[3] == MidiTimecodeSubId1
        && message[4] == FullMessageSubId2
        && message[9] == SysExEnd;

    /// <summary>Drops any partial assembly and the interpolation anchor (device closed, chase disabled).</summary>
    public void Reset()
    {
        _expectedPiece = 0;
        _haveAnchor = false;
        _anchorSeconds = 0;
        _anchorTicks = 0;
        _anchorRate = default;
        _havePendingJump = false;
        LastQuarterFrameExtendedAssembly = false;
    }

    /// <summary>Feeds one quarter-frame DATA byte (the byte after the 0xF1 status). Returns an update
    /// only on the message that completes an assembly - i.e. once per 2 frames.</summary>
    public MidiTimecodeUpdate? FeedQuarterFrame(byte dataByte, long timestampTicks)
    {
        var piece = (dataByte >> 4) & 0x07;
        var nibble = dataByte & 0x0F;
        LastQuarterFrameExtendedAssembly = false;

        if (piece == 0)
        {
            // A fresh window always restarts cleanly, whatever state the previous one was left in.
            _nibbles[0] = nibble;
            _expectedPiece = 1;
            _windowStartTicks = timestampTicks;
            return null;
        }

        if (_expectedPiece != piece)
        {
            // Out of order (dropped/duplicated/reverse): abandon the partial and wait for a piece 0.
            _expectedPiece = 0;
            return null;
        }

        LastQuarterFrameExtendedAssembly = true;
        _nibbles[piece] = nibble;
        _expectedPiece = piece + 1;
        if (piece != QuarterFramesPerTimecode - 1)
            return null;

        _expectedPiece = 0;
        var rate = (MidiTimecodeRate)((_nibbles[7] >> 1) & 0x03);
        var candidate = new MidiTimecodeValue(
            Hours: _nibbles[6] | ((_nibbles[7] & 0x01) << 4),
            Minutes: _nibbles[4] | ((_nibbles[5] & 0x03) << 4),
            Seconds: _nibbles[2] | ((_nibbles[3] & 0x03) << 4),
            Frames: _nibbles[0] | ((_nibbles[1] & 0x01) << 4),
            Rate: rate);
        if (!candidate.IsValid)
            return null;

        // The assembled label describes the instant piece 0 arrived, NOT now: see the 2-frame lag note.
        return CommitQuarterFrame(candidate, _windowStartTicks);
    }

    /// <summary>Feeds a complete full-frame SysEx (<c>F0 7F dev 01 01 hh mm ss ff F7</c>). Returns null
    /// when the message is not a well-formed full frame.</summary>
    public MidiTimecodeUpdate? FeedFullFrame(ReadOnlySpan<byte> message, long timestampTicks)
    {
        if (!IsFullFrame(message))
            return null;

        var hourByte = message[5];
        var candidate = SnapDropFrame(new MidiTimecodeValue(
            Hours: hourByte & 0x1F,
            Minutes: message[6] & 0x7F,
            Seconds: message[7] & 0x7F,
            Frames: message[8] & 0x7F,
            Rate: (MidiTimecodeRate)((hourByte >> 5) & 0x03)));
        if (!candidate.IsValid)
            return null;

        // A locate invalidates any partial quarter-frame window in flight - and any splice candidate
        // waiting for corroboration, since the locate re-anchors everything by itself.
        _expectedPiece = 0;
        _havePendingJump = false;
        return Anchor(
            candidate, candidate.TotalSeconds, timestampTicks,
            running: false, MidiTimecodeUpdateKind.Located);
    }

    /// <summary>Convenience entry for callers holding raw wire bytes: dispatches on the status byte
    /// (0xF1 quarter-frame, 0xF0 SysEx). Anything else returns null.</summary>
    public MidiTimecodeUpdate? Feed(ReadOnlySpan<byte> bytes, long timestampTicks)
    {
        if (bytes.Length == 2 && bytes[0] == QuarterFrameStatus)
            return FeedQuarterFrame(bytes[1], timestampTicks);
        if (bytes.Length > 0 && bytes[0] == SysExStart)
            return FeedFullFrame(bytes, timestampTicks);
        return null;
    }

    /// <summary>Classifies a freshly assembled quarter-frame label against the previous anchor's
    /// wall-time prediction and re-anchors on it - EXCEPT when the label carries the nibble-splice
    /// fingerprint, in which case it is held for corroboration and nothing is reported (see the splice
    /// note in the class remarks).</summary>
    private MidiTimecodeUpdate? CommitQuarterFrame(MidiTimecodeValue timecode, long timestampTicks)
    {
        var seconds = timecode.TotalSeconds;
        var tolerance = Tolerance(timecode.Rate);

        // A held candidate is believed only once an INDEPENDENT assembly lands where it predicts. A
        // genuine relocate rolls on from its new position and corroborates within one timecode; a
        // spliced label - which describes a position the sender was never at - never does.
        if (_havePendingJump)
        {
            var corroborated =
                _pendingJumpRate == timecode.Rate
                && Math.Abs(seconds - PredictFrom(_pendingJumpSeconds, _pendingJumpTicks, timestampTicks))
                   <= tolerance;
            _havePendingJump = false;
            if (corroborated)
                return Anchor(timecode, seconds, timestampTicks, running: true, MidiTimecodeUpdateKind.Jumped);
            // Not corroborated: the candidate was noise. Fall through and classify THIS label against
            // the still-valid anchor - after a splice the sender has simply rolled on, so it normally
            // lands exactly on the wall prediction and reads as Continued.
        }

        if (!_haveAnchor || _anchorRate != timecode.Rate)
            return Anchor(timecode, seconds, timestampTicks, running: true, MidiTimecodeUpdateKind.Resynced);

        // A free-running sender lands within a frame or two of where wall time says it should be.
        // Anything further is a relocate - or a resume after a stop, which for a scheduler means
        // the same thing: the run before it is over.
        var predicted = PredictFrom(_anchorSeconds, _anchorTicks, timestampTicks);
        var delta = seconds - predicted;
        if (Math.Abs(delta) <= tolerance)
            return Anchor(timecode, seconds, timestampTicks, running: true, MidiTimecodeUpdateKind.Continued);

        if (IsNibbleSpliceFingerprint(timecode, predicted))
        {
            _havePendingJump = true;
            _pendingJumpTicks = timestampTicks;
            _pendingJumpSeconds = seconds;
            _pendingJumpRate = timecode.Rate;
            return null; // unconfirmed - never reported, and the anchor stays where it was
        }

        return Anchor(timecode, seconds, timestampTicks, running: true, MidiTimecodeUpdateKind.Jumped);
    }

    private double PredictFrom(double fromSeconds, long fromTicks, long atTicks) =>
        fromSeconds + ((atTicks - fromTicks) / (double)_ticksPerSecond);

    /// <summary>Snaps the two 29.97 drop-frame labels that do not exist - <c>:00</c> and <c>:01</c> at the
    /// top of every minute except each tenth - onto the first one that does, <c>:02</c>.
    /// <para><see cref="MidiTimecodeValue.IsValid"/> rejects them on purpose: an AUTHORED target must fire
    /// where the operator typed it, and mapping <c>00:01:00:00</c> onto a frame whose real label sits in
    /// the previous second would fire early. A DECODER cannot afford the same strictness. Plenty of senders
    /// emit the nonexistent label on a full-frame LOCATE, and dropping the message discards the whole
    /// event - no park, no generation bump - leaving every consumer on a baseline the sender has left,
    /// which for a crossing consumer means a burst on the next roll. A quarter-frame stream self-heals in
    /// 2 frames; a locate is a one-shot. Both offending labels unambiguously mean "the top of this
    /// minute", so accepting them one frame late beats not accepting them at all.</para></summary>
    private static MidiTimecodeValue SnapDropFrame(MidiTimecodeValue value) =>
        value.Rate == MidiTimecodeRate.Fps2997Drop
        && value.Seconds == 0
        && value.Minutes % 10 != 0
        && value.Frames is >= 0 and < 2
            ? value with { Frames = 2 }
            : value;

    /// <summary>A free-running sender lands within a frame or two of the wall-time prediction.</summary>
    private static double Tolerance(MidiTimecodeRate rate) =>
        Math.Max(0.020, 2.0 * MidiTimecodeRates.SecondsPerFrame(rate));

    /// <summary>The arithmetic signature of a merged assembly. Pieces 0..3 carry the FRAME and SECOND
    /// nibbles, pieces 4..7 the MINUTE and HOUR ones, and the update is stamped with the window's piece 0
    /// - so a splice keeps the low half of the window it started on, which is precisely the label the
    /// wall-time prediction describes, and takes only the high half from a later window. The corrupt
    /// label therefore matches the predicted label's <c>ss:ff</c> exactly while its <c>hh:mm</c> is
    /// somewhere ahead. Nothing else produces that shape: a relocate moves the whole label.
    /// <para>±1 frame of slack on the predicted position absorbs ordinary arrival jitter (half a frame is
    /// 20 ms at 25 fps). The test is on the LABEL FIELDS rather than on a seconds difference so that
    /// 29.97 drop-frame - whose labelled minute is 1798 frames, or 1800 across a ten-minute boundary -
    /// needs no special case. A genuine relocate that happens to land on the predicted <c>ss:ff</c> a
    /// whole number of minutes away is not misread: it is merely held one extra timecode (~83 ms) until
    /// the next assembly corroborates it.</para>
    /// <para>"A later window" is a statement about the LABEL SPACE, which wraps at 24 h - so the
    /// directional guard compares wrapped frame distance, not raw seconds. Locked at <c>23:59:59:21</c>,
    /// the splice that straddles midnight assembles <c>00:00:59:23</c>: one labelled minute AHEAD of the
    /// prediction, but ~24 h behind it on a plain <c>&lt;=</c> - which let the corrupt label straight
    /// through as a relocate and re-baselined every consumer a day backwards.</para></summary>
    private static bool IsNibbleSpliceFingerprint(MidiTimecodeValue candidate, double predictedSeconds)
    {
        var framesPerDay = MidiTimecodeRates.FramesPerDay(candidate.Rate);
        var predictedFrames = (long)Math.Round(
            predictedSeconds / MidiTimecodeRates.SecondsPerFrame(candidate.Rate));
        // Wrapped distance from the prediction to the candidate. The high half can only come from a LATER
        // window, so anything at or behind the prediction (a wrapped distance of zero, or one that is
        // shorter measured backwards) is not a splice.
        var forward = ((candidate.FrameNumber - predictedFrames) % framesPerDay + framesPerDay) % framesPerDay;
        if (forward == 0 || forward > framesPerDay / 2)
            return false;

        for (var slack = -1L; slack <= 1L; slack++)
        {
            var predictedLabel = MidiTimecodeValue.FromFrameNumber(predictedFrames + slack, candidate.Rate);
            if (predictedLabel.Seconds == candidate.Seconds
                && predictedLabel.Frames == candidate.Frames
                && (predictedLabel.Minutes != candidate.Minutes || predictedLabel.Hours != candidate.Hours))
            {
                return true;
            }
        }

        return false;
    }

    private MidiTimecodeUpdate Anchor(
        MidiTimecodeValue timecode,
        double seconds,
        long timestampTicks,
        bool running,
        MidiTimecodeUpdateKind kind)
    {
        _haveAnchor = true;
        _anchorTicks = timestampTicks;
        _anchorSeconds = seconds;
        _anchorRate = timecode.Rate;
        return new MidiTimecodeUpdate(kind, timecode, timestampTicks, running);
    }
}

/// <summary>Snapshot of the chase clock, cheap enough to take once per UI sweep.</summary>
/// <param name="HasSignal">A timecode has been locked at least once, so <paramref name="Position"/> means
/// something (it may be frozen).</param>
/// <param name="IsChasing">Messages are still arriving: the position is live and interpolating.</param>
/// <param name="Rate">Frame rate the SENDER declared.</param>
/// <param name="PositionSeconds">Interpolated position in seconds from 00:00:00:00; frozen at the last
/// message while stalled or parked.</param>
/// <param name="Position">The same position as an <c>hh:mm:ss:ff</c> label.</param>
/// <param name="Generation">Incremented on every discontinuity (first lock, relocate, full-frame locate,
/// resume after a stall). Consumers use it as the identity of "this continuous run".</param>
/// <param name="GenerationStartSeconds">Position at the instant this generation began - the baseline
/// that keeps a locate from firing everything it skipped over. It is a DECODED LABEL, so it can sit up to
/// <see cref="MidiTimecodeChaseClock.MaxPositionLeadSeconds"/> behind a <paramref name="PositionSeconds"/>
/// a consumer has already acted on without the sender having moved backwards.</param>
/// <param name="UndecodedQuarterFrames">Quarter-frames fed since the last COMPLETED assembly. Eight is
/// one healthy timecode and a dropped message costs one window (16); a number that keeps climbing while
/// <paramref name="HasSignal"/> stays false is the signature of timecode that ARRIVES and never decodes -
/// two sources feeding the same decoder (every piece duplicated), a mangled stream, or a permanent
/// reverse/shuttle chase. Without it that failure looks exactly like "no cable plugged in".</param>
public readonly record struct MidiTimecodeChaseState(
    bool HasSignal,
    bool IsChasing,
    MidiTimecodeRate Rate,
    double PositionSeconds,
    MidiTimecodeValue Position,
    int Generation,
    double GenerationStartSeconds,
    int UndecodedQuarterFrames = 0);

/// <summary>
/// Thread-safe MTC chase clock: fed on the MIDI I/O thread, read from anywhere. Wraps a
/// <see cref="MidiTimecodeDecoder"/> with the two things a decoder cannot know on its own - wall-time
/// interpolation between quarter-frames, and stall detection.
/// </summary>
/// <remarks>
/// <para><strong>Interpolation, and its ceiling.</strong> Each assembled timecode anchors (tick, seconds);
/// a read reports <c>anchor + elapsed</c>. Between the 2-frame assemblies that is pure wall-clock
/// extrapolation, which is exactly what cancels the decoder's inherent 2-frame read lag - and that is also
/// the whole of its mandate, so the elapsed part is capped at <see cref="MaxPositionLeadSeconds"/>
/// (4 frames). Everything past that would be a position asserted on no evidence.</para>
/// <para><strong>Stall.</strong> Quarter-frames arrive 4× per frame (10 ms at 25 fps). After
/// <see cref="StallTimeout"/> without one that EXTENDED an in-order assembly - arrival alone is not
/// evidence of forward motion, a reverse chase emits pieces 7..0 - the clock stops extrapolating and
/// FREEZES at that last in-sequence message: a consumer that fires on crossings must never free-wheel
/// through a pile of targets because the sender was switched off, put in reverse, or garbled. A
/// full-frame locate parks the clock (a locate is not a roll), so the position sits exactly on the
/// located label until quarter-frames resume.</para>
/// <para><strong>Jump/relocate.</strong> Any discontinuity bumps <see cref="MidiTimecodeChaseState.Generation"/>
/// and republishes <see cref="MidiTimecodeChaseState.GenerationStartSeconds"/>, which is the whole
/// mechanism a scheduler needs to retire everything behind a locate instead of firing a burst.</para>
/// <para>The lock is held for a handful of instructions; at 100 messages/s (25 fps) against a 4 Hz reader
/// it is uncontended, and it is what keeps the reader from seeing a torn anchor.</para>
/// </remarks>
public sealed class MidiTimecodeChaseClock
{
    /// <summary>Silence longer than this means the sender stopped. ~10 missed quarter-frames at 25 fps -
    /// long enough to ride out poll jitter, short enough that a freeze is felt as "signal lost".</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>How long <see cref="MidiTimecodeChaseState.IsChasing"/> survives without a COMPLETED
    /// assembly. Messages arriving only prove the sender is talking; only an assembly says where it is.
    /// One timecode spans 2 frames (~83 ms at 24 fps) and the window peaks at twice that, so 500 ms rides
    /// out two consecutive lost assemblies without the operator's "receiving" chip flickering.
    /// <para>This is a LIVENESS bound only. It is deliberately NOT the bound on how far the reported
    /// position may be predicted past the last decoded label - see
    /// <see cref="MaxPositionLeadSeconds"/>, which is an order of magnitude tighter. Using this one for
    /// both let the position free-run half a second forward on a stream that arrives and never assembles,
    /// and a crossing consumer fired every target inside it.</para></summary>
    public static readonly TimeSpan AssemblyStallTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly MidiTimecodeDecoder _decoder;
    private readonly Func<long> _ticks;
    private readonly long _ticksPerSecond;
    private readonly double _stallSeconds = StallTimeout.TotalSeconds;
    private readonly double _assemblyStallSeconds = AssemblyStallTimeout.TotalSeconds;

    private bool _haveSignal;
    private bool _running;
    private long _anchorTicks;
    private double _anchorSeconds;
    private long _lastMessageTicks;
    private MidiTimecodeRate _rate;
    private int _generation;
    private double _generationStartSeconds;
    private int _undecodedQuarterFrames;

    /// <summary>The most a position reported by <see cref="Read"/> may lead the last DECODED label, for a
    /// sender at <paramref name="rate"/>: 4 frames (133-167 ms).
    /// <para><strong>Why 4 and not the liveness bound.</strong> Extrapolation exists for exactly one
    /// reason - to cancel the decoder's inherent 2-frame read lag (see <see cref="MidiTimecodeDecoder"/>).
    /// The honest bound is therefore that lag (2 frames, one assembly window) plus the tolerance the
    /// decoder itself allows a label to miss the wall prediction by before calling it a relocate (another
    /// 2 frames). Past that the reported position stops being a prediction and becomes an invention, and
    /// a consumer that fires on crossings fires on positions the sender never reached: a reverse/shuttle
    /// chase (pieces 7..0, nothing ever assembles) or a link that breaks every window keeps quarter-frames
    /// ARRIVING, and the position used to run forward at real-time rate for the whole
    /// <see cref="AssemblyStallTimeout"/> - half a second, 12 frames - on no evidence at all.</para>
    /// <para><strong>Why it is public.</strong> A crossing consumer needs the SAME number, and cannot
    /// derive it. <see cref="MidiTimecodeChaseState.GenerationStartSeconds"/> is a decoded label, so a new
    /// run's baseline can legitimately sit this far behind a position the consumer already acted on -
    /// which is not a rewind. A consumer that re-arms a fired target below this slack double-fires it on
    /// any sender whose labels advance slower than wall time (a varispeed/jog pass is classified as a
    /// relocate on every single assembly, so the baseline churns per assembly). Guessing a second,
    /// unrelated constant on the consumer side is exactly how that bug happened.</para></summary>
    public static double MaxPositionLeadSeconds(MidiTimecodeRate rate) =>
        4.0 * MidiTimecodeRates.SecondsPerFrame(rate);

    /// <param name="ticks">Monotonic tick source; defaults to <see cref="Stopwatch.GetTimestamp"/>.
    /// Tests inject a hand-advanced counter so nothing in the chase path needs a timer.</param>
    /// <param name="ticksPerSecond">Frequency of that tick domain; defaults to <see cref="Stopwatch.Frequency"/>.</param>
    public MidiTimecodeChaseClock(Func<long>? ticks = null, long ticksPerSecond = 0)
    {
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency;
        _ticks = ticks ?? Stopwatch.GetTimestamp;
        _decoder = new MidiTimecodeDecoder(_ticksPerSecond);
    }

    /// <summary>Feeds one quarter-frame data byte (I/O thread). Returns the update that completed an
    /// assembly, or null.</summary>
    public MidiTimecodeUpdate? FeedQuarterFrame(byte dataByte)
    {
        var now = _ticks();
        lock (_gate)
        {
            var update = _decoder.FeedQuarterFrame(dataByte, now);
            _undecodedQuarterFrames = update is null ? _undecodedQuarterFrames + 1 : 0;
            if (update is { } assembled)
                Apply(assembled);
            // Liveness counts a quarter-frame only when it EXTENDED an in-order assembly, not merely
            // because it arrived: that is the one thing that says the sender is moving forward at real
            // time between assemblies. A reverse/shuttle chase emits pieces 7..0, and while every one of
            // its piece 0s is accepted (a fresh window always restarts cleanly), nothing after it ever
            // fits - so stamping on arrival held the clock "chasing" and ran the position FORWARD while
            // the deck ran backwards. A window that completes counts too, whatever the decoder made of
            // the label (an unconfirmed splice reports nothing yet is plainly in-sequence traffic).
            if (update is not null || _decoder.LastQuarterFrameExtendedAssembly)
                _lastMessageTicks = now;
            return update;
        }
    }

    /// <summary>Feeds a full-frame locate SysEx (I/O thread). Returns null when it is not one.</summary>
    public MidiTimecodeUpdate? FeedFullFrame(ReadOnlySpan<byte> message)
    {
        var now = _ticks();
        lock (_gate)
        {
            var update = _decoder.FeedFullFrame(message, now);
            if (update is not { } located)
                return null;
            Apply(located);
            _undecodedQuarterFrames = 0;
            _lastMessageTicks = now;
            return update;
        }
    }

    /// <summary>
    /// Feeds one COMPLETE timecode frame that some other transport decoded - the entry point for
    /// timecode that does not arrive as MIDI wire bytes.
    /// </summary>
    /// <param name="timecode">The decoded label.</param>
    /// <param name="isRunning">True while the sender is rolling; false when it is parked (a decoder
    /// that has lost bit-lock, or a deck sitting still), which starts a fresh run like a locate.</param>
    /// <remarks>
    /// <para>
    /// Everything below the wire format in this class - stall timeouts, free-run extrapolation, the
    /// jump/resync classification, generation counting - is about timecode as a concept and has nothing
    /// to do with MIDI. Only the two ingestion methods above are MIDI-shaped. LTC (SMPTE timecode on an
    /// audio track) is the case that makes this worth exposing: it delivers a whole frame at a time
    /// rather than eight nibbles, so it needs none of the quarter-frame assembly and can drive the same
    /// chase machinery directly.
    /// </para>
    /// <para>
    /// Continuity is classified exactly as the quarter-frame path classifies it, so a caller cannot
    /// accidentally get different stall/jump behaviour by choosing a different transport.
    /// </para>
    /// </remarks>
    public MidiTimecodeUpdate FeedFrame(MidiTimecodeValue timecode, bool isRunning = true)
    {
        var now = _ticks();
        lock (_gate)
        {
            var kind = ClassifyFrame(timecode, now, isRunning);
            var update = new MidiTimecodeUpdate(kind, timecode, now, isRunning);
            Apply(update);
            _undecodedQuarterFrames = 0;
            _lastMessageTicks = now;
            return update;
        }
    }

    /// <summary>Continuity of a whole-frame update against the current anchor. Mirrors the decoder's
    /// own rule: matches the prediction ⇒ Continued, otherwise a relocate.</summary>
    private MidiTimecodeUpdateKind ClassifyFrame(MidiTimecodeValue timecode, long now, bool isRunning)
    {
        if (!isRunning)
            return MidiTimecodeUpdateKind.Located;
        if (!_haveSignal || !_running || timecode.Rate != _rate)
            return MidiTimecodeUpdateKind.Resynced;

        var elapsedSeconds = (now - _anchorTicks) / (double)_ticksPerSecond;
        var predicted = _anchorSeconds + elapsedSeconds;
        var drift = Math.Abs(timecode.TotalSeconds - predicted);
        // One frame of slack: a decoder's own timestamp granularity is a frame, so anything inside that
        // is the sender running normally rather than relocating.
        return drift <= MidiTimecodeRates.SecondsPerFrame(timecode.Rate)
            ? MidiTimecodeUpdateKind.Continued
            : MidiTimecodeUpdateKind.Jumped;
    }

    /// <summary>Drops the lock and the signal (device closed, chase turned off). The generation still
    /// advances, so a consumer holding the previous one treats whatever comes next as a new run.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _decoder.Reset();
            _undecodedQuarterFrames = 0;
            if (!_haveSignal)
                return;
            _haveSignal = false;
            _running = false;
            _generation++;
            _generationStartSeconds = 0;
            _anchorSeconds = 0;
        }
    }

    /// <summary>Interpolated snapshot for the reader (UI sweep).</summary>
    public MidiTimecodeChaseState Read()
    {
        var now = _ticks();
        lock (_gate)
        {
            if (!_haveSignal)
                return new MidiTimecodeChaseState(
                    false, false, _rate, 0, default, _generation, 0, _undecodedQuarterFrames);

            // THREE separate facts, deliberately not conflated:
            //  - liveness: a quarter-frame that EXTENDED an in-order assembly arrived recently;
            //  - decodability: an assembly COMPLETED recently (AssemblyStallTimeout);
            //  - lead: how far the position may be PREDICTED past the label that assembly produced
            //    (MaxPositionLeadSeconds - one assembly window plus the decoder's own jump tolerance).
            // The first two gate IsChasing, which the UI shows as "receiving". Only the third bounds the
            // POSITION, and it has to be the tight one: a crossing consumer acts on the position, so
            // anything it reports past that bound is a target the sender never reached.
            var idleSeconds = (now - _lastMessageTicks) / (double)_ticksPerSecond;
            var assemblyAgeSeconds = (now - _anchorTicks) / (double)_ticksPerSecond;
            var chasing = _running
                          && idleSeconds <= _stallSeconds
                          && assemblyAgeSeconds <= _assemblyStallSeconds;
            var maxReference = _anchorTicks + (long)(MaxPositionLeadSeconds(_rate) * _ticksPerSecond);
            // Parked (a full-frame locate is not a roll): sit exactly ON the located label, so traffic
            // that keeps arriving without assembling cannot creep the parked chip off the target.
            // Chasing: extrapolate to now. Stalled: freeze where the last in-sequence message left us.
            // Either way never further from the last decoded label than the prediction bound allows.
            var reference = _running
                ? Math.Min(chasing ? now : _lastMessageTicks, maxReference)
                : _anchorTicks;
            var seconds = _anchorSeconds + ((reference - _anchorTicks) / (double)_ticksPerSecond);
            if (seconds < 0)
                seconds = 0;
            return new MidiTimecodeChaseState(
                HasSignal: true,
                IsChasing: chasing,
                Rate: _rate,
                PositionSeconds: seconds,
                Position: MidiTimecodeValue.FromSeconds(seconds, _rate),
                Generation: _generation,
                GenerationStartSeconds: _generationStartSeconds,
                UndecodedQuarterFrames: _undecodedQuarterFrames);
        }
    }

    private void Apply(in MidiTimecodeUpdate update)
    {
        var seconds = update.Timecode.TotalSeconds;
        // Gap since the last DECODED label, not since the last message. Measured against
        // _lastMessageTicks this test could never be true on the quarter-frame path: the update is
        // stamped with piece 0's tick while _lastMessageTicks already holds piece 6's, ~60 ms LATER, so
        // the difference was always negative and "resume after a stall" only ever fired through the
        // Jumped path - which misses the case the whole rule exists for, a dropout the sender rolled
        // straight through (the resumed label still matches wall prediction, so it reads as Continued
        // and the scheduler bursts through every target the freeze hid). Anchor-to-anchor is ~83 ms on
        // a healthy stream and ~167 ms after a dropped message, hence the assembly bound rather than
        // the 100 ms silence bound.
        var wasStalled = _haveSignal
                         && _running
                         && (update.TimestampTicks - _anchorTicks) / (double)_ticksPerSecond
                            > _assemblyStallSeconds;
        if (!_haveSignal || wasStalled || update.Kind != MidiTimecodeUpdateKind.Continued)
        {
            _generation++;
            _generationStartSeconds = seconds;
        }

        _haveSignal = true;
        _running = update.IsRunning;
        _anchorTicks = update.TimestampTicks;
        _anchorSeconds = seconds;
        _rate = update.Timecode.Rate;
    }
}
