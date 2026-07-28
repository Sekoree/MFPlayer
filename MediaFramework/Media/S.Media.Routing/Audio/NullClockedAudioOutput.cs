using System.Diagnostics;

namespace S.Media.Routing;

/// <summary>
/// Device-less <see cref="IAudioOutput"/> that consumes submitted samples at exactly real-time
/// rate and exposes that consumption as an <see cref="IPlaybackClock"/>. Gives headless hosts
/// (CI, render boxes, visualizer-only rigs) the same sample-accurate pacing and master-clock
/// promotion path as a hardware backend, with no audio device.
/// </summary>
/// <remarks>
/// <para>
/// Samples are discarded on <see cref="Submit"/>; only per-channel frame counts are tracked in a
/// virtual queue drained by a wall-clock model: the sample due at index <c>n</c> (segment-local)
/// is consumed at the absolute instant <c>segmentAnchor + n/rate</c>. All waits target that
/// absolute deadline and every pass recomputes it from the anchor, so scheduler jitter in an
/// individual wait can never accumulate into drift. (A <see cref="PeriodicTimer"/> cadence would:
/// it quantizes fractional-ms periods.)
/// </para>
/// <para>
/// Clock contract mirrors <c>PortAudioOutput</c>: <see cref="ElapsedSinceStart"/> is
/// consumed-samples/rate, monotonic within a segment, re-anchored to zero by <see cref="Start"/>
/// and <see cref="Flush"/>; an empty queue freezes it (underrun silence never advances the clock),
/// and late submissions after an underrun are consumed from "now", not backfilled into the gap.
/// </para>
/// </remarks>
public sealed class NullClockedAudioOutput : IAudioOutput, IClockedOutput, IFlushableOutput, IPlaybackClock, IDisposable
{
    private readonly AudioFormat _format;
    private readonly int _capacitySamples;
    private readonly Lock _gate = new();

    /// <summary>Lifetime per-channel frame counters; queue depth is the difference.</summary>
    private long _submittedSamples;
    private long _consumedSamples;
    /// <summary>Segment drain anchor: sample (consumed − base) is due at anchor + samples/rate.</summary>
    private long _segmentAnchorTimestamp;
    private long _segmentConsumedBase;
    /// <summary><see cref="_consumedSamples"/> baseline for <see cref="IPlaybackClock"/> - reset on Start/Flush.</summary>
    private long _playbackEpochSamples;
    private bool _isRunning;
    private bool _disposed;

    public NullClockedAudioOutput(AudioFormat format, int capacityFrames = 16384)
    {
        if (format.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(format), "sample rate must be positive");
        if (format.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(format), "channel count must be positive");
        if (capacityFrames < 64) throw new ArgumentOutOfRangeException(nameof(capacityFrames), "must be >= 64");

        _format = format;
        _capacitySamples = capacityFrames;
        TargetQueueSamples = capacityFrames / 2;
    }

    public AudioFormat Format => _format;

    /// <summary>
    /// Target queue depth (samples per channel) maintained by <see cref="WaitForCapacity"/>.
    /// Defaults to half the virtual capacity, same heuristic as the hardware backends.
    /// </summary>
    public int TargetQueueSamples { get; set; }

    public bool IsRunning
    {
        get { lock (_gate) return _isRunning; }
    }

    /// <summary>Total frames (samples per channel) consumed so far. Monotonic across Stop/Start.</summary>
    public long ConsumedSamples
    {
        get
        {
            lock (_gate)
            {
                AdvanceConsumptionLocked(Stopwatch.GetTimestamp());
                return _consumedSamples;
            }
        }
    }

    /// <summary>Samples per channel currently sitting in the virtual queue.</summary>
    public int QueuedSamples
    {
        get
        {
            lock (_gate)
            {
                AdvanceConsumptionLocked(Stopwatch.GetTimestamp());
                return (int)(_submittedSamples - _consumedSamples);
            }
        }
    }

    public int CapacitySamples => _capacitySamples;

    /// <summary><see cref="IPlaybackClock.ElapsedSinceStart"/>: consumed samples since the last Start/Flush epoch, over rate.</summary>
    public TimeSpan ElapsedSinceStart
    {
        get
        {
            lock (_gate)
            {
                AdvanceConsumptionLocked(Stopwatch.GetTimestamp());
                var samples = _consumedSamples - _playbackEpochSamples;
                return samples > 0 ? TimeSpan.FromSeconds(samples / (double)_format.SampleRate) : TimeSpan.Zero;
            }
        }
    }

    /// <summary><see cref="IPlaybackClock.IsAdvancing"/>: true between Start and Stop/Dispose.</summary>
    public bool IsAdvancing
    {
        get { lock (_gate) return _isRunning && !_disposed; }
    }

