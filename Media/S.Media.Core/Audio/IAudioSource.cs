using S.Media.Core.Errors;
using S.Media.Core.Media;

namespace S.Media.Core.Audio;

public interface IAudioSource : IDisposable
{
    Guid Id { get; }

    AudioSourceState State { get; }

    AudioStreamInfo StreamInfo { get; }

    /// <summary>
    /// Per-source volume multiplier. Default: <c>1.0</c>.
    /// <para>
    /// Valid range: <c>0.0</c> (silent) to <c>float.MaxValue</c> (amplification).
    /// Negative values are not supported and implementations should clamp to <c>0.0</c>.
    /// Values above <c>1.0</c> amplify the signal and may cause clipping.
    /// </para>
    /// </summary>
    float Volume { get; set; }

    /// <summary>Total sample count for the stream, or <see langword="null"/> for live/unknown-duration sources.</summary>
    long? TotalSampleCount { get; }

    int Start();

    int Stop();

    int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead);

    int Seek(double positionSeconds);

    /// <summary>
    /// Seeks to the specified sample position. Non-seekable sources return
    /// <see cref="MediaErrorCode.MediaSourceNonSeekable"/> by default.
    /// </summary>
    int SeekToSample(long sampleIndex) => (int)MediaErrorCode.MediaSourceNonSeekable;

    double PositionSeconds { get; }

    double DurationSeconds { get; }
}
