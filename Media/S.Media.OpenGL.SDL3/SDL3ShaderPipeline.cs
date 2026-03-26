using System.Runtime.InteropServices;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using SDL3;

namespace S.Media.OpenGL.SDL3;

/// <summary>
/// Self-contained GL shader/texture/upload/draw pipeline for the SDL3 embedded video use-case.
/// Mirrors the proven standalone rendering path from <see cref="SDL3VideoView"/> and the
/// Avalonia renderer, supporting all 11 <see cref="VideoPixelFormat"/> values including 10-bit.
/// </summary>
public sealed class SDL3ShaderPipeline : IDisposable
{
    // GL constants
    private const int GL_TEXTURE_2D = 0x0DE1;
    private const int GL_TEXTURE0 = 0x84C0;
    private const int GL_TEXTURE1 = 0x84C1;
    private const int GL_TEXTURE2 = 0x84C2;
    private const int GL_TEXTURE_MIN_FILTER = 0x2801;
    private const int GL_TEXTURE_MAG_FILTER = 0x2800;
    private const int GL_TEXTURE_WRAP_S = 0x2802;
    private const int GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_LINEAR = 0x2601;
    private const int GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_ARRAY_BUFFER = 0x8892;
    private const int GL_STATIC_DRAW = 0x88E4;
    private const int GL_FLOAT = 0x1406;
    private const int GL_TRIANGLES = 0x0004;
    private const int GL_COLOR_BUFFER_BIT = 0x00004000;
    private const int GL_VERTEX_SHADER = 0x8B31;
    private const int GL_FRAGMENT_SHADER = 0x8B30;
    private const int GL_COMPILE_STATUS = 0x8B81;
    private const int GL_LINK_STATUS = 0x8B82;
    private const int GL_UNPACK_ALIGNMENT = 0x0CF5;
    private const int GL_UNSIGNED_BYTE = 0x1401;
    private const int GL_UNSIGNED_SHORT = 0x1403;
    private const int GL_RGBA = 0x1908;
    private const int GL_BGRA = 0x80E1;
    private const int GL_RGBA8 = 0x8058;
    private const int GL_R8 = 0x8229;
    private const int GL_RG8 = 0x822B;
    private const int GL_R16 = 0x822A;
    private const int GL_RG16 = 0x822C;
    private const int GL_RED = 0x1903;
    private const int GL_RG = 0x8227;

    // GL delegate types
    private delegate void GlClearColorProc(float r, float g, float b, float a);
    private delegate void GlClearProc(int mask);
    private delegate int GlCreateShaderProc(int type);
    private delegate void GlShaderSourceProc(int shader, int count, nint strings, nint lengths);
    private delegate void GlCompileShaderProc(int shader);
    private delegate void GlGetShaderIvProc(int shader, int pname, out int param);
    private delegate int GlCreateProgramProc();
    private delegate void GlAttachShaderProc(int program, int shader);
    private delegate void GlBindAttribLocationProc(int program, int index, string name);
    private delegate void GlLinkProgramProc(int program);
    private delegate void GlGetProgramIvProc(int program, int pname, out int param);
    private delegate void GlUseProgramProc(int program);
    private delegate void GlDeleteShaderProc(int shader);
    private delegate void GlDeleteProgramProc(int program);
    private delegate int GlGetUniformLocationProc(int program, string name);
    private delegate void GlUniform1iProc(int location, int value);
    private delegate void GlGenVertexArraysProc(int n, out int arrays);
    private delegate void GlBindVertexArrayProc(int array);
    private delegate void GlDeleteVertexArraysProc(int n, in int arrays);
    private delegate void GlGenBuffersProc(int n, out int buffers);
    private delegate void GlBindBufferProc(int target, int buffer);
    private delegate void GlBufferDataProc(int target, nint size, nint data, int usage);
    private delegate void GlDeleteBuffersProc(int n, in int buffers);
    private delegate void GlEnableVertexAttribArrayProc(int index);
    private delegate void GlVertexAttribPointerProc(int index, int size, int type, int normalized, int stride, nint pointer);
    private delegate void GlGenTexturesProc(int n, out int textures);
    private delegate void GlBindTextureProc(int target, int texture);
    private delegate void GlTexParameteriProc(int target, int pname, int param);
    private delegate void GlTexImage2DProc(int target, int level, int internalFormat, int width, int height, int border, int format, int type, nint pixels);
    private delegate void GlTexSubImage2DProc(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, nint pixels);
    private delegate void GlPixelStoreIProc(int pname, int param);
    private delegate void GlActiveTextureProc(int texture);
    private delegate void GlDeleteTexturesProc(int n, in int textures);
    private delegate void GlDrawArraysProc(int mode, int first, int count);

