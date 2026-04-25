using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Converts video frame pixel data from one format to another.
/// Analogous to <see cref="Audio.IAudioResampler"/> for the video pipeline.
/// </summary>
public interface IPixelFormatConverter : IDisposable
{
    /// <summary>
    /// Converts pixel data from <paramref name="source"/>'s pixel format
    /// to <paramref name="dstFormat"/>.
    /// </summary>
    /// <returns>A new VideoFrame with the converted data (caller disposes MemoryOwner).</returns>
    VideoFrame Convert(VideoFrame source, PixelFormat dstFormat);
}

