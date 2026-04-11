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
        long Fallback,
        long SameFormatPassthrough,
        long RawMarkerPassthrough,
        long Converted,
        long SinkFormatHits,
        long SinkFormatMisses);

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

    private readonly Lock _lock = new();
    private IVideoChannel[] _channels = [];
    private volatile IVideoChannel? _activeChannel;
    private volatile SinkTarget[] _sinkTargets = [];
    private VideoFrame? _lastFrame;
    private VideoFrame? _stagedFrame;
    private bool _hasLeaderPtsOrigin;
    private long _leaderPtsOriginTicks;
    private readonly IPixelFormatConverter _pixelConverter;
    private bool _disposed;

    /// <summary>
    /// When true, the leader output path skips mixer-side conversion and passes
    /// raw source frames through. The render output endpoint then performs conversion.
    /// Equivalent to <see cref="IVideoSinkFormatCapabilities.BypassMixerConversion"/> for sinks.
    /// </summary>
    public bool LeaderBypassConversion { get; set; }
    private long _heldFrameCount;
    private long _droppedStaleFrameCount;
    private long _fallbackConversionCount;
    private long _presentCallCount;
    private long _leaderPresentCount;
    private long _leaderNullCount;
    private long _pullAttemptCount;
    private long _pullHitCount;
    private long _sameFormatPassthroughCount;
    private long _rawMarkerPassthroughCount;
    private long _convertedCount;
    private long _sinkFormatHitCount;
    private long _sinkFormatMissCount;

    private static readonly TimeSpan LeadTolerance = TimeSpan.FromMilliseconds(5);
    private readonly TimeSpan _dropLagThreshold;

    public VideoMixer(VideoFormat outputFormat)
    {
        OutputFormat = outputFormat;
        _pixelConverter = new BasicPixelFormatConverter();

        // Keep lag tolerance near 2 frame intervals so the mixer can catch up quickly
        // on heavy streams (for example 4K60 software decode) without over-dropping.
        var fps = outputFormat.FrameRate;
        var byFrameRate = fps > 0
            ? TimeSpan.FromSeconds(2d / fps)
            : TimeSpan.FromMilliseconds(66);
        _dropLagThreshold = byFrameRate < TimeSpan.FromMilliseconds(30)
            ? TimeSpan.FromMilliseconds(30)
            : byFrameRate;
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
        Fallback: Interlocked.Read(ref _fallbackConversionCount),
        SameFormatPassthrough: Interlocked.Read(ref _sameFormatPassthroughCount),
        RawMarkerPassthrough: Interlocked.Read(ref _rawMarkerPassthroughCount),
        Converted: Interlocked.Read(ref _convertedCount),
        SinkFormatHits: Interlocked.Read(ref _sinkFormatHitCount),
        SinkFormatMisses: Interlocked.Read(ref _sinkFormatMissCount));

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
            clockPosition, OutputFormat.PixelFormat,
            bypassConversion: LeaderBypassConversion);

        if (leader.HasValue)
            Interlocked.Increment(ref _leaderPresentCount);
        else
            Interlocked.Increment(ref _leaderNullCount);

        var sinks = _sinkTargets;
        for (int i = 0; i < sinks.Length; i++)
        {
            var st = sinks[i];

            // Fan-out: when a sink is routed to the same channel as the leader, derive
            // its frame from the leader's current frame (_lastFrame) rather than pulling
            // another frame from the channel.  This guarantees leader and all co-routed
            // sinks see exactly the same decoded frames, with per-sink format conversion
            // (e.g. leader gets native Yuv422p10; NDI sink receives it as Rgba32).
            if (st.ActiveChannel != null
                && _activeChannel   != null
                && ReferenceEquals(st.ActiveChannel, _activeChannel))
            {
                if (st.Sink.IsRunning && _lastFrame.HasValue)
                    DeliverFanOutFrame(st);
                continue;
            }

            var sinkPixelFormat = ResolveSinkPixelFormat(st.Sink, out bool bypassConversion);
            var sinkFrame = PresentForTarget(st.ActiveChannel, ref st.StagedFrame, ref st.LastFrame,
                ref st.HasPtsOrigin, ref st.PtsOriginTicks,
                clockPosition, sinkPixelFormat,
                countSinkFormatStats: true,
                bypassConversion: bypassConversion);
            if (sinkFrame.HasValue && st.Sink.IsRunning)
                st.Sink.ReceiveFrame(sinkFrame.Value);
        }

        return leader;
    }

    /// <summary>
    /// Delivers the leader's current frame to a sink that shares the leader's channel.
    /// Converts if the sink's preferred pixel format differs from the raw leader frame.
    /// The converted frame (if any) is disposed after delivery — sinks must copy data
    /// inside <see cref="IVideoSink.ReceiveFrame"/> rather than holding references.
    /// </summary>
    private void DeliverFanOutFrame(SinkTarget st)
    {
        var raw = _lastFrame!.Value;

        Interlocked.Increment(ref _pullAttemptCount);
        Interlocked.Increment(ref _pullHitCount);

        var sinkFormat = ResolveSinkPixelFormat(st.Sink, out bool bypass);

        if (bypass || raw.PixelFormat == sinkFormat)
        {
            Interlocked.Increment(ref _sinkFormatHitCount);
            Interlocked.Increment(ref _rawMarkerPassthroughCount);
            st.Sink.ReceiveFrame(raw);
            return;
        }

        // Conversion required (e.g. Yuv422p10 → Rgba32 for an NDI sink).
        Interlocked.Increment(ref _sinkFormatMissCount);
        if (raw.PixelFormat != PixelFormat.Rgba32 && raw.PixelFormat != PixelFormat.Bgra32)
            Interlocked.Increment(ref _fallbackConversionCount);

        var converted = _pixelConverter.Convert(raw, sinkFormat);
        Interlocked.Increment(ref _convertedCount);

        st.Sink.ReceiveFrame(converted);

        // Free the temporary conversion buffer; the sink is required to have
        // copied any data it needs during ReceiveFrame (e.g. NDIVideoSink does this).
        converted.MemoryOwner?.Dispose();
    }

    private VideoFrame? PresentForTarget(
        IVideoChannel? channel,
        ref VideoFrame? staged,
        ref VideoFrame? last,
        ref bool hasPtsOrigin,
        ref long ptsOriginTicks,
        TimeSpan clockPosition,
        PixelFormat outputPixelFormat,
        bool countSinkFormatStats = false,
        bool bypassConversion = false)
    {
        if (channel is null)
            return last;

        if (!staged.HasValue)
            staged = PullAndConvert(channel, outputPixelFormat, ref hasPtsOrigin, ref ptsOriginTicks, countSinkFormatStats, bypassConversion);

        // Bootstrap per-target playback: once we have any frame, present it immediately
        // so startup/decode latency cannot keep the output black indefinitely.
        if (!last.HasValue && staged.HasValue)
        {
            last = staged;
            staged = null;
            return last;
        }

        while (staged.HasValue && staged.Value.Pts + _dropLagThreshold < clockPosition)
        {
            staged.Value.MemoryOwner?.Dispose();
            staged = PullAndConvert(channel, outputPixelFormat, ref hasPtsOrigin, ref ptsOriginTicks, countSinkFormatStats, bypassConversion);
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
        ref long ptsOriginTicks,
        bool countSinkFormatStats,
        bool bypassConversion)
    {
        Interlocked.Increment(ref _pullAttemptCount);
        int got = channel.FillBuffer(_pullBuffer, 1);
        if (got <= 0)
            return null;

        Interlocked.Increment(ref _pullHitCount);

        var raw = _pullBuffer[0];
        if (countSinkFormatStats)
        {
            if (bypassConversion || raw.PixelFormat == outputPixelFormat)
                Interlocked.Increment(ref _sinkFormatHitCount);
            else
                Interlocked.Increment(ref _sinkFormatMissCount);
        }

        if (bypassConversion)
        {
            Interlocked.Increment(ref _rawMarkerPassthroughCount);
            return NormalizePts(raw, ref hasPtsOrigin, ref ptsOriginTicks);
        }

        if (raw.PixelFormat != PixelFormat.Rgba32 && raw.PixelFormat != PixelFormat.Bgra32)
            Interlocked.Increment(ref _fallbackConversionCount);

        var converted = raw.PixelFormat == outputPixelFormat
            ? raw
            : _pixelConverter.Convert(raw, outputPixelFormat);

        if (raw.PixelFormat == outputPixelFormat)
            Interlocked.Increment(ref _sameFormatPassthroughCount);
        else
            Interlocked.Increment(ref _convertedCount);

        if (!ReferenceEquals(raw.MemoryOwner, converted.MemoryOwner))
            raw.MemoryOwner?.Dispose();

        return NormalizePts(converted, ref hasPtsOrigin, ref ptsOriginTicks);
    }

    private static PixelFormat ResolveSinkPixelFormat(IVideoSink sink, out bool bypassConversion)
    {
        bypassConversion = false;
        if (sink is IVideoSinkFormatCapabilities caps)
        {
            bypassConversion = caps.BypassMixerConversion;
            if (bypassConversion)
                return PixelFormat.Rgba32;

            foreach (var pf in caps.PreferredPixelFormats)
                if (IsSupportedSinkTargetFormat(pf))
                    return pf;
        }


        return PixelFormat.Rgba32;
    }

    private static bool IsSupportedSinkTargetFormat(PixelFormat pf)
        => pf is PixelFormat.Rgba32 or PixelFormat.Bgra32;

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

