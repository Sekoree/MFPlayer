using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Concrete implementation of <see cref="IVideoMixer"/>.
/// Manages video channels and pulls frames from the active one.
/// Single-channel presentation in v1 (no compositing / layering).
/// Backend-agnostic — usable with SDL3, Avalonia, NDI, or any other output.
/// </summary>
public sealed class VideoMixer : IVideoMixer
{
    public readonly record struct DiagnosticsSnapshot(
        long PresentCalls,
        long LeaderPresented,
        long LeaderReturnedNull,
        long PullAttempts,
        long PullHits,
        long Held,
        long Dropped,
        long Fallback);

    private sealed class SinkTarget
    {
        public readonly IVideoSink Sink;
        public volatile IVideoChannel? ActiveChannel;
        public VideoFrame? LastFrame;
        public VideoFrame? StagedFrame;
        public bool HasPtsOrigin;
        public long PtsOriginTicks;

        public SinkTarget(IVideoSink sink) => Sink = sink;

        public void ResetState()
        {
            LastFrame?.MemoryOwner?.Dispose();
            StagedFrame?.MemoryOwner?.Dispose();
            LastFrame = null;
            StagedFrame = null;
            HasPtsOrigin = false;
            PtsOriginTicks = 0;
        }
    }

    private readonly object _lock = new();
    private IVideoChannel[] _channels = [];
    private volatile IVideoChannel? _activeChannel;
    private volatile SinkTarget[] _sinkTargets = [];
    private VideoFrame? _lastFrame;
    private VideoFrame? _stagedFrame;
    private bool _hasLeaderPtsOrigin;
    private long _leaderPtsOriginTicks;
    private readonly IPixelFormatConverter _pixelConverter;
    private bool _disposed;
    private long _heldFrameCount;
    private long _droppedStaleFrameCount;
    private long _fallbackConversionCount;
    private long _presentCallCount;
    private long _leaderPresentCount;
    private long _leaderNullCount;
    private long _pullAttemptCount;
    private long _pullHitCount;

    private static readonly TimeSpan LeadTolerance = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan DropLagThreshold = TimeSpan.FromMilliseconds(90);

    public VideoMixer(VideoFormat outputFormat)
    {
        OutputFormat = outputFormat;
        _pixelConverter = new BasicPixelFormatConverter();
    }

    /// <inheritdoc/>
    public VideoFormat OutputFormat { get; }

    /// <inheritdoc/>
    public int ChannelCount
    {
        get
        {
            lock (_lock) return _channels.Length;
        }
    }

    /// <inheritdoc/>
    public IVideoChannel? ActiveChannel => _activeChannel;

    /// <inheritdoc/>
    public int SinkCount => _sinkTargets.Length;

    public long HeldFrameCount => Interlocked.Read(ref _heldFrameCount);
    public long DroppedStaleFrameCount => Interlocked.Read(ref _droppedStaleFrameCount);
    public long FallbackConversionCount => Interlocked.Read(ref _fallbackConversionCount);

    public DiagnosticsSnapshot GetDiagnosticsSnapshot() => new(
        PresentCalls: Interlocked.Read(ref _presentCallCount),
        LeaderPresented: Interlocked.Read(ref _leaderPresentCount),
        LeaderReturnedNull: Interlocked.Read(ref _leaderNullCount),
        PullAttempts: Interlocked.Read(ref _pullAttemptCount),
        PullHits: Interlocked.Read(ref _pullHitCount),
        Held: Interlocked.Read(ref _heldFrameCount),
        Dropped: Interlocked.Read(ref _droppedStaleFrameCount),
        Fallback: Interlocked.Read(ref _fallbackConversionCount));

    /// <inheritdoc/>
    public void AddChannel(IVideoChannel channel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channel);

        lock (_lock)
        {
            var old = _channels;
            var neo = new IVideoChannel[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = channel;
            _channels = neo;

            // Auto-activate the first channel added.
            if (_activeChannel is null)
                _activeChannel = channel;
        }
    }

