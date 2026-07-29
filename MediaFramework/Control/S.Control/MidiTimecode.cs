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

    /// <summary>Rebuilds a label from a real-time position (rounded to the nearest whole frame - the
    /// chase clock interpolates between quarter-frames, so its position lands mid-frame).</summary>
    public static MidiTimecodeValue FromSeconds(double seconds, MidiTimecodeRate rate)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        var frames = (long)Math.Floor(seconds / MidiTimecodeRates.SecondsPerFrame(rate) + 1e-6);
        return FromFrameNumber(frames, rate);
    }

    /// <summary>True when every field is inside the range its rate allows.</summary>
    public bool IsValid =>
        Hours is >= 0 and < 24
        && Minutes is >= 0 and < 60
        && Seconds is >= 0 and < 60
        && Frames >= 0 && Frames < MidiTimecodeRates.FramesPerSecond(Rate);

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
    }

    /// <summary>Feeds one quarter-frame DATA byte (the byte after the 0xF1 status). Returns an update
    /// only on the message that completes an assembly - i.e. once per 2 frames.</summary>
    public MidiTimecodeUpdate? FeedQuarterFrame(byte dataByte, long timestampTicks)
    {
        var piece = (dataByte >> 4) & 0x07;
        var nibble = dataByte & 0x0F;

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
        var candidate = new MidiTimecodeValue(
            Hours: hourByte & 0x1F,
            Minutes: message[6] & 0x7F,
            Seconds: message[7] & 0x7F,
            Frames: message[8] & 0x7F,
            Rate: (MidiTimecodeRate)((hourByte >> 5) & 0x03));
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
    /// the next assembly corroborates it.</para></summary>
    private static bool IsNibbleSpliceFingerprint(MidiTimecodeValue candidate, double predictedSeconds)
    {
        if (candidate.TotalSeconds <= predictedSeconds)
            return false; // the minute/hour half always comes from a LATER window, never an earlier one

        var predictedFrames = (long)Math.Round(
            predictedSeconds / MidiTimecodeRates.SecondsPerFrame(candidate.Rate));
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
/// that keeps a locate from firing everything it skipped over.</param>
public readonly record struct MidiTimecodeChaseState(
    bool HasSignal,
    bool IsChasing,
    MidiTimecodeRate Rate,
    double PositionSeconds,
    MidiTimecodeValue Position,
    int Generation,
    double GenerationStartSeconds);

/// <summary>
/// Thread-safe MTC chase clock: fed on the MIDI I/O thread, read from anywhere. Wraps a
/// <see cref="MidiTimecodeDecoder"/> with the two things a decoder cannot know on its own - wall-time
/// interpolation between quarter-frames, and stall detection.
/// </summary>
/// <remarks>
/// <para><strong>Interpolation.</strong> Each assembled timecode anchors (tick, seconds); a read reports
/// <c>anchor + elapsed</c>. Between the 2-frame assemblies that is pure wall-clock extrapolation, which
/// is exactly what cancels the decoder's inherent 2-frame read lag.</para>
/// <para><strong>Stall.</strong> Quarter-frames arrive 4× per frame (10 ms at 25 fps). After
/// <see cref="StallTimeout"/> of silence the clock stops extrapolating and FREEZES at the last message's
/// position: a consumer that fires on crossings must never free-wheel through a pile of targets because
/// the sender was switched off. A full-frame locate parks the clock the same way (a locate is not a
/// roll), so the position sits exactly on the located label until quarter-frames resume.</para>
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

    /// <summary>How long the clock keeps extrapolating without a COMPLETED assembly. Messages arriving
    /// only prove the sender is talking; only an assembly says where it is. A stream that keeps arriving
    /// and never assembles - a reverse/shuttle chase emits pieces 7..0, so nothing ever completes - was
    /// otherwise held "chasing" by the message stamp alone and free-ran the position FORWARD at
    /// real-time rate while the deck ran backwards, firing every scheduler target it "crossed". One
    /// timecode spans 2 frames (~83 ms at 24 fps) and the read window peaks at twice that, so 500 ms
    /// rides out two consecutive lost assemblies without flickering.</summary>
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
            if (update is { } assembled)
                Apply(assembled); // BEFORE the liveness stamp - Apply's stall test reads the PREVIOUS one
            // Every quarter-frame counts as liveness, not just the one that completes a timecode -
            // otherwise a sender whose assembly keeps breaking would look stalled while it is talking.
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
            _lastMessageTicks = now;
            return update;
        }
    }

    /// <summary>Drops the lock and the signal (device closed, chase turned off). The generation still
    /// advances, so a consumer holding the previous one treats whatever comes next as a new run.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _decoder.Reset();
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
                return new MidiTimecodeChaseState(false, false, _rate, 0, default, _generation, 0);

            var idleSeconds = (now - _lastMessageTicks) / (double)_ticksPerSecond;
            // Liveness (any message) and decodability (a completed assembly) are separate conditions:
            // see AssemblyStallTimeout. Extrapolation needs BOTH, so an undecodable-but-live stream
            // freezes instead of free-running forward.
            var maxReference = _anchorTicks + (long)(_assemblyStallSeconds * _ticksPerSecond);
            var chasing = _running && idleSeconds <= _stallSeconds && now <= maxReference;
            // Chasing: extrapolate to now. Stalled or parked: freeze where the last message left us -
            // but never further from the last decoded label than the assembly-stall bound, or a stream
            // that keeps talking without assembling would drag the frozen position along with it.
            var reference = Math.Min(chasing ? now : _lastMessageTicks, maxReference);
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
                GenerationStartSeconds: _generationStartSeconds);
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
