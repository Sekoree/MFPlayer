using S.Control;

namespace HaPlay.Services;

/// <summary>
/// MTC chase source for <see cref="CueSchedulerService"/> (Ideas/Next-Round-Plan-2026-07-28.md D1).
/// Sits on the SAME always-on device-input seam the per-cue triggers use
/// (<c>ControlInputSession.InputObserved</c> → <c>ControlWorkspaceViewModel.InputObserved</c> →
/// <c>MainViewModel.OnControlInputObserved</c>), and decodes MIDI Time Code straight on the I/O thread
/// into a <see cref="MidiTimecodeChaseClock"/> the UI-thread sweep reads.
/// </summary>
/// <remarks>
/// <para><strong>Why this seam and not a new one.</strong> <c>ControlMonitorRecord</c> already carries
/// everything MTC needs: <c>ControlMIDIMessagePayload.FromMIDIMessage</c> maps a quarter-frame to
/// <see cref="ControlMIDIMessageType.MIDITimeCode"/> with the DATA BYTE in <c>MIDIValue</c>, and a
/// full-frame locate to <see cref="ControlMIDIMessageType.SysEx"/> whose bytes ride <c>RawBytes</c>.
/// The one caveat is that <c>RawBytes</c> is gated by <c>ControlMonitorOptions.IncludeRawBytes</c>
/// (default ON): with raw bytes switched off in the Control monitor settings, quarter-frame chase still
/// works and only full-frame LOCATES go undetected.</para>
/// <para><strong>Never a UI-thread post.</strong> MTC is 100 messages/s at 25 fps. Decoding happens on
/// the calling (PortMIDI poll) thread and lands in a lock-guarded clock; the 250 ms scheduler sweep
/// takes one snapshot. Nothing is queued, marshalled or allocated per message.</para>
/// <para><strong>Default off.</strong> <see cref="Enabled"/> is driven by "does any loaded list hold a
/// Timecode schedule" (the scheduler sets it each sweep). While off, <see cref="OnControlInput"/> costs
/// a protocol compare plus one enum compare and touches neither the decoder nor the clock. It still
/// returns true for MTC records so they are dropped before the trigger service's dispatcher post - a
/// quarter-frame can never match a cue trigger binding anyway.</para>
/// <para><strong>One source at a time.</strong> Two Control devices bound to the same physical MIDI
/// input produce TWO monitor records per message, which would break quarter-frame sequencing. The first
/// source seen latches; records from any other are ignored until the latched one goes quiet for
/// <see cref="MidiTimecodeChaseClock.StallTimeout"/>, at which point the next one to speak takes over.</para>
/// </remarks>
public sealed class CueTimecodeChaseService
{
    private readonly MidiTimecodeChaseClock _clock;
    private readonly Func<long> _ticks;
    private readonly long _ticksPerSecond;
    private readonly Lock _sourceGate = new();

    private Guid? _sourceDeviceId;
    private string? _sourceEndpoint;
    private long _sourceLastTicks;
    private bool _haveSource;
    private volatile bool _enabled;

    /// <param name="clock">Injectable for tests (hand-advanced tick source); production uses the
    /// Stopwatch-backed default.</param>
    /// <param name="ticks">Tick source for the source-latch timeout - pass the same one the clock uses.</param>
    /// <param name="ticksPerSecond">Frequency of that tick domain.</param>
    public CueTimecodeChaseService(
        MidiTimecodeChaseClock? clock = null,
        Func<long>? ticks = null,
        long ticksPerSecond = 0)
    {
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : System.Diagnostics.Stopwatch.Frequency;
        _ticks = ticks ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _clock = clock ?? new MidiTimecodeChaseClock(_ticks, _ticksPerSecond);
    }

    /// <summary>Decode gate. Set by the scheduler sweep to "a Timecode schedule exists in some loaded
    /// list" - NOT to the armed toggle, so an operator can watch the incoming timecode before arming.
    /// Turning it off drops the lock so a later re-enable starts a fresh run.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (!value)
                Reset();
        }
    }

    /// <summary>Snapshot of the chase clock (UI thread; one per sweep).</summary>
    public MidiTimecodeChaseState Read() => _clock.Read();

    /// <summary>Drops the chase lock and the latched source.</summary>
    public void Reset()
    {
        _clock.Reset();
        lock (_sourceGate)
        {
            _haveSource = false;
            _sourceDeviceId = null;
            _sourceEndpoint = null;
        }
    }

    /// <summary>
    /// Always-on device input, on the I/O thread. Returns true when the record IS timecode and has been
    /// consumed here - the caller must then NOT hand it to the trigger path.
    /// </summary>
    public bool OnControlInput(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Protocol != ControlMonitorProtocol.MIDI)
            return false;

        switch (record.MIDIMessageType)
        {
            case ControlMIDIMessageType.MIDITimeCode:
                // Quarter-frame: the payload puts the data byte (piece << 4 | nibble) in MIDIValue.
                if (_enabled && record.MIDIValue is { } data && AcceptSource(record))
                    _clock.FeedQuarterFrame((byte)(data & 0xFF));
                return true;

            case ControlMIDIMessageType.SysEx:
                // Only a full-frame locate is ours; every other SysEx stays available to other consumers.
                if (record.RawBytes is not { } raw || !MidiTimecodeDecoder.IsFullFrame(raw))
                    return false;
                if (_enabled && AcceptSource(record))
                    _clock.FeedFullFrame(raw);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Latches the first timecode source and rejects the rest (see the duplicate-record note),
    /// re-latching once the incumbent has been silent past the stall timeout.</summary>
    private bool AcceptSource(ControlMonitorRecord record)
    {
        var now = _ticks();
        lock (_sourceGate)
        {
            var isIncumbent = _haveSource
                              && _sourceDeviceId == record.DeviceInstanceId
                              && string.Equals(_sourceEndpoint, record.Endpoint, StringComparison.Ordinal);
            if (!isIncumbent)
            {
                var idleSeconds = (now - _sourceLastTicks) / (double)_ticksPerSecond;
                if (_haveSource && idleSeconds <= MidiTimecodeChaseClock.StallTimeout.TotalSeconds)
                    return false; // the latched source is still talking - this one is a duplicate/rival
                _haveSource = true;
                _sourceDeviceId = record.DeviceInstanceId;
                _sourceEndpoint = record.Endpoint;
            }

            _sourceLastTicks = now;
            return true;
        }
    }
}
