using S.Media.Core.Video;

namespace S.Media.OpenGL.SDL3;

/// <summary>
/// Encodes all GL texture parameters needed to upload a single YUV frame.
/// Shared between <see cref="SDL3VideoView"/> and <see cref="SDL3ShaderPipeline"/>.
/// </summary>
internal readonly record struct YuvPlan(
    int ModeId,
    bool IsSemiPlanar,
    int YWidth,  int YHeight,  int YRowBytes,  int YInternalFormat, int YFormat,  int YType,
    int UvWidth, int UvHeight, int UvRowBytes, int UvInternalFormat, int UvFormat, int UvType,
    int UWidth,  int UHeight,  int URowBytes,  int UInternalFormat, int UFormat,  int UType,
    int VWidth,  int VHeight,  int VRowBytes,  int VInternalFormat, int VFormat,  int VType)
{
    // GL constants used only by the YUV upload path — kept local to avoid polluting callers.
    private const int GL_UNSIGNED_BYTE  = 0x1401;
    private const int GL_UNSIGNED_SHORT = 0x1403;
    private const int GL_RED            = 0x1903;
    private const int GL_RG             = 0x8227;
    private const int GL_R8             = 0x8229;
    private const int GL_R16            = 0x822A;
    private const int GL_RG8            = 0x822B;
    private const int GL_RG16           = 0x822C;

    /// <summary>
    /// Builds the upload plan for <paramref name="frame"/>'s pixel format.
    /// Returns <see langword="false"/> (and <paramref name="plan"/> = <c>default</c>) for
    /// unsupported formats; callers should fall back or return an error.
    /// </summary>
    public static bool TryBuild(VideoFrame frame, out YuvPlan plan)
    {
        var w    = frame.Width;
        var h    = frame.Height;
        var cw   = (w + 1) / 2;
        var ch420 = (h + 1) / 2;

        plan = frame.PixelFormat switch
        {
            VideoPixelFormat.Nv12 => new YuvPlan(1, true,
                w, h, w,    GL_R8,  GL_RED, GL_UNSIGNED_BYTE,
                cw, ch420, cw * 2, GL_RG8, GL_RG, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0),

            VideoPixelFormat.Yuv420P => new YuvPlan(2, false,
                w, h, w,    GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                cw, ch420, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                cw, ch420, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE),

            VideoPixelFormat.Yuv422P => new YuvPlan(2, false,
                w, h, w,  GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                cw, h, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                cw, h, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE),

            VideoPixelFormat.Yuv444P => new YuvPlan(2, false,
                w, h, w, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                w, h, w, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                w, h, w, GL_R8, GL_RED, GL_UNSIGNED_BYTE),

            VideoPixelFormat.P010Le => new YuvPlan(3, true,
                w, h, w * 2,    GL_R16,  GL_RED, GL_UNSIGNED_SHORT,
                cw, ch420, cw * 4, GL_RG16, GL_RG, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0),

            VideoPixelFormat.Yuv420P10Le => new YuvPlan(4, false,
                w, h, w * 2,     GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                cw, ch420, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                cw, ch420, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),

            VideoPixelFormat.Yuv422P10Le => new YuvPlan(4, false,
                w, h, w * 2,  GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                cw, h, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                cw, h, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),

            VideoPixelFormat.Yuv444P10Le => new YuvPlan(4, false,
                w, h, w * 2,  GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                w, h, w * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                w, h, w * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),

            _ => default,
        };

        return plan.ModeId != 0;
    }
}

