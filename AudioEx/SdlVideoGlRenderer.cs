using System.Runtime.InteropServices;
using Seko.OwnAudioNET.Video;
using SDL3;

namespace AudioEx;

internal sealed partial class SdlVideoGlRenderer : IDisposable
{
    private const int GlArrayBuffer = 0x8892;
    private const int GlStaticDraw = 0x88E4;
    private const int GlFloat = 0x1406;
    private const int GlTriangles = 0x0004;
    private const int GlTexture2D = 0x0DE1;
    private const int GlTexture0 = 0x84C0;
    private const int GlTexture1 = GlTexture0 + 1;
    private const int GlTexture2 = GlTexture0 + 2;
    private const int GlColorBufferBit = 0x00004000;
    private const int GlVertexShader = 0x8B31;
    private const int GlFragmentShader = 0x8B30;
    private const int GlCompileStatus = 0x8B81;
    private const int GlLinkStatus = 0x8B82;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlLinear = 0x2601;
    private const int GlRgba8 = 0x8058;
    private const int GlRgba = 0x1908;
    private const int GlUnsignedByte = 0x1401;
    private const int GlR8 = 0x8229;
    private const int GlR16 = 0x822A;
    private const int GlRg8 = 0x822B;
    private const int GlRg16 = 0x822C;
    private const int GlRed = 0x1903;
    private const int GlRg = 0x8227;
    private const int GlUnsignedShort = 0x1403;

    private bool _initialized;
    private bool _disposed;

    private int _program;
    private int _yuvProgram;
    private int _vao;
    private int _vbo;
    private int _textureRgba;
    private int _textureY;
    private int _textureUv;
    private int _textureU;
    private int _textureV;
    private int _textureWidth;
    private int _textureHeight;
    private bool _useYuvProgram;
    private int _yuvPixelFormat;

    private int _yuvTextureYLocation = -1;
    private int _yuvTextureULocation = -1;
    private int _yuvTextureVLocation = -1;
    private int _yuvPixelFormatLocation = -1;

    private byte[]? _plane0Scratch;
    private byte[]? _plane1Scratch;
    private byte[]? _plane2Scratch;

    private TextureUploadState _rgbaState;
    private TextureUploadState _yState;
    private TextureUploadState _uvState;
    private TextureUploadState _uState;
    private TextureUploadState _vState;

    private delegate void ViewportProc(int x, int y, int width, int height);
    private delegate void ClearColorProc(float r, float g, float b, float a);
    private delegate void ClearProc(int mask);
    private delegate int CreateShaderProc(int type);
    private delegate void ShaderSourceProc(int shader, int count, nint strings, nint lengths);
    private delegate void CompileShaderProc(int shader);
    private delegate void GetShaderIvProc(int shader, int pname, out int param);
    private delegate void GetShaderInfoLogProc(int shader, int maxLength, out int length, nint infoLog);
    private delegate void DeleteShaderProc(int shader);
    private delegate int CreateProgramProc();
    private delegate void AttachShaderProc(int program, int shader);
    private delegate void LinkProgramProc(int program);
    private delegate void GetProgramIvProc(int program, int pname, out int param);
    private delegate void GetProgramInfoLogProc(int program, int maxLength, out int length, nint infoLog);
    private delegate void UseProgramProc(int program);
    private delegate void DeleteProgramProc(int program);
    private delegate void BindAttribLocationProc(int program, int index, [MarshalAs(UnmanagedType.LPStr)] string name);
    private delegate int GetUniformLocationProc(int program, [MarshalAs(UnmanagedType.LPStr)] string name);
    private delegate void Uniform1IProc(int location, int value);
    private delegate int GenVertexArraysProc();
    private delegate void BindVertexArrayProc(int array);
    private delegate void DeleteVertexArraysProc(int array);
    private delegate int GenBuffersProc();
    private delegate void BindBufferProc(int target, int buffer);
    private delegate void BufferDataProc(int target, nint size, nint data, int usage);
    private delegate void DeleteBuffersProc(int buffer);
    private delegate void EnableVertexAttribArrayProc(int index);
    private delegate void VertexAttribPointerProc(int index, int size, int type, byte normalized, int stride, nint pointer);
    private delegate int GenTexturesProc();
    private delegate void ActiveTextureProc(int texture);
    private delegate void BindTextureProc(int target, int texture);
    private delegate void TexParameteriProc(int target, int pname, int param);
    private delegate void TexImage2DProc(int target, int level, int internalFormat, int width, int height, int border, int format, int type, nint pixels);
    private delegate void TexSubImage2DProc(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, nint pixels);
    private delegate void DeleteTexturesProc(int texture);
    private delegate void DrawArraysProc(int mode, int first, int count);

