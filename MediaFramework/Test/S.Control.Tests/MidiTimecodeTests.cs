using Xunit;

namespace S.Control.Tests;

/// <summary>
/// MIDI Time Code decode + chase clock (Ideas/Next-Round-Plan-2026-07-28.md D1). Everything here feeds
/// byte sequences with hand-supplied timestamps - there is not a timer anywhere in the decode path, so
/// assembly, the 2-frame read lag, interpolation, stall and jump/relocate detection are all exactly
/// reproducible.
/// </summary>
public sealed class MidiTimecodeTests
{
    /// <summary>Microsecond tick domain: makes every timing assertion below plain arithmetic.</summary>
    private const long TicksPerSecond = 1_000_000;

    private static long Ms(double milliseconds) => (long)(milliseconds * 1000.0);

    /// <summary>The 8 quarter-frame DATA bytes a sender emits for one timecode, in wire order.</summary>
    private static byte[] QuarterFrames(int hours, int minutes, int seconds, int frames, MidiTimecodeRate rate) =>
    [
        (byte)(0x00 | (frames & 0x0F)),
        (byte)(0x10 | ((frames >> 4) & 0x01)),
        (byte)(0x20 | (seconds & 0x0F)),
        (byte)(0x30 | ((seconds >> 4) & 0x03)),
        (byte)(0x40 | (minutes & 0x0F)),
        (byte)(0x50 | ((minutes >> 4) & 0x03)),
        (byte)(0x60 | (hours & 0x0F)),
        (byte)(0x70 | ((hours >> 4) & 0x01) | ((int)rate << 1)),
    ];

    private static byte[] FullFrame(int hours, int minutes, int seconds, int frames, MidiTimecodeRate rate) =>
    [
        0xF0, 0x7F, 0x7F, 0x01, 0x01,
        (byte)(((int)rate << 5) | (hours & 0x1F)),
        (byte)minutes, (byte)seconds, (byte)frames,
        0xF7,
    ];

    // ---- Timecode value arithmetic ----

    [Theory]
    [InlineData(MidiTimecodeRate.Fps24, 24)]
    [InlineData(MidiTimecodeRate.Fps25, 25)]
    [InlineData(MidiTimecodeRate.Fps2997Drop, 30)]
    [InlineData(MidiTimecodeRate.Fps30, 30)]
    public void FrameNumber_RoundTrips_ThroughEveryRate(MidiTimecodeRate rate, int fps)
    {
        var value = new MidiTimecodeValue(1, 2, 3, fps - 1, rate);
        Assert.Equal(value, MidiTimecodeValue.FromFrameNumber(value.FrameNumber, rate));
        Assert.Equal(fps, MidiTimecodeRates.FramesPerSecond(rate));
    }

    [Fact]
    public void DropFrame_SkipsTheTwoLabelsPerMinute_ExceptEveryTenth()
    {
        const MidiTimecodeRate df = MidiTimecodeRate.Fps2997Drop;
        // 00:00:59:29 → 00:01:00:02 is ONE frame apart in drop-frame (00 and 01 do not exist).
        var last = new MidiTimecodeValue(0, 0, 59, 29, df);
        var next = new MidiTimecodeValue(0, 1, 0, 2, df);
        Assert.Equal(last.FrameNumber + 1, next.FrameNumber);
        Assert.Equal(next, MidiTimecodeValue.FromFrameNumber(next.FrameNumber, df));

        // …but the tenth minute keeps its 00 label.
        var tenth = new MidiTimecodeValue(0, 10, 0, 0, df);
        Assert.Equal(new MidiTimecodeValue(0, 9, 59, 29, df).FrameNumber + 1, tenth.FrameNumber);
        Assert.Equal(tenth, MidiTimecodeValue.FromFrameNumber(tenth.FrameNumber, df));

        // ABSOLUTE spec values, not a tolerance. The only pin here used to be
        // `Assert.Equal(3600.0, …TotalSeconds, 1)`, which passes for the correct 3599.9964 AND for a
        // SecondsPerFrame(DF) of 1/30 - i.e. for drop-frame arithmetic that had been broken outright.
        // These are the published SMPTE frame counts, so both halves of the conversion are nailed down:
        Assert.Equal(1800, new MidiTimecodeValue(0, 1, 0, 2, df).FrameNumber); // first label of minute 1
        Assert.Equal(17982, new MidiTimecodeValue(0, 10, 0, 0, df).FrameNumber); // 10 min = 17982 frames
        Assert.Equal(107892, new MidiTimecodeValue(1, 0, 0, 0, df).FrameNumber); // 1 h = 6 × 17982
        Assert.Equal(2589407, new MidiTimecodeValue(23, 59, 59, 29, df).FrameNumber); // last of the day
        Assert.Equal(2589408, MidiTimecodeRates.FramesPerDay(df));

        // …and the real-time length of an hour of drop-frame labels: 107892 × 1001/30000 s. Non-drop 30
        // would read exactly 3596.4 here, plain 3600.0 if SecondsPerFrame(DF) were 1/30.
        Assert.Equal(3599.9964, new MidiTimecodeValue(1, 0, 0, 0, df).TotalSeconds, 4);
        Assert.Equal(1001.0 / 30000.0, MidiTimecodeRates.SecondsPerFrame(df), 10);
    }

