using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Chooses output pixel format routing for local renderers.
/// </summary>
public static class LocalVideoOutputRoutingPolicy
{
    public static PixelFormat SelectLeaderPixelFormat(
        VideoFormat source,
        bool supportsNv12,
        bool supportsYuv420p,
        bool supportsYuv422p10 = false,
        bool supportsUyvy422 = false,
        PixelFormat fallback = PixelFormat.Bgra32)
    {
        return source.PixelFormat switch
        {
            PixelFormat.Nv12 when supportsNv12 => PixelFormat.Nv12,
            PixelFormat.Yuv420p when supportsYuv420p => PixelFormat.Yuv420p,
            PixelFormat.Yuv422p10 when supportsYuv422p10 => PixelFormat.Yuv422p10,
            PixelFormat.Uyvy422 when supportsUyvy422 => PixelFormat.Uyvy422,
            _ => fallback
        };
    }
}

