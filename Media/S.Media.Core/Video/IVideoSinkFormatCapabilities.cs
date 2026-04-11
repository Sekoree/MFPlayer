using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Optional sink capability interface that advertises acceptable input formats
/// in descending preference order.
/// </summary>
public interface IVideoSinkFormatCapabilities
{
    /// <summary>
    /// Ordered list of acceptable input formats for <see cref="IVideoSink.ReceiveFrame"/>.
    /// The mixer uses the first supported format.
    /// </summary>
    IReadOnlyList<PixelFormat> PreferredPixelFormats { get; }

    /// <summary>
    /// When true, mixer-side conversion is skipped and raw source frames are forwarded.
    /// Endpoints can then apply conversion policy locally.
    /// </summary>
    bool PreferRawFramePassthrough => false;
}