    /// <inheritdoc/>
    public void RemoveChannel(Guid channelId)
    {
        lock (_lock)
        {
            var old = _channels;
            int idx = -1;
            for (int i = 0; i < old.Length; i++)
            {
                if (old[i].Id == channelId) { idx = i; break; }
            }
            if (idx < 0) return;

            // If removing the active channel, clear it.
            if (_activeChannel?.Id == channelId)
            {
                _activeChannel = null;
                ResetLeaderState();
            }

            foreach (var st in _sinkTargets)
                if (st.ActiveChannel?.Id == channelId)
                {
                    st.ActiveChannel = null;
                    st.ResetState();
                }

            var neo = new IVideoChannel[old.Length - 1];
            for (int i = 0, j = 0; i < old.Length; i++)
                if (i != idx) neo[j++] = old[i];
            _channels = neo;
        }
    }

    /// <inheritdoc/>
    public void SetActiveChannel(Guid? channelId)
    {
        if (channelId is null)
        {
            _activeChannel = null;
            ResetLeaderState();
            return;
        }

        lock (_lock)
        {
            foreach (var ch in _channels)
            {
                if (ch.Id == channelId.Value)
                {
                    _activeChannel = ch;
                    ResetLeaderState();
                    return;
                }
            }
        }
    }

    /// <inheritdoc/>
    public void RegisterSink(IVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lock)
        {
            foreach (var st in _sinkTargets)
                if (ReferenceEquals(st.Sink, sink))
                    return;

            var old = _sinkTargets;
            var neo = new SinkTarget[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = new SinkTarget(sink);
            _sinkTargets = neo;
        }
    }