    private ViewportProc? _glViewport;
    private ClearColorProc? _glClearColor;
    private ClearProc? _glClear;
    private CreateShaderProc? _glCreateShader;
    private ShaderSourceProc? _glShaderSource;
    private CompileShaderProc? _glCompileShader;
    private GetShaderIvProc? _glGetShaderIv;
    private GetShaderInfoLogProc? _glGetShaderInfoLog;
    private DeleteShaderProc? _glDeleteShader;
    private CreateProgramProc? _glCreateProgram;
    private AttachShaderProc? _glAttachShader;
    private LinkProgramProc? _glLinkProgram;
    private GetProgramIvProc? _glGetProgramIv;
    private GetProgramInfoLogProc? _glGetProgramInfoLog;
    private UseProgramProc? _glUseProgram;
    private DeleteProgramProc? _glDeleteProgram;
    private BindAttribLocationProc? _glBindAttribLocation;
    private GetUniformLocationProc? _glGetUniformLocation;
    private Uniform1IProc? _glUniform1I;
    private GenVertexArraysProc? _glGenVertexArrays;
    private BindVertexArrayProc? _glBindVertexArray;
    private DeleteVertexArraysProc? _glDeleteVertexArrays;
    private GenBuffersProc? _glGenBuffers;
    private BindBufferProc? _glBindBuffer;
    private BufferDataProc? _glBufferData;
    private DeleteBuffersProc? _glDeleteBuffers;
    private EnableVertexAttribArrayProc? _glEnableVertexAttribArray;
    private VertexAttribPointerProc? _glVertexAttribPointer;
    private GenTexturesProc? _glGenTextures;
    private ActiveTextureProc? _glActiveTexture;
    private BindTextureProc? _glBindTexture;
    private TexParameteriProc? _glTexParameteri;
    private TexImage2DProc? _glTexImage2D;
    private TexSubImage2DProc? _glTexSubImage2D;
    private DeleteTexturesProc? _glDeleteTextures;
    private DrawArraysProc? _glDrawArrays;

    private static readonly float[] QuadVertices =
    [
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
         1f,  1f, 1f, 0f,
        -1f, -1f, 0f, 1f,
         1f,  1f, 1f, 0f,
        -1f,  1f, 0f, 0f
    ];

    private struct TextureUploadState
    {
        public bool IsInitialized;
        public int Width;
        public int Height;
        public int InternalFormat;
        public int Format;
        public int Type;
    }

    public SdlVideoGlRenderer()
    {
    }

    public bool Initialize(out string error)
    {
        error = string.Empty;
        if (_initialized)
            return true;

        if (!LoadGlFunctions(out error))
            return false;

        var vertexShader = BuildVertexShader();
        var fragmentShader = BuildFragmentShader();
        var yuvShader = BuildYuvFragmentShader();

        _program = BuildProgram(vertexShader, fragmentShader, out error);
        if (_program == 0)
            return false;

        _yuvProgram = BuildProgram(vertexShader, yuvShader, out error);
        if (_yuvProgram == 0)
            return false;

        _yuvTextureYLocation = _glGetUniformLocation!(_yuvProgram, "uTextureY");
        _yuvTextureULocation = _glGetUniformLocation!(_yuvProgram, "uTextureU");
        _yuvTextureVLocation = _glGetUniformLocation!(_yuvProgram, "uTextureV");
        _yuvPixelFormatLocation = _glGetUniformLocation!(_yuvProgram, "uPixelFormat");

        _glUseProgram!(_yuvProgram);
        if (_yuvTextureYLocation >= 0) _glUniform1I!(_yuvTextureYLocation, 0);
        if (_yuvTextureULocation >= 0) _glUniform1I!(_yuvTextureULocation, 1);
        if (_yuvTextureVLocation >= 0) _glUniform1I!(_yuvTextureVLocation, 2);
        _glUseProgram!(0);

        _vao = _glGenVertexArrays!();
        _vbo = _glGenBuffers!();
        _glBindVertexArray!(_vao);
        _glBindBuffer!(GlArrayBuffer, _vbo);

        var handle = GCHandle.Alloc(QuadVertices, GCHandleType.Pinned);
        try
        {
            _glBufferData!(GlArrayBuffer, QuadVertices.Length * sizeof(float), handle.AddrOfPinnedObject(), GlStaticDraw);
        }
        finally
        {
            handle.Free();
        }

        var stride = 4 * sizeof(float);
        _glEnableVertexAttribArray!(0);
        _glVertexAttribPointer!(0, 2, GlFloat, 0, stride, nint.Zero);
        _glEnableVertexAttribArray!(1);
        _glVertexAttribPointer!(1, 2, GlFloat, 0, stride, 2 * sizeof(float));

        _glBindBuffer!(GlArrayBuffer, 0);
        _glBindVertexArray!(0);

        _textureRgba = _glGenTextures!();
        _textureY = _glGenTextures!();
        _textureUv = _glGenTextures!();
        _textureU = _glGenTextures!();
        _textureV = _glGenTextures!();

        ConfigureTexture(_textureRgba);
        ConfigureTexture(_textureY);
        ConfigureTexture(_textureUv);
        ConfigureTexture(_textureU);
        ConfigureTexture(_textureV);

        _initialized = true;
        return true;
    }

