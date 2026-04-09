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
}