    // GL function pointers
    private GlClearColorProc? _glClearColor;
    private GlClearProc? _glClear;
    private GlCreateShaderProc? _glCreateShader;
    private GlShaderSourceProc? _glShaderSource;
    private GlCompileShaderProc? _glCompileShader;
    private GlGetShaderIvProc? _glGetShaderIv;
    private GlCreateProgramProc? _glCreateProgram;
    private GlAttachShaderProc? _glAttachShader;
    private GlBindAttribLocationProc? _glBindAttribLocation;
    private GlLinkProgramProc? _glLinkProgram;
    private GlGetProgramIvProc? _glGetProgramIv;
    private GlUseProgramProc? _glUseProgram;
    private GlDeleteShaderProc? _glDeleteShader;
    private GlDeleteProgramProc? _glDeleteProgram;
    private GlGetUniformLocationProc? _glGetUniformLocation;
    private GlUniform1iProc? _glUniform1I;
    private GlGenVertexArraysProc? _glGenVertexArrays;
    private GlBindVertexArrayProc? _glBindVertexArray;
    private GlDeleteVertexArraysProc? _glDeleteVertexArrays;
    private GlGenBuffersProc? _glGenBuffers;
    private GlBindBufferProc? _glBindBuffer;
    private GlBufferDataProc? _glBufferData;
    private GlDeleteBuffersProc? _glDeleteBuffers;
    private GlEnableVertexAttribArrayProc? _glEnableVertexAttribArray;
    private GlVertexAttribPointerProc? _glVertexAttribPointer;
    private GlGenTexturesProc? _glGenTextures;
    private GlBindTextureProc? _glBindTexture;
    private GlTexParameteriProc? _glTexParameteri;
    private GlTexImage2DProc? _glTexImage2D;
    private GlTexSubImage2DProc? _glTexSubImage2D;
    private GlPixelStoreIProc? _glPixelStoreI;
    private GlActiveTextureProc? _glActiveTexture;
    private GlDeleteTexturesProc? _glDeleteTextures;
    private GlDrawArraysProc? _glDrawArrays;

    // GL resource handles
    private readonly Lock _gate = new();
    private int _rgbaProgram;
    private int _yuvProgram;
    private int _yuvPixelFormatLocation = -1;
    private int _vao;
    private int _vbo;
    private int _textureRgba;
    private int _textureY;
    private int _textureU;
    private int _textureV;
    private TextureUploadState _rgbaUploadState;
    private TextureUploadState _yUploadState;
    private TextureUploadState _uUploadState;
    private TextureUploadState _vUploadState;
    private byte[]? _packedRgbaScratch;
    private byte[]? _plane0Scratch;
    private byte[]? _plane1Scratch;
    private byte[]? _plane2Scratch;
    private bool _initialized;
    private bool _glReady;
    private bool _disposed;

    // Last uploaded frame info for draw
    private bool _lastFrameIsYuv;
    private int _lastYuvModeId;

