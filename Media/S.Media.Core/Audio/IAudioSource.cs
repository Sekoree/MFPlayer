using S.Media.Core.Media;

namespace S.Media.Core.Audio;

public interface IAudioSource : IDisposable
{
    Guid Id { get; }

    AudioSourceState State { get; }

    AudioStreamInfo StreamInfo { get; }

    /// <summary>Per-source volume multiplier (0.0 = silent, 1.0 = unity). Default: 1.0.</summary>
    float Volume { get; set; }

    /// <summary>Total sample count for the stream, or <see langword="null"/> for live/unknown-duration sources.</summary>
    long? TotalSampleCount { get; }

    int Start();

    int Stop();

    int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead);

    int Seek(double positionSeconds);

    double PositionSeconds { get; }

    double DurationSeconds { get; }
}
