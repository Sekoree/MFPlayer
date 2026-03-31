using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using S.Media.Core.Video;
using S.Media.OpenGL;

namespace S.Media.OpenGL.Avalonia.Controls;

/// <summary>
/// Internal GL renderer that manages shader programs, textures, VAO/VBO, and upload logic
/// for presenting <see cref="VideoFrame"/> data via Avalonia's <see cref="GlInterface"/>.
/// Ported from the proven VideoGL shader/texture/upload path.
/// </summary>
internal sealed class AvaloniaGLRenderer : IDisposable
{
    // GL constants not in Avalonia's GlConsts
    private const int GL_TEXTURE1 = 0x84C1;
    private const int GL_TEXTURE2 = 0x84C2;
    private const int GL_UNPACK_ALIGNMENT = 0x0CF5;
    private const int GL_UNPACK_ROW_LENGTH = 0x0CF2;
    private const int GL_R8 = 0x8229;
    private const int GL_RG8 = 0x822B;
    private const int GL_RED = 0x1903;
    private const int GL_RG = 0x8227;
    private const int GL_RGBA8 = 0x8058;
    private const int GL_R16 = 0x822A;
    private const int GL_RG16 = 0x822C;
    private const int GL_UNSIGNED_SHORT = 0x1403;
    private const int GL_BLEND = 0x0BE2;

