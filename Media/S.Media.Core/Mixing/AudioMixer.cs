using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;

namespace S.Media.Core.Mixing;

using S.Media.Core.Audio;

/// <summary>
/// Concrete implementation of <see cref="IAudioMixer"/>.
///
/// <para>
/// Each registered <see cref="IAudioSink"/> gets its own independent mix buffer built from
/// only the channels explicitly routed to it via <see cref="RouteTo"/> (or all channels when
/// <see cref="ChannelFallback.Broadcast"/> is configured). <see cref="IAudioSink.ReceiveBuffer"/>
/// is called directly from inside <see cref="FillOutputBuffer"/> on the RT thread.
/// </para>
/// <para>
/// Sample-rate conversion is always source → leader rate (one pass per channel, shared across all
/// sinks). Sinks that require a different sample rate should use an internal resampler.
/// </para>
/// </summary>
internal sealed class AudioMixer : IAudioMixer
{
    private const int DefaultPreallocatedFrames = 1024;

    // ── Nested types ──────────────────────────────────────────────────────

    private sealed class SinkTarget
    {
        public readonly IAudioSink Sink;
        public          int        Channels;   // 0 = resolve lazily from leader channel count
        public          AudioFormat SinkFormat;
        public          float[]    MixBuffer = [];
        public SinkTarget(IAudioSink sink, int channels, int sampleRate)
        {
            Sink = sink;
            Channels = channels;
            SinkFormat = new AudioFormat(sampleRate, channels > 0 ? channels : 1);
        }
    }

    private sealed class SinkRoute
    {
        public readonly SinkTarget           Target;
        public readonly (int dst, float gain)[][] BakedRoutes;
        public SinkRoute(SinkTarget target, (int dst, float gain)[][] baked)
        { Target = target; BakedRoutes = baked; }
    }

    private sealed class ChannelSlot
    {
        public readonly IAudioChannel              Channel;
        public readonly IAudioResampler?           Resampler;         // src→leader; null if same rate
        public readonly (int dst, float gain)[][]  LeaderBakedRoutes;
        public readonly bool                       OwnsResampler;

        public float[] SrcBuf      = [];
        public float[] ResampleBuf = [];

        // Copy-on-write; volatile so RT path gets a consistent snapshot without locking.
        public volatile SinkRoute[] SinkRoutes = [];
        public volatile SinkRoute?[] SinkRouteBySinkIndex = [];

        // ── Per-channel time offset ────────────────────────────────────
        // Positive = delay (insert silence), negative = advance (discard frames).
        // Written under _editLock; read on RT path via Volatile.Read.
        public TimeSpan TimeOffset;
        // Remaining frames of silence to insert (>0) or frames to discard (<0).
        // Decremented on the RT path. Atomic long for RT safety.
        public long OffsetFramesRemaining;

        public ChannelSlot(IAudioChannel ch, IAudioResampler? rs,
                           (int dst, float gain)[][] leaderBaked, bool ownsRs)
        {
            Channel           = ch;
            Resampler         = rs;
            LeaderBakedRoutes = leaderBaked;
            OwnsResampler     = ownsRs;
        }
    }

    // ── State ─────────────────────────────────────────────────────────────

    private volatile ChannelSlot[] _slots       = [];
    private volatile SinkTarget[]  _sinkTargets = [];
    private readonly Lock        _editLock    = new();

    private float[] _mixBuffer    = [];
    private float[] _peakLevels   = [];
    private float[] _peakSnapshot = [];

    private int  _framesPerBuffer;
    private bool _disposed;
    private volatile float _masterVolume = 1.0f;
    private long _rtLeaderCapacityMisses;
    private long _rtSinkCapacityMisses;
    private long _rtSlotCapacityMisses;

    private static readonly ILogger Log = MediaCoreLogging.GetLogger(nameof(AudioMixer));

    // ── IAudioMixer ───────────────────────────────────────────────────────

