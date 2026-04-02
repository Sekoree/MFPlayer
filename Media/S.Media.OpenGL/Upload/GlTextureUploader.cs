using S.Media.Core.Video;

namespace S.Media.OpenGL.Upload;

/// <summary>
/// Backend-agnostic GPU texture upload engine. Accepts a <see cref="VideoFrame"/> and
/// uploads its pixel data to one or more GL textures using the supplied delegate-based
/// GL function table (<see cref="GlUploadFunctions"/>).
/// <para>
/// This class is shared by all OpenGL rendering backends (SDL3, Avalonia, etc.) so that
/// texture upload logic is implemented once. Backends only need to:
/// <list type="number">
/// <item>Load GL functions into a <see cref="GlUploadFunctions"/> instance.</item>
/// <item>Allocate texture IDs and register them via <see cref="SetTextureIds"/>.</item>
/// <item>Call <see cref="Upload"/> each frame.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class GlTextureUploader
{
    // ── GL constants ────────────────────────────────────────────────────────────
    private const int GL_TEXTURE_2D    = 0x0DE1;
    private const int GL_UNPACK_ALIGNMENT = 0x0CF5;
    private const int GL_RGBA8         = 0x8058;
    private const int GL_RGBA          = 0x1908;
    private const int GL_BGRA          = 0x80E1;
    private const int GL_UNSIGNED_BYTE = 0x1401;

    // ── State ───────────────────────────────────────────────────────────────────
    private GlUploadFunctions? _gl;
    private int _rgbaTexture;
    private int _yTexture;
    private int _uTexture;
    private int _vTexture;

    private TextureUploadState _rgbaState;
    private TextureUploadState _yState;
    private TextureUploadState _uState;
    private TextureUploadState _vState;

    // Scratch buffers for stride-packing (reused across frames to avoid GC pressure).
    private byte[]? _packedRgbaScratch;
    private byte[]? _plane0Scratch;
    private byte[]? _plane1Scratch;
    private byte[]? _plane2Scratch;

    /// <summary>
    /// Configures the GL function pointers used for all texture uploads.
    /// Must be called once after the GL context is current and functions are loaded.
    /// </summary>
    public void SetFunctions(GlUploadFunctions gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        _gl = gl;
    }

    /// <summary>
    /// Registers the texture IDs that uploads will target. For RGBA frames only
    /// <paramref name="rgbaTexture"/> is used; for YUV frames, all four IDs are used
    /// (V is ignored for semi-planar formats).
    /// </summary>
    public void SetTextureIds(int rgbaTexture, int yTexture, int uTexture, int vTexture)
    {
        _rgbaTexture = rgbaTexture;
        _yTexture = yTexture;
        _uTexture = uTexture;
        _vTexture = vTexture;
    }

    /// <summary>
    /// Uploads <paramref name="frame"/>'s pixel data to the registered GL textures.
    /// Returns <see langword="true"/> on success.
    /// </summary>
    public bool Upload(VideoFrame frame)
    {
        if (_gl is null)
            return false;

        if (frame.PixelFormat is VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32)
            return UploadRgba(frame);

        return UploadYuv(frame);
    }

    /// <summary>Resets all tracked upload state (call when textures are recreated).</summary>
    public void Reset()
    {
        _rgbaState = default;
        _yState = default;
        _uState = default;
        _vState = default;
        _packedRgbaScratch = null;
        _plane0Scratch = null;
        _plane1Scratch = null;
        _plane2Scratch = null;
    }

    // ── RGBA upload ─────────────────────────────────────────────────────────────

    private bool UploadRgba(VideoFrame frame)
    {
        if (!TryGetPackedRgbaBytes(frame, out var packed, out var glFormat))
            return false;

        UploadTexture(ref _rgbaState, _rgbaTexture, frame.Width, frame.Height, GL_RGBA8, glFormat, GL_UNSIGNED_BYTE, packed);
        return true;
    }

    // ── YUV upload ──────────────────────────────────────────────────────────────

    private bool UploadYuv(VideoFrame frame)
    {
        if (!YuvUploadPlan.TryBuild(frame, out var plan))
            return false;

        var yData = PackPlane(frame.Plane0, frame.Plane0Stride, plan.YRowBytes, plan.YHeight, ref _plane0Scratch);
        if (yData is null)
            return false;

        UploadTexture(ref _yState, _yTexture, plan.YWidth, plan.YHeight, plan.YInternalFormat, plan.YFormat, plan.YType, yData);

        if (plan.IsSemiPlanar)
        {
            var uvData = PackPlane(frame.Plane1, frame.Plane1Stride, plan.UvRowBytes, plan.UvHeight, ref _plane1Scratch);
            if (uvData is null)
                return false;
            UploadTexture(ref _uState, _uTexture, plan.UvWidth, plan.UvHeight, plan.UvInternalFormat, plan.UvFormat, plan.UvType, uvData);
        }
        else
        {
            var uData = PackPlane(frame.Plane1, frame.Plane1Stride, plan.URowBytes, plan.UHeight, ref _plane1Scratch);
            var vData = PackPlane(frame.Plane2, frame.Plane2Stride, plan.VRowBytes, plan.VHeight, ref _plane2Scratch);
            if (uData is null || vData is null)
                return false;
            UploadTexture(ref _uState, _uTexture, plan.UWidth, plan.UHeight, plan.UInternalFormat, plan.UFormat, plan.UType, uData);
            UploadTexture(ref _vState, _vTexture, plan.VWidth, plan.VHeight, plan.VInternalFormat, plan.VFormat, plan.VType, vData);
        }

        return true;
    }

    // ── Core texture upload ─────────────────────────────────────────────────────

    private void UploadTexture(ref TextureUploadState state, int textureId, int width, int height, int internalFormat, int format, int type, ReadOnlySpan<byte> data)
    {
        _gl!.BindTexture(GL_TEXTURE_2D, textureId);
        _gl.PixelStorei(GL_UNPACK_ALIGNMENT, 1);

        var reallocate = !state.IsInitialized
            || state.Width != width
            || state.Height != height
            || state.InternalFormat != internalFormat
            || state.Format != format
            || state.Type != type;

        unsafe
        {
            fixed (byte* ptr = data)
            {
                if (reallocate)
                {
                    _gl.TexImage2D(GL_TEXTURE_2D, 0, internalFormat, width, height, 0, format, type, (nint)ptr);
                    state = new TextureUploadState
                    {
                        IsInitialized = true,
                        Width = width,
                        Height = height,
                        InternalFormat = internalFormat,
                        Format = format,
                        Type = type,
                    };
                }
                else
                {
                    _gl.TexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, format, type, (nint)ptr);
                }
            }
        }
    }

    // ── Plane packing helpers ───────────────────────────────────────────────────

    private bool TryGetPackedRgbaBytes(VideoFrame frame, out ReadOnlySpan<byte> packed, out int glFormat)
    {
        glFormat = frame.PixelFormat == VideoPixelFormat.Bgra32 ? GL_BGRA : GL_RGBA;
        packed = default;

        var requiredStride = frame.Width * 4;
        var requiredLength = checked(requiredStride * frame.Height);
        if (frame.Plane0.Length < requiredLength)
            return false;

        if (frame.Plane0Stride == requiredStride)
        {
            packed = frame.Plane0.Span.Slice(0, requiredLength);
            return true;
        }

        if (_packedRgbaScratch is null || _packedRgbaScratch.Length < requiredLength)
            _packedRgbaScratch = new byte[requiredLength];

        var source = frame.Plane0.Span;
        for (var y = 0; y < frame.Height; y++)
        {
            var srcOffset = y * frame.Plane0Stride;
            var dstOffset = y * requiredStride;
            source.Slice(srcOffset, requiredStride).CopyTo(_packedRgbaScratch.AsSpan(dstOffset, requiredStride));
        }

        packed = _packedRgbaScratch.AsSpan(0, requiredLength);
        return true;
    }

    private static byte[]? PackPlane(ReadOnlyMemory<byte> plane, int stride, int rowBytes, int height, ref byte[]? scratch)
    {
        if (rowBytes <= 0 || height <= 0 || stride < rowBytes)
            return null;

        var requiredLength = checked(rowBytes * height);
        if (plane.Length < checked(stride * height))
            return null;

        if (scratch is null || scratch.Length < requiredLength)
            scratch = new byte[requiredLength];

        var source = plane.Span;
        var destination = scratch.AsSpan(0, requiredLength);
        for (var y = 0; y < height; y++)
            source.Slice(y * stride, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));

        return scratch;
    }

    // ── Types ───────────────────────────────────────────────────────────────────

    private struct TextureUploadState
    {
        public bool IsInitialized;
        public int Width;
        public int Height;
        public int InternalFormat;
        public int Format;
        public int Type;
    }
}

