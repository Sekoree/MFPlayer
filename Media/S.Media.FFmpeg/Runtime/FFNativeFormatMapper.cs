using FFmpeg.AutoGen;
using S.Media.Core.Video;

namespace S.Media.FFmpeg.Runtime;

internal static class FFNativeFormatMapper
{
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
}