    public bool RenderFrame(VideoFrame frame, int surfaceWidth, int surfaceHeight)
    {
        if (!_initialized)
            return false;

        _glViewport!(0, 0, surfaceWidth, surfaceHeight);
        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(GlColorBufferBit);

        var uploaded = frame.PixelFormat switch
        {
            VideoPixelFormat.Rgba32 => UploadRgbaFrame(frame),
            VideoPixelFormat.Nv12 => UploadNv12Frame(frame),
            VideoPixelFormat.Yuv420p => UploadYuv420pFrame(frame),
            VideoPixelFormat.Yuv422p => UploadYuv422pFrame(frame),
            VideoPixelFormat.Yuv444p => UploadYuv444pFrame(frame),
            VideoPixelFormat.Yuv422p10le => UploadYuv422p10leFrame(frame),
            VideoPixelFormat.Yuv420p10le => UploadYuv420p10leFrame(frame),
            VideoPixelFormat.Yuv444p10le => UploadYuv444p10leFrame(frame),
            VideoPixelFormat.P010le => UploadP010leFrame(frame),
            _ => false
        };

        if (!uploaded)
        {
            if (_textureWidth > 0 && _textureHeight > 0)
                DrawCurrentFrame(surfaceWidth, surfaceHeight);

            return false;
        }

        DrawCurrentFrame(surfaceWidth, surfaceHeight);
        return true;
    }

    public bool RenderLastFrame(int surfaceWidth, int surfaceHeight)
    {
        if (!_initialized)
            return false;

        _glViewport!(0, 0, surfaceWidth, surfaceHeight);
        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(GlColorBufferBit);

        if (_textureWidth <= 0 || _textureHeight <= 0)
            return false;

        DrawCurrentFrame(surfaceWidth, surfaceHeight);
        return true;
    }

    private void DrawCurrentFrame(int surfaceWidth, int surfaceHeight)
    {
        var viewport = GetAspectFitRect(surfaceWidth, surfaceHeight, _textureWidth, _textureHeight);
        _glViewport!(viewport.X, viewport.Y, viewport.Width, viewport.Height);

        if (_useYuvProgram)
        {
            _glUseProgram!(_yuvProgram);
            if (_yuvPixelFormatLocation >= 0)
                _glUniform1I!(_yuvPixelFormatLocation, _yuvPixelFormat);
        }
        else
        {
            _glUseProgram!(_program);
        }

        _glBindVertexArray!(_vao);
        _glDrawArrays!(GlTriangles, 0, 6);
        _glBindVertexArray!(0);
        _glUseProgram!(0);
        _glBindTexture!(GlTexture2D, 0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_textureRgba != 0) _glDeleteTextures?.Invoke(_textureRgba);
        if (_textureY != 0) _glDeleteTextures?.Invoke(_textureY);
        if (_textureUv != 0) _glDeleteTextures?.Invoke(_textureUv);
        if (_textureU != 0) _glDeleteTextures?.Invoke(_textureU);
        if (_textureV != 0) _glDeleteTextures?.Invoke(_textureV);
        if (_vbo != 0) _glDeleteBuffers?.Invoke(_vbo);
        if (_vao != 0) _glDeleteVertexArrays?.Invoke(_vao);
        if (_program != 0) _glDeleteProgram?.Invoke(_program);
        if (_yuvProgram != 0) _glDeleteProgram?.Invoke(_yuvProgram);
    }