    // Quad vertices: posX, posY, texU, texV — 2 triangles = full-screen quad
    private static readonly float[] QuadVertices =
    [
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
         1f,  1f, 1f, 0f,
        -1f, -1f, 0f, 1f,
         1f,  1f, 1f, 0f,
        -1f,  1f, 0f, 0f,
    ];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TexSubImage2DProc(int target, int level, int xoffset, int yoffset,
        int width, int height, int format, int type, nint pixels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetUniformLocationProc(int program, [MarshalAs(UnmanagedType.LPStr)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Uniform1iProc(int location, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PixelStoreIProc(int pname, int param);

    private int _rgbaProgram;
    private int _yuvProgram;
    private int _vbo;
    private int _vao;
    private int _textureRgba;
    private int _textureY;
    private int _textureU;
    private int _textureV;
    private int _textureWidth;
    private int _textureHeight;
    private bool _canUseGpuYuvPath;
    private bool _can16BitTextures;
    private int _yuvPixelFormatLocation = -1;
    private int _yuvFullRangeLocation = -1;      // B6
    private TexSubImage2DProc? _texSubImage2D;
    private GetUniformLocationProc? _getUniformLocation;
    private Uniform1iProc? _uniform1i;
    private PixelStoreIProc? _pixelStoreI;

    // Texture allocation tracking (avoids glTexImage2D when only data changes)
    private (int w, int h, int internalFmt) _rgbaTexState;
    private (int w, int h, int internalFmt) _yTexState;
    private (int w, int h, int internalFmt) _uTexState;
    private (int w, int h, int internalFmt) _vTexState;

    private bool _ready;
    private bool _disposed;

    public bool IsReady => _ready;
    public int TextureWidth => _textureWidth;
    public int TextureHeight => _textureHeight;

    /// <summary>Time (ms) spent uploading textures during the last <see cref="RenderFrame"/> call.</summary>
    public double LastUploadMs { get; private set; }

    /// <summary>Time (ms) spent in the draw call during the last <see cref="RenderFrame"/> call.</summary>
    public double LastPresentMs { get; private set; }

    public void Initialize(GlInterface gl)
    {
        if (_disposed) return;

        // Resolve extra GL entry points
        ResolveProc(gl, "glTexSubImage2D", out _texSubImage2D);
        ResolveProc(gl, "glGetUniformLocation", out _getUniformLocation);
        ResolveProc(gl, "glUniform1i", out _uniform1i);
        ResolveProc(gl, "glPixelStorei", out _pixelStoreI);

        var isEs = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;

        // Build shader programs
        _rgbaProgram = BuildProgram(gl,
            isEs ? GlslShaders.VertexEs : GlslShaders.VertexCore,
            isEs ? GlslShaders.FragmentRgbaEs : GlslShaders.FragmentRgbaCore);

        _yuvProgram = BuildProgram(gl,
            isEs ? GlslShaders.VertexEs : GlslShaders.VertexCore,
            isEs ? GlslShaders.FragmentYuvEs : GlslShaders.FragmentYuvCore);

        _canUseGpuYuvPath = _yuvProgram != 0 && _getUniformLocation != null && _uniform1i != null;

        var ver = gl.ContextInfo.Version;
        _can16BitTextures = isEs
            ? (ver.Major > 3 || (ver.Major == 3 && ver.Minor >= 2))
            : ver.Major >= 3;

        if (_canUseGpuYuvPath)
            InitializeYuvUniforms(gl);

        // VAO + VBO
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vbo);

        var handle = GCHandle.Alloc(QuadVertices, GCHandleType.Pinned);
        try
        {
            gl.BufferData(GlConsts.GL_ARRAY_BUFFER, QuadVertices.Length * sizeof(float),
                handle.AddrOfPinnedObject(), GlConsts.GL_STATIC_DRAW);
        }
        finally { handle.Free(); }

        var stride = 4 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, stride, nint.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GlConsts.GL_FLOAT, 0, stride, 2 * sizeof(float));
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
        gl.BindVertexArray(0);

        // Textures
        _textureRgba = CreateLinearTexture(gl);
        _textureY = CreateLinearTexture(gl);
        _textureU = CreateLinearTexture(gl);
        _textureV = CreateLinearTexture(gl);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

        _ready = true;
    }

    /// <summary>
    /// Upload a video frame and draw it into the current framebuffer.
    /// Call between OnOpenGlRender's gl.BindFramebuffer and return.
    /// </summary>
    public void RenderFrame(GlInterface gl, int fb, VideoFrame frame, int surfaceWidth, int surfaceHeight, bool keepAspectRatio)
    {
        if (!_ready || _disposed) return;

        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        gl.Viewport(0, 0, surfaceWidth, surfaceHeight);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

        if (frame.Width <= 0 || frame.Height <= 0 || frame.Plane0.IsEmpty) return;

        var useYuv = false;
        var yuvMode = 0;

        var uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();

        switch (frame.PixelFormat)
        {
            case VideoPixelFormat.Rgba32:
            case VideoPixelFormat.Bgra32:
                UploadRgba(gl, frame);
                break;

            case VideoPixelFormat.Nv12:
                if (_canUseGpuYuvPath)
                    { UploadNv12Gpu(gl, frame); useYuv = true; yuvMode = 1; }
                else
                    UploadRgba(gl, frame);
                break;

            case VideoPixelFormat.Yuv420P:
            case VideoPixelFormat.Yuv422P:
            case VideoPixelFormat.Yuv444P:
                if (_canUseGpuYuvPath)
                    { UploadPlanar8Gpu(gl, frame); useYuv = true; yuvMode = 2; }
                else
                    UploadRgba(gl, frame);
                break;

            case VideoPixelFormat.P010Le:
                if (_canUseGpuYuvPath && _can16BitTextures)
                    { UploadP010Gpu(gl, frame); useYuv = true; yuvMode = 3; }
                else
                    UploadRgba(gl, frame);
                break;

            case VideoPixelFormat.Yuv420P10Le:
            case VideoPixelFormat.Yuv422P10Le:
            case VideoPixelFormat.Yuv444P10Le:
                if (_canUseGpuYuvPath && _can16BitTextures)
                    { UploadPlanar10Gpu(gl, frame); useYuv = true; yuvMode = 4; }
                else
                    UploadRgba(gl, frame);
                break;

            default:
                return;
        }

        LastUploadMs = System.Diagnostics.Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;

        var vp = GetAspectFitViewport(surfaceWidth, surfaceHeight, _textureWidth, _textureHeight, keepAspectRatio);
        gl.Viewport(vp.x, vp.y, vp.w, vp.h);

        if (useYuv)
        {
            gl.UseProgram(_yuvProgram);
            if (_uniform1i != null && _yuvPixelFormatLocation >= 0)
                _uniform1i(_yuvPixelFormatLocation, yuvMode);
            if (_uniform1i != null && _yuvFullRangeLocation >= 0)  // B6
                _uniform1i(_yuvFullRangeLocation, frame.IsFullRange ? 1 : 0);
        }
        else
        {
            gl.UseProgram(_rgbaProgram);
        }

        var drawStart = System.Diagnostics.Stopwatch.GetTimestamp();
        gl.BindVertexArray(_vao);
        gl.DrawArrays(GlConsts.GL_TRIANGLES, 0, 6);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);
        LastPresentMs = System.Diagnostics.Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds;
    }

    public void Deinitialize(GlInterface gl)
    {
        DeleteIfNonZero(gl.DeleteBuffer, ref _vbo);
        DeleteIfNonZero(gl.DeleteVertexArray, ref _vao);
        DeleteIfNonZero(gl.DeleteTexture, ref _textureRgba);
        DeleteIfNonZero(gl.DeleteTexture, ref _textureY);
        DeleteIfNonZero(gl.DeleteTexture, ref _textureU);
        DeleteIfNonZero(gl.DeleteTexture, ref _textureV);
        DeleteIfNonZero(gl.DeleteProgram, ref _rgbaProgram);
        DeleteIfNonZero(gl.DeleteProgram, ref _yuvProgram);
        _ready = false;
        _rgbaTexState = default;
        _yTexState = default;
        _uTexState = default;
        _vTexState = default;
    }

    public void Dispose()
    {
        _disposed = true;
        _ready = false;
    }

    // ── Upload helpers ───────────────────────────────────────────────────────

    private unsafe void UploadRgba(GlInterface gl, VideoFrame frame)
    {
        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureRgba);
        UploadPlane(gl, ref _rgbaTexState, frame.Plane0.Span, frame.Plane0Stride,
            frame.Width, frame.Height, GL_RGBA8, GlConsts.GL_RGBA, GlConsts.GL_UNSIGNED_BYTE, frame.Width * 4);
        _textureWidth = frame.Width;
        _textureHeight = frame.Height;
    }

