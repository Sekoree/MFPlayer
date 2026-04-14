using System.Collections.Immutable;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Concrete implementation of <see cref="IVideoMixer"/>.
/// Manages video channels and pulls frames from the active one.
/// Single-channel presentation in v1 (no compositing / layering).
/// Backend-agnostic — usable with SDL3, Avalonia, NDI, or any other output.
/// </summary>
internal sealed class VideoMixer : IVideoMixer
{
    private static readonly ILogger Log = MediaCoreLogging.GetLogger(nameof(VideoMixer));

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
    // Lock-free read path: replaced Dictionary with an immutable snapshot updated under _lock.
    private ImmutableDictionary<Guid, long> _channelOffsetTicks = ImmutableDictionary<Guid, long>.Empty;
    private volatile IVideoChannel? _activeChannel;
    private volatile SinkTarget[] _sinkTargets = [];
    private VideoFrame? _lastFrame;
    private VideoFrame? _stagedFrame;
    private bool _hasLeaderPtsOrigin;
    private long _leaderPtsOriginTicks;
    private bool _disposed;

    private long _heldFrameCount;
    private long _droppedStaleFrameCount;
    private long _fallbackConversionCount = 0;
    private long _presentCallCount;
    private long _leaderPresentCount;
    private long _leaderNullCount;
    private long _pullAttemptCount;
    private long _pullHitCount;
    private long _sameFormatPassthroughCount = 0;
    private long _rawMarkerPassthroughCount;
    private long _convertedCount = 0;
    private long _sinkFormatHitCount;
    private long _sinkFormatMissCount;

    private static readonly TimeSpan LeadTolerance = TimeSpan.FromMilliseconds(5);
    private readonly TimeSpan _dropLagThreshold;

