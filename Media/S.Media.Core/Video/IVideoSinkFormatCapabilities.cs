using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Optional sink capability interface that advertises acceptable input formats
/// in descending preference order.
/// </summary>
/// <remarks>
/// <para>
/// The mixer routes raw source frames and does not perform pixel conversion.
/// Endpoints convert at their own boundary when required.
/// </para>
/// </remarks>
public interface IVideoSinkFormatCapabilities
{
    /// <summary>
    /// Ordered list of preferred input formats for <see cref="IVideoSink.ReceiveFrame"/>.
    /// Used for diagnostics and capability signaling only; conversion is sink-owned.
    /// </summary>
    IReadOnlyList<PixelFormat> PreferredPixelFormats { get; }
}