    public void Start()
    {
        lock (_gate)
        {
            // Checked under the gate: a Start racing Dispose must never mark a disposed
            // instance running (its counters would keep advancing after disposal).
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning) return;
            _segmentAnchorTimestamp = Stopwatch.GetTimestamp();
            _segmentConsumedBase = _consumedSamples;
            _playbackEpochSamples = _consumedSamples;
            _isRunning = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_isRunning) return;
            // Settle consumption up to now so ElapsedSinceStart freezes at the stop position.
            AdvanceConsumptionLocked(Stopwatch.GetTimestamp());
            _isRunning = false;
        }
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packedSamples.Length % _format.Channels != 0)
            throw new ArgumentException(
                $"packedSamples.Length {packedSamples.Length} is not a multiple of channel count {_format.Channels}",
                nameof(packedSamples));

        var frames = packedSamples.Length / _format.Channels;
        lock (_gate)
        {
            AdvanceConsumptionLocked(Stopwatch.GetTimestamp());
            // Same overflow policy as the hardware ring: drop the excess rather than block.
            var free = _capacitySamples - (_submittedSamples - _consumedSamples);
            var accepted = Math.Min(frames, free);
            if (accepted > 0)
                _submittedSamples += accepted;
        }
    }

    /// <summary>
    /// <see cref="IFlushableOutput.Flush"/>: drops the virtual queue and re-anchors
    /// <see cref="ElapsedSinceStart"/> to zero; the clock then freezes until the next
    /// <see cref="Submit"/> (an empty queue never advances it).
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = Stopwatch.GetTimestamp();
            AdvanceConsumptionLocked(now);
            _submittedSamples = _consumedSamples;
            _playbackEpochSamples = _consumedSamples;
            _segmentConsumedBase = _consumedSamples;
            _segmentAnchorTimestamp = now;
        }
    }

    /// <summary>
    /// <see cref="IClockedOutput"/>: blocks until adding <paramref name="chunkSamples"/> would
    /// leave the queue at or below <see cref="TargetQueueSamples"/>, sleeping until the absolute
    /// wall-clock deadline at which the drain model consumes the excess.
    /// </summary>
    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        if (chunkSamples <= 0) return !token.IsCancellationRequested;

        while (!token.IsCancellationRequested)
        {
            long waitTimestampTicks;
            lock (_gate)
            {
                if (_disposed) return false;
                // Before Start nothing drains - report ready so prefill can fill the queue
                // (mirrors PortAudioOutput's not-running behaviour).
                if (!_isRunning) return true;

                var now = Stopwatch.GetTimestamp();
                AdvanceConsumptionLocked(now);
                var queued = _submittedSamples - _consumedSamples;
                // A target below one chunk could never be satisfied even by a fully drained
                // queue - clamp so the wait terminates instead of spinning on underrun re-anchors.
                var target = Math.Max(TargetQueueSamples, chunkSamples);
                if (queued + chunkSamples <= target) return true;

                var excess = queued + chunkSamples - target;
                var dueSample = _consumedSamples - _segmentConsumedBase + excess;
                var deadline = _segmentAnchorTimestamp
                               + (long)(dueSample / (double)_format.SampleRate * Stopwatch.Frequency);
                waitTimestampTicks = deadline - now;
            }

            // Cap each sleep slice so a Dispose (which has no waiter handle to signal) is
            // observed within ~100 ms instead of a full drain deadline; the absolute-deadline
            // recompute on the next pass keeps the slicing drift-free.
            var waitMs = (int)Math.Clamp(
                (long)Math.Ceiling(waitTimestampTicks * 1000.0 / Stopwatch.Frequency), 1, 100);
            if (token.WaitHandle.WaitOne(waitMs)) return false;
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            AdvanceConsumptionLocked(Stopwatch.GetTimestamp());
            _isRunning = false;
            _disposed = true;
        }
    }

    /// <summary>
    /// Settles the drain model up to <paramref name="nowTimestamp"/>. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void AdvanceConsumptionLocked(long nowTimestamp)
    {
        if (!_isRunning) return;
        var elapsedTicks = nowTimestamp - _segmentAnchorTimestamp;
        if (elapsedTicks <= 0) return;

        var dueSamples = _segmentConsumedBase
                         + (long)(elapsedTicks / (double)Stopwatch.Frequency * _format.SampleRate);
        var take = Math.Min(dueSamples - _consumedSamples, _submittedSamples - _consumedSamples);
        if (take > 0)
            _consumedSamples += take;

        if (_consumedSamples < dueSamples)
        {
            // Underrun: the shortfall was silence. Re-anchor so late submissions are consumed
            // from now on rather than backfilled into the gap at infinite speed.
            _segmentAnchorTimestamp = nowTimestamp;
            _segmentConsumedBase = _consumedSamples;
        }
    }
}
