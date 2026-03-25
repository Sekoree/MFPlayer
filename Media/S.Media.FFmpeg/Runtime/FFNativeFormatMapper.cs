using FFmpeg.AutoGen;
using S.Media.Core.Video;

namespace S.Media.FFmpeg.Runtime;

internal static class FFNativeFormatMapper
{
    private static readonly VideoPixelFormat[] PreferredFallbackOrder =
    [
        VideoPixelFormat.Nv12,
        VideoPixelFormat.Yuv420P,
        VideoPixelFormat.Yuv422P,
        VideoPixelFormat.Rgba32,
        VideoPixelFormat.Bgra32,
    ];

    public static VideoPixelFormat MapPixelFormat(int? nativePixelFormat)
    {
        if (nativePixelFormat is null)
        {
            return VideoPixelFormat.Unknown;
        }

        return (AVPixelFormat)nativePixelFormat.Value switch
        {
            AVPixelFormat.AV_PIX_FMT_RGBA => VideoPixelFormat.Rgba32,
            AVPixelFormat.AV_PIX_FMT_BGRA => VideoPixelFormat.Bgra32,
            AVPixelFormat.AV_PIX_FMT_YUV420P => VideoPixelFormat.Yuv420P,
            AVPixelFormat.AV_PIX_FMT_NV12 => VideoPixelFormat.Nv12,
            AVPixelFormat.AV_PIX_FMT_YUV422P => VideoPixelFormat.Yuv422P,
            AVPixelFormat.AV_PIX_FMT_YUV422P10LE => VideoPixelFormat.Yuv422P10Le,
            AVPixelFormat.AV_PIX_FMT_P010LE => VideoPixelFormat.P010Le,
            AVPixelFormat.AV_PIX_FMT_YUV420P10LE => VideoPixelFormat.Yuv420P10Le,
            AVPixelFormat.AV_PIX_FMT_YUV444P => VideoPixelFormat.Yuv444P,
            AVPixelFormat.AV_PIX_FMT_YUV444P10LE => VideoPixelFormat.Yuv444P10Le,
            _ => VideoPixelFormat.Unknown,
        };
    }

    public static VideoPixelFormat ResolvePreferredPixelFormat(
        int? nativePixelFormat,
        int width,
        int height,
        ReadOnlyMemory<byte> plane0,
        int plane0Stride,
        ReadOnlyMemory<byte> plane1,
        int plane1Stride,
        ReadOnlyMemory<byte> plane2,
        int plane2Stride)
    {
        var mapped = MapPixelFormat(nativePixelFormat);
        if (mapped != VideoPixelFormat.Unknown)
        {
            return mapped;
        }

        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);

        var hasPlane0 = !plane0.IsEmpty && plane0Stride > 0;
        var hasPlane1 = !plane1.IsEmpty && plane1Stride > 0;
        var hasPlane2 = !plane2.IsEmpty && plane2Stride > 0;

        if (!hasPlane0)
        {
            return VideoPixelFormat.Rgba32;
        }

        if (hasPlane1 && hasPlane2)
        {
            var plane1Height = Math.Max(1, plane1.Length / Math.Max(1, plane1Stride));
            var isHalfHeight = Math.Abs(plane1Height - ((safeHeight + 1) / 2)) <= 1;
            var isFullHeight = Math.Abs(plane1Height - safeHeight) <= 1;
            var is10BitStride = plane0Stride >= safeWidth * 2;

            if (isHalfHeight)
            {
                return is10BitStride ? VideoPixelFormat.Yuv420P10Le : VideoPixelFormat.Yuv420P;
            }

            if (isFullHeight)
            {
                var is444 = plane1Stride >= safeWidth - 1;
                if (is444)
                {
                    return is10BitStride ? VideoPixelFormat.Yuv444P10Le : VideoPixelFormat.Yuv444P;
                }

                return is10BitStride ? VideoPixelFormat.Yuv422P10Le : VideoPixelFormat.Yuv422P;
            }
        }

        if (hasPlane1)
        {
            return plane0Stride >= safeWidth * 2 ? VideoPixelFormat.P010Le : VideoPixelFormat.Nv12;
        }

        if (plane0Stride >= safeWidth * 4)
        {
            return VideoPixelFormat.Rgba32;
        }

        foreach (var candidate in PreferredFallbackOrder)
        {
            if (candidate == VideoPixelFormat.Rgba32 || candidate == VideoPixelFormat.Bgra32)
            {
                continue;
            }

            return candidate;
        }

        return VideoPixelFormat.Rgba32;
    }
}