/// <summary>
/// Delegate-based GL function table for texture upload operations.
/// Backends populate this with their platform-specific function pointers
/// (e.g. via <c>SDL.GLGetProcAddress</c> or <c>wglGetProcAddress</c>).
/// </summary>
internal sealed class GlUploadFunctions
{
    /// <summary><c>void glBindTexture(GLenum target, GLuint texture)</c></summary>
    public required Action<int, int> BindTexture { get; init; }

    /// <summary><c>void glPixelStorei(GLenum pname, GLint param)</c></summary>
    public required Action<int, int> PixelStorei { get; init; }

    /// <summary><c>void glTexImage2D(GLenum target, GLint level, GLint internalformat, GLsizei width, GLsizei height, GLint border, GLenum format, GLenum type, const void *pixels)</c></summary>
    public required Action<int, int, int, int, int, int, int, int, nint> TexImage2D { get; init; }

    /// <summary><c>void glTexSubImage2D(GLenum target, GLint level, GLint xoffset, GLint yoffset, GLsizei width, GLsizei height, GLenum format, GLenum type, const void *pixels)</c></summary>
    public required Action<int, int, int, int, int, int, int, int, nint> TexSubImage2D { get; init; }
}

/// <summary>
/// Upload plan for a single YUV frame. Mirrors the SDL3-specific <c>YuvPlan</c> record
/// but lives in the shared <c>S.Media.OpenGL</c> layer so all backends can use it.
/// </summary>
internal readonly record struct YuvUploadPlan(
    int ModeId,
    bool IsSemiPlanar,
    int YWidth,  int YHeight,  int YRowBytes,  int YInternalFormat, int YFormat,  int YType,
    int UvWidth, int UvHeight, int UvRowBytes, int UvInternalFormat, int UvFormat, int UvType,
    int UWidth,  int UHeight,  int URowBytes,  int UInternalFormat, int UFormat,  int UType,
    int VWidth,  int VHeight,  int VRowBytes,  int VInternalFormat, int VFormat,  int VType)
{
    // GL constants used only by the YUV upload path.
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
    /// Returns <see langword="false"/> for unsupported formats.
    /// </summary>
    public static bool TryBuild(VideoFrame frame, out YuvUploadPlan plan)
    {
        var w    = frame.Width;
        var h    = frame.Height;
        var cw   = (w + 1) / 2;
        var ch420 = (h + 1) / 2;

        plan = frame.PixelFormat switch
        {
            VideoPixelFormat.Nv12 => new YuvUploadPlan(1, true,
                w, h, w,    GL_R8,  GL_RED, GL_UNSIGNED_BYTE,
                cw, ch420, cw * 2, GL_RG8, GL_RG, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0),

            VideoPixelFormat.Yuv420P => new YuvUploadPlan(2, false,
                w, h, w,    GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                cw, ch420, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                cw, ch420, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE),

            VideoPixelFormat.Yuv422P => new YuvUploadPlan(2, false,
                w, h, w,  GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                cw, h, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                cw, h, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE),

            VideoPixelFormat.Yuv444P => new YuvUploadPlan(2, false,
                w, h, w, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                w, h, w, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                w, h, w, GL_R8, GL_RED, GL_UNSIGNED_BYTE),

            VideoPixelFormat.P010Le => new YuvUploadPlan(3, true,
                w, h, w * 2,    GL_R16,  GL_RED, GL_UNSIGNED_SHORT,
                cw, ch420, cw * 4, GL_RG16, GL_RG, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0),

            VideoPixelFormat.Yuv420P10Le => new YuvUploadPlan(4, false,
                w, h, w * 2,     GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                cw, ch420, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                cw, ch420, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),

            VideoPixelFormat.Yuv422P10Le => new YuvUploadPlan(4, false,
                w, h, w * 2,  GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                cw, h, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                cw, h, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),

            VideoPixelFormat.Yuv444P10Le => new YuvUploadPlan(4, false,
                w, h, w * 2,  GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                w, h, w * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                w, h, w * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),

            _ => default,
        };

        return plan.ModeId != 0;
    }
}

