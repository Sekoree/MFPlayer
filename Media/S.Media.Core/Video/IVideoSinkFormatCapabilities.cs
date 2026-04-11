using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Optional sink capability interface that advertises acceptable input formats
/// in descending preference order.
/// </summary>
/// <remarks>
/// <para>
/// The mixer checks <see cref="PreferredPixelFormats"/> when deciding which pixel
/// format to deliver to a sink. Only <see cref="PixelFormat.Rgba32"/> and
/// <see cref="PixelFormat.Bgra32"/> are eligible for mixer-side conversion.
/// YUV formats (<c>Nv12</c>, <c>Yuv420p</c>, <c>Yuv422p10</c>) are only delivered
/// when <see cref="BypassMixerConversion"/> is <see langword="true"/>; the sink then
/// performs any required YUV-to-RGB conversion at its own boundary.
/// </para>
/// </remarks>
public interface IVideoSinkFormatCapabilities
{
    /// <summary>
    /// Ordered list of acceptable input formats for <see cref="IVideoSink.ReceiveFrame"/>.
    /// The mixer uses the first supported format. Only Rgba32 and Bgra32 can be
    /// produced by mixer-side conversion; list YUV formats only when
    /// <see cref="BypassMixerConversion"/> is true.
    /// </summary>
    IReadOnlyList<PixelFormat> PreferredPixelFormats { get; }

    /// <summary>
    /// When true, mixer-side conversion is skipped and raw source frames are forwarded.
    /// The endpoint is then responsible for conversion at its boundary.
    /// Required when <see cref="PreferredPixelFormats"/> includes YUV formats.
    /// </summary>
    bool BypassMixerConversion => false;
}
