using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Unified push endpoint contract for video frames.
/// Implemented by sink/output adapters during API unification.
/// </summary>
public interface IVideoFrameEndpoint : IMediaEndpoint
{
    IReadOnlyList<PixelFormat> SupportedPixelFormats { get; }

    /// <summary>
    /// Pushes one video frame into the endpoint.
    /// Implementations may buffer internally.
    /// </summary>
    void WriteFrame(in VideoFrame frame);
}
