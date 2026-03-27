namespace S.Media.Core.Audio;

public interface IAudioSource : IDisposable
{
    Guid SourceId { get; }

    AudioSourceState State { get; }

    int Start();

    int Stop();

    int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead);

    int Seek(double positionSeconds);

    double PositionSeconds { get; }

    double DurationSeconds { get; }
}