    private void UploadNv12Gpu(GlInterface gl, VideoFrame frame)
    {
        var w = frame.Width;
        var h = frame.Height;
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;

        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
        UploadPlane(gl, ref _yTexState, frame.Plane0.Span, frame.Plane0Stride,
            w, h, GL_R8, GL_RED, GlConsts.GL_UNSIGNED_BYTE, w);

        // NV12 UV plane is interleaved (RG) at half resolution
        gl.ActiveTexture(GL_TEXTURE1);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
        UploadPlane(gl, ref _uTexState, frame.Plane1.Span, frame.Plane1Stride,
            cw, ch, GL_RG8, GL_RG, GlConsts.GL_UNSIGNED_BYTE, cw * 2);

        _textureWidth = w;
        _textureHeight = h;
    }

    private void UploadPlanar8Gpu(GlInterface gl, VideoFrame frame)
    {
        var w = frame.Width;
        var h = frame.Height;
        // Chroma dimensions depend on format
        var cw = frame.PixelFormat == VideoPixelFormat.Yuv444P ? w : (w + 1) / 2;
        var ch = frame.PixelFormat switch
        {
            VideoPixelFormat.Yuv420P => (h + 1) / 2,
            _ => h,
        };

        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
        UploadPlane(gl, ref _yTexState, frame.Plane0.Span, frame.Plane0Stride,
            w, h, GL_R8, GL_RED, GlConsts.GL_UNSIGNED_BYTE, w);

        gl.ActiveTexture(GL_TEXTURE1);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
        UploadPlane(gl, ref _uTexState, frame.Plane1.Span, frame.Plane1Stride,
            cw, ch, GL_R8, GL_RED, GlConsts.GL_UNSIGNED_BYTE, cw);

        gl.ActiveTexture(GL_TEXTURE2);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
        UploadPlane(gl, ref _vTexState, frame.Plane2.Span, frame.Plane2Stride,
            cw, ch, GL_R8, GL_RED, GlConsts.GL_UNSIGNED_BYTE, cw);

        _textureWidth = w;
        _textureHeight = h;
    }

