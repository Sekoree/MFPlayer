using S.Media.Core.Media;

namespace S.Media.Core.Video;

public interface IVideoSource : IDisposable
{
    Guid Id { get; }

    VideoSourceState State { get; }

    /// <summary>Codec, resolution, frame rate, and duration info. Returns <see langword="default"/> when unavailable.</summary>
    VideoStreamInfo StreamInfo { get; }

    int Start();

    int Stop();

    int ReadFrame(out VideoFrame frame);

    int Seek(double positionSeconds);

    int SeekToFrame(long frameIndex);

    double PositionSeconds { get; }

    double DurationSeconds { get; }

    long CurrentFrameIndex { get; }

    long? CurrentDecodeFrameIndex { get; }

    long? TotalFrameCount { get; }

    bool IsSeekable { get; }
}