    public int EnsureInitialized()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.SDL3EmbedTeardownFailed;
            }

            _initialized = true;
            return MediaResult.Success;
        }
    }

    public int Upload(VideoFrame frame)
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            var validation = frame.ValidateForPush();
            if (validation != MediaResult.Success)
            {
                return validation;
            }

            if (!_glReady)
            {
                var init = InitializeGlResources();
                if (init != MediaResult.Success)
                {
                    return init;
                }
            }

            if (frame.Width <= 0 || frame.Height <= 0 || frame.Plane0.IsEmpty)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            return UploadFrame(frame);
        }
    }

    public int Draw()
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            if (!_glReady)
            {
                return MediaResult.Success;
            }

            return DrawLastUploaded();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initialized = false;
            DisposeGlResources();
        }
    }

    // ── GL initialization ─────────────────────────────────────────────────────

    private int InitializeGlResources()
    {
        if (!LoadGlFunctions())
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        // Compile RGBA program
        var vs = CompileShader(GL_VERTEX_SHADER, VertexShaderSource);
        if (vs == 0) return (int)MediaErrorCode.SDL3EmbedInitializeFailed;

        var fs = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSource);
        if (fs == 0) { _glDeleteShader!(vs); return (int)MediaErrorCode.SDL3EmbedInitializeFailed; }

        _rgbaProgram = LinkProgram(vs, fs);
        _glDeleteShader!(vs);
        _glDeleteShader!(fs);
        if (_rgbaProgram == 0) return (int)MediaErrorCode.SDL3EmbedInitializeFailed;

        // Compile YUV program
        vs = CompileShader(GL_VERTEX_SHADER, VertexShaderSource);
        if (vs == 0) return (int)MediaErrorCode.SDL3EmbedInitializeFailed;

        var yuvFs = CompileShader(GL_FRAGMENT_SHADER, YuvFragmentShaderSource);
        if (yuvFs == 0) { _glDeleteShader!(vs); return (int)MediaErrorCode.SDL3EmbedInitializeFailed; }

        _yuvProgram = LinkProgram(vs, yuvFs);
        _glDeleteShader!(vs);
        _glDeleteShader!(yuvFs);
        if (_yuvProgram == 0) return (int)MediaErrorCode.SDL3EmbedInitializeFailed;

        // RGBA program uniforms
        var uTexture = _glGetUniformLocation!(_rgbaProgram, "uTexture");
        _glUseProgram!(_rgbaProgram);
        if (uTexture >= 0) _glUniform1I!(uTexture, 0);
        _glUseProgram!(0);

        // YUV program uniforms
        _glUseProgram!(_yuvProgram);
        var uTextureY = _glGetUniformLocation!(_yuvProgram, "uTextureY");
        var uTextureU = _glGetUniformLocation!(_yuvProgram, "uTextureU");
        var uTextureV = _glGetUniformLocation!(_yuvProgram, "uTextureV");
        _yuvPixelFormatLocation = _glGetUniformLocation!(_yuvProgram, "uPixelFormat");
        if (uTextureY >= 0) _glUniform1I!(uTextureY, 0);
        if (uTextureU >= 0) _glUniform1I!(uTextureU, 1);
        if (uTextureV >= 0) _glUniform1I!(uTextureV, 2);
        _glUseProgram!(0);

        // VAO + VBO
        _glGenVertexArrays!(1, out _vao);
        _glGenBuffers!(1, out _vbo);
        _glBindVertexArray!(_vao);
        _glBindBuffer!(GL_ARRAY_BUFFER, _vbo);

        float[] quad =
        [
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
             1f,  1f, 1f, 0f,
            -1f, -1f, 0f, 1f,
             1f,  1f, 1f, 0f,
            -1f,  1f, 0f, 0f,
        ];

        unsafe
        {
            fixed (float* ptr = quad)
            {
                _glBufferData!(GL_ARRAY_BUFFER, quad.Length * sizeof(float), (nint)ptr, GL_STATIC_DRAW);
            }
        }

        var stride = 4 * sizeof(float);
        _glEnableVertexAttribArray!(0);
        _glVertexAttribPointer!(0, 2, GL_FLOAT, 0, stride, nint.Zero);
        _glEnableVertexAttribArray!(1);
        _glVertexAttribPointer!(1, 2, GL_FLOAT, 0, stride, new nint(2 * sizeof(float)));
        _glBindBuffer!(GL_ARRAY_BUFFER, 0);
        _glBindVertexArray!(0);

        // Textures
        _textureRgba = CreateLinearTexture();
        _textureY = CreateLinearTexture();
        _textureU = CreateLinearTexture();
        _textureV = CreateLinearTexture();

        _glReady = true;
        return MediaResult.Success;
    }

    private int LinkProgram(int vs, int fs)
    {
        var prog = _glCreateProgram!();
        _glAttachShader!(prog, vs);
        _glAttachShader!(prog, fs);
        _glBindAttribLocation!(prog, 0, "aPosition");
        _glBindAttribLocation!(prog, 1, "aTexCoord");
        _glLinkProgram!(prog);

        _glGetProgramIv!(prog, GL_LINK_STATUS, out var linked);
        if (linked != 0) return prog;

        _glDeleteProgram!(prog);
        return 0;
    }

    private int CreateLinearTexture()
    {
        _glGenTextures!(1, out var tex);
        _glBindTexture!(GL_TEXTURE_2D, tex);
        _glTexParameteri!(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _glTexParameteri!(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _glTexParameteri!(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri!(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        _glBindTexture!(GL_TEXTURE_2D, 0);
        return tex;
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    private int UploadFrame(VideoFrame frame)
    {
        if (frame.PixelFormat is VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32)
        {
            return UploadRgba(frame);
        }

        return UploadYuv(frame);
    }

    private int UploadRgba(VideoFrame frame)
    {
        if (!TryGetPackedRgbaBytes(frame, out var packed, out var glFormat))
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        UploadTexture(ref _rgbaUploadState, _textureRgba, frame.Width, frame.Height, GL_RGBA8, glFormat, GL_UNSIGNED_BYTE, packed);
        _lastFrameIsYuv = false;
        return MediaResult.Success;
    }

    private int UploadYuv(VideoFrame frame)
    {
        if (!TryBuildYuvPlan(frame, out var plan))
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        // Y plane
        var yData = PackPlane(frame.Plane0, frame.Plane0Stride, plan.YRowBytes, plan.YHeight, ref _plane0Scratch);
        if (yData is null)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        UploadTexture(ref _yUploadState, _textureY, plan.YWidth, plan.YHeight, plan.YInternalFormat, plan.YFormat, plan.YType, yData);

        if (plan.IsSemiPlanar)
        {
            var uvData = PackPlane(frame.Plane1, frame.Plane1Stride, plan.UvRowBytes, plan.UvHeight, ref _plane1Scratch);
            if (uvData is null)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            UploadTexture(ref _uUploadState, _textureU, plan.UvWidth, plan.UvHeight, plan.UvInternalFormat, plan.UvFormat, plan.UvType, uvData);
        }
        else
        {
            var uData = PackPlane(frame.Plane1, frame.Plane1Stride, plan.URowBytes, plan.UHeight, ref _plane1Scratch);
            var vData = PackPlane(frame.Plane2, frame.Plane2Stride, plan.VRowBytes, plan.VHeight, ref _plane2Scratch);
            if (uData is null || vData is null)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            UploadTexture(ref _uUploadState, _textureU, plan.UWidth, plan.UHeight, plan.UInternalFormat, plan.UFormat, plan.UType, uData);
            UploadTexture(ref _vUploadState, _textureV, plan.VWidth, plan.VHeight, plan.VInternalFormat, plan.VFormat, plan.VType, vData);
        }

        _lastFrameIsYuv = true;
        _lastYuvModeId = plan.ModeId;
        return MediaResult.Success;
    }

    // ── Draw ──────────────────────────────────────────────────────────────────

    private int DrawLastUploaded()
    {
        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(GL_COLOR_BUFFER_BIT);

        if (_lastFrameIsYuv)
        {
            _glUseProgram!(_yuvProgram);
            if (_yuvPixelFormatLocation >= 0)
            {
                _glUniform1I!(_yuvPixelFormatLocation, _lastYuvModeId);
            }

            _glActiveTexture!(GL_TEXTURE0);
            _glBindTexture!(GL_TEXTURE_2D, _textureY);
            _glActiveTexture!(GL_TEXTURE1);
            _glBindTexture!(GL_TEXTURE_2D, _textureU);
            _glActiveTexture!(GL_TEXTURE2);
            _glBindTexture!(GL_TEXTURE_2D, IsSemiPlanarMode(_lastYuvModeId) ? _textureU : _textureV);
        }
        else
        {
            _glUseProgram!(_rgbaProgram);
            _glActiveTexture!(GL_TEXTURE0);
            _glBindTexture!(GL_TEXTURE_2D, _textureRgba);
        }

        _glBindVertexArray!(_vao);
        _glDrawArrays!(GL_TRIANGLES, 0, 6);
        _glBindVertexArray!(0);
        _glUseProgram!(0);
        return MediaResult.Success;
    }

    private static bool IsSemiPlanarMode(int modeId) => modeId is 1 or 3; // NV12=1, P010LE=3

    // ── Texture upload helper ─────────────────────────────────────────────────

    private void UploadTexture(ref TextureUploadState state, int textureId, int width, int height, int internalFormat, int format, int type, ReadOnlySpan<byte> data)
    {
        _glBindTexture!(GL_TEXTURE_2D, textureId);
        _glPixelStoreI!(GL_UNPACK_ALIGNMENT, 1);

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
                    _glTexImage2D!(GL_TEXTURE_2D, 0, internalFormat, width, height, 0, format, type, (nint)ptr);
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
                    _glTexSubImage2D!(GL_TEXTURE_2D, 0, 0, 0, width, height, format, type, (nint)ptr);
                }
            }
        }
    }

    // ── Packing helpers ───────────────────────────────────────────────────────

    private bool TryGetPackedRgbaBytes(VideoFrame frame, out ReadOnlySpan<byte> packed, out int glFormat)
    {
        glFormat = frame.PixelFormat == VideoPixelFormat.Bgra32 ? GL_BGRA : GL_RGBA;
        packed = default;

        var requiredStride = frame.Width * 4;
        var requiredLength = checked(requiredStride * frame.Height);
        if (frame.Plane0.Length < requiredLength)
        {
            return false;
        }

        if (frame.Plane0Stride == requiredStride)
        {
            packed = frame.Plane0.Span.Slice(0, requiredLength);
            return true;
        }

        if (_packedRgbaScratch is null || _packedRgbaScratch.Length < requiredLength)
        {
            _packedRgbaScratch = new byte[requiredLength];
        }

        var source = frame.Plane0.Span;
        for (var y = 0; y < frame.Height; y++)
        {
            source.Slice(y * frame.Plane0Stride, requiredStride).CopyTo(_packedRgbaScratch.AsSpan(y * requiredStride, requiredStride));
        }

        packed = _packedRgbaScratch.AsSpan(0, requiredLength);
        return true;
    }

    private static byte[]? PackPlane(ReadOnlyMemory<byte> plane, int stride, int rowBytes, int height, ref byte[]? scratch)
    {
        if (rowBytes <= 0 || height <= 0 || stride < rowBytes)
        {
            return null;
        }

        var requiredLength = checked(rowBytes * height);
        if (plane.Length < checked(stride * height))
        {
            return null;
        }

        if (scratch is null || scratch.Length < requiredLength)
        {
            scratch = new byte[requiredLength];
        }

        var source = plane.Span;
        var destination = scratch.AsSpan(0, requiredLength);
        for (var y = 0; y < height; y++)
        {
            source.Slice(y * stride, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
        }

        return scratch;
    }

    // ── YUV upload plan ───────────────────────────────────────────────────────

    private static bool TryBuildYuvPlan(VideoFrame frame, out YuvPlan plan)
    {
        var width = frame.Width;
        var height = frame.Height;
        var cw = (width + 1) / 2;
        var ch420 = (height + 1) / 2;

        plan = frame.PixelFormat switch
        {
            VideoPixelFormat.Nv12 => new YuvPlan(1, true,
                width, height, width, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                cw, ch420, cw * 2, GL_RG8, GL_RG, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0),
            VideoPixelFormat.Yuv420P => new YuvPlan(2, false,
                width, height, width, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                cw, ch420, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                cw, ch420, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE),
            VideoPixelFormat.Yuv422P => new YuvPlan(2, false,
                width, height, width, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                cw, height, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                cw, height, cw, GL_R8, GL_RED, GL_UNSIGNED_BYTE),
            VideoPixelFormat.Yuv444P => new YuvPlan(2, false,
                width, height, width, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                0, 0, 0, 0, 0, 0,
                width, height, width, GL_R8, GL_RED, GL_UNSIGNED_BYTE,
                width, height, width, GL_R8, GL_RED, GL_UNSIGNED_BYTE),
            VideoPixelFormat.P010Le => new YuvPlan(3, true,
                width, height, width * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                cw, ch420, cw * 4, GL_RG16, GL_RG, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0),
            VideoPixelFormat.Yuv420P10Le => new YuvPlan(4, false,
                width, height, width * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                cw, ch420, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                cw, ch420, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),
            VideoPixelFormat.Yuv422P10Le => new YuvPlan(4, false,
                width, height, width * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                cw, height, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                cw, height, cw * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),
            VideoPixelFormat.Yuv444P10Le => new YuvPlan(4, false,
                width, height, width * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                0, 0, 0, 0, 0, 0,
                width, height, width * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT,
                width, height, width * 2, GL_R16, GL_RED, GL_UNSIGNED_SHORT),
            _ => default,
        };

        return plan.ModeId != 0;
    }

    private readonly record struct YuvPlan(
        int ModeId,
        bool IsSemiPlanar,
        int YWidth, int YHeight, int YRowBytes, int YInternalFormat, int YFormat, int YType,
        int UvWidth, int UvHeight, int UvRowBytes, int UvInternalFormat, int UvFormat, int UvType,
        int UWidth, int UHeight, int URowBytes, int UInternalFormat, int UFormat, int UType,
        int VWidth, int VHeight, int VRowBytes, int VInternalFormat, int VFormat, int VType);

    // ── GL function loading ───────────────────────────────────────────────────

    private bool LoadGlFunctions()
    {
        T? Load<T>(string name) where T : Delegate
        {
            var pointer = SDL.GLGetProcAddress(name);
            return pointer == nint.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        _glClearColor = Load<GlClearColorProc>("glClearColor");
        _glClear = Load<GlClearProc>("glClear");
        _glCreateShader = Load<GlCreateShaderProc>("glCreateShader");
        _glShaderSource = Load<GlShaderSourceProc>("glShaderSource");
        _glCompileShader = Load<GlCompileShaderProc>("glCompileShader");
        _glGetShaderIv = Load<GlGetShaderIvProc>("glGetShaderiv");
        _glCreateProgram = Load<GlCreateProgramProc>("glCreateProgram");
        _glAttachShader = Load<GlAttachShaderProc>("glAttachShader");
        _glBindAttribLocation = Load<GlBindAttribLocationProc>("glBindAttribLocation");
        _glLinkProgram = Load<GlLinkProgramProc>("glLinkProgram");
        _glGetProgramIv = Load<GlGetProgramIvProc>("glGetProgramiv");
        _glUseProgram = Load<GlUseProgramProc>("glUseProgram");
        _glDeleteShader = Load<GlDeleteShaderProc>("glDeleteShader");
        _glDeleteProgram = Load<GlDeleteProgramProc>("glDeleteProgram");
        _glGetUniformLocation = Load<GlGetUniformLocationProc>("glGetUniformLocation");
        _glUniform1I = Load<GlUniform1iProc>("glUniform1i");
        _glGenVertexArrays = Load<GlGenVertexArraysProc>("glGenVertexArrays");
        _glBindVertexArray = Load<GlBindVertexArrayProc>("glBindVertexArray");
        _glDeleteVertexArrays = Load<GlDeleteVertexArraysProc>("glDeleteVertexArrays");
        _glGenBuffers = Load<GlGenBuffersProc>("glGenBuffers");
        _glBindBuffer = Load<GlBindBufferProc>("glBindBuffer");
        _glBufferData = Load<GlBufferDataProc>("glBufferData");
        _glDeleteBuffers = Load<GlDeleteBuffersProc>("glDeleteBuffers");
        _glEnableVertexAttribArray = Load<GlEnableVertexAttribArrayProc>("glEnableVertexAttribArray");
        _glVertexAttribPointer = Load<GlVertexAttribPointerProc>("glVertexAttribPointer");
        _glGenTextures = Load<GlGenTexturesProc>("glGenTextures");
        _glBindTexture = Load<GlBindTextureProc>("glBindTexture");
        _glTexParameteri = Load<GlTexParameteriProc>("glTexParameteri");
        _glTexImage2D = Load<GlTexImage2DProc>("glTexImage2D");
        _glTexSubImage2D = Load<GlTexSubImage2DProc>("glTexSubImage2D");
        _glPixelStoreI = Load<GlPixelStoreIProc>("glPixelStorei");
        _glActiveTexture = Load<GlActiveTextureProc>("glActiveTexture");
        _glDeleteTextures = Load<GlDeleteTexturesProc>("glDeleteTextures");
        _glDrawArrays = Load<GlDrawArraysProc>("glDrawArrays");

        return _glClearColor is not null
            && _glClear is not null
            && _glCreateShader is not null
            && _glShaderSource is not null
            && _glCompileShader is not null
            && _glGetShaderIv is not null
            && _glCreateProgram is not null
            && _glAttachShader is not null
            && _glBindAttribLocation is not null
            && _glLinkProgram is not null
            && _glGetProgramIv is not null
            && _glUseProgram is not null
            && _glDeleteShader is not null
            && _glDeleteProgram is not null
            && _glGetUniformLocation is not null
            && _glUniform1I is not null
            && _glGenVertexArrays is not null
            && _glBindVertexArray is not null
            && _glDeleteVertexArrays is not null
            && _glGenBuffers is not null
            && _glBindBuffer is not null
            && _glBufferData is not null
            && _glDeleteBuffers is not null
            && _glEnableVertexAttribArray is not null
            && _glVertexAttribPointer is not null
            && _glGenTextures is not null
            && _glBindTexture is not null
            && _glTexParameteri is not null
            && _glTexImage2D is not null
            && _glTexSubImage2D is not null
            && _glPixelStoreI is not null
            && _glActiveTexture is not null
            && _glDeleteTextures is not null
            && _glDrawArrays is not null;
    }

    private int CompileShader(int shaderType, string source)
    {
        var shader = _glCreateShader!(shaderType);
        var sourcePtr = Marshal.StringToHGlobalAnsi(source);
        var sourceArray = Marshal.AllocHGlobal(nint.Size);
        try
        {
            Marshal.WriteIntPtr(sourceArray, sourcePtr);
            _glShaderSource!(shader, 1, sourceArray, nint.Zero);
            _glCompileShader!(shader);
        }
        finally
        {
            Marshal.FreeHGlobal(sourceArray);
            Marshal.FreeHGlobal(sourcePtr);
        }

        _glGetShaderIv!(shader, GL_COMPILE_STATUS, out var compiled);
        if (compiled != 0)
        {
            return shader;
        }

        _glDeleteShader!(shader);
        return 0;
    }

    // ── GL resource cleanup ───────────────────────────────────────────────────

    private void DisposeGlResources()
    {
        if (_textureY != 0) { var t = _textureY; _glDeleteTextures?.Invoke(1, in t); _textureY = 0; }
        if (_textureU != 0) { var t = _textureU; _glDeleteTextures?.Invoke(1, in t); _textureU = 0; }
        if (_textureV != 0) { var t = _textureV; _glDeleteTextures?.Invoke(1, in t); _textureV = 0; }
        if (_textureRgba != 0) { var t = _textureRgba; _glDeleteTextures?.Invoke(1, in t); _textureRgba = 0; }
        if (_vbo != 0) { var b = _vbo; _glDeleteBuffers?.Invoke(1, in b); _vbo = 0; }
        if (_vao != 0) { var a = _vao; _glDeleteVertexArrays?.Invoke(1, in a); _vao = 0; }
        if (_rgbaProgram != 0) { _glDeleteProgram?.Invoke(_rgbaProgram); _rgbaProgram = 0; }
        if (_yuvProgram != 0) { _glDeleteProgram?.Invoke(_yuvProgram); _yuvProgram = 0; }

        _yuvPixelFormatLocation = -1;
        _rgbaUploadState = default;
        _yUploadState = default;
        _uUploadState = default;
        _vUploadState = default;
        _packedRgbaScratch = null;
        _plane0Scratch = null;
        _plane1Scratch = null;
        _plane2Scratch = null;
        _glReady = false;
    }

    // ── Shaders ───────────────────────────────────────────────────────────────

    private const string VertexShaderSource =
        "#version 330 core\n" +
        "layout(location=0) in vec2 aPosition;\n" +
        "layout(location=1) in vec2 aTexCoord;\n" +
        "out vec2 vTexCoord;\n" +
        "void main(){ gl_Position = vec4(aPosition, 0.0, 1.0); vTexCoord = aTexCoord; }";

    private const string FragmentShaderSource =
        "#version 330 core\n" +
        "in vec2 vTexCoord;\n" +
        "out vec4 FragColor;\n" +
        "uniform sampler2D uTexture;\n" +
        "void main(){ FragColor = texture(uTexture, vTexCoord); }";

    private const string YuvFragmentShaderSource =
        "#version 330 core\n" +
        "in vec2 vTexCoord;\n" +
        "uniform sampler2D uTextureY;\n" +
        "uniform sampler2D uTextureU;\n" +
        "uniform sampler2D uTextureV;\n" +
        "uniform int uPixelFormat;\n" +
        "out vec4 FragColor;\n" +
        "vec3 yuvToRgb(float y, float u, float v){ float r=y+1.5748*v; float g=y-0.1873*u-0.4681*v; float b=y+1.8556*u; return clamp(vec3(r,g,b),0.0,1.0); }\n" +
        "void main(){\n" +
        "  float scale=(uPixelFormat==4)?(65535.0/1023.0):1.0;\n" +
        "  float y=texture(uTextureY,vTexCoord).r*scale;\n" +
        "  float u; float v;\n" +
        "  if(uPixelFormat==1 || uPixelFormat==3){ vec2 uv=texture(uTextureU,vTexCoord).rg*scale; u=uv.r-0.5; v=uv.g-0.5; }\n" +
        "  else { u=texture(uTextureU,vTexCoord).r*scale-0.5; v=texture(uTextureV,vTexCoord).r*scale-0.5; }\n" +
        "  FragColor=vec4(yuvToRgb(y,u,v),1.0);\n" +
        "}";

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