    private void UploadP010Gpu(GlInterface gl, VideoFrame frame)
    {
        var w = frame.Width;
        var h = frame.Height;
        var cw = (w + 1) / 2;
        var ch = (h + 1) / 2;

        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
        UploadPlane(gl, ref _yTexState, frame.Plane0.Span, frame.Plane0Stride,
            w, h, GL_R16, GL_RED, GL_UNSIGNED_SHORT, w * 2);

        gl.ActiveTexture(GL_TEXTURE1);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
        UploadPlane(gl, ref _uTexState, frame.Plane1.Span, frame.Plane1Stride,
            cw, ch, GL_RG16, GL_RG, GL_UNSIGNED_SHORT, cw * 4);

        _textureWidth = w;
        _textureHeight = h;
    }

    private void UploadPlanar10Gpu(GlInterface gl, VideoFrame frame)
    {
        var w = frame.Width;
        var h = frame.Height;
        var cw = frame.PixelFormat == VideoPixelFormat.Yuv444P10Le ? w : (w + 1) / 2;
        var ch = frame.PixelFormat switch
        {
            VideoPixelFormat.Yuv420P10Le => (h + 1) / 2,
            _ => h,
        };

        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
        UploadPlane(gl, ref _yTexState, frame.Plane0.Span, frame.Plane0Stride,
            w, h, GL_R16, GL_RED, GL_UNSIGNED_SHORT, w * 2);

        gl.ActiveTexture(GL_TEXTURE1);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
        UploadPlane(gl, ref _uTexState, frame.Plane1.Span, frame.Plane1Stride,
            cw, ch, GL_R16, GL_RED, GL_UNSIGNED_SHORT, cw * 2);

        gl.ActiveTexture(GL_TEXTURE2);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
        UploadPlane(gl, ref _vTexState, frame.Plane2.Span, frame.Plane2Stride,
            cw, ch, GL_R16, GL_RED, GL_UNSIGNED_SHORT, cw * 2);

        _textureWidth = w;
        _textureHeight = h;
    }

