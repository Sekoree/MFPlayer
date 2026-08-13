using S.Media.Core.Video;
using S.Media.Decode.FFmpeg.Audio;
using S.Media.Decode.FFmpeg.Video;

namespace HaCue2.Machine;

/// <summary>One decoded frame, as plain BGRA the UI can put straight into a bitmap.</summary>
/// <param name="Stride">Bytes per row, which is not always <c>Width * 4</c>.</param>
public sealed record ClipFrame(int Width, int Height, int Stride, byte[] Bgra);

/// <summary>
/// What a media file looks like: its peaks, and a frame from anywhere inside it.
/// </summary>
/// <remarks>
/// <para>
/// MACHINE facts, like <see cref="MediaProbe"/>'s: they come from opening the file, they differ per
/// box, and none of them belongs in a document. The trim editor is the caller - trimming half an hour
/// off the front of a two-hour recording by typing seconds into a box is not something anybody should
/// have to do, and both of these exist so they do not have to.
/// </para>
/// <para>
/// <b>Everything here is cancellable and off the UI thread.</b> A scan of a long file takes seconds,
/// and a scrub produces a new frame request on every pointer move - a caller that could not abandon
/// either would be a frozen window.
/// </para>
/// </remarks>
public static class MediaScan
{
    /// <summary>How many peaks a scan produces, at most.</summary>
    /// <remarks>
    /// Four thousand rather than the few hundred a scrubber needs, because this one is zoomed INTO:
    /// at 512 buckets a two-hour recording is fourteen seconds per bar, which cannot show where a
    /// speech starts. At 4096 it is under two seconds a bar, and the array is 16 kB.
    /// </remarks>
    public const int MaxBuckets = 4096;

    /// <summary>How often a partial scan is published while it runs.</summary>
    private static readonly TimeSpan PartialInterval = TimeSpan.FromMilliseconds(150);

    private const int ReadChunkSamples = 4096;

    /// <summary>
    /// Past this, the file is SAMPLED rather than read through.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed: reading every sample of a 2 h 32 m recording took 88 seconds on this box.
    /// A trim editor that takes a minute and a half to draw is one nobody waits for, and the recordings
    /// this feature exists for - "trim the first half hour" - are exactly the long ones.
    /// </remarks>
    private static readonly TimeSpan SampleBeyond = TimeSpan.FromMinutes(12);

    /// <summary>
    /// The most buckets a SAMPLED scan produces.
    /// </summary>
    /// <remarks>
    /// Measured, and the reason is worth writing down: a 49-minute ProRes master took 544 seconds at
    /// 4096 buckets while its seeks measured 0 ms - the cost is the demuxer walking enormous
    /// interleaved video packets to reach each audio position, so it scales with the NUMBER of samples
    /// taken and not with how far apart they are. A thousand bounds that, and on a two-hour file it is
    /// still a bar every seven seconds; the cache is what makes even that a one-time cost.
    /// </remarks>
    private const int MaxSampledBuckets = 1000;

    /// <summary>How much audio is read at each sampled point.</summary>
    /// <remarks>
    /// Long enough to catch a real signal and short enough that a thousand of them are cheap. This is
    /// the honesty cost of sampling: a transient shorter than the gap between windows is not seen, and
    /// the waveform is a picture of the file rather than a measurement of it. For "where does the
    /// speech start" that is enough; for a level check it is not, and the meters are.
    /// </remarks>
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// A normalized peak per bucket, or null when the file has no audio this machine can read.
    /// </summary>
    /// <param name="onPartial">
    /// Called with what has been analysed so far, throttled. It is what makes the waveform fill in
    /// left to right instead of appearing when a long file finishes - on a two-hour recording the
    /// difference is a window that looks broken for ten seconds and one that does not.
    /// </param>
    public static Task<float[]?> WaveformAsync(
        string path,
        int buckets = MaxBuckets,
        CancellationToken cancellationToken = default,
        Action<float[]>? onPartial = null) =>
        Task.Run(() => Waveform(path, buckets, cancellationToken, onPartial), cancellationToken);

    /// <summary>
    /// A normalized peak per bucket across ONE window of the file, read through exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole-file scan cannot answer a zoomed-in question. Past <see cref="SampleBeyond"/> it samples,
    /// capped at <see cref="MaxSampledBuckets"/> - on a two-hour recording that is one bar every seven
    /// seconds, so an editor showing thirty seconds of it has four bars to draw with and draws blocks.
    /// </para>
    /// <para>
    /// Reading a window through is cheap for exactly the reason the whole file is not: the cost was never
    /// the seeking, it was the number of samples taken, and a window has few. Thirty seconds is thirty
    /// seconds of decoding whether the file around it is four minutes or four hours.
    /// </para>
    /// <para>
    /// Normalized within the window, like every other scan here. The caller knows what the coarse pass
    /// said about the same stretch and is better placed to decide whether to keep that scale.
    /// </para>
    /// </remarks>
    public static Task<float[]?> WaveformWindowAsync(
        string path,
        TimeSpan from,
        TimeSpan length,
        int buckets = MaxBuckets,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => WaveformWindow(path, from, length, buckets, cancellationToken), cancellationToken);