    /// <inheritdoc/>
    public void UnregisterSink(IVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lock)
        {
            var old = _sinkTargets;
            int idx = -1;
            for (int i = 0; i < old.Length; i++)
                if (ReferenceEquals(old[i].Sink, sink)) { idx = i; break; }
            if (idx < 0) return;

            old[idx].LastFrame?.MemoryOwner?.Dispose();
            old[idx].StagedFrame?.MemoryOwner?.Dispose();

            var neo = new SinkTarget[old.Length - 1];
            for (int i = 0, j = 0; i < old.Length; i++)
                if (i != idx) neo[j++] = old[i];
            _sinkTargets = neo;
        }
    }

    /// <inheritdoc/>
    public void SetActiveChannelForSink(IVideoSink sink, Guid? channelId)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lock)
        {
            SinkTarget? target = null;
            foreach (var st in _sinkTargets)
                if (ReferenceEquals(st.Sink, sink)) { target = st; break; }
            if (target == null)
                throw new InvalidOperationException("Sink is not registered. Call RegisterSink first.");

            if (channelId is null)
            {
                target.ActiveChannel = null;
                target.ResetState();
                return;
            }

            foreach (var ch in _channels)
                if (ch.Id == channelId.Value)
                {
                    target.ActiveChannel = ch;
                    target.ResetState();
                    return;
                }

            throw new InvalidOperationException("Channel is not registered.");
        }
    }

    // Pre-allocated single-frame buffer to avoid per-call allocation.
    private readonly VideoFrame[] _pullBuffer = new VideoFrame[1];

    /// <inheritdoc/>
    public VideoFrame? PresentNextFrame(TimeSpan clockPosition)
    {
        Interlocked.Increment(ref _presentCallCount);
        var leader = PresentForTarget(_activeChannel, ref _stagedFrame, ref _lastFrame,
            ref _hasLeaderPtsOrigin, ref _leaderPtsOriginTicks,
            clockPosition, OutputFormat.PixelFormat);

        if (leader.HasValue)
            Interlocked.Increment(ref _leaderPresentCount);
        else
            Interlocked.Increment(ref _leaderNullCount);

        var sinks = _sinkTargets;
        for (int i = 0; i < sinks.Length; i++)
        {
            var st = sinks[i];
            var sinkFrame = PresentForTarget(st.ActiveChannel, ref st.StagedFrame, ref st.LastFrame,
                ref st.HasPtsOrigin, ref st.PtsOriginTicks,
                clockPosition, PixelFormat.Rgba32);
            if (sinkFrame.HasValue && st.Sink.IsRunning)
                st.Sink.ReceiveFrame(sinkFrame.Value);
        }

        return leader;
    }

    private VideoFrame? PresentForTarget(
        IVideoChannel? channel,
        ref VideoFrame? staged,
        ref VideoFrame? last,
        ref bool hasPtsOrigin,
        ref long ptsOriginTicks,
        TimeSpan clockPosition,
        PixelFormat outputPixelFormat)
    {
        if (channel is null)
            return last;

        if (!staged.HasValue)
            staged = PullAndConvert(channel, outputPixelFormat, ref hasPtsOrigin, ref ptsOriginTicks);

        // Bootstrap per-target playback: once we have any frame, present it immediately
        // so startup/decode latency cannot keep the output black indefinitely.
        if (!last.HasValue && staged.HasValue)
        {
            last = staged;
            staged = null;
            return last;
        }

        while (staged.HasValue && staged.Value.Pts + DropLagThreshold < clockPosition)
        {
            staged.Value.MemoryOwner?.Dispose();
            staged = PullAndConvert(channel, outputPixelFormat, ref hasPtsOrigin, ref ptsOriginTicks);
            Interlocked.Increment(ref _droppedStaleFrameCount);
        }

        if (staged.HasValue && staged.Value.Pts <= clockPosition + LeadTolerance)
        {
            last?.MemoryOwner?.Dispose();
            last = staged;
            staged = null;
            return last;
        }

        if (staged.HasValue)
            Interlocked.Increment(ref _heldFrameCount);

        return last;
    }

    private VideoFrame? PullAndConvert(
        IVideoChannel channel,
        PixelFormat outputPixelFormat,
        ref bool hasPtsOrigin,
        ref long ptsOriginTicks)
    {
        Interlocked.Increment(ref _pullAttemptCount);
        int got = channel.FillBuffer(_pullBuffer, 1);
        if (got <= 0)
            return null;

        Interlocked.Increment(ref _pullHitCount);

        var raw = _pullBuffer[0];
        if (raw.PixelFormat != PixelFormat.Rgba32 && raw.PixelFormat != PixelFormat.Bgra32)
            Interlocked.Increment(ref _fallbackConversionCount);

        var canonical = raw.PixelFormat == PixelFormat.Rgba32
            ? raw
            : _pixelConverter.Convert(raw, PixelFormat.Rgba32);

        if (!ReferenceEquals(raw.MemoryOwner, canonical.MemoryOwner))
            raw.MemoryOwner?.Dispose();

        if (outputPixelFormat == PixelFormat.Rgba32)
            return NormalizePts(canonical, ref hasPtsOrigin, ref ptsOriginTicks);

        var converted = _pixelConverter.Convert(canonical, outputPixelFormat);
        if (!ReferenceEquals(canonical.MemoryOwner, converted.MemoryOwner))
            canonical.MemoryOwner?.Dispose();

        return NormalizePts(converted, ref hasPtsOrigin, ref ptsOriginTicks);
    }

    private static VideoFrame NormalizePts(VideoFrame frame, ref bool hasPtsOrigin, ref long ptsOriginTicks)
    {
        if (!hasPtsOrigin)
        {
            hasPtsOrigin = true;
            ptsOriginTicks = frame.Pts.Ticks;
        }

        long normalizedTicks = frame.Pts.Ticks - ptsOriginTicks;
        if (normalizedTicks < 0) normalizedTicks = 0;
        if (normalizedTicks == frame.Pts.Ticks)
            return frame;

        return frame with { Pts = TimeSpan.FromTicks(normalizedTicks) };
    }

    private void ResetLeaderState()
    {
        _lastFrame?.MemoryOwner?.Dispose();
        _stagedFrame?.MemoryOwner?.Dispose();
        _lastFrame = null;
        _stagedFrame = null;
        _hasLeaderPtsOrigin = false;
        _leaderPtsOriginTicks = 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pixelConverter.Dispose();
        ResetLeaderState();
        foreach (var st in _sinkTargets)
            st.ResetState();
        _sinkTargets = [];
    }
}

