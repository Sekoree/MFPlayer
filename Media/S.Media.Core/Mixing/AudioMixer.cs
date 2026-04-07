using System.Runtime.CompilerServices;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;

namespace S.Media.Core.Mixing;

using S.Media.Core.Audio;

/// <summary>
/// Concrete implementation of <see cref="IAudioMixer"/>.
/// Thread-safe channel management; RT-safe (allocation-free) <see cref="FillOutputBuffer"/>.
/// </summary>
public sealed class AudioMixer : IAudioMixer
{
    // ── Channel slot ──────────────────────────────────────────────────────

    private sealed class ChannelSlot
    {
        public readonly IAudioChannel          Channel;
        public readonly IAudioResampler        Resampler;
        public readonly (int dst, float gain)[][] BakedRoutes; // [srcCh][routeIdx]
        public readonly bool                   OwnsResampler;

        // Pre-allocated scratch buffers (sized on first FillOutputBuffer call)
        public float[] SrcBuf      = [];
        public float[] ResampleBuf = [];

        public ChannelSlot(IAudioChannel ch, IAudioResampler rs,
                           (int, float)[][] baked, bool ownsRs)
        {
            Channel       = ch;
            Resampler     = rs;
            BakedRoutes   = baked;
            OwnsResampler = ownsRs;
        }
    }

    // ── State ─────────────────────────────────────────────────────────────

    // Snapshot array — replaced atomically on Add/Remove; never mutated in-place.
    private volatile ChannelSlot[] _slots = [];
    private readonly object        _editLock = new();

    private float[] _mixBuffer   = [];
    private float[] _peakLevels  = [];
    private float[] _peakSnapshot= [];  // exposed via PeakLevels (avoids array allocation on get)

    private bool _disposed;

    // ── IAudioMixer ───────────────────────────────────────────────────────

    public IAudioOutput Output       { get; }
    public int          ChannelCount => _slots.Length;

    private volatile float _masterVolume = 1.0f;
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Max(0f, value);
    }

    public IReadOnlyList<float> PeakLevels => _peakSnapshot;

    public AudioMixer(IAudioOutput output)
    {
        Output = output ?? throw new ArgumentNullException(nameof(output));
    }

    // ── Channel management (user thread) ─────────────────────────────────

    public void AddChannel(
        IAudioChannel    channel,
        ChannelRouteMap  routeMap,
        IAudioResampler? resampler = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(routeMap);

        bool ownsResampler = false;
        if (resampler == null &&
            channel.SourceFormat.SampleRate != Output.HardwareFormat.SampleRate)
        {
            resampler     = new LinearResampler();
            ownsResampler = true;
        }
        resampler ??= new LinearResampler(); // identity path (rates match → fast copy)

        var baked = routeMap.BakeRoutes(channel.SourceFormat.Channels);
        var slot  = new ChannelSlot(channel, resampler, baked, ownsResampler);

        lock (_editLock)
        {
            var old = _slots;
            var neo = new ChannelSlot[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1]  = slot;
            _slots   = neo; // volatile write — RT thread sees new array atomically
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
                if (i == idx)
                {
                    if (old[i].OwnsResampler) old[i].Resampler.Dispose();
                    continue;
                }
                neo[j++] = old[i];
            }
            _slots = neo;
        }
    }

    // ── FillOutputBuffer — RT hot path (no alloc, no lock) ───────────────

    public void FillOutputBuffer(Span<float> dest, int frameCount, AudioFormat outputFormat)
    {
        int outCh      = outputFormat.Channels;
        int outSamples = frameCount * outCh;

        // One-time lazy allocation (first call only; PA always uses same frameCount).
        EnsureWorkBuffers(outSamples, outCh);

        // Zero mix buffer
        _mixBuffer.AsSpan(0, outSamples).Clear();

        // Snapshot — no lock; volatile read is atomic for reference types.
        var slots = _slots;

        foreach (var slot in slots)
        {
            var srcFmt     = slot.Channel.SourceFormat;
            int srcCh      = srcFmt.Channels;
            double ratio   = (double)srcFmt.SampleRate / outputFormat.SampleRate;
            int srcFrames  = (int)Math.Ceiling(frameCount * ratio) + 1;
            int srcSamples = srcFrames * srcCh;

            // Ensure per-slot scratch buffers
            if (slot.SrcBuf.Length < srcSamples)
                slot.SrcBuf = new float[srcSamples];
            if (slot.ResampleBuf.Length < frameCount * srcCh)
                slot.ResampleBuf = new float[frameCount * srcCh];

            // 1. Pull
            slot.Channel.FillBuffer(slot.SrcBuf.AsSpan(0, srcSamples), srcFrames);

            // 2. Resample (rate only; channels preserved)
            slot.Resampler.Resample(
                slot.SrcBuf.AsSpan(0, srcSamples),
                slot.ResampleBuf.AsSpan(0, frameCount * srcCh),
                srcFmt, outputFormat.SampleRate);

            // 3. Apply channel volume
            float vol = slot.Channel.Volume;
            if (Math.Abs(vol - 1.0f) > 1e-5f)
                MultiplyInPlace(slot.ResampleBuf.AsSpan(0, frameCount * srcCh), vol);

            // 4. Scatter via route map
            ScatterIntoMix(_mixBuffer, slot.ResampleBuf, frameCount, srcCh, outCh, slot.BakedRoutes);
        }

        // 5. Master volume
        float mv = _masterVolume;
        if (Math.Abs(mv - 1.0f) > 1e-5f)
            MultiplyInPlace(_mixBuffer.AsSpan(0, outSamples), mv);

        // 6. Update peak levels
        UpdatePeaks(_mixBuffer.AsSpan(0, outSamples), outCh);

        // 7. Copy to output
        _mixBuffer.AsSpan(0, outSamples).CopyTo(dest);
    }

    // ── Private helpers ───────────────────────────────────────────────────

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
        for (int i = 0; i < buf.Length; i++)
            buf[i] *= gain;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScatterIntoMix(
        float[] mix, float[] src,
        int frameCount, int srcCh, int dstCh,
        (int dst, float gain)[][] bakedRoutes)
    {
        for (int f = 0; f < frameCount; f++)
        {
            for (int sc = 0; sc < srcCh; sc++)
            {
                float sample = src[f * srcCh + sc];
                var routes   = bakedRoutes[sc];
                for (int r = 0; r < routes.Length; r++)
                {
                    int   dc   = routes[r].dst;
                    float gain = routes[r].gain;
                    if (dc < dstCh)
                        mix[f * dstCh + dc] += sample * gain;
                }
            }
        }
    }

    private void UpdatePeaks(Span<float> buf, int outCh)
    {
        // Reset peaks each tick
        Array.Clear(_peakLevels);

        for (int i = 0; i < buf.Length; i++)
        {
            int ch  = i % outCh;
            float a = Math.Abs(buf[i]);
            if (a > _peakLevels[ch]) _peakLevels[ch] = a;
        }

        // Publish snapshot (copy so callers reading PeakLevels get a stable array)
        _peakLevels.AsSpan().CopyTo(_peakSnapshot);
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_editLock)
        {
            foreach (var s in _slots)
                if (s.OwnsResampler) s.Resampler.Dispose();
            _slots = [];
        }
    }
}