    private static float[]? WaveformWindow(
        string path, TimeSpan from, TimeSpan length, int buckets, CancellationToken cancellationToken)
    {
        if (length <= TimeSpan.Zero)
            return null;

        AudioFileDecoder decoder;
        try
        {
            decoder = AudioFileDecoder.Open(path);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return null;
        }

        try
        {
            if (decoder.Duration <= TimeSpan.Zero || from >= decoder.Duration)
                return null;

            var channels = Math.Max(1, decoder.Format.Channels);
            var remaining = decoder.Duration - from;
            var span = length < remaining ? length : remaining;
            var wanted = (long)(span.TotalSeconds * decoder.Format.SampleRate);
            if (wanted <= 0)
                return null;

            var count = (int)Math.Clamp(Math.Min(buckets, wanted), 1, MaxBuckets);

            if (from > TimeSpan.Zero)
                decoder.Seek(from);

            var peaks = new float[count];
            var buffer = new float[ReadChunkSamples * channels];
            var perBucket = (double)wanted / count;
            long frameIndex = 0;

            while (frameIndex < wanted && !decoder.IsExhausted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = decoder.ReadInto(buffer);
                if (read <= 0)
                    break;

                for (var frame = 0; frame < read / channels && frameIndex < wanted; frame++)
                {
                    Peak(peaks, Math.Min(count - 1, (int)(frameIndex / perBucket)), buffer, frame, channels);
                    frameIndex++;
                }
            }

            // Nothing was read - a seek that landed past the audio, a stream that ended early. A silent
            // array would be drawn as a flat line, which claims the window IS silent.
            return frameIndex > 0 ? Normalized(peaks) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A container that will not seek there. The coarse waveform stays on screen.
            return null;
        }
        finally
        {
            decoder.Dispose();
        }
    }

    private static float[]? Waveform(
        string path, int buckets, CancellationToken cancellationToken, Action<float[]>? onPartial)
    {
        AudioFileDecoder decoder;

        try
        {
            decoder = AudioFileDecoder.Open(path);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // No audio, an unreadable container, a path that has gone. The editor draws no waveform
            // and says so; none of them is worth throwing out of a background scan.
            return null;
        }

        try
        {
            if (decoder.Duration <= TimeSpan.Zero)
                return null;

            var channels = Math.Max(1, decoder.Format.Channels);
            var totalSamples = (long)(decoder.Duration.TotalSeconds * decoder.Format.SampleRate);

            if (totalSamples <= 0)
                return null;

            var count = Math.Clamp(buckets, 1, MaxBuckets);
            count = (int)Math.Min(count, totalSamples);

            return decoder.Duration > SampleBeyond
                ? Sampled(decoder, Math.Min(count, MaxSampledBuckets), channels, cancellationToken, onPartial)
                : ReadThrough(decoder, count, channels, totalSamples, cancellationToken, onPartial);
        }
        catch (OperationCanceledException)
        {
            // The editor moved on, or closed. Nothing to report.
            return null;
        }
        finally
        {
            decoder.Dispose();
        }
    }

    /// <summary>Reads the whole file. Exact, and only affordable on a short one.</summary>
    private static float[] ReadThrough(
        AudioFileDecoder decoder,
        int count,
        int channels,
        long totalSamples,
        CancellationToken cancellationToken,
        Action<float[]>? onPartial)
    {
        var perBucket = (double)totalSamples / count;
        var peaks = new float[count];
        var buffer = new float[ReadChunkSamples * channels];
        long sample = 0;
        var published = Environment.TickCount64;

        while (!decoder.IsExhausted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = decoder.ReadInto(buffer);

            if (read <= 0)
                break;

            var frames = read / channels;

            for (var frame = 0; frame < frames; frame++)
            {
                var bucket = Math.Min(count - 1, (int)(sample / perBucket));
                Peak(peaks, bucket, buffer, frame, channels);
                sample++;
            }

            if (onPartial is not null
                && Environment.TickCount64 - published >= PartialInterval.TotalMilliseconds)
            {
                published = Environment.TickCount64;
                onPartial(Normalized(peaks));
            }
        }

        return Normalized(peaks);
    }

    /// <summary>
    /// Seeks to each bucket and listens briefly.
    /// </summary>
    /// <remarks>
    /// A picture of the file rather than a measurement of it, and the only way a two-hour recording
    /// draws in a usable time. Every bucket is still a real peak of real audio - what is lost is
    /// whatever happened in the gaps, which is why <see cref="SampleBeyond"/> is generous enough that
    /// ordinary cue media is read through exactly.
    /// </remarks>
    private static float[] Sampled(
        AudioFileDecoder decoder,
        int count,
        int channels,
        CancellationToken cancellationToken,
        Action<float[]>? onPartial)
    {
        var peaks = new float[count];
        var buffer = new float[ReadChunkSamples * channels];
        var perBucket = decoder.Duration / count;
        var published = Environment.TickCount64;

        for (var bucket = 0; bucket < count; bucket++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                decoder.Seek(perBucket * bucket);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A container that will not seek there. That bucket stays silent rather than taking
                // the whole scan down.
                continue;
            }

            var wanted = (long)(SampleWindow.TotalSeconds * decoder.Format.SampleRate) * channels;
            long taken = 0;

            while (taken < wanted && !decoder.IsExhausted)
            {
                var read = decoder.ReadInto(buffer);

                if (read <= 0)
                    break;

                for (var frame = 0; frame < read / channels; frame++)
                    Peak(peaks, bucket, buffer, frame, channels);

                taken += read;
            }

            if (onPartial is not null
                && Environment.TickCount64 - published >= PartialInterval.TotalMilliseconds)
            {
                published = Environment.TickCount64;
                onPartial(Normalized(peaks));
            }
        }