    private bool LoadGlFunctions(out string error)
    {
        error = string.Empty;

        bool Load<T>(string name, out T? d) where T : Delegate
        {
            var p = SDL.GLGetProcAddress(name);
            if (p == nint.Zero)
            {
                d = null;
                return false;
            }

            d = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }

        if (!Load("glViewport", out _glViewport) ||
            !Load("glClearColor", out _glClearColor) ||
            !Load("glClear", out _glClear) ||
            !Load("glCreateShader", out _glCreateShader) ||
            !Load("glShaderSource", out _glShaderSource) ||
            !Load("glCompileShader", out _glCompileShader) ||
            !Load("glGetShaderiv", out _glGetShaderIv) ||
            !Load("glGetShaderInfoLog", out _glGetShaderInfoLog) ||
            !Load("glDeleteShader", out _glDeleteShader) ||
            !Load("glCreateProgram", out _glCreateProgram) ||
            !Load("glAttachShader", out _glAttachShader) ||
            !Load("glLinkProgram", out _glLinkProgram) ||
            !Load("glGetProgramiv", out _glGetProgramIv) ||
            !Load("glGetProgramInfoLog", out _glGetProgramInfoLog) ||
            !Load("glUseProgram", out _glUseProgram) ||
            !Load("glDeleteProgram", out _glDeleteProgram) ||
            !Load("glBindAttribLocation", out _glBindAttribLocation) ||
            !Load("glGetUniformLocation", out _glGetUniformLocation) ||
            !Load("glUniform1i", out _glUniform1I) ||
            !Load("glGenVertexArrays", out _glGenVertexArrays) ||
            !Load("glBindVertexArray", out _glBindVertexArray) ||
            !Load("glDeleteVertexArrays", out _glDeleteVertexArrays) ||
            !Load("glGenBuffers", out _glGenBuffers) ||
            !Load("glBindBuffer", out _glBindBuffer) ||
            !Load("glBufferData", out _glBufferData) ||
            !Load("glDeleteBuffers", out _glDeleteBuffers) ||
            !Load("glEnableVertexAttribArray", out _glEnableVertexAttribArray) ||
            !Load("glVertexAttribPointer", out _glVertexAttribPointer) ||
            !Load("glGenTextures", out _glGenTextures) ||
            !Load("glActiveTexture", out _glActiveTexture) ||
            !Load("glBindTexture", out _glBindTexture) ||
            !Load("glTexParameteri", out _glTexParameteri) ||
            !Load("glTexImage2D", out _glTexImage2D) ||
            !Load("glDeleteTextures", out _glDeleteTextures) ||
            !Load("glDrawArrays", out _glDrawArrays))
        {
            error = "Failed to load required OpenGL functions.";
            return false;
        }

        // Optional fast path
        Load("glTexSubImage2D", out _glTexSubImage2D);

        return true;
    }

    private void ConfigureTexture(int texture)
    {
        _glBindTexture!(GlTexture2D, texture);
        _glTexParameteri!(GlTexture2D, GlTextureMinFilter, GlLinear);
        _glTexParameteri!(GlTexture2D, GlTextureMagFilter, GlLinear);
        _glBindTexture!(GlTexture2D, 0);
    }

    private int BuildProgram(string vertexSource, string fragmentSource, out string error)
    {
        error = string.Empty;

        var vertex = CompileShader(GlVertexShader, vertexSource, out error);
        if (vertex == 0)
            return 0;

        var fragment = CompileShader(GlFragmentShader, fragmentSource, out error);
        if (fragment == 0)
        {
            _glDeleteShader!(vertex);
            return 0;
        }

        var program = _glCreateProgram!();
        _glAttachShader!(program, vertex);
        _glAttachShader!(program, fragment);
        _glBindAttribLocation!(program, 0, "aPosition");
        _glBindAttribLocation!(program, 1, "aTexCoord");
        _glLinkProgram!(program);

        _glDeleteShader!(vertex);
        _glDeleteShader!(fragment);

        _glGetProgramIv!(program, GlLinkStatus, out var linked);
        if (linked == 0)
        {
            error = GetProgramLog(program);
            _glDeleteProgram!(program);
            return 0;
        }

        return program;
    }

