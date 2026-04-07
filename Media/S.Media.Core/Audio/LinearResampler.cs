using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Stateful linear-interpolation resampler.
/// Suitable for most playback use-cases; zero external dependencies.
/// For professional-quality resampling inject <c>SwrResampler</c> from S.Media.FFmpeg instead.
/// </summary>
public sealed class LinearResampler : IAudioResampler
{
    // Fractional position inside the current input buffer (in frames).
    // Carried across calls so buffer boundaries are seamless.
    private double _phase;

    // Last sample of the previous input buffer, per channel.
    // Used to interpolate across the very first output sample of each new call.
    private float[] _prevTail = [];

    private bool _disposed;

    public int Resample(
        ReadOnlySpan<float> input,
        Span<float>         output,
        AudioFormat         inputFormat,
        int                 outputSampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int channels = inputFormat.Channels;

        if (inputFormat.SampleRate == outputSampleRate)
        {
            // No conversion needed — fast copy path.
            int copy = Math.Min(input.Length, output.Length);
            input[..copy].CopyTo(output);
            _phase = 0;
            return copy / channels;
        }

        double step        = (double)inputFormat.SampleRate / outputSampleRate;
        int    inputFrames = input.Length / channels;
        int    outFrames   = output.Length / channels;

        // Ensure _prevTail is large enough.
        if (_prevTail.Length < channels)
            _prevTail = new float[channels];

        int written = 0;
        for (int i = 0; i < outFrames; i++)
        {
            long   idx = (long)_phase;
            double t   = _phase - idx;

            for (int ch = 0; ch < channels; ch++)
            {
                // Sample at floor(phase)
                float s0 = idx == 0
                    ? _prevTail[ch]
                    : input[(int)((idx - 1) * channels) + ch];

                // We actually need samples at idx and idx+1 for forward interpolation.
                // Rewrite: use idx as current, idx+1 as next.
                s0 = idx < inputFrames
                    ? input[(int)(idx * channels) + ch]
                    : _prevTail[ch]; // beyond end — shouldn't normally happen

                float s1 = (idx + 1) < inputFrames
                    ? input[(int)((idx + 1) * channels) + ch]
                    : s0; // hold last value if we'd go out of range

                output[i * channels + ch] = s0 + (s1 - s0) * (float)t;
            }

            _phase += step;
            written++;
        }

        // Save tail samples for the next call and advance phase.
        int consumed = (int)_phase;
        consumed = Math.Min(consumed, inputFrames - 1);
        if (consumed >= 0 && inputFrames > 0)
        {
            for (int ch = 0; ch < channels; ch++)
                _prevTail[ch] = input[consumed * channels + ch];
        }

        // Carry over only the fractional part; consumed frames are gone.
        _phase -= (long)_phase;

        return written;
    }

    public void Reset()
    {
        _phase   = 0;
        Array.Clear(_prevTail);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