        return Normalized(peaks);
    }

    /// <summary>
    /// The loudest of one frame's channels into a bucket.
    /// </summary>
    /// <remarks>
    /// The PEAK across channels, not a mix: a trim point is found by looking for where something
    /// starts, and a signal on one channel only must not average away to nothing.
    /// </remarks>
    private static void Peak(float[] peaks, int bucket, float[] buffer, int frame, int channels)
    {
        for (var channel = 0; channel < channels; channel++)
        {
            var level = Math.Abs(buffer[(frame * channels) + channel]);

            if (level > peaks[bucket])
                peaks[bucket] = level;
        }
    }

    /// <summary>
    /// Peaks scaled so the loudest is 1.
    /// </summary>
    /// <remarks>
    /// Against the running peak mid-scan and the global one at the end, so the shape reads correctly
    /// while it fills in. A quiet file drawn against absolute full scale is a flat line, which is the
    /// one thing a waveform must not be.
    /// </remarks>
    private static float[] Normalized(float[] peaks)
    {
        var copy = new float[peaks.Length];
        var loudest = 0f;

        foreach (var peak in peaks)
        {
            if (peak > loudest)
                loudest = peak;
        }

        if (loudest <= 0)
            return copy;

        for (var index = 0; index < peaks.Length; index++)
            copy[index] = peaks[index] / loudest;

        return copy;
    }

    /// <summary>
    /// One frame from inside a file, as BGRA - or null when there is no picture to get.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opened, seeked, read and closed per call. A decoder held open across a scrub would be faster and
    /// would also be a file handle and a hardware context living for as long as a window is open, on a
    /// path where the answer arrives in tens of milliseconds either way.
    /// </para>
    /// <para>
    /// A seek lands on the nearest KEYFRAME before the position, so the frame is at or before what was
    /// asked for. That is the right compromise here: this is a picture to recognise a moment by, not a
    /// frame to cut on, and decoding forward to an exact frame would turn a scrub into a stutter.
    /// </para>
    /// </remarks>
    public static Task<ClipFrame?> FrameAsync(
        string path, TimeSpan at, CancellationToken cancellationToken = default) =>
        Task.Run(() => Frame(path, at, cancellationToken), cancellationToken);

    private static ClipFrame? Frame(string path, TimeSpan at, CancellationToken cancellationToken)
    {
        VideoFileDecoder decoder;

        try
        {
            // Software decode: a hardware frame comes back on a GPU surface this has no context to read,
            // and one frame on demand is not worth a device for.
            decoder = VideoFileDecoder.Open(path, new VideoDecoderOpenOptions
            {
                TryHardwareAcceleration = false,
            });
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return null;
        }

        VideoCpuFrameConverter? converter = null;

        try
        {
            // A SEEK CAN THROW, and does: an attached picture in a FLAC answers av_seek_frame with
            // "Operation not permitted", because a single still has nothing to seek within. That is not
            // a failure to report - the frame it has IS the answer for an audio cue - so the seek is
            // attempted and stepped past, and the read happens from wherever that left the decoder.
            if (at > TimeSpan.Zero)
            {
                try
                {
                    decoder.Seek(at);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    // Unseekable, or past the end. Read what is there.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!decoder.TryReadNextFrame(out var frame))
                return null;

            using (frame)
            {
                var format = frame.Format;

                if (format.PixelFormat == PixelFormat.Bgra32)
                    return Copy(frame);

                if (!VideoCpuFrameConverter.CanConvert(
                        format.PixelFormat, PixelFormat.Bgra32, format.Width, format.Height))
                    return null;

                converter = new VideoCpuFrameConverter();
                converter.Configure(format.PixelFormat, PixelFormat.Bgra32, format.Width, format.Height);

                using var converted = converter.Convert(frame, frame.ColorTransferHint);
                return Copy(converted);
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A seek past the end, a codec this build cannot decode, a cancelled scrub. The editor
            // shows no picture, which is a truthful answer and not a crash.
            return null;
        }
        finally
        {
            converter?.Dispose();
            decoder.Dispose();
        }
    }

    /// <summary>Copies a frame's pixels out, so nothing survives the decoder that produced them.</summary>
    private static ClipFrame Copy(VideoFrame frame) =>
        new(frame.Format.Width, frame.Format.Height, frame.Strides[0], frame.Planes[0].ToArray());
}