    private int CompileShader(int shaderType, string source, out string error)
    {
        error = string.Empty;

        var shader = _glCreateShader!(shaderType);
        var srcPtr = Marshal.StringToHGlobalAnsi(source);
        try
        {
            var srcArray = Marshal.AllocHGlobal(nint.Size);
            try
            {
                Marshal.WriteIntPtr(srcArray, srcPtr);
                _glShaderSource!(shader, 1, srcArray, nint.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(srcArray);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(srcPtr);
        }

        _glCompileShader!(shader);
        _glGetShaderIv!(shader, GlCompileStatus, out var compiled);
        if (compiled == 0)
        {
            error = GetShaderLog(shader);
            _glDeleteShader!(shader);
            return 0;
        }

        return shader;
    }

    private string GetShaderLog(int shader)
    {
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            _glGetShaderInfoLog!(shader, 4096, out var length, buffer);
            return length > 0 ? Marshal.PtrToStringAnsi(buffer, length) ?? "Shader compile failed." : "Shader compile failed.";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string GetProgramLog(int program)
    {
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            _glGetProgramInfoLog!(program, 4096, out var length, buffer);
            return length > 0 ? Marshal.PtrToStringAnsi(buffer, length) ?? "Program link failed." : "Program link failed.";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private unsafe void UploadTexture2D(ref TextureUploadState state, int width, int height, int internalFormat, int format, int type, byte[] data)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            var pixels = (nint)ptr;
            var reallocate = !state.IsInitialized ||
                             state.Width != width ||
                             state.Height != height ||
                             state.InternalFormat != internalFormat ||
                             state.Format != format ||
                             state.Type != type;

            if (reallocate)
            {
                _glTexImage2D!(GlTexture2D, 0, internalFormat, width, height, 0, format, type, nint.Zero);
                state.IsInitialized = true;
                state.Width = width;
                state.Height = height;
                state.InternalFormat = internalFormat;
                state.Format = format;
                state.Type = type;
            }

            if (_glTexSubImage2D != null)
            {
                _glTexSubImage2D(GlTexture2D, 0, 0, 0, width, height, format, type, pixels);
            }
            else
            {
                _glTexImage2D!(GlTexture2D, 0, internalFormat, width, height, 0, format, type, pixels);
            }
        }
    }

    private bool UploadRgbaFrame(VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        var rgba = GetTightlyPackedPlane(frame, 0, frame.Width * 4, frame.Height, ref _plane0Scratch);
        if (rgba == null)
            return false;

        _glActiveTexture!(GlTexture0);
        _glBindTexture!(GlTexture2D, _textureRgba);
        UploadTexture2D(ref _rgbaState, frame.Width, frame.Height, GlRgba8, GlRgba, GlUnsignedByte, rgba);

        SetCurrentFrameState(frame.Width, frame.Height, useYuvProgram: false, pixelFormatCode: 0);
        return true;
    }

    private bool UploadNv12Frame(VideoFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;

        var y = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uv = GetTightlyPackedPlane(frame, 1, cw * 2, ch, ref _plane1Scratch);
        if (y == null || uv == null)
            return false;

        _glActiveTexture!(GlTexture0);
        _glBindTexture!(GlTexture2D, _textureY);
        UploadTexture2D(ref _yState, width, height, GlR8, GlRed, GlUnsignedByte, y);

        _glActiveTexture!(GlTexture1);
        _glBindTexture!(GlTexture2D, _textureUv);
        UploadTexture2D(ref _uvState, cw, ch, GlRg8, GlRg, GlUnsignedByte, uv);

        _glActiveTexture!(GlTexture2);
        _glBindTexture!(GlTexture2D, _textureUv);
        _glActiveTexture!(GlTexture0);

        SetCurrentFrameState(width, height, useYuvProgram: true, pixelFormatCode: 1);
        return true;
    }

    private bool UploadYuv420pFrame(VideoFrame frame) => UploadPlanar8Bit(frame, (frame.Width + 1) / 2, (frame.Height + 1) / 2, 2);
    private bool UploadYuv422pFrame(VideoFrame frame) => UploadPlanar8Bit(frame, (frame.Width + 1) / 2, frame.Height, 2);
    private bool UploadYuv444pFrame(VideoFrame frame) => UploadPlanar8Bit(frame, frame.Width, frame.Height, 2);

    private bool UploadYuv420p10leFrame(VideoFrame frame) => UploadPlanar16Bit(frame, (frame.Width + 1) / 2, (frame.Height + 1) / 2, 4);
    private bool UploadYuv422p10leFrame(VideoFrame frame) => UploadPlanar16Bit(frame, (frame.Width + 1) / 2, frame.Height, 4);
    private bool UploadYuv444p10leFrame(VideoFrame frame) => UploadPlanar16Bit(frame, frame.Width, frame.Height, 4);

    private bool UploadP010leFrame(VideoFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        var cw = (width + 1) / 2;
        var ch = (height + 1) / 2;

        var y = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var uv = GetTightlyPackedPlane(frame, 1, cw * 4, ch, ref _plane1Scratch);
        if (y == null || uv == null)
            return false;

        _glActiveTexture!(GlTexture0);
        _glBindTexture!(GlTexture2D, _textureY);
        UploadTexture2D(ref _yState, width, height, GlR16, GlRed, GlUnsignedShort, y);

        _glActiveTexture!(GlTexture1);
        _glBindTexture!(GlTexture2D, _textureUv);
        UploadTexture2D(ref _uvState, cw, ch, GlRg16, GlRg, GlUnsignedShort, uv);

        _glActiveTexture!(GlTexture2);
        _glBindTexture!(GlTexture2D, _textureUv);
        _glActiveTexture!(GlTexture0);

        SetCurrentFrameState(width, height, useYuvProgram: true, pixelFormatCode: 3);
        return true;
    }

    private bool UploadPlanar8Bit(VideoFrame frame, int chromaWidth, int chromaHeight, int pixelFormatCode)
    {
        var width = frame.Width;
        var height = frame.Height;

        var y = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var u = GetTightlyPackedPlane(frame, 1, chromaWidth, chromaHeight, ref _plane1Scratch);
        var v = GetTightlyPackedPlane(frame, 2, chromaWidth, chromaHeight, ref _plane2Scratch);
        if (y == null || u == null || v == null)
            return false;

        _glActiveTexture!(GlTexture0);
        _glBindTexture!(GlTexture2D, _textureY);
        UploadTexture2D(ref _yState, width, height, GlR8, GlRed, GlUnsignedByte, y);

        _glActiveTexture!(GlTexture1);
        _glBindTexture!(GlTexture2D, _textureU);
        UploadTexture2D(ref _uState, chromaWidth, chromaHeight, GlR8, GlRed, GlUnsignedByte, u);

        _glActiveTexture!(GlTexture2);
        _glBindTexture!(GlTexture2D, _textureV);
        UploadTexture2D(ref _vState, chromaWidth, chromaHeight, GlR8, GlRed, GlUnsignedByte, v);
        _glActiveTexture!(GlTexture0);

        SetCurrentFrameState(width, height, useYuvProgram: true, pixelFormatCode: pixelFormatCode);
        return true;
    }

    private bool UploadPlanar16Bit(VideoFrame frame, int chromaWidth, int chromaHeight, int pixelFormatCode)
    {
        var width = frame.Width;
        var height = frame.Height;

        var y = GetTightlyPackedPlane(frame, 0, width * 2, height, ref _plane0Scratch);
        var u = GetTightlyPackedPlane(frame, 1, chromaWidth * 2, chromaHeight, ref _plane1Scratch);
        var v = GetTightlyPackedPlane(frame, 2, chromaWidth * 2, chromaHeight, ref _plane2Scratch);
        if (y == null || u == null || v == null)
            return false;

        _glActiveTexture!(GlTexture0);
        _glBindTexture!(GlTexture2D, _textureY);
        UploadTexture2D(ref _yState, width, height, GlR16, GlRed, GlUnsignedShort, y);

        _glActiveTexture!(GlTexture1);
        _glBindTexture!(GlTexture2D, _textureU);
        UploadTexture2D(ref _uState, chromaWidth, chromaHeight, GlR16, GlRed, GlUnsignedShort, u);

        _glActiveTexture!(GlTexture2);
        _glBindTexture!(GlTexture2D, _textureV);
        UploadTexture2D(ref _vState, chromaWidth, chromaHeight, GlR16, GlRed, GlUnsignedShort, v);
        _glActiveTexture!(GlTexture0);

        SetCurrentFrameState(width, height, useYuvProgram: true, pixelFormatCode: pixelFormatCode);
        return true;
    }

    private void SetCurrentFrameState(int width, int height, bool useYuvProgram, int pixelFormatCode)
    {
        _textureWidth = width;
        _textureHeight = height;
        _useYuvProgram = useYuvProgram;
        _yuvPixelFormat = pixelFormatCode;
    }

    // Helper methods are split into partial files:
    // - SdlVideoGlRenderer.Packing.cs
    // - SdlVideoGlRenderer.Shaders.cs
}

