using FFmpeg.AutoGen;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Media;

namespace S.Media.FFmpeg;

/// <summary>
/// <see cref="IAudioResampler"/> backed by libswresample (sinc resampler).
/// Performs sample-rate conversion only; channel count and sample format (Float32) are preserved.
/// Stateful: carries fractional delay across buffer boundaries for seamless output.
/// </summary>
public sealed unsafe class SwrResampler : IAudioResampler
{
    private SwrContext* _swr;
    private int         _lastInRate;
    private int         _lastOutRate;
    private int         _lastChannels;
    private bool        _disposed;

    public int GetRequiredInputFrames(int outputFrames, AudioFormat inputFormat, int outputSampleRate)
    {
        if (inputFormat.SampleRate == outputSampleRate)
            return outputFrames;
        // swresample manages its own internal delay line so a simple ceiling is sufficient.
        return (int)Math.Ceiling(outputFrames * ((double)inputFormat.SampleRate / outputSampleRate)) + 1;
    }

    public int Resample(
        ReadOnlySpan<float> input,
        Span<float>         output,
        AudioFormat         inputFormat,
        int                 outputSampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int channels  = inputFormat.Channels;
        int inRate    = inputFormat.SampleRate;
        int outRate   = outputSampleRate;
        int inFrames  = input.Length  / channels;
        int outFrames = output.Length / channels;

        // (Re)initialise if parameters changed.
        if (_swr == null || inRate != _lastInRate || outRate != _lastOutRate || channels != _lastChannels)
            Reinitialise(channels, inRate, outRate);

        fixed (float* pIn  = input)
        fixed (float* pOut = output)
        {
            byte* inPtr  = (byte*)pIn;
            byte* outPtr = (byte*)pOut;
            int written = ffmpeg.swr_convert(_swr, &outPtr, outFrames, &inPtr, inFrames);
            return written < 0 ? 0 : written;
        }
    }

    public void Reset()
    {
        if (_swr != null)
            ffmpeg.swr_init(_swr); // flush delay buffer
    }

    private void Reinitialise(int channels, int inRate, int outRate)
    {
        if (_swr != null)
        {
            fixed (SwrContext** pp = &_swr)
                ffmpeg.swr_free(pp);
        }

        _swr = ffmpeg.swr_alloc();
        if (_swr == null) throw new MediaDecodeException("swr_alloc failed.");

        AVChannelLayout layout = default;
        ffmpeg.av_channel_layout_default(&layout, channels);

        ffmpeg.av_opt_set_chlayout(_swr, "in_chlayout",  &layout, 0);
        ffmpeg.av_opt_set_chlayout(_swr, "out_chlayout", &layout, 0);
        ffmpeg.av_opt_set_int(_swr, "in_sample_rate",  inRate,  0);
        ffmpeg.av_opt_set_int(_swr, "out_sample_rate", outRate, 0);
        ffmpeg.av_opt_set_sample_fmt(_swr, "in_sample_fmt",
            AVSampleFormat.AV_SAMPLE_FMT_FLT, 0);
        ffmpeg.av_opt_set_sample_fmt(_swr, "out_sample_fmt",
            AVSampleFormat.AV_SAMPLE_FMT_FLT, 0);

        int ret = ffmpeg.swr_init(_swr);
        if (ret < 0) throw new MediaDecodeException($"swr_init failed: {ret}");

        _lastInRate   = inRate;
        _lastOutRate  = outRate;
        _lastChannels = channels;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_swr != null)
            fixed (SwrContext** pp = &_swr)
                ffmpeg.swr_free(pp);
    }
}

