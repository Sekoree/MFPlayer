using System.Runtime.CompilerServices;
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

        slot.SrcBuf      = new float[srcFrames * srcCh];
        slot.ResampleBuf = new float[framesPerBuffer * srcCh];
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
    }

    public void RemoveChannel(Guid channelId)
    {
        lock (_editLock)
        {
            var old = _slots;
            int idx = -1;
            for (int i = 0; i < old.Length; i++)
                if (old[i].Channel.Id == channelId) { idx = i; break; }
            if (idx < 0) return;

            var neo = new ChannelSlot[old.Length - 1];
            for (int i = 0, j = 0; i < old.Length; i++)
            {
                if (i == idx) { if (old[i].OwnsResampler) old[i].Resampler?.Dispose(); continue; }
                neo[j++] = old[i];
            }
            _slots = neo;
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

            int srcFrames  = sameRate ? frameCount
                : (int)Math.Ceiling(frameCount * ((double)srcFmt.SampleRate / outputFormat.SampleRate)) + 1;
            int srcSamples = srcFrames * srcCh;

            if (slot.SrcBuf.Length < srcSamples || slot.ResampleBuf.Length < frameCount * srcCh)
            {
                Interlocked.Increment(ref _rtSlotCapacityMisses);
                continue;
            }

            // 1. Pull
            slot.Channel.FillBuffer(slot.SrcBuf.AsSpan(0, srcSamples), srcFrames);

            // 2. Resample to leader rate (or direct copy)
            if (sameRate)
                slot.SrcBuf.AsSpan(0, frameCount * srcCh)
                    .CopyTo(slot.ResampleBuf.AsSpan(0, frameCount * srcCh));
            else
                slot.Resampler!.Resample(
                    slot.SrcBuf.AsSpan(0, srcSamples),
                    slot.ResampleBuf.AsSpan(0, frameCount * srcCh),
                    srcFmt, outputFormat.SampleRate);

            // 3. Per-channel volume
            float vol = slot.Channel.Volume;
            if (Math.Abs(vol - 1.0f) > 1e-5f)
                MultiplyInPlace(slot.ResampleBuf.AsSpan(0, frameCount * srcCh), vol);

            // 4. Scatter into leader mix buffer
            ScatterIntoMix(_mixBuffer, slot.ResampleBuf, frameCount, srcCh, outCh, slot.LeaderBakedRoutes);

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
                    ScatterIntoMix(st.MixBuffer, slot.ResampleBuf, frameCount, srcCh, sinkCh, route.BakedRoutes);
                else if (DefaultFallback == ChannelFallback.Broadcast)
                    ScatterIntoMix(st.MixBuffer, slot.ResampleBuf, frameCount, srcCh, sinkCh, slot.LeaderBakedRoutes);
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

    private int TryGetLeaderChannels()
    {
        try   { return LeaderFormat.Channels; }
        catch { return 0; }
    }

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
        for (int i = 0; i < buf.Length; i++) buf[i] *= gain;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScatterIntoMix(
        float[] mix, float[] src,
        int frameCount, int srcCh, int dstCh,
        (int dst, float gain)[][] bakedRoutes)
    {
        for (int f = 0; f < frameCount; f++)
            for (int sc = 0; sc < srcCh; sc++)
            {
                float sample = src[f * srcCh + sc];
                var routes   = bakedRoutes[sc];
                for (int r = 0; r < routes.Length; r++)
                {
                    int dc = routes[r].dst;
                    if (dc < dstCh)
                        mix[f * dstCh + dc] += sample * routes[r].gain;
                }
            }
    }

    private void UpdatePeaks(Span<float> buf, int outCh)
    {
        Array.Clear(_peakLevels);
        for (int i = 0; i < buf.Length; i++)
        {
            float a = Math.Abs(buf[i]);
            if (a > _peakLevels[i % outCh]) _peakLevels[i % outCh] = a;
        }
        _peakLevels.AsSpan().CopyTo(_peakSnapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_editLock)
        {
            foreach (var s in _slots)
                if (s.OwnsResampler) s.Resampler?.Dispose();
            _slots       = [];
            _sinkTargets = [];
        }
    }
}

