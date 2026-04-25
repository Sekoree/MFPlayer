using S.Media.Core.Audio;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// A single video source that feeds into an <see cref="IVideoMixer"/>.
/// Analogous to <see cref="Audio.IAudioChannel"/> but for video frames.
/// </summary>
public interface IVideoChannel : IMediaChannel<VideoFrame>
{
    /// <summary>The native format of this video source.</summary>
    VideoFormat SourceFormat { get; }

    /// <summary>Current playback position (derived from the last presented frame's PTS).</summary>
    TimeSpan Position { get; }

    /// <summary>
    /// Best-effort PTS of the next frame that would be returned by <see cref="IMediaChannel{TFrame}.FillBuffer"/>,
    /// or an interpolated value for smooth drift readouts when no newer frame is buffered yet.
    /// Defaults to <see cref="Position"/> for implementations that don't track the ring head.
    /// </summary>
    TimeSpan NextExpectedPts => Position;

    /// <summary>
    /// Number of frames the internal ring buffer can hold.
    /// Configured at construction time.
    /// </summary>
    int BufferDepth { get; }

    /// <summary>Number of frames currently available in the ring buffer.</summary>
    int BufferAvailable { get; }

    /// <summary>
    /// Raised (on a background thread) when the pull path finds the ring buffer empty
    /// after at least one frame has been seen, indicating a genuine decoder underrun.
    /// </summary>
    event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>
    /// Subscribe to the frame stream. Each subscription gets its own bounded queue so
    /// multiple consumers do not race for frames. Implementations that don't support
    /// native fan-out may return a thin wrapper over <see cref="IMediaChannel{TFrame}.FillBuffer"/> —
    /// in that case, at most one subscription should be alive at a time.
    /// </summary>
    IVideoSubscription Subscribe(VideoSubscriptionOptions options)
        => new FillBufferSubscription(this, options);
}
