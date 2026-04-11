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
    /// When true, the mixer skips its own conversion and passes source/raw frames
    /// directly, letting the endpoint handle any required conversion at its boundary.
    /// </summary>
    bool BypassMixerConversion => false;

    /// <summary>
    /// Pushes one video frame into the endpoint.
    /// Implementations may buffer internally.
    /// </summary>
    void WriteFrame(in VideoFrame frame);
}