    private unsafe void UploadPlane(GlInterface gl,
        ref (int w, int h, int internalFmt) state,
        ReadOnlySpan<byte> planeData, int planeStride,
        int width, int height,
        int internalFormat, int format, int type,
        int tightRowBytes)
    {
        if (planeData.IsEmpty || width <= 0 || height <= 0) return;

        // GL_UNPACK_ROW_LENGTH is specified in pixels (texels), not bytes.
        // bytes-per-pixel = components × bytes-per-component
        var bytesPerPixel = format switch
        {
            var f when f == GlConsts.GL_RGBA => 4,        // 4 × GL_UNSIGNED_BYTE
            var f when f == GL_RG            => type == GL_UNSIGNED_SHORT ? 4 : 2,   // 2 × 2 or 2 × 1
            _                                => type == GL_UNSIGNED_SHORT ? 2 : 1,   // 1 × 2 or 1 × 1
        };
        var stridePixels = planeStride / bytesPerPixel;

        _pixelStoreI?.Invoke(GL_UNPACK_ALIGNMENT, 1);
        _pixelStoreI?.Invoke(GL_UNPACK_ROW_LENGTH, stridePixels);

        fixed (byte* ptr = planeData)
        {
            if (state.w != width || state.h != height || state.internalFmt != internalFormat)
            {
                // Allocate new texture
                gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, internalFormat,
                    width, height, 0, format, type, (nint)ptr);
                state = (width, height, internalFormat);
            }
            else if (_texSubImage2D != null)
            {
                // Fast sub-image update
                _texSubImage2D(GlConsts.GL_TEXTURE_2D, 0, 0, 0,
                    width, height, format, type, (nint)ptr);
            }
            else
            {
                gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, internalFormat,
                    width, height, 0, format, type, (nint)ptr);
            }
        }

        // Reset row length
        _pixelStoreI?.Invoke(GL_UNPACK_ROW_LENGTH, 0);
    }

    // ── Shader source strings ────────────────────────────────────────────────
    // Ported from VideoGlShaders — dual profile (core 3.3 + ES 3.0)

    // Shader sources are in GlslShaders.cs (S.Media.OpenGL) — single source of truth.
    // (VertexShaderCore/Es, FragmentShaderCore/Es, YuvFragmentShaderCore/Es removed from this file.)
    private void InitializeYuvUniforms(GlInterface gl)
    {
        if (_getUniformLocation == null || _uniform1i == null || _yuvProgram == 0) return;

        gl.UseProgram(_yuvProgram);
        var yLoc = _getUniformLocation(_yuvProgram, "uTextureY");
        var uLoc = _getUniformLocation(_yuvProgram, "uTextureU");
        var vLoc = _getUniformLocation(_yuvProgram, "uTextureV");
        _yuvPixelFormatLocation = _getUniformLocation(_yuvProgram, "uPixelFormat");
        _yuvFullRangeLocation   = _getUniformLocation(_yuvProgram, "uFullRange");  // B6

        if (yLoc >= 0) _uniform1i(yLoc, 0);
        if (uLoc >= 0) _uniform1i(uLoc, 1);
        if (vLoc >= 0) _uniform1i(vLoc, 2);

        gl.UseProgram(0);
    }

    private static int BuildProgram(GlInterface gl, string vertexSource, string fragmentSource)
    {
        var vs = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        var vsErr = gl.CompileShaderAndGetError(vs, vertexSource);
        if (!string.IsNullOrWhiteSpace(vsErr)) return 0;

        var fs = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        var fsErr = gl.CompileShaderAndGetError(fs, fragmentSource);
        if (!string.IsNullOrWhiteSpace(fsErr)) return 0;

        var prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.BindAttribLocationString(prog, 0, "aPosition");
        gl.BindAttribLocationString(prog, 1, "aTexCoord");
        var linkErr = gl.LinkProgramAndGetError(prog);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        if (!string.IsNullOrWhiteSpace(linkErr))
        {
            gl.DeleteProgram(prog);
            return 0;
        }

        return prog;
    }

    private static int CreateLinearTexture(GlInterface gl)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, tex);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);
        return tex;
    }

    private static void ResolveProc<T>(GlInterface gl, string name, out T? del) where T : Delegate
    {
        var addr = gl.GetProcAddress(name);
        del = addr != nint.Zero ? Marshal.GetDelegateForFunctionPointer<T>(addr) : null;
    }

    private static void DeleteIfNonZero(Action<int> delete, ref int handle)
    {
        if (handle != 0) { delete(handle); handle = 0; }
    }

    private static (int x, int y, int w, int h) GetAspectFitViewport(
        int surfaceW, int surfaceH, int videoW, int videoH, bool keepAspect)
    {
        if (!keepAspect || videoW <= 0 || videoH <= 0)
            return (0, 0, surfaceW, surfaceH);

        var scaleX = surfaceW / (double)videoW;
        var scaleY = surfaceH / (double)videoH;
        var scale = Math.Min(scaleX, scaleY);
        var w = (int)(videoW * scale);
        var h = (int)(videoH * scale);
        return ((surfaceW - w) / 2, (surfaceH - h) / 2, w, h);
    }
}
