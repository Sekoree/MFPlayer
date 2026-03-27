namespace S.Media.Core.Video;

public interface IVideoSource : IDisposable
{
    Guid SourceId { get; }

    VideoSourceState State { get; }

    int Start();

    int Stop();

    int ReadFrame(out VideoFrame frame);

    int Seek(double positionSeconds);

    int SeekToFrame(long frameIndex);

    int SeekToFrame(long frameIndex, out long currentFrameIndex, out long? totalFrameCount);

    double PositionSeconds { get; }

    double DurationSeconds { get; }

    long CurrentFrameIndex { get; }

    long? CurrentDecodeFrameIndex { get; }

    long? TotalFrameCount { get; }

    bool IsSeekable { get; }
}