    [Theory]
    [InlineData("00:01:00:00", false)]
    [InlineData("00:01:00:01", false)]
    [InlineData("00:01:00:02", true)]
    [InlineData("00:10:00:00", true)]
    public void DropFrame_RejectsLabelsThatDoNotExist(string text, bool expected)
    {
        Assert.Equal(
            expected,
            MidiTimecodeValue.TryParse(text, MidiTimecodeRate.Fps2997Drop, out _));
    }

    [Theory]
    [InlineData("01:02:03:04", true)]
    [InlineData("01:02:03;04", true)]
    [InlineData("1:2:3:4", true)]
    [InlineData("01:02:03", false)]
    [InlineData("01:02:03:25", false)] // 25 frames is out of range at 25 fps
    [InlineData("24:00:00:00", false)]
    [InlineData("01:02:03:04:05", false)]
    [InlineData("", false)]
    [InlineData("nope", false)]
    public void TryParse_AcceptsOnlyWellFormedTargetsForTheRate(string text, bool expected)
    {
        Assert.Equal(expected, MidiTimecodeValue.TryParse(text, MidiTimecodeRate.Fps25, out var value));
        if (expected)
            Assert.True(value.IsValid);
    }

    [Fact]
    public void ToString_IsTheCanonicalTwoDigitForm() =>
        Assert.Equal("01:02:03:04", new MidiTimecodeValue(1, 2, 3, 4, MidiTimecodeRate.Fps25).ToString());

    [Fact]
    public void IsValid_RejectsAnUnknownRateValue() =>
        Assert.False(new MidiTimecodeValue(1, 2, 3, 4, (MidiTimecodeRate)99).IsValid);

    // ---- Quarter-frame assembly ----

    [Theory]
    [InlineData(MidiTimecodeRate.Fps24)]
    [InlineData(MidiTimecodeRate.Fps25)]
    [InlineData(MidiTimecodeRate.Fps2997Drop)]
    [InlineData(MidiTimecodeRate.Fps30)]
    public void QuarterFrames_AssembleOnPieceSeven_WithTheSendersRate(MidiTimecodeRate rate)
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        var bytes = QuarterFrames(21, 47, 33, 11, rate);

        for (var i = 0; i < 7; i++)
            Assert.Null(decoder.FeedQuarterFrame(bytes[i], Ms(i * 10)));