    public AudioFormat     LeaderFormat    { get; }
    public int             ChannelCount    => _slots.Length;
    public ChannelFallback DefaultFallback { get; }

    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Max(0f, value);
    }

    public IReadOnlyList<float> PeakLevels => _peakSnapshot;
    public long RtLeaderCapacityMisses => Interlocked.Read(ref _rtLeaderCapacityMisses);
    public long RtSinkCapacityMisses   => Interlocked.Read(ref _rtSinkCapacityMisses);
    public long RtSlotCapacityMisses   => Interlocked.Read(ref _rtSlotCapacityMisses);

    /// <param name="leaderFormat">
    /// Audio format of the leader output (sample rate + channel count).
    /// Used for resampler decisions and mix-buffer sizing.
    /// </param>
    /// <param name="defaultFallback">
    /// Routing policy for channels that have no explicit sink route.
    /// Defaults to <see cref="ChannelFallback.Silent"/>.
    /// </param>
    public AudioMixer(AudioFormat leaderFormat, ChannelFallback defaultFallback = ChannelFallback.Silent)
    {
        LeaderFormat    = leaderFormat;
        DefaultFallback = defaultFallback;

        // Keep RT path allocation-free even before explicit PrepareBuffers()
        // by provisioning a conservative startup capacity.
        _framesPerBuffer = DefaultPreallocatedFrames;
        EnsureWorkBuffers(_framesPerBuffer * LeaderFormat.Channels, LeaderFormat.Channels);
        Log.LogInformation("AudioMixer created: {SampleRate}Hz, {Channels}ch, fallback={Fallback}",
            leaderFormat.SampleRate, leaderFormat.Channels, defaultFallback);
    }

    // ── Pre-allocation ────────────────────────────────────────────────────

    public void PrepareBuffers(int framesPerBuffer)
    {
        if (framesPerBuffer <= 0) return;
        _framesPerBuffer = framesPerBuffer;
        int outCh = LeaderFormat.Channels;
        EnsureWorkBuffers(framesPerBuffer * outCh, outCh);

        foreach (var slot in _slots)
            AllocateSlotBuffers(slot, framesPerBuffer);

        foreach (var st in _sinkTargets)
        {
            int ch = ResolvedSinkChannels(st, outCh);
            st.Channels  = ch;
            st.SinkFormat = new AudioFormat(LeaderFormat.SampleRate, ch);
            st.MixBuffer = new float[framesPerBuffer * ch];
        }
    }

    private void AllocateSlotBuffers(ChannelSlot slot, int framesPerBuffer)
    {
        var srcFmt    = slot.Channel.SourceFormat;
        int srcCh     = srcFmt.Channels;
        bool sameRate = slot.Resampler == null;

        int srcFrames = sameRate
            ? framesPerBuffer
            : (int)Math.Ceiling(framesPerBuffer * ((double)srcFmt.SampleRate / LeaderFormat.SampleRate)) + 1;

        slot.SrcBuf = new float[srcFrames * srcCh];
        // ResampleBuf is only needed when resampling; same-rate slots use SrcBuf directly.
        slot.ResampleBuf = sameRate ? [] : new float[framesPerBuffer * srcCh];
    }

    private static int ResolvedSinkChannels(SinkTarget st, int leaderChannels)
        => st.Channels > 0 ? st.Channels : leaderChannels;

    // ── Channel management ────────────────────────────────────────────────

    public void AddChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(routeMap);

        bool ownsResampler = false;
        bool sameRate = channel.SourceFormat.SampleRate == LeaderFormat.SampleRate;

        if (resampler == null && !sameRate)
        {
            resampler     = new LinearResampler();
            ownsResampler = true;
        }

        Log.LogDebug("AddChannel: id={ChannelId} format={SampleRate}Hz/{Channels}ch sameRate={SameRate} resampler={HasResampler}",
            channel.Id, channel.SourceFormat.SampleRate, channel.SourceFormat.Channels, sameRate, resampler != null);

        var baked = routeMap.BakeRoutes(channel.SourceFormat.Channels);
        var slot  = new ChannelSlot(channel, resampler, baked, ownsResampler);

        if (_framesPerBuffer > 0)
            AllocateSlotBuffers(slot, _framesPerBuffer);

        lock (_editLock)
        {
            // Broadcast: pre-create default sink routes for all already-registered sinks.
            if (DefaultFallback == ChannelFallback.Broadcast)
            {
                var sinkTargets = _sinkTargets;
                if (sinkTargets.Length > 0)
                {
                    var initialRoutes = new SinkRoute[sinkTargets.Length];
                    for (int i = 0; i < sinkTargets.Length; i++)
                        initialRoutes[i] = new SinkRoute(sinkTargets[i], baked);
                    slot.SinkRoutes = initialRoutes;
                }
            }

            slot.SinkRouteBySinkIndex = BuildSinkRouteIndex(slot.SinkRoutes, _sinkTargets);

            var old = _slots;
            var neo = new ChannelSlot[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = slot;
            _slots  = neo;
        }

        Log.LogInformation("Channel added: id={ChannelId}, total channels={Count}", channel.Id, _slots.Length);
    }

    public void RemoveChannel(Guid channelId)
    {
        lock (_editLock)
        {
            var old = _slots;
            int idx = -1;
            for (int i = 0; i < old.Length; i++)
                if (old[i].Channel.Id == channelId) { idx = i; break; }
            if (idx < 0)
            {
                Log.LogDebug("RemoveChannel: id={ChannelId} not found", channelId);
                return;
            }

            var neo = new ChannelSlot[old.Length - 1];
            for (int i = 0, j = 0; i < old.Length; i++)
            {
                if (i == idx) { if (old[i].OwnsResampler) old[i].Resampler?.Dispose(); continue; }
                neo[j++] = old[i];
            }
            _slots = neo;
        }
        Log.LogInformation("Channel removed: id={ChannelId}, total channels={Count}", channelId, _slots.Length);
    }

    // ── Per-channel time offset ────────────────────────────────────────────

    public void SetChannelTimeOffset(Guid channelId, TimeSpan offset)
    {
        lock (_editLock)
        {
            var slot = FindSlot(channelId)
                ?? throw new InvalidOperationException("Channel is not registered.");

            slot.TimeOffset = offset;
            long frames = (long)(Math.Abs(offset.TotalSeconds) * LeaderFormat.SampleRate);
            // Positive = delay (silence); negative = advance (discard).
            slot.OffsetFramesRemaining = offset >= TimeSpan.Zero ? frames : -frames;

            Log.LogInformation("Audio channel time offset set: id={ChannelId}, offset={OffsetMs}ms, frames={Frames}",
                channelId, offset.TotalMilliseconds, slot.OffsetFramesRemaining);
        }
    }

    public TimeSpan GetChannelTimeOffset(Guid channelId)
    {
        lock (_editLock)
        {
            var slot = FindSlot(channelId);
            return slot?.TimeOffset ?? TimeSpan.Zero;
        }
    }

    // ── Per-sink routing ──────────────────────────────────────────────────

    public void RouteTo(Guid channelId, IAudioSink sink, ChannelRouteMap routeMap)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(routeMap);
        lock (_editLock)
        {
            var slot = FindSlot(channelId);
            if (slot == null) return;

            var target = FindTarget(sink)
                ?? throw new InvalidOperationException(
                    "Sink is not registered. Call AggregateOutput.AddSink (or Mixer.RegisterSink) first.");

            var baked = routeMap.BakeRoutes(slot.Channel.SourceFormat.Channels);
            SetSinkRouteOnSlot(slot, target, baked);
            slot.SinkRouteBySinkIndex = BuildSinkRouteIndex(slot.SinkRoutes, _sinkTargets);
        }
    }

    public void UnrouteTo(Guid channelId, IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_editLock)
        {
            var slot   = FindSlot(channelId);   if (slot   == null) return;
            var target = FindTarget(sink);       if (target == null) return;
            RemoveSinkRouteFromSlot(slot, target);
            slot.SinkRouteBySinkIndex = BuildSinkRouteIndex(slot.SinkRoutes, _sinkTargets);
        }
    }

    // ── Sink registration ─────────────────────────────────────────────────

    public void RegisterSink(IAudioSink sink, int channels = 0)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_editLock)
        {
            if (FindTarget(sink) != null) return; // idempotent

            int ch = channels > 0 ? channels : TryGetLeaderChannels();
            var target = new SinkTarget(sink, ch, LeaderFormat.SampleRate);
            Log.LogDebug("RegisterSink: type={SinkType} channels={Channels}", sink.GetType().Name, ch);

            if (_framesPerBuffer > 0 && ch > 0)
                target.MixBuffer = new float[_framesPerBuffer * ch];

            if (DefaultFallback == ChannelFallback.Broadcast)
                foreach (var slot in _slots)
                    SetSinkRouteOnSlot(slot, target, slot.LeaderBakedRoutes);

            var old = _sinkTargets;
            var neo = new SinkTarget[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = target;
            _sinkTargets = neo;

            foreach (var slot in _slots)
                slot.SinkRouteBySinkIndex = BuildSinkRouteIndex(slot.SinkRoutes, neo);
        }
        Log.LogInformation("Sink registered: type={SinkType}, total sinks={Count}", sink.GetType().Name, _sinkTargets.Length);
    }

    public void UnregisterSink(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_editLock)
        {
            var old = _sinkTargets;
            int idx = -1;
            for (int i = 0; i < old.Length; i++)
                if (ReferenceEquals(old[i].Sink, sink)) { idx = i; break; }
            if (idx < 0) return;

            var target = old[idx];
            foreach (var slot in _slots)
                RemoveSinkRouteFromSlot(slot, target);

            var neo = new SinkTarget[old.Length - 1];
            for (int i = 0, j = 0; i < old.Length; i++)
                if (i != idx) neo[j++] = old[i];
            _sinkTargets = neo;

            foreach (var slot in _slots)
                slot.SinkRouteBySinkIndex = BuildSinkRouteIndex(slot.SinkRoutes, neo);
        }
        Log.LogInformation("Sink unregistered: type={SinkType}, total sinks={Count}", sink.GetType().Name, _sinkTargets.Length);
    }

    // ── FillOutputBuffer — RT hot path (no alloc, no lock) ───────────────

    public void FillOutputBuffer(Span<float> dest, int frameCount, AudioFormat outputFormat)
    {
        int outCh      = outputFormat.Channels;
        int outSamples = frameCount * outCh;

        if (_mixBuffer.Length < outSamples || _peakLevels.Length < outCh || _peakSnapshot.Length < outCh)
        {
            dest.Clear();
            Interlocked.Increment(ref _rtLeaderCapacityMisses);
            return;
        }

        _mixBuffer.AsSpan(0, outSamples).Clear();

        var sinkTargets = _sinkTargets;
        for (int si = 0; si < sinkTargets.Length; si++)
        {
            var st     = sinkTargets[si];
            int sinkCh = ResolvedSinkChannels(st, outCh);
            int sinkN  = frameCount * sinkCh;
            if (st.MixBuffer.Length < sinkN)
            {
                Interlocked.Increment(ref _rtSinkCapacityMisses);
                continue;
            }

            st.MixBuffer.AsSpan(0, sinkN).Clear();
        }

        var slots = _slots;
        foreach (var slot in slots)
        {
            var srcFmt    = slot.Channel.SourceFormat;
            int srcCh     = srcFmt.Channels;
            bool sameRate = slot.Resampler == null;

            int mixFrames = frameCount;
            int srcFrameStart = 0;
            int dstFrameStart = 0;

            // 0. Handle time offset (delay = insert silence, advance = discard frames).
            long offsetRemaining = Volatile.Read(ref slot.OffsetFramesRemaining);
            if (offsetRemaining > 0)
            {
                // Delay: output silence for this slot.
                if (offsetRemaining >= frameCount)
                {
                    Volatile.Write(ref slot.OffsetFramesRemaining, offsetRemaining - frameCount);
                    // SrcBuf stays zeroed — skip pull, fill with silence effectively
                    // (the slot contributes nothing to the mix this cycle).
                    continue;
                }

                // Partial delay: fill the first part with silence and mix the remainder.
                int delayFrames = (int)Math.Min(offsetRemaining, frameCount);
                Volatile.Write(ref slot.OffsetFramesRemaining, 0);
                dstFrameStart = delayFrames;
                mixFrames = frameCount - delayFrames;
            }
            else if (offsetRemaining < 0)
            {
                // Advance: pull-and-discard frames from the channel.
                long toDiscard = -offsetRemaining;
                if (toDiscard >= frameCount)
                {
                    // Pull a full buffer and discard it entirely.
                    int discardSrcFrames = sameRate
                        ? frameCount
                        : slot.Resampler!.GetRequiredInputFrames(frameCount, srcFmt, outputFormat.SampleRate);
                    int discardSrcSamples = discardSrcFrames * srcCh;
                    if (slot.SrcBuf.Length < discardSrcSamples)
                    {
                        Interlocked.Increment(ref _rtSlotCapacityMisses);
                        continue;
                    }

                    slot.Channel.FillBuffer(slot.SrcBuf.AsSpan(0, discardSrcSamples), discardSrcFrames);
                    Volatile.Write(ref slot.OffsetFramesRemaining, offsetRemaining + frameCount);
                    continue;
                }

                // Partial advance: discard within the same callback by skipping
                // a prefix of the pulled audio for this cycle.
                int discardFrames = (int)Math.Min(toDiscard, frameCount);
                Volatile.Write(ref slot.OffsetFramesRemaining, 0);
                srcFrameStart = discardFrames;
                mixFrames = frameCount - discardFrames;
            }

            if (mixFrames <= 0)
                continue;

            int requiredOutFrames = mixFrames + srcFrameStart;

            // Use the resampler's own frame-count calculation to account for
            // internally buffered pending frames.
            int srcFrames  = sameRate ? requiredOutFrames
                : slot.Resampler!.GetRequiredInputFrames(requiredOutFrames, srcFmt, outputFormat.SampleRate);
            int srcSamples = srcFrames * srcCh;

            if (slot.SrcBuf.Length < srcSamples || (!sameRate && slot.ResampleBuf.Length < requiredOutFrames * srcCh))
            {
                Interlocked.Increment(ref _rtSlotCapacityMisses);
                continue;
            }

            // 1. Pull
            slot.Channel.FillBuffer(slot.SrcBuf.AsSpan(0, srcSamples), srcFrames);

            // 2. Resample to leader rate — or alias SrcBuf directly (zero-copy).
            float[] activeBuf;
            int activeSamples;
            if (sameRate)
            {
                activeBuf     = slot.SrcBuf;
                activeSamples = requiredOutFrames * srcCh;
            }
            else
            {
                slot.Resampler!.Resample(
                    slot.SrcBuf.AsSpan(0, srcSamples),
                    slot.ResampleBuf.AsSpan(0, requiredOutFrames * srcCh),
                    srcFmt, outputFormat.SampleRate);
                activeBuf     = slot.ResampleBuf;
                activeSamples = requiredOutFrames * srcCh;
            }

            // 3. Per-channel volume
            float vol = slot.Channel.Volume;
            if (Math.Abs(vol - 1.0f) > 1e-5f)
                MultiplyInPlace(activeBuf.AsSpan(0, activeSamples), vol);

            // 4. Scatter into leader mix buffer
            ScatterIntoMix(_mixBuffer, activeBuf, mixFrames, srcCh, outCh, slot.LeaderBakedRoutes, srcFrameStart, dstFrameStart);

            // 5. Scatter into each sink's mix buffer
            var sinkRoutesByIndex = slot.SinkRouteBySinkIndex;
            for (int si = 0; si < sinkTargets.Length; si++)
            {
                var st     = sinkTargets[si];
                int sinkCh = ResolvedSinkChannels(st, outCh);
                int sinkN  = frameCount * sinkCh;
                if (st.MixBuffer.Length < sinkN)
                    continue;

                SinkRoute? route = si < sinkRoutesByIndex.Length ? sinkRoutesByIndex[si] : null;

                if (route != null)
                    ScatterIntoMix(st.MixBuffer, activeBuf, mixFrames, srcCh, sinkCh, route.BakedRoutes, srcFrameStart, dstFrameStart);
                else if (DefaultFallback == ChannelFallback.Broadcast)
                    ScatterIntoMix(st.MixBuffer, activeBuf, mixFrames, srcCh, sinkCh, slot.LeaderBakedRoutes, srcFrameStart, dstFrameStart);
            }
        }

        // 6. Master volume (leader + all sinks)
        float mv    = _masterVolume;
        bool applyMv = Math.Abs(mv - 1.0f) > 1e-5f;
        if (applyMv)
        {
            MultiplyInPlace(_mixBuffer.AsSpan(0, outSamples), mv);
            for (int si = 0; si < sinkTargets.Length; si++)
            {
                var st     = sinkTargets[si];
                int sinkCh = ResolvedSinkChannels(st, outCh);
                int sinkN  = frameCount * sinkCh;
                if (st.MixBuffer.Length < sinkN)
                    continue;

                MultiplyInPlace(st.MixBuffer.AsSpan(0, sinkN), mv);
            }
        }

        // 7. Peak levels (leader)
        UpdatePeaks(_mixBuffer.AsSpan(0, outSamples), outCh);

        // 8. Copy leader mix to hardware buffer
        _mixBuffer.AsSpan(0, outSamples).CopyTo(dest);

        // 9. Distribute per-sink mixes (RT-safe: no alloc, no lock)
        for (int si = 0; si < sinkTargets.Length; si++)
        {
            var st = sinkTargets[si];
            if (!st.Sink.IsRunning) continue;
            int sinkCh  = ResolvedSinkChannels(st, outCh);
            int sinkN   = frameCount * sinkCh;
            if (st.MixBuffer.Length < sinkN)
                continue;

            st.Sink.ReceiveBuffer(st.MixBuffer.AsSpan(0, sinkN), frameCount, st.SinkFormat);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private ChannelSlot? FindSlot(Guid channelId)
    {
        foreach (var s in _slots)
            if (s.Channel.Id == channelId) return s;
        return null;
    }

    private SinkTarget? FindTarget(IAudioSink sink)
    {
        foreach (var st in _sinkTargets)
            if (ReferenceEquals(st.Sink, sink)) return st;
        return null;
    }

    private int TryGetLeaderChannels() => LeaderFormat.Channels;

    private static void SetSinkRouteOnSlot(ChannelSlot slot, SinkTarget target,
                                           (int dst, float gain)[][] baked)
    {
        var old = slot.SinkRoutes;
        int existingIdx = -1;
        for (int i = 0; i < old.Length; i++)
            if (ReferenceEquals(old[i].Target, target)) { existingIdx = i; break; }

        SinkRoute[] neo;
        if (existingIdx >= 0)
        {
            neo = new SinkRoute[old.Length];
            old.CopyTo(neo, 0);
            neo[existingIdx] = new SinkRoute(target, baked);
        }
        else
        {
            neo = new SinkRoute[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = new SinkRoute(target, baked);
        }
        slot.SinkRoutes = neo;
    }

    private static void RemoveSinkRouteFromSlot(ChannelSlot slot, SinkTarget target)
    {
        var old = slot.SinkRoutes;
        int idx = -1;
        for (int i = 0; i < old.Length; i++)
            if (ReferenceEquals(old[i].Target, target)) { idx = i; break; }
        if (idx < 0) return;

        var neo = new SinkRoute[old.Length - 1];
        for (int i = 0, j = 0; i < old.Length; i++)
            if (i != idx) neo[j++] = old[i];
        slot.SinkRoutes = neo;
    }

    private static SinkRoute?[] BuildSinkRouteIndex(SinkRoute[] routes, SinkTarget[] sinkTargets)
    {
        var byIndex = new SinkRoute?[sinkTargets.Length];
        for (int si = 0; si < sinkTargets.Length; si++)
        {
            var target = sinkTargets[si];
            for (int ri = 0; ri < routes.Length; ri++)
            {
                if (!ReferenceEquals(routes[ri].Target, target)) continue;
                byIndex[si] = routes[ri];
                break;
            }
        }

        return byIndex;
    }

    private void EnsureWorkBuffers(int outSamples, int outCh)
    {
        if (_mixBuffer.Length >= outSamples) return;
        _mixBuffer    = new float[outSamples];
        _peakLevels   = new float[outCh];
        _peakSnapshot = new float[outCh];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MultiplyInPlace(Span<float> buf, float gain)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && buf.Length >= Vector<float>.Count)
        {
            var vGain = new Vector<float>(gain);
            int vecEnd = buf.Length - (buf.Length % Vector<float>.Count);
            for (; i < vecEnd; i += Vector<float>.Count)
            {
                var v = new Vector<float>(buf[i..]);
                (v * vGain).CopyTo(buf[i..]);
            }
        }
        for (; i < buf.Length; i++) buf[i] *= gain;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScatterIntoMix(
        float[] mix, float[] src,
        int frameCount, int srcCh, int dstCh,
        (int dst, float gain)[][] bakedRoutes,
        int srcFrameOffset = 0,
        int dstFrameOffset = 0)
    {
        // Source-channel-outer / frame-inner: loads the route array once per
        // source channel instead of once per frame, and uses running offsets
        // to avoid per-frame multiplication.
        for (int sc = 0; sc < srcCh; sc++)
        {
            var routes = bakedRoutes[sc];
            if (routes.Length == 0) continue;

            // Fast path: single route (identity, mono→stereo, etc.)
            if (routes.Length == 1)
            {
                int dc   = routes[0].dst;
                float g  = routes[0].gain;
                if (dc >= dstCh) continue;
                int srcOff = (srcFrameOffset * srcCh) + sc;
                int dstOff = (dstFrameOffset * dstCh) + dc;
                for (int f = 0; f < frameCount; f++)
                {
                    mix[dstOff] += src[srcOff] * g;
                    srcOff += srcCh;
                    dstOff += dstCh;
                }
                continue;
            }

            // General path: multiple routes per source channel.
            {
                int srcOff = (srcFrameOffset * srcCh) + sc;
                int dstBase = dstFrameOffset * dstCh;
                for (int f = 0; f < frameCount; f++)
                {
                    float sample = src[srcOff];
                    for (int r = 0; r < routes.Length; r++)
                    {
                        int dc = routes[r].dst;
                        if (dc < dstCh)
                            mix[dstBase + dc] += sample * routes[r].gain;
                    }
                    srcOff  += srcCh;
                    dstBase += dstCh;
                }
            }
        }
    }

    private void UpdatePeaks(Span<float> buf, int outCh)
    {
        Array.Clear(_peakLevels);
        int frames = buf.Length / outCh;
        for (int f = 0; f < frames; f++)
        {
            int offset = f * outCh;
            for (int ch = 0; ch < outCh; ch++)
            {
                float a = Math.Abs(buf[offset + ch]);
                if (a > _peakLevels[ch]) _peakLevels[ch] = a;
            }
        }
        _peakLevels.AsSpan().CopyTo(_peakSnapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("AudioMixer disposing ({SlotCount} channels, {SinkCount} sinks)", _slots.Length, _sinkTargets.Length);
        lock (_editLock)
        {
            foreach (var s in _slots)
                if (s.OwnsResampler) s.Resampler?.Dispose();
            _slots       = [];
            _sinkTargets = [];
        }
        Log.LogDebug("AudioMixer disposed");
    }
}

