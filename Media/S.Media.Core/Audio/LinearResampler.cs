using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Stateful linear-interpolation resampler.
/// Suitable for most playback use-cases; zero external dependencies.
/// For professional-quality resampling inject <c>SwrResampler</c> from S.Media.FFmpeg instead.
/// </summary>
/// <remarks>
/// Cross-buffer continuity is maintained by saving the unconsumed tail frames from each
/// call and prepending them to the next. This means one small heap allocation per call
/// when the rate differs; the tail is typically 1-3 frames.
/// </remarks>
public sealed class LinearResampler : IAudioResampler
{
    // Fractional read-head position within the effective (pending + new) input window.
    // Carried across calls. Always < 1.0 at the start of each call.
    private double _phase;

    // Unconsumed frames saved from the previous call, prepended to the next input.
    // Length = _pendingFrames * channels (valid floats).
    private float[] _pendingBuf    = [];
    private int     _pendingFrames;

    // Pre-allocated scratch buffer for the [pending ++ input] splice.
    // Grown lazily; never shrunk. Avoids a heap allocation on every RT call when
    // _pendingFrames > 0 (previously "float[]? tmp = new float[totalFrames * channels]").
    private float[] _combinedBuf = [];

    private bool _disposed;

    public int GetRequiredInputFrames(int outputFrames, AudioFormat inputFormat, int outputSampleRate)
    {
        if (inputFormat.SampleRate == outputSampleRate)
            return outputFrames;

        double step = (double)inputFormat.SampleRate / outputSampleRate;

        // The interpolation loop accesses effective[idx] and effective[idx+1] where
        // idx = (long)(_phase + i * step).  The maximum index touched is:
        //   maxIdx  = (long)(_phase + (outputFrames - 1) * step)
        //   maxIdx1 = maxIdx + 1          (for s1 interpolation neighbour)
        // Total effective frames needed = maxIdx + 2  (0-based → count).
        long maxIdx      = (long)(_phase + (outputFrames - 1) * step);
        int  totalNeeded = (int)(maxIdx + 2);
        int  newNeeded   = totalNeeded - _pendingFrames;
        return Math.Max(0, newNeeded);
    }

    public int Resample(
        ReadOnlySpan<float> input,
        Span<float>         output,
        AudioFormat         inputFormat,
        int                 outputSampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int channels   = inputFormat.Channels;
        int inputFrames = input.Length / channels;
        int outFrames   = output.Length / channels;

        if (inputFormat.SampleRate == outputSampleRate)
        {
            // Fast copy path; discard pending since rates match and we don't resample.
            int copy = Math.Min(input.Length, output.Length);
            input[..copy].CopyTo(output);
            _phase         = 0;
            _pendingFrames = 0;
            return copy / channels;
        }

        double step = (double)inputFormat.SampleRate / outputSampleRate;

        // Build the effective input: [pending tail from previous call] ++ [new input].
        // This ensures interpolation is seamless at buffer boundaries (fixes Q4 bug).
        int pendingCount = _pendingFrames;
        int totalFrames  = pendingCount + inputFrames;

        ReadOnlySpan<float> effective;

        if (pendingCount > 0)
        {
            // Grow the combined buffer lazily (typically 1-2 pending frames when the
            // caller uses GetRequiredInputFrames).  Multiplicative growth (double capacity)
            // amortises allocation cost exponentially — critical for RT-thread safety,
            // since a simple "+1-frame" slack caused per-callback allocations and GC pauses.
            int need = totalFrames * channels;
            if (_combinedBuf.Length < need)
                _combinedBuf = new float[Math.Max(need, _combinedBuf.Length * 2)];
            _pendingBuf.AsSpan(0, pendingCount * channels).CopyTo(_combinedBuf);
            input.CopyTo(_combinedBuf.AsSpan(pendingCount * channels));
            effective = _combinedBuf.AsSpan(0, need);
        }
        else
        {
            effective = input;
        }

        // ── Linear interpolation loop ──────────────────────────────────────
        // For each output frame, _phase identifies a fractional position in the
        // effective input window.  We take the two nearest input frames (s0, s1)
        // and blend: output = s0 + (s1 - s0) * t, where t is the fractional part.
        // _phase advances by `step` (= srcRate / dstRate) per output frame.
        // After the loop, unconsumed input frames are saved as "pending" for the
        // next call, ensuring seamless interpolation across buffer boundaries.
        int written = 0;
        for (int i = 0; i < outFrames; i++)
        {
            long   idx = (long)_phase;
            double t   = _phase - idx;

            // Clamp idx to valid range — prevents overflow when casting to nint for indexing.
            if (idx < 0) idx = 0;
            if (idx >= totalFrames) idx = totalFrames - 1;

            for (int ch = 0; ch < channels; ch++)
            {
                int i0 = (int)(idx * channels) + ch;
                float s0 = idx < totalFrames
                    ? effective[i0]
                    : (totalFrames > 0 ? effective[(totalFrames - 1) * channels + ch] : 0f);

                int i1 = (int)((idx + 1) * channels) + ch;
                float s1 = (idx + 1) < totalFrames
                    ? effective[i1]
                    : s0; // hold last value at end of stream

                output[i * channels + ch] = s0 + (s1 - s0) * (float)t;
            }

            _phase += step;
            written++;
        }

        // ── Save unconsumed tail for cross-buffer continuity ───────────────
        // `consumed` = how many input frames the interpolation loop has fully
        // passed over.  Any remaining frames (totalFrames - consumed) are saved
        // in _pendingBuf and prepended to the next call's input.  _phase is then
        // normalised to be relative to the new pending window origin (always < step
        // in steady state).  This carry mechanism is what makes the resampler
        // stateful and allows seamless buffer-boundary interpolation.
        long consumed = Math.Min((long)_phase, totalFrames);
        _pendingFrames = (int)(totalFrames - consumed);

        if (_pendingFrames > 0)
        {
            int need = _pendingFrames * channels;
            if (_pendingBuf.Length < need)
                _pendingBuf = new float[Math.Max(need, _pendingBuf.Length * 2)];
            effective.Slice((int)(consumed * channels), need).CopyTo(_pendingBuf);
        }
        else
        {
            _pendingFrames = 0;
        }

        // Phase is now the fractional offset within the pending window for next call.
        _phase -= consumed;

        return written;
    }

    public void Reset()
    {
        _phase         = 0;
        _pendingFrames = 0;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