        var update = decoder.FeedQuarterFrame(bytes[7], Ms(70));
        Assert.NotNull(update);
        Assert.Equal(new MidiTimecodeValue(21, 47, 33, 11, rate), update!.Value.Timecode);
        Assert.True(update.Value.IsRunning);
        Assert.Equal(MidiTimecodeUpdateKind.Resynced, update.Value.Kind); // first lock
    }

    [Fact]
    public void AssembledValue_IsStampedAtPieceZero_NotAtTheAssembly()
    {
        // THE 2-FRAME LAG. Eight quarter-frames span exactly 2 frames, so a timecode can only be read
        // 2 frames after the sender was at it. The decoder therefore reports the value against the
        // timestamp of PIECE 0 - the instant it actually describes - which is what lets the chase
        // clock interpolate the staleness away instead of running 2 frames behind forever.
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        var bytes = QuarterFrames(1, 0, 0, 0, MidiTimecodeRate.Fps25);
        MidiTimecodeUpdate? update = null;
        for (var i = 0; i < 8; i++)
            update = decoder.FeedQuarterFrame(bytes[i], Ms(i * 10)) ?? update;

        Assert.NotNull(update);
        Assert.Equal(Ms(0), update!.Value.TimestampTicks); // piece 0, 70 ms before the assembly completed
    }

    [Fact]
    public void OutOfOrderPieces_DropThePartial_AndResyncOnTheNextPieceZero()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        var bytes = QuarterFrames(2, 0, 0, 0, MidiTimecodeRate.Fps25);

        decoder.FeedQuarterFrame(bytes[0], 0);
        decoder.FeedQuarterFrame(bytes[1], Ms(10));
        // Piece 4 arrives where piece 2 was due (a dropped pair): the partial is abandoned…
        Assert.Null(decoder.FeedQuarterFrame(bytes[4], Ms(20)));
        for (var i = 5; i < 8; i++)
            Assert.Null(decoder.FeedQuarterFrame(bytes[i], Ms(20 + (i * 10))));

        // …and only a fresh, complete sequence assembles - never a half-corrupt label.
        var next = QuarterFrames(2, 0, 0, 2, MidiTimecodeRate.Fps25);
        MidiTimecodeUpdate? update = null;
        for (var i = 0; i < 8; i++)
            update = decoder.FeedQuarterFrame(next[i], Ms(100 + (i * 10))) ?? update;
        Assert.NotNull(update);
        Assert.Equal(new MidiTimecodeValue(2, 0, 0, 2, MidiTimecodeRate.Fps25), update!.Value.Timecode);
    }

    /// <summary>
    /// THE 8-MESSAGE DROPOUT. Sequence discipline alone does not protect the assembly: losing exactly a
    /// whole multiple of 8 quarter-frames resumes on the very piece index the assembler is waiting for,
    /// so pieces 0..3 of one window (the FRAME and SECOND nibbles) merge with pieces 4..7 of a later one
    /// (the MINUTE and HOUR nibbles). Inside a minute that is harmless - the low half already is the
    /// label the window started on. Across a minute ROLLOVER the merged label is a full minute wrong,
    /// and it used to be reported: the chase clock then read it as a relocate and re-baselined every
    /// consumer a minute ahead of a sender that had not moved.
    /// </summary>
    [Fact]
    public void QuarterFrames_DropoutOfAWholeWindow_NeverReportsTheSplicedLabel()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        var locked = Feed(decoder, 0, 0, 59, 21, MidiTimecodeRate.Fps25, startTicks: 0);
        Assert.Equal(new MidiTimecodeValue(0, 0, 59, 21, MidiTimecodeRate.Fps25), locked.Timecode);

        // The next window describes 00:00:59:23. Its pieces 0..3 arrive; then 8 messages are lost
        // (pieces 4..7 of this window plus 0..3 of the next); then pieces 4..7 of the window describing
        // 00:01:00:00 land on exactly the index the assembler expects. The assembly that completes says
        // 00:01:59:23 - a place the sender was never at.
        var current = QuarterFrames(0, 0, 59, 23, MidiTimecodeRate.Fps25);
        var next = QuarterFrames(0, 1, 0, 0, MidiTimecodeRate.Fps25);
        for (var i = 0; i < 4; i++)
            Assert.Null(decoder.FeedQuarterFrame(current[i], Ms(80 + (i * 10))));
        for (var i = 4; i < 8; i++)
            Assert.Null(decoder.FeedQuarterFrame(next[i], Ms(200 + ((i - 4) * 10))));

        // The sender simply rolled on, so the next clean window is exactly where wall time says it is -
        // and because the splice never moved the anchor, it reads as an ordinary continuation.
        var resumed = Feed(decoder, 0, 1, 0, 2, MidiTimecodeRate.Fps25, startTicks: Ms(240));
        Assert.Equal(new MidiTimecodeValue(0, 1, 0, 2, MidiTimecodeRate.Fps25), resumed.Timecode);
        Assert.Equal(MidiTimecodeUpdateKind.Continued, resumed.Kind);
    }

    /// <summary>The corroboration rule must not swallow a REAL relocate that happens to carry the splice
    /// fingerprint (same <c>ss:ff</c> as the prediction, a whole minute away). It costs one timecode of
    /// latency and is then reported normally, because a rolling sender confirms it.</summary>
    [Fact]
    public void QuarterFrames_ARelocateShapedLikeASplice_IsReportedOnceCorroborated()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        Feed(decoder, 0, 0, 10, 0, MidiTimecodeRate.Fps25, startTicks: 0);

        var bytes = QuarterFrames(0, 1, 10, 2, MidiTimecodeRate.Fps25);
        MidiTimecodeUpdate? held = null;
        for (var i = 0; i < 8; i++)
            held = decoder.FeedQuarterFrame(bytes[i], Ms(80 + (i * 10))) ?? held;
        Assert.Null(held); // unconfirmed - arithmetically indistinguishable from a splice on its own

        var confirmed = Feed(decoder, 0, 1, 10, 4, MidiTimecodeRate.Fps25, startTicks: Ms(160));
        Assert.Equal(MidiTimecodeUpdateKind.Jumped, confirmed.Kind);
        Assert.Equal(new MidiTimecodeValue(0, 1, 10, 4, MidiTimecodeRate.Fps25), confirmed.Timecode);
    }

    [Fact]
    public void FreeRunningSender_IsContinued_ARelocateIsJumped()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        var first = Feed(decoder, 1, 0, 0, 0, MidiTimecodeRate.Fps25, startTicks: 0);
        Assert.Equal(MidiTimecodeUpdateKind.Resynced, first.Kind);

        // Two frames later in both wall time (80 ms) and label (+2 frames): a continuous run.
        var second = Feed(decoder, 1, 0, 0, 2, MidiTimecodeRate.Fps25, startTicks: Ms(80));
        Assert.Equal(MidiTimecodeUpdateKind.Continued, second.Kind);

        // Same wall cadence, but the sender is suddenly an hour away: a relocate.
        var third = Feed(decoder, 2, 0, 0, 0, MidiTimecodeRate.Fps25, startTicks: Ms(160));
        Assert.Equal(MidiTimecodeUpdateKind.Jumped, third.Kind);
    }

    // ---- Full frames ----

    [Fact]
    public void FullFrame_DecodesTheLocate_AndReportsAParkedSender()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        var update = decoder.FeedFullFrame(FullFrame(10, 20, 30, 12, MidiTimecodeRate.Fps30), Ms(5));

        Assert.NotNull(update);
        Assert.Equal(new MidiTimecodeValue(10, 20, 30, 12, MidiTimecodeRate.Fps30), update!.Value.Timecode);
        Assert.Equal(MidiTimecodeUpdateKind.Located, update.Value.Kind);
        Assert.False(update.Value.IsRunning); // a full frame is what a PARKED/locating deck emits
    }

    /// <summary>
    /// A sloppy sender's locate at a drop-frame label that does not exist (<c>:00</c>/<c>:01</c> at the top
    /// of a non-tenth minute) is SNAPPED onto the first label that does, not dropped. This reverses an
    /// earlier assertion, deliberately: authoring-side rejection (<see cref="MidiTimecodeValue.IsValid"/>,
    /// which a typed schedule target still goes through) and decoder-side acceptance are different jobs.
    /// Dropping the message discarded the whole locate - no park, no generation bump - so every consumer
    /// kept a baseline the sender had already left, which for a crossing consumer is a burst on the next
    /// roll. Quarter-frames self-heal within 2 frames; a locate is a one-shot.
    /// </summary>
    [Fact]
    public void FullFrame_SnapsANonexistentDropFrameLabel_RatherThanDroppingTheLocate()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);

        var update = decoder.FeedFullFrame(
            FullFrame(0, 1, 0, 0, MidiTimecodeRate.Fps2997Drop), Ms(5));
        Assert.NotNull(update);
        Assert.Equal(
            new MidiTimecodeValue(0, 1, 0, 2, MidiTimecodeRate.Fps2997Drop),
            update!.Value.Timecode);
        Assert.Equal(MidiTimecodeUpdateKind.Located, update.Value.Kind);

        // Only those two labels are snapped: the tenth minute keeps its own :00, and a frame field that is
        // out of range for the rate is still garbage and still rejected.
        var tenth = decoder.FeedFullFrame(
            FullFrame(0, 10, 0, 0, MidiTimecodeRate.Fps2997Drop), Ms(10));
        Assert.Equal(new MidiTimecodeValue(0, 10, 0, 0, MidiTimecodeRate.Fps2997Drop), tenth!.Value.Timecode);
        Assert.Null(decoder.FeedFullFrame(
            FullFrame(0, 1, 0, 31, MidiTimecodeRate.Fps2997Drop), Ms(15)));

        // The authoring side is unchanged - a typed target must fire where the operator typed it.
        Assert.False(MidiTimecodeValue.TryParse("00:01:00:00", MidiTimecodeRate.Fps2997Drop, out _));
    }

    [Fact]
    public void NonTimecodeSysEx_IsRejected()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        Assert.False(MidiTimecodeDecoder.IsFullFrame([0xF0, 0x7F, 0x7F, 0x06, 0x01, 0xF7]));
        Assert.Null(decoder.FeedFullFrame([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF7], 0));
        // Truncated full frame.
        Assert.Null(decoder.FeedFullFrame([0xF0, 0x7F, 0x7F, 0x01, 0x01, 0x01, 0x00, 0x00, 0xF7], 0));
    }

    [Fact]
    public void Feed_DispatchesOnTheStatusByte()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        Assert.Null(decoder.Feed([0xF8], 0)); // timing clock is not ours
        var update = decoder.Feed(FullFrame(3, 0, 0, 0, MidiTimecodeRate.Fps25), 0);
        Assert.NotNull(update);
        Assert.Equal(3, update!.Value.Timecode.Hours);

        var bytes = QuarterFrames(4, 0, 0, 0, MidiTimecodeRate.Fps25);
        MidiTimecodeUpdate? assembled = null;
        for (var i = 0; i < 8; i++)
            assembled = decoder.Feed([0xF1, bytes[i]], Ms(100 + (i * 10))) ?? assembled;
        Assert.Equal(4, assembled!.Value.Timecode.Hours);
    }

    // ---- Chase clock ----

    [Fact]
    public void ChaseClock_InterpolatesBetweenAssemblies_SoThePositionIsCurrent()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        Assert.False(clock.Read().HasSignal);

        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);

        // 10 ms after the assembly completed we are 80 ms past the label the sequence described -
        // exactly 2 frames at 25 fps, i.e. the read lag interpolated away.
        now = Ms(80);
        var state = clock.Read();
        Assert.True(state.IsChasing);
        Assert.Equal(MidiTimecodeRate.Fps25, state.Rate);
        Assert.Equal(3600.080, state.PositionSeconds, 3);
        Assert.Equal(new MidiTimecodeValue(1, 0, 0, 2, MidiTimecodeRate.Fps25), state.Position);
    }

    [Fact]
    public void ChaseClock_Stalls_AndFreezesInsteadOfFreeWheeling()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var lastMessage = now; // piece 7's tick

        now = lastMessage + Ms(50); // still inside the stall timeout
        Assert.True(clock.Read().IsChasing);

        now = lastMessage + Ms(5000); // sender gone
        var stalled = clock.Read();
        Assert.False(stalled.IsChasing);
        Assert.True(stalled.HasSignal);
        // Frozen at the last message, NOT 5 s further on - a consumer firing on crossings must never
        // sweep through everything the sender never actually played.
        Assert.Equal(3600.070, stalled.PositionSeconds, 3);
    }

    [Fact]
    public void ChaseClock_ResumeAfterAStall_StartsANewGeneration()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var firstRun = clock.Read().Generation;

        now += Ms(10_000); // sender switched off, then rolls again from where it stopped
        FeedClock(clock, ref now, 1, 0, 0, 2, MidiTimecodeRate.Fps25);

        var state = clock.Read();
        Assert.NotEqual(firstRun, state.Generation);
        Assert.Equal(new MidiTimecodeValue(1, 0, 0, 2, MidiTimecodeRate.Fps25).TotalSeconds,
            state.GenerationStartSeconds, 3);
    }

    [Fact]
    public void ChaseClock_ContinuousRun_KeepsOneGeneration()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var generation = clock.Read().Generation;

        for (var i = 1; i <= 10; i++)
        {
            now = Ms(80 * i);
            FeedClock(clock, ref now, 1, 0, 0, 2 * i, MidiTimecodeRate.Fps25);
            Assert.Equal(generation, clock.Read().Generation);
        }
    }

    [Fact]
    public void ChaseClock_FullFrameLocate_ParksExactlyOnTheTarget_AndOpensANewGeneration()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var before = clock.Read().Generation;

        now += Ms(20);
        Assert.NotNull(clock.FeedFullFrame(FullFrame(0, 30, 0, 0, MidiTimecodeRate.Fps25)));

        now += Ms(500); // a parked deck stays put however long we wait
        var state = clock.Read();
        Assert.False(state.IsChasing);
        Assert.NotEqual(before, state.Generation);
        Assert.Equal(new MidiTimecodeValue(0, 30, 0, 0, MidiTimecodeRate.Fps25), state.Position);
        Assert.Equal(1800.0, state.GenerationStartSeconds, 3);
    }

    [Fact]
    public void ChaseClock_Reset_DropsTheSignal()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        Assert.True(clock.Read().HasSignal);

        clock.Reset();
        var state = clock.Read();
        Assert.False(state.HasSignal);
        Assert.False(state.IsChasing);
    }

    /// <summary>
    /// A stream that keeps ARRIVING but never assembles must freeze, not free-run. A reverse/shuttle
    /// chase emits quarter-frames descending (7,6,…,0), so the sequence discipline never completes an
    /// assembly - and the class contract says that "is a stall from the scheduler's point of view".
    /// <para><strong>Assertion tightened, on purpose.</strong> This test used to allow the position to
    /// run 500 ms forward (<c>&lt;= locked + 0.51</c>), codifying the assembly-stall window as the bound
    /// on the reported POSITION. That is not a defensible bound for a consumer that fires on crossings: a
    /// review reproduced a rewind in which 400 ms of descending quarter-frames pushed the position 470 ms
    /// past the last real label, fired a cue the sender never reached, and then fired it AGAIN on the real
    /// forward pass. Liveness now counts only quarter-frames that EXTEND an in-order assembly, and the
    /// extrapolation is capped at <see cref="MidiTimecodeChaseClock.MaxPositionLeadSeconds"/>, so a stream
    /// that proves nothing about forward motion moves the position by exactly nothing. <c>IsChasing</c>
    /// still goes false, which is what the operator's chip needs to show.</para>
    /// </summary>
    [Fact]
    public void ChaseClock_StreamThatNeverAssembles_FreezesInsteadOfRunningForward()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var locked = clock.Read();
        Assert.True(locked.IsChasing);

        // Two seconds of DESCENDING quarter-frames: the sender is plainly talking (10 ms apart, far
        // inside the silence timeout) but nothing ever assembles.
        var descending = QuarterFrames(1, 0, 0, 0, MidiTimecodeRate.Fps25);
        for (var i = 0; i < 200; i++)
        {
            now += Ms(10);
            clock.FeedQuarterFrame(descending[7 - (i % 8)]);
        }

        var state = clock.Read();
        Assert.False(state.IsChasing);
        Assert.True(state.HasSignal);
        // Not one millisecond of forward run on 2 s of traffic that never extended an assembly.
        Assert.Equal(locked.PositionSeconds, state.PositionSeconds, 6);
        // …and the diagnostic that makes "arriving but not decoding" visible instead of looking like an
        // unplugged cable.
        Assert.Equal(200, state.UndecodedQuarterFrames);
    }

    /// <summary>
    /// The hard ceiling on prediction. Extrapolation exists to cancel the decoder's 2-frame read lag, so
    /// the position may lead the last DECODED label by one assembly window plus the decoder's own 2-frame
    /// jump tolerance - 4 frames - and not one frame more, whatever traffic keeps arriving. The number is
    /// published because <c>CueSchedulerService</c> has to re-arm a fired target against exactly it: with
    /// a smaller slack a sender whose labels advance slower than wall time (classified as a relocate on
    /// every assembly, so the baseline churns per assembly) re-armed and re-fired the same cue.
    /// </summary>
    [Fact]
    public void ChaseClock_PositionNeverLeadsTheDecodedLabel_ByMoreThanThePublishedBound()
    {
        Assert.Equal(4.0 / 25.0, MidiTimecodeChaseClock.MaxPositionLeadSeconds(MidiTimecodeRate.Fps25), 9);
        Assert.Equal(4.0 / 24.0, MidiTimecodeChaseClock.MaxPositionLeadSeconds(MidiTimecodeRate.Fps24), 9);
        Assert.Equal(
            4.0 * 1001.0 / 30000.0,
            MidiTimecodeChaseClock.MaxPositionLeadSeconds(MidiTimecodeRate.Fps2997Drop), 9);
        // An order of magnitude tighter than the liveness bound - the two are NOT the same knob.
        Assert.True(
            MidiTimecodeChaseClock.MaxPositionLeadSeconds(MidiTimecodeRate.Fps25)
            < MidiTimecodeChaseClock.AssemblyStallTimeout.TotalSeconds / 2.0);

        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var label = new MidiTimecodeValue(1, 0, 0, 0, MidiTimecodeRate.Fps25).TotalSeconds;
        var ceiling = label + MidiTimecodeChaseClock.MaxPositionLeadSeconds(MidiTimecodeRate.Fps25);

        // A LOSSY link: pieces 0..3 of every window arrive in order (so liveness keeps being stamped and
        // the sender is genuinely rolling forward) and 4..7 never do. Nothing assembles for 2 s.
        var bytes = QuarterFrames(1, 0, 0, 2, MidiTimecodeRate.Fps25);
        for (var window = 0; window < 25; window++)
        {
            for (var i = 0; i < 4; i++)
            {
                now += Ms(20);
                clock.FeedQuarterFrame(bytes[i]);
            }

            var position = clock.Read().PositionSeconds;
            Assert.True(position <= ceiling + 1e-9, $"position {position} passed the {ceiling} ceiling");
        }

        var final = clock.Read();
        Assert.False(final.IsChasing); // past the assembly-stall bound as well
        Assert.Equal(ceiling, final.PositionSeconds, 3);
    }

    /// <summary>
    /// The 8-message splice AT THE 24-HOUR WRAP. The fingerprint's directional guard ("the minute/hour half
    /// always comes from a LATER window") was a raw <c>candidate.TotalSeconds &lt;= predictedSeconds</c>,
    /// which is false exactly once a day: locked at 23:59:59:21, a splice straddling midnight assembles
    /// 00:00:59:23 - one labelled minute ahead in the label space, ~24 h BEHIND on a seconds comparison.
    /// The guard let it through as a relocate, re-baselining every consumer a day backwards and retiring
    /// every armed target with it. The distance is now measured wrapped.
    /// </summary>
    [Fact]
    public void QuarterFrames_SpliceAcrossTheTwentyFourHourWrap_IsStillNeverReported()
    {
        var decoder = new MidiTimecodeDecoder(TicksPerSecond);
        var locked = Feed(decoder, 23, 59, 59, 21, MidiTimecodeRate.Fps25, startTicks: 0);
        Assert.Equal(new MidiTimecodeValue(23, 59, 59, 21, MidiTimecodeRate.Fps25), locked.Timecode);

        // Pieces 0..3 of the 23:59:59:23 window (frame + second nibbles), then 8 lost messages, then
        // pieces 4..7 of the 00:00:00:00 window (minute + hour nibbles). The merged label is 00:00:59:23 -
        // a place the sender was never at, and one the unfixed guard reported as a relocate.
        var current = QuarterFrames(23, 59, 59, 23, MidiTimecodeRate.Fps25);
        var next = QuarterFrames(0, 0, 0, 0, MidiTimecodeRate.Fps25);
        for (var i = 0; i < 4; i++)
            Assert.Null(decoder.FeedQuarterFrame(current[i], Ms(80 + (i * 10))));
        for (var i = 4; i < 8; i++)
            Assert.Null(decoder.FeedQuarterFrame(next[i], Ms(200 + ((i - 4) * 10))));

        // The sender rolled straight through midnight, so the next clean window is the REAL 00:00:00:02.
        // It reads as a relocate, and that is deliberate rather than a leftover: the label space wraps at
        // 24 h but a consumer's baseline is linear seconds, so the day rollover has to open a new run -
        // treating it as a continuation would leave a scheduler comparing a 00:00:xx target against an
        // 86 000-second baseline and retiring it unfired. What must never happen is the SPLICED label
        // being reported, which is what re-baselined everything ~24 h backwards.
        var resumed = Feed(decoder, 0, 0, 0, 2, MidiTimecodeRate.Fps25, startTicks: Ms(240));
        Assert.Equal(new MidiTimecodeValue(0, 0, 0, 2, MidiTimecodeRate.Fps25), resumed.Timecode);
        Assert.Equal(MidiTimecodeUpdateKind.Jumped, resumed.Kind);
    }

    /// <summary>The same midnight splice through the chase clock: the phantom 00:00:59:23 must never
    /// become the clock's position or open a generation. Unfixed, the clock relocated to 59.9 s - a whole
    /// day backwards - and every consumer re-baselined there mid-splice.</summary>
    [Fact]
    public void ChaseClock_SpliceAcrossTheTwentyFourHourWrap_NeitherMovesNorReBaselinesTheRun()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 23, 59, 59, 21, MidiTimecodeRate.Fps25);
        var locked = clock.Read();

        var current = QuarterFrames(23, 59, 59, 23, MidiTimecodeRate.Fps25);
        var next = QuarterFrames(0, 0, 0, 0, MidiTimecodeRate.Fps25);
        for (var i = 0; i < 4; i++)
        {
            now += Ms(10);
            clock.FeedQuarterFrame(current[i]);
        }

        now += Ms(80); // the 8 lost messages
        for (var i = 4; i < 8; i++)
        {
            now += Ms(10);
            clock.FeedQuarterFrame(next[i]);
        }

        var state = clock.Read();
        Assert.Equal(locked.Generation, state.Generation);
        Assert.InRange(state.PositionSeconds, 86399.8, 86400.1); // still in the last second of the day
        Assert.Equal(locked.GenerationStartSeconds, state.GenerationStartSeconds, 6);
    }

    /// <summary>Two sources feeding one decoder (a mis-latched duplicate) duplicates every piece, so
    /// nothing ever assembles and the chip reads "no signal" forever. The counter is the only way that is
    /// distinguishable from an unplugged cable.</summary>
    [Fact]
    public void ChaseClock_DuplicatedPieces_NeverLock_ButTheUndecodedCountMakesItVisible()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        var bytes = QuarterFrames(1, 0, 0, 0, MidiTimecodeRate.Fps25);
        for (var window = 0; window < 5; window++)
        {
            foreach (var b in bytes)
            {
                for (var copy = 0; copy < 2; copy++)
                {
                    now += Ms(5);
                    clock.FeedQuarterFrame(b);
                }
            }
        }

        var state = clock.Read();
        Assert.False(state.HasSignal);
        Assert.Equal(80, state.UndecodedQuarterFrames); // 5 windows × 8 pieces × 2 copies
    }

    /// <summary>A parked sender sits exactly ON the located label. Traffic that keeps arriving without
    /// assembling used to drag the frozen position up to 500 ms past it, so the operator's chip read a
    /// dozen frames off a deck that had not moved.</summary>
    [Fact]
    public void ChaseClock_ParkedOnALocate_DoesNotCreepWhileUndecodableTrafficArrives()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        Assert.NotNull(clock.FeedFullFrame(FullFrame(0, 30, 0, 0, MidiTimecodeRate.Fps25)));

        var descending = QuarterFrames(0, 30, 0, 0, MidiTimecodeRate.Fps25);
        for (var i = 0; i < 100; i++)
        {
            now += Ms(10);
            clock.FeedQuarterFrame(descending[7 - (i % 8)]);
        }

        var state = clock.Read();
        Assert.False(state.IsChasing);
        Assert.Equal(1800.0, state.PositionSeconds, 6);
        Assert.Equal(new MidiTimecodeValue(0, 30, 0, 0, MidiTimecodeRate.Fps25), state.Position);
    }

    /// <summary>
    /// A dropout the sender ROLLED STRAIGHT THROUGH must open a new generation. The resumed label still
    /// matches wall-clock prediction, so it reads as <c>Continued</c> and the Jumped path never fires;
    /// the dedicated stall test was measuring the gap against the last MESSAGE tick, which on the
    /// quarter-frame path is piece 6 of the very same window (~60 ms AFTER the update's own piece-0
    /// stamp) and so could never be positive. Without the generation bump the scheduler keeps its old
    /// baseline and bursts through every target the freeze hid.
    /// </summary>
    [Fact]
    public void ChaseClock_DropoutTheSenderRolledThrough_StartsANewGeneration()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var before = clock.Read().Generation;

        // 2 s of lost transmission; the deck never stopped, so it resumes exactly where wall time says
        // it should be - 2 s later = 50 frames at 25 fps, i.e. 01:00:02:00.
        now += Ms(2000);
        FeedClock(clock, ref now, 1, 0, 2, 0, MidiTimecodeRate.Fps25);

        var state = clock.Read();
        Assert.NotEqual(before, state.Generation);
        Assert.Equal(
            new MidiTimecodeValue(1, 0, 2, 0, MidiTimecodeRate.Fps25).TotalSeconds,
            state.GenerationStartSeconds, 3);
    }

    /// <summary>The new assembly bound must not make an ordinary run churn generations: a healthy 25 fps
    /// stream assembles every 80 ms, and a single dropped message only stretches that to 160 ms.</summary>
    [Fact]
    public void ChaseClock_RunWithADroppedMessage_KeepsOneGeneration()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var generation = clock.Read().Generation;

        // Drop piece 3 of the next timecode: that whole assembly is abandoned (2 frames lost), and the
        // one after it lands 160 ms behind the first.
        var dropped = QuarterFrames(1, 0, 0, 2, MidiTimecodeRate.Fps25);
        for (var i = 0; i < 8; i++)
        {
            now += Ms(10);
            if (i != 3)
                clock.FeedQuarterFrame(dropped[i]);
        }

        now += Ms(10);
        FeedClock(clock, ref now, 1, 0, 0, 4, MidiTimecodeRate.Fps25);
        var state = clock.Read();
        Assert.Equal(generation, state.Generation);
        Assert.True(state.IsChasing);
    }

    /// <summary>The same 8-message dropout seen through the chase clock: the position must stay in the
    /// 59th second and the run must not churn a generation. Unfixed it reported the spliced label, so the
    /// clock relocated a minute ahead and every crossing consumer re-baselined there.</summary>
    [Fact]
    public void ChaseClock_EightMessageDropoutAcrossAMinute_DoesNotJumpAMinuteAhead()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 0, 0, 59, 21, MidiTimecodeRate.Fps25);
        var generation = clock.Read().Generation;

        var current = QuarterFrames(0, 0, 59, 23, MidiTimecodeRate.Fps25);
        var next = QuarterFrames(0, 1, 0, 0, MidiTimecodeRate.Fps25);
        for (var i = 0; i < 4; i++)
        {
            now += Ms(10);
            clock.FeedQuarterFrame(current[i]);
        }

        now += Ms(80); // the 8 lost messages
        for (var i = 4; i < 8; i++)
        {
            now += Ms(10);
            clock.FeedQuarterFrame(next[i]);
        }

        var state = clock.Read();
        Assert.Equal(generation, state.Generation);
        Assert.InRange(state.PositionSeconds, 59.8, 60.3);
    }

    /// <summary>
    /// Evidence for the scheduler's chase-lateness rule (Next-Round-Plan D1 follow-up 7c): chase-domain
    /// milliseconds cannot silently drift away from real ones INSIDE a run, so measuring lateness along
    /// the chase against a real-millisecond grace floor is sound. The clock is wall-time interpolation
    /// anchored on labels, and the decoder only reports Continued while the label tracks the wall
    /// prediction to within 2 frames; a sender whose labels run measurably faster than real time is a
    /// JUMP on every assembly, which opens a new generation and re-baselines the consumer rather than
    /// letting the two domains diverge.
    /// </summary>
    [Fact]
    public void ChaseClock_SenderRunningFasterThanRealTime_OpensANewGenerationEveryAssembly()
    {
        var now = 0L;
        var clock = new MidiTimecodeChaseClock(() => now, TicksPerSecond);
        FeedClock(clock, ref now, 1, 0, 0, 0, MidiTimecodeRate.Fps25);
        var generation = clock.Read().Generation;

        // 3x speed: 6 frames of label per 2 frames (80 ms) of wall time.
        var frame = new MidiTimecodeValue(1, 0, 0, 0, MidiTimecodeRate.Fps25).FrameNumber;
        for (var i = 0; i < 3; i++)
        {
            frame += 6;
            var tc = MidiTimecodeValue.FromFrameNumber(frame, MidiTimecodeRate.Fps25);
            now += Ms(10);
            FeedClock(clock, ref now, tc.Hours, tc.Minutes, tc.Seconds, tc.Frames, MidiTimecodeRate.Fps25);
            var next = clock.Read().Generation;
            Assert.NotEqual(generation, next);
            generation = next;
        }
    }

    private static MidiTimecodeUpdate Feed(
        MidiTimecodeDecoder decoder,
        int hours,
        int minutes,
        int seconds,
        int frames,
        MidiTimecodeRate rate,
        long startTicks)
    {
        var bytes = QuarterFrames(hours, minutes, seconds, frames, rate);
        MidiTimecodeUpdate? update = null;
        for (var i = 0; i < 8; i++)
            update = decoder.FeedQuarterFrame(bytes[i], startTicks + Ms(i * 10)) ?? update;
        Assert.NotNull(update);
        return update!.Value;
    }

    /// <summary>Pushes one full 8-message sequence through the clock, advancing the tick source by one
    /// quarter-frame period (10 ms at 25 fps) per message. Leaves <paramref name="now"/> on piece 7.</summary>
    private static void FeedClock(
        MidiTimecodeChaseClock clock,
        ref long now,
        int hours,
        int minutes,
        int seconds,
        int frames,
        MidiTimecodeRate rate)
    {
        var bytes = QuarterFrames(hours, minutes, seconds, frames, rate);
        for (var i = 0; i < 8; i++)
        {
            if (i > 0)
                now += Ms(10);
            clock.FeedQuarterFrame(bytes[i]);
        }
    }
}
