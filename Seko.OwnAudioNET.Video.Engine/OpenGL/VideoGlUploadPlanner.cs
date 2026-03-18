namespace Seko.OwnAudioNET.Video.OpenGL;

/// <summary>
/// Shared per-format upload planning for GPU YUV/RGBA texture paths.
/// Keeps chroma sizing and shader mode mapping identical across renderers.
/// </summary>
public static class VideoGlUploadPlanner
{
    public enum VideoGlPlaneSlot
    {
        Rgba,
        Y,
        Uv,
        U,
        V
    }

    public enum VideoGlYuvMode
    {
        None = 0,
        Nv12 = 1,
        Planar8 = 2,
        P010 = 3,
        Planar10 = 4
    }

    public readonly record struct VideoGlPlanePlan(
        int PlaneIndex,
        VideoGlPlaneSlot Slot,
        int Width,
        int Height,
        int RowBytes,
        int InternalFormat,
        int Format,
        int Type);

    public readonly record struct VideoGlGpuPlan(
        bool IsSupported,
        bool IsYuv,
        bool Is10Bit,
        VideoGlYuvMode YuvMode,
        int PlaneCount,
        VideoGlPlanePlan Plane0,
        VideoGlPlanePlan Plane1,
        VideoGlPlanePlan Plane2);


    public static bool IsSemiPlanar(VideoGlYuvMode mode)
        => mode is VideoGlYuvMode.Nv12 or VideoGlYuvMode.P010;

    public static VideoGlGpuPlan CreateGpuUploadPlan(VideoPixelFormat pixelFormat, int width, int height)
    {
        var cw = (width + 1) / 2;
        var ch420 = (height + 1) / 2;

        return pixelFormat switch
        {
            VideoPixelFormat.Rgba32 => new(
                true, false, false, VideoGlYuvMode.None, 1,
                PlaneRgba(width, height),
                default,
                default),

            VideoPixelFormat.Nv12 => new(
                true, true, false, VideoGlYuvMode.Nv12, 2,
                PlaneY(width, height, width, false),
                PlaneUv(cw, ch420, cw * 2, false),
                default),

            VideoPixelFormat.Yuv420p => new(
                true, true, false, VideoGlYuvMode.Planar8, 3,
                PlaneY(width, height, width, false),
                PlaneU(cw, ch420, cw, false),
                PlaneV(cw, ch420, cw, false)),

            VideoPixelFormat.Yuv422p => new(
                true, true, false, VideoGlYuvMode.Planar8, 3,
                PlaneY(width, height, width, false),
                PlaneU(cw, height, cw, false),
                PlaneV(cw, height, cw, false)),

            VideoPixelFormat.Yuv444p => new(
                true, true, false, VideoGlYuvMode.Planar8, 3,
                PlaneY(width, height, width, false),
                PlaneU(width, height, width, false),
                PlaneV(width, height, width, false)),

            VideoPixelFormat.P010le => new(
                true, true, true, VideoGlYuvMode.P010, 2,
                PlaneY(width, height, width * 2, true),
                PlaneUv(cw, ch420, cw * 4, true),
                default),

            VideoPixelFormat.Yuv420p10le => new(
                true, true, true, VideoGlYuvMode.Planar10, 3,
                PlaneY(width, height, width * 2, true),
                PlaneU(cw, ch420, cw * 2, true),
                PlaneV(cw, ch420, cw * 2, true)),

            VideoPixelFormat.Yuv422p10le => new(
                true, true, true, VideoGlYuvMode.Planar10, 3,
                PlaneY(width, height, width * 2, true),
                PlaneU(cw, height, cw * 2, true),
                PlaneV(cw, height, cw * 2, true)),

            VideoPixelFormat.Yuv444p10le => new(
                true, true, true, VideoGlYuvMode.Planar10, 3,
                PlaneY(width, height, width * 2, true),
                PlaneU(width, height, width * 2, true),
                PlaneV(width, height, width * 2, true)),

            _ => default
        };
    }

    private static VideoGlPlanePlan PlaneRgba(int width, int height)
        => new(0, VideoGlPlaneSlot.Rgba, width, height, width * 4,
            VideoGlConstants.Rgba8, VideoGlConstants.Rgba, VideoGlConstants.UnsignedByte);

    private static VideoGlPlanePlan PlaneY(int width, int height, int rowBytes, bool is16Bit)
        => new(0, VideoGlPlaneSlot.Y, width, height, rowBytes,
            is16Bit ? VideoGlConstants.R16 : VideoGlConstants.R8,
            VideoGlConstants.Red,
            is16Bit ? VideoGlConstants.UnsignedShort : VideoGlConstants.UnsignedByte);

    private static VideoGlPlanePlan PlaneUv(int width, int height, int rowBytes, bool is16Bit)
        => new(1, VideoGlPlaneSlot.Uv, width, height, rowBytes,
            is16Bit ? VideoGlConstants.Rg16 : VideoGlConstants.Rg8,
            VideoGlConstants.Rg,
            is16Bit ? VideoGlConstants.UnsignedShort : VideoGlConstants.UnsignedByte);

    private static VideoGlPlanePlan PlaneU(int width, int height, int rowBytes, bool is16Bit)
        => new(1, VideoGlPlaneSlot.U, width, height, rowBytes,
            is16Bit ? VideoGlConstants.R16 : VideoGlConstants.R8,
            VideoGlConstants.Red,
            is16Bit ? VideoGlConstants.UnsignedShort : VideoGlConstants.UnsignedByte);

    private static VideoGlPlanePlan PlaneV(int width, int height, int rowBytes, bool is16Bit)
        => new(2, VideoGlPlaneSlot.V, width, height, rowBytes,
            is16Bit ? VideoGlConstants.R16 : VideoGlConstants.R8,
            VideoGlConstants.Red,
            is16Bit ? VideoGlConstants.UnsignedShort : VideoGlConstants.UnsignedByte);
}