    public VideoMixer(VideoFormat outputFormat)
    {
        OutputFormat = outputFormat;

        // Keep lag tolerance near 2 frame intervals so the mixer can catch up quickly
        // on heavy streams (for example 4K60 software decode) without over-dropping.
        var fps = outputFormat.FrameRate;
        var byFrameRate = fps > 0
            ? TimeSpan.FromSeconds(2d / fps)
            : TimeSpan.FromMilliseconds(66);
        _dropLagThreshold = byFrameRate < TimeSpan.FromMilliseconds(30)
            ? TimeSpan.FromMilliseconds(30)
            : byFrameRate;

        Log.LogDebug("VideoMixer created: {Width}x{Height} @ {FrameRate}fps, dropLag={DropLagMs}ms",
            outputFormat.Width, outputFormat.Height, outputFormat.FrameRate, _dropLagThreshold.TotalMilliseconds);
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
    public int SinkCount => _sinkTargets.Length;

    /// <inheritdoc/>
    public IVideoChannel? ActiveChannel => _activeChannel;

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
            _channels = CopyOnWriteArray.Add(_channels, channel);

            if (_activeChannel is null)
                _activeChannel = channel;
        }
        Log.LogInformation("Video channel added: id={ChannelId}, total={ChannelCount}", channel.Id, _channels.Length);
    }

    /// <inheritdoc/>
    public void RemoveChannel(Guid channelId)
    {
        lock (_lock)
        {
            var old = _channels;
            int idx = CopyOnWriteArray.IndexOf(old, ch => ch.Id == channelId);
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

            _channels = CopyOnWriteArray.RemoveAt(old, idx);
            Volatile.Write(ref _channelOffsetTicks, _channelOffsetTicks.Remove(channelId));
        }
        Log.LogInformation("Video channel removed: id={ChannelId}", channelId);
    }

    /// <inheritdoc/>
    public void RouteChannelToPrimaryOutput(Guid channelId)
    {
        lock (_lock)
        {
            foreach (var ch in _channels)
            {
                if (ch.Id == channelId)
                {
                    _activeChannel = ch;
                    ResetLeaderState();
                    return;
                }
            }
        }

        throw new InvalidOperationException("Channel is not registered.");
    }

    /// <inheritdoc/>
    public void UnroutePrimaryOutput()
    {
        _activeChannel = null;
        ResetLeaderState();
    }

    /// <inheritdoc/>
    public void SetChannelTimeOffset(Guid channelId, TimeSpan offset)
    {
        lock (_lock)
        {
            bool found = false;
            foreach (var ch in _channels)
                if (ch.Id == channelId) { found = true; break; }
            if (!found)
                throw new InvalidOperationException("Channel is not registered.");

            var updated = offset == TimeSpan.Zero
                ? _channelOffsetTicks.Remove(channelId)
                : _channelOffsetTicks.SetItem(channelId, offset.Ticks);
            Volatile.Write(ref _channelOffsetTicks, updated);

            Log.LogInformation("Video channel time offset set: id={ChannelId}, offset={OffsetMs}ms",
                channelId, offset.TotalMilliseconds);
        }
    }

    /// <inheritdoc/>
    public TimeSpan GetChannelTimeOffset(Guid channelId)
    {
        var snap = Volatile.Read(ref _channelOffsetTicks);
        return snap.TryGetValue(channelId, out long ticks)
            ? TimeSpan.FromTicks(ticks)
            : TimeSpan.Zero;
    }

    private TimeSpan GetOffsetForChannel(IVideoChannel? channel)
    {
        if (channel is null) return TimeSpan.Zero;
        // Lock-free read: _channelOffsetTicks is an immutable snapshot replaced atomically.
        var snap = Volatile.Read(ref _channelOffsetTicks);
        return snap.TryGetValue(channel.Id, out long ticks)
            ? TimeSpan.FromTicks(ticks)
            : TimeSpan.Zero;
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
            _sinkTargets = CopyOnWriteArray.Add(old, new SinkTarget(sink));
        }
        Log.LogInformation("Video sink registered: type={SinkType}, total={SinkCount}", sink.GetType().Name, _sinkTargets.Length);
    }

    /// <inheritdoc/>
    public void UnregisterSink(IVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lock)
        {
            var old = _sinkTargets;
            int idx = CopyOnWriteArray.IndexOf(old, st => ReferenceEquals(st.Sink, sink));
            if (idx < 0) return;

            old[idx].LastFrame?.MemoryOwner?.Dispose();
            old[idx].StagedFrame?.MemoryOwner?.Dispose();

            _sinkTargets = CopyOnWriteArray.RemoveAt(old, idx);
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

            // Auto-unroute: if the sink is already routed to a different channel, reset it first.
            if (target.ActiveChannel is not null && target.ActiveChannel.Id != channelId.Value)
            {
                Log.LogInformation("Auto-unrouting sink from channel {OldId} before routing to {NewId}",
                    target.ActiveChannel.Id, channelId.Value);
                target.ActiveChannel = null;
                target.ResetState();
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

    // Reusable dictionary to avoid per-frame allocation in PresentNextFrame (§3.2).
    // Maps channel ID → frame already pulled this tick.  When multiple sinks are
    // routed to the same channel, the first sink pulls the frame; subsequent sinks
    // reuse it from this cache ("fan-out"), keeping all co-routed sinks frame-aligned
    // and avoiding redundant ring-buffer reads.
    private readonly Dictionary<Guid, VideoFrame?> _sharedChannelFrames = new();

    /// <inheritdoc/>
    public VideoFrame? PresentNextFrame(TimeSpan clockPosition)
    {
        Interlocked.Increment(ref _presentCallCount);

        var leaderOffset = GetOffsetForChannel(_activeChannel);
        var leaderClock = clockPosition - leaderOffset;
        var leader = PresentForTarget(_activeChannel, ref _stagedFrame, ref _lastFrame,
            ref _hasLeaderPtsOrigin, ref _leaderPtsOriginTicks,
            leaderClock,
            countSinkFormatStats: false);

        if (leader.HasValue)
            Interlocked.Increment(ref _leaderPresentCount);
        else
            Interlocked.Increment(ref _leaderNullCount);

        var sinks = _sinkTargets;
        _sharedChannelFrames.Clear();
        for (int i = 0; i < sinks.Length; i++)
        {
            var st = sinks[i];

            // Fan-out: when a sink is routed to the same channel as the leader, derive
            // its frame from the leader's current frame (_lastFrame) rather than pulling
            // another frame from the channel. This keeps all co-routed targets frame-aligned.
            if (st.ActiveChannel != null
                && _activeChannel   != null
                && ReferenceEquals(st.ActiveChannel, _activeChannel))
            {
                if (st.Sink.IsRunning && _lastFrame.HasValue)
                    DeliverFanOutFrame(st);
                continue;
            }

            if (st.ActiveChannel != null && _sharedChannelFrames.TryGetValue(st.ActiveChannel.Id, out var shared))
            {
                if (shared.HasValue && st.Sink.IsRunning)
                    DeliverSharedFrame(st, shared.Value, countPullAsFanOut: false);
                continue;
            }

            var sinkOffset = GetOffsetForChannel(st.ActiveChannel);
            var sinkClock = clockPosition - sinkOffset;
            var sinkFrame = PresentForTarget(st.ActiveChannel, ref st.StagedFrame, ref st.LastFrame,
                ref st.HasPtsOrigin, ref st.PtsOriginTicks,
                sinkClock,
                countSinkFormatStats: true,
                sink: st.Sink);
            if (st.ActiveChannel != null)
                _sharedChannelFrames[st.ActiveChannel.Id] = sinkFrame;
            if (sinkFrame.HasValue && st.Sink.IsRunning)
                st.Sink.ReceiveFrame(sinkFrame.Value);
        }

        return leader;
    }

    /// <summary>
    /// Delivers the leader's current frame to a sink that shares the leader's channel.
    /// Conversion is intentionally not performed in the mixer; sinks are responsible
    /// for converting to endpoint-specific formats.
    /// </summary>
    private void DeliverFanOutFrame(SinkTarget st)
    {
        DeliverSharedFrame(st, _lastFrame!.Value, countPullAsFanOut: true);
    }

    private void DeliverSharedFrame(SinkTarget st, in VideoFrame raw, bool countPullAsFanOut)
    {
        if (countPullAsFanOut)
        {
            Interlocked.Increment(ref _pullAttemptCount);
            Interlocked.Increment(ref _pullHitCount);
            Interlocked.Increment(ref _rawMarkerPassthroughCount);
        }

        if (st.Sink is IVideoSinkFormatCapabilities caps)
        {
            bool hit = false;
            for (int i = 0; i < caps.PreferredPixelFormats.Count; i++)
            {
                if (caps.PreferredPixelFormats[i] == raw.PixelFormat)
                {
                    hit = true;
                    break;
                }
            }
            if (hit) Interlocked.Increment(ref _sinkFormatHitCount);
            else Interlocked.Increment(ref _sinkFormatMissCount);
        }
        else
        {
            Interlocked.Increment(ref _sinkFormatHitCount);
        }

        st.Sink.ReceiveFrame(raw);
    }

    /// <summary>
    /// Core frame-presentation logic shared between the leader output and per-sink targets.
    /// Implements a simple two-slot (staged + last) pipeline:
    /// <list type="number">
    ///   <item><b>Pull</b>: if no frame is staged, pull one from the channel's ring buffer
    ///     and normalise its PTS relative to this target's origin.</item>
    ///   <item><b>Bootstrap</b>: if no frame has ever been shown, present the first available
    ///     frame immediately (avoids indefinite black on startup/seek).</item>
    ///   <item><b>Drop stale</b>: if the staged frame's PTS is more than
    ///     <see cref="_dropLagThreshold"/> behind the clock, discard it and pull the next
    ///     (lets the mixer catch up after a decode stall).</item>
    ///   <item><b>Advance</b>: if the staged frame's PTS is at or before
    ///     <c>clockPosition + LeadTolerance</c>, promote it to "last" (the displayed frame).</item>
    ///   <item><b>Hold</b>: otherwise keep displaying "last" until the staged frame's PTS
    ///     arrives.</item>
    /// </list>
    /// </summary>
    private VideoFrame? PresentForTarget(
        IVideoChannel? channel,
        ref VideoFrame? staged,
        ref VideoFrame? last,
        ref bool hasPtsOrigin,
        ref long ptsOriginTicks,
        TimeSpan clockPosition,
        bool countSinkFormatStats,
        IVideoSink? sink = null)
    {
        if (channel is null)
            return last;

        if (!staged.HasValue)
            staged = PullRawFrame(channel, ref hasPtsOrigin, ref ptsOriginTicks, countSinkFormatStats, sink);

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
            staged = PullRawFrame(channel, ref hasPtsOrigin, ref ptsOriginTicks, countSinkFormatStats, sink);
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

    private VideoFrame? PullRawFrame(
        IVideoChannel channel,
        ref bool hasPtsOrigin,
        ref long ptsOriginTicks,
        bool countSinkFormatStats,
        IVideoSink? sink)
    {
        Interlocked.Increment(ref _pullAttemptCount);
        int got = channel.FillBuffer(_pullBuffer, 1);
        if (got <= 0)
            return null;

        Interlocked.Increment(ref _pullHitCount);

        var raw = _pullBuffer[0];

        if (countSinkFormatStats)
        {
            bool hit = true;
            if (sink is IVideoSinkFormatCapabilities caps)
            {
                hit = false;
                for (int i = 0; i < caps.PreferredPixelFormats.Count; i++)
                {
                    if (caps.PreferredPixelFormats[i] == raw.PixelFormat)
                    {
                        hit = true;
                        break;
                    }
                }
            }

            if (hit) Interlocked.Increment(ref _sinkFormatHitCount);
            else Interlocked.Increment(ref _sinkFormatMissCount);
        }

        Interlocked.Increment(ref _rawMarkerPassthroughCount);
        return NormalizePts(raw, ref hasPtsOrigin, ref ptsOriginTicks);
    }

    /// <summary>
    /// Normalises a frame's PTS to be relative to the first frame seen by this target.
    /// Each target (leader + each sink) maintains its own origin so that targets started
    /// at different times or after a seek all count from zero independently.
    /// </summary>
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
        Log.LogInformation("Disposing VideoMixer: channels={ChannelCount}, sinks={SinkCount}", _channels.Length, _sinkTargets.Length);
        ResetLeaderState();
        foreach (var st in _sinkTargets)
            st.ResetState();
        _sinkTargets = [];
    }
}

