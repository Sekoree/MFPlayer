using System.Runtime.InteropServices;
using System.Text;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.SDL3;

/// <summary>
/// Minimal OpenGL 3.3 core-profile renderer: uploads a BGRA32 texture and draws a fullscreen quad.
/// GL functions are loaded via <c>SDL_GL_GetProcAddress</c> — no external GL binding library required.
/// </summary>
internal sealed unsafe class GLRenderer : IDisposable
{
    // ── GL constants ──────────────────────────────────────────────────────

    private const uint GL_TEXTURE_2D          = 0x0DE1;
    private const uint GL_TEXTURE_MIN_FILTER  = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER  = 0x2800;
    private const uint GL_TEXTURE_WRAP_S      = 0x2802;
    private const uint GL_TEXTURE_WRAP_T      = 0x2803;
    private const uint GL_NEAREST             = 0x2600;
    private const uint GL_LINEAR              = 0x2601;
    private const uint GL_CLAMP_TO_EDGE       = 0x812F;
    private const uint GL_RGBA8               = 0x8058;
    private const uint GL_R8                  = 0x8229;
    private const uint GL_RG8                 = 0x822B;
    private const uint GL_R16UI               = 0x8234;
    private const uint GL_RED                 = 0x1903;
    private const uint GL_RED_INTEGER         = 0x8D94;
    private const uint GL_RG                  = 0x8227;
    private const uint GL_RGBA                = 0x1908;
    private const uint GL_BGRA                = 0x80E1;
    private const uint GL_UNSIGNED_BYTE       = 0x1401;
    private const uint GL_UNSIGNED_SHORT      = 0x1403;
    private const uint GL_FLOAT               = 0x1406;
    private const uint GL_TRIANGLES           = 0x0004;
    private const uint GL_ARRAY_BUFFER        = 0x8892;
    private const uint GL_STATIC_DRAW         = 0x88E4;
    private const uint GL_FRAGMENT_SHADER     = 0x8B30;
    private const uint GL_VERTEX_SHADER       = 0x8B31;
    private const uint GL_COMPILE_STATUS      = 0x8B81;
    private const uint GL_LINK_STATUS         = 0x8B82;
    private const uint GL_COLOR_BUFFER_BIT    = 0x4000;
    private const uint GL_FALSE               = 0;
    private const uint GL_TRUE                = 1;
    private const uint GL_TEXTURE0            = 0x84C0;
    private const uint GL_TEXTURE1            = 0x84C1;
    private const uint GL_TEXTURE2            = 0x84C2;

    // ── GL function pointers ──────────────────────────────────────────────

    private delegate void   GlViewport(int x, int y, int w, int h);
    private delegate void   GlClearColor(float r, float g, float b, float a);
    private delegate void   GlClear(uint mask);
    private delegate void   GlGenTextures(int n, uint* textures);
    private delegate void   GlDeleteTextures(int n, uint* textures);
    private delegate void   GlBindTexture(uint target, uint texture);
    private delegate void   GlTexImage2D(uint target, int level, uint internalFormat, int w, int h, int border, uint format, uint type, void* data);
    private delegate void   GlTexSubImage2D(uint target, int level, int x, int y, int w, int h, uint format, uint type, void* data);
    private delegate void   GlTexParameteri(uint target, uint pname, uint param);
    private delegate uint   GlCreateShader(uint type);
    private delegate void   GlShaderSource(uint shader, int count, byte** strings, int* lengths);
    private delegate void   GlCompileShader(uint shader);
    private delegate void   GlGetShaderiv(uint shader, uint pname, int* result);
    private delegate void   GlGetShaderInfoLog(uint shader, int maxLen, int* length, byte* infoLog);
    private delegate uint   GlCreateProgram();
    private delegate void   GlAttachShader(uint program, uint shader);
    private delegate void   GlLinkProgram(uint program);
    private delegate void   GlGetProgramiv(uint program, uint pname, int* result);
    private delegate void   GlGetProgramInfoLog(uint program, int maxLen, int* length, byte* infoLog);
    private delegate void   GlUseProgram(uint program);
    private delegate void   GlDeleteShader(uint shader);
    private delegate void   GlDeleteProgram(uint program);
    private delegate int    GlGetUniformLocation(uint program, byte* name);
    private delegate void   GlUniform1i(int location, int value);
    private delegate void   GlGenVertexArrays(int n, uint* arrays);
    private delegate void   GlDeleteVertexArrays(int n, uint* arrays);
    private delegate void   GlBindVertexArray(uint array);
    private delegate void   GlGenBuffers(int n, uint* buffers);
    private delegate void   GlDeleteBuffers(int n, uint* buffers);
    private delegate void   GlBindBuffer(uint target, uint buffer);
    private delegate void   GlBufferData(uint target, nint size, void* data, uint usage);
    private delegate void   GlEnableVertexAttribArray(uint index);
    private delegate void   GlVertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, void* pointer);
    private delegate void   GlDrawArrays(uint mode, int first, int count);
    private delegate void   GlActiveTexture(uint texture);

    // Instances
    private GlViewport                _glViewport = null!;
    private GlClearColor              _glClearColor = null!;
    private GlClear                   _glClear = null!;
    private GlGenTextures             _glGenTextures = null!;
    private GlDeleteTextures          _glDeleteTextures = null!;
    private GlBindTexture             _glBindTexture = null!;
    private GlTexImage2D              _glTexImage2D = null!;
    private GlTexSubImage2D           _glTexSubImage2D = null!;
    private GlTexParameteri           _glTexParameteri = null!;
    private GlCreateShader            _glCreateShader = null!;
    private GlShaderSource            _glShaderSource = null!;
    private GlCompileShader           _glCompileShader = null!;
    private GlGetShaderiv             _glGetShaderiv = null!;
    private GlGetShaderInfoLog        _glGetShaderInfoLog = null!;
    private GlCreateProgram           _glCreateProgram = null!;
    private GlAttachShader            _glAttachShader = null!;
    private GlLinkProgram             _glLinkProgram = null!;
    private GlGetProgramiv            _glGetProgramiv = null!;
    private GlGetProgramInfoLog       _glGetProgramInfoLog = null!;
    private GlUseProgram              _glUseProgram = null!;
    private GlDeleteShader            _glDeleteShader = null!;
    private GlDeleteProgram           _glDeleteProgram = null!;
    private GlGetUniformLocation      _glGetUniformLocation = null!;
    private GlUniform1i               _glUniform1i = null!;
    private GlGenVertexArrays         _glGenVertexArrays = null!;
    private GlDeleteVertexArrays      _glDeleteVertexArrays = null!;
    private GlBindVertexArray         _glBindVertexArray = null!;
    private GlGenBuffers              _glGenBuffers = null!;
    private GlDeleteBuffers           _glDeleteBuffers = null!;
    private GlBindBuffer              _glBindBuffer = null!;
    private GlBufferData              _glBufferData = null!;
    private GlEnableVertexAttribArray _glEnableVertexAttribArray = null!;
    private GlVertexAttribPointer     _glVertexAttribPointer = null!;
    private GlDrawArrays              _glDrawArrays = null!;
    private GlActiveTexture           _glActiveTexture = null!;

    // ── GL state ──────────────────────────────────────────────────────────

    private uint _texture;
    private uint _textureY;
    private uint _textureUv;
    private uint _textureU;
    private uint _textureV;
    private uint _textureY422P10;
    private uint _textureU422P10;
    private uint _textureV422P10;
    private uint _textureUyvy;
    private uint _program;
    private uint _programNv12;
    private uint _programI420;
    private uint _programI422P10;
    private uint _programUyvy422;
    private uint _vao;
    private uint _vbo;
    private int  _texWidth;
    private int  _texHeight;
    private int  _texWidthNv12;
    private int  _texHeightNv12;
    private int  _texWidthI420;
    private int  _texHeightI420;
    private int  _texWidthI422P10;
    private int  _texHeightI422P10;
    private int  _texWidthUyvy;
    private int  _texHeightUyvy;
    private int  _uNv12LimitedRangeLoc = -1;
    private int  _uNv12ColorMatrixLoc = -1;
    private int  _uI420LimitedRangeLoc = -1;
    private int  _uI420ColorMatrixLoc = -1;
    private int  _uI422P10LimitedRangeLoc = -1;
    private int  _uI422P10ColorMatrixLoc = -1;
    private int  _uUyvyVideoWidthLoc = -1;
    private int  _uUyvyLimitedRangeLoc = -1;
    private int  _uUyvyColorMatrixLoc = -1;
    private YuvColorRange _i422P10ColorRange = YuvColorRange.Auto;
    private YuvColorMatrix _i422P10ColorMatrix = YuvColorMatrix.Auto;
    private bool _disposed;
    private int _windowWidth;
    private int _windowHeight;
    private int _videoWidth;
    private int _videoHeight;

    public YuvColorRange YuvColorRange
    {
        get => _i422P10ColorRange;
        set => _i422P10ColorRange = NormalizeColorRange(value);
    }

    public YuvColorMatrix YuvColorMatrix
    {
        get => _i422P10ColorMatrix;
        set => _i422P10ColorMatrix = NormalizeColorMatrix(value);
    }

    public bool I422P10LimitedRange
    {
        get => _i422P10ColorRange == YuvColorRange.Limited;
        set => _i422P10ColorRange = value ? YuvColorRange.Limited : YuvColorRange.Full;
    }

    public YuvColorRange I422P10ColorRange
    {
        get => YuvColorRange;
        set => YuvColorRange = value;
    }

    public YuvColorMatrix I422P10ColorMatrix
    {
        get => YuvColorMatrix;
        set => YuvColorMatrix = value;
    }

    public bool I422P10UseBt709Matrix
    {
        get => _i422P10ColorMatrix == YuvColorMatrix.Bt709;
        set => _i422P10ColorMatrix = value ? YuvColorMatrix.Bt709 : YuvColorMatrix.Bt601;
    }

    // ── Shaders ───────────────────────────────────────────────────────────

    private const string VertexShaderSource = GlShaderSources.VertexPassthrough;
    private const string FragmentShaderSource = GlShaderSources.FragmentPassthrough;
    private const string FragmentShaderSourceNv12 = GlShaderSources.FragmentNv12;
    private const string FragmentShaderSourceI420 = GlShaderSources.FragmentI420;
    private const string FragmentShaderSourceI422P10 = GlShaderSources.FragmentI422P10;
    private const string FragmentShaderSourceUyvy422 = GlShaderSources.FragmentUyvy422;

    // ── Initialisation ────────────────────────────────────────────────────

    public void Initialise(int viewportWidth, int viewportHeight)
    {
        LoadGLFunctions();

        // Shader program
        uint vs = CompileShader(GL_VERTEX_SHADER, VertexShaderSource);
        uint fs = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSource);
        _program = _glCreateProgram();
        _glAttachShader(_program, vs);
        _glAttachShader(_program, fs);
        _glLinkProgram(_program);
        CheckProgram(_program);

        uint fsNv12 = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSourceNv12);
        _programNv12 = _glCreateProgram();
        _glAttachShader(_programNv12, vs);
        _glAttachShader(_programNv12, fsNv12);
        _glLinkProgram(_programNv12);
        CheckProgram(_programNv12);

        uint fsI420 = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSourceI420);
        _programI420 = _glCreateProgram();
        _glAttachShader(_programI420, vs);
        _glAttachShader(_programI420, fsI420);
        _glLinkProgram(_programI420);
        CheckProgram(_programI420);

        uint fsI422P10 = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSourceI422P10);
        _programI422P10 = _glCreateProgram();
        _glAttachShader(_programI422P10, vs);
        _glAttachShader(_programI422P10, fsI422P10);
        _glLinkProgram(_programI422P10);
        CheckProgram(_programI422P10);

        uint fsUyvy422 = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSourceUyvy422);
        _programUyvy422 = _glCreateProgram();
        _glAttachShader(_programUyvy422, vs);
        _glAttachShader(_programUyvy422, fsUyvy422);
        _glLinkProgram(_programUyvy422);
        CheckProgram(_programUyvy422);

        _glDeleteShader(vs);
        _glDeleteShader(fs);
        _glDeleteShader(fsNv12);
        _glDeleteShader(fsI420);
        _glDeleteShader(fsI422P10);
        _glDeleteShader(fsUyvy422);

        // Set texture uniform to unit 0
        _glUseProgram(_program);
        fixed (byte* name = "uTexture\0"u8)
        {
            int loc = _glGetUniformLocation(_program, name);
            _glUniform1i(loc, 0);
        }

        _glUseProgram(_programNv12);
        fixed (byte* nameY = "uTexY\0"u8)
        {
            int locY = _glGetUniformLocation(_programNv12, nameY);
            _glUniform1i(locY, 0);
        }
        fixed (byte* nameUv = "uTexUV\0"u8)
        {
            int locUv = _glGetUniformLocation(_programNv12, nameUv);
            _glUniform1i(locUv, 1);
        }
        fixed (byte* nameLimited = "uLimitedRange\0"u8)
        {
            _uNv12LimitedRangeLoc = _glGetUniformLocation(_programNv12, nameLimited);
            if (_uNv12LimitedRangeLoc >= 0)
                _glUniform1i(_uNv12LimitedRangeLoc, 0);
        }
        fixed (byte* nameMatrix = "uColorMatrix\0"u8)
        {
            _uNv12ColorMatrixLoc = _glGetUniformLocation(_programNv12, nameMatrix);
            if (_uNv12ColorMatrixLoc >= 0)
                _glUniform1i(_uNv12ColorMatrixLoc, 0);
        }

        _glUseProgram(_programI420);
        fixed (byte* nameY = "uTexY\0"u8)
        {
            int locY = _glGetUniformLocation(_programI420, nameY);
            _glUniform1i(locY, 0);
        }
        fixed (byte* nameU = "uTexU\0"u8)
        {
            int locU = _glGetUniformLocation(_programI420, nameU);
            _glUniform1i(locU, 1);
        }
        fixed (byte* nameV = "uTexV\0"u8)
        {
            int locV = _glGetUniformLocation(_programI420, nameV);
            _glUniform1i(locV, 2);
        }
        fixed (byte* nameLimited = "uLimitedRange\0"u8)
        {
            _uI420LimitedRangeLoc = _glGetUniformLocation(_programI420, nameLimited);
            if (_uI420LimitedRangeLoc >= 0)
                _glUniform1i(_uI420LimitedRangeLoc, 0);
        }
        fixed (byte* nameMatrix = "uColorMatrix\0"u8)
        {
            _uI420ColorMatrixLoc = _glGetUniformLocation(_programI420, nameMatrix);
            if (_uI420ColorMatrixLoc >= 0)
                _glUniform1i(_uI420ColorMatrixLoc, 0);
        }

        _glUseProgram(_programI422P10);
        fixed (byte* nameY = "uTexY\0"u8)
        {
            int locY = _glGetUniformLocation(_programI422P10, nameY);
            _glUniform1i(locY, 0);
        }
        fixed (byte* nameU = "uTexU\0"u8)
        {
            int locU = _glGetUniformLocation(_programI422P10, nameU);
            _glUniform1i(locU, 1);
        }
        fixed (byte* nameV = "uTexV\0"u8)
        {
            int locV = _glGetUniformLocation(_programI422P10, nameV);
            _glUniform1i(locV, 2);
        }
        fixed (byte* nameLimited = "uLimitedRange\0"u8)
        {
            _uI422P10LimitedRangeLoc = _glGetUniformLocation(_programI422P10, nameLimited);
            if (_uI422P10LimitedRangeLoc >= 0)
                _glUniform1i(_uI422P10LimitedRangeLoc, 0);
        }
        fixed (byte* nameMatrix = "uColorMatrix\0"u8)
        {
            _uI422P10ColorMatrixLoc = _glGetUniformLocation(_programI422P10, nameMatrix);
            if (_uI422P10ColorMatrixLoc >= 0)
                _glUniform1i(_uI422P10ColorMatrixLoc, 0);
        }

        _glUseProgram(_programUyvy422);
        fixed (byte* nameUyvyTex = "uTexUYVY\0"u8)
        {
            int locTex = _glGetUniformLocation(_programUyvy422, nameUyvyTex);
            _glUniform1i(locTex, 0);
        }
        fixed (byte* nameUyvyWidth = "uVideoWidth\0"u8)
        {
            _uUyvyVideoWidthLoc = _glGetUniformLocation(_programUyvy422, nameUyvyWidth);
            if (_uUyvyVideoWidthLoc >= 0)
                _glUniform1i(_uUyvyVideoWidthLoc, 0);
        }
        fixed (byte* nameLimited = "uLimitedRange\0"u8)
        {
            _uUyvyLimitedRangeLoc = _glGetUniformLocation(_programUyvy422, nameLimited);
            if (_uUyvyLimitedRangeLoc >= 0)
                _glUniform1i(_uUyvyLimitedRangeLoc, 0);
        }
        fixed (byte* nameMatrix = "uColorMatrix\0"u8)
        {
            _uUyvyColorMatrixLoc = _glGetUniformLocation(_programUyvy422, nameMatrix);
            if (_uUyvyColorMatrixLoc >= 0)
                _glUniform1i(_uUyvyColorMatrixLoc, 0);
        }

        // Fullscreen quad (2 triangles): position (x,y) + UV (u,v)
        // Note: UV y is flipped (1→0) so the image isn't upside-down.
        var quadVerts = GlShaderSources.FullscreenQuadVerts;

        fixed (uint* pVao = &_vao) _glGenVertexArrays(1, pVao);
        fixed (uint* pVbo = &_vbo) _glGenBuffers(1, pVbo);

        _glBindVertexArray(_vao);
        _glBindBuffer(GL_ARRAY_BUFFER, _vbo);

        fixed (float* pData = quadVerts)
            _glBufferData(GL_ARRAY_BUFFER, (nint)(quadVerts.Length * sizeof(float)), pData, GL_STATIC_DRAW);

        // layout(location=0) in vec2 aPos
        _glEnableVertexAttribArray(0);
        _glVertexAttribPointer(0, 2, GL_FLOAT, (byte)GL_FALSE, 4 * sizeof(float), (void*)0);

        // layout(location=1) in vec2 aUV
        _glEnableVertexAttribArray(1);
        _glVertexAttribPointer(1, 2, GL_FLOAT, (byte)GL_FALSE, 4 * sizeof(float), (void*)(2 * sizeof(float)));

        _glBindVertexArray(0);

        // Texture
        fixed (uint* pTex = &_texture) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _texture);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pTex = &_textureY) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _textureY);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pTex = &_textureUv) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _textureUv);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pTex = &_textureU) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _textureU);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pTex = &_textureV) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _textureV);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pTex = &_textureY422P10) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _textureY422P10);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pTex = &_textureU422P10) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _textureU422P10);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pTex = &_textureV422P10) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _textureV422P10);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pTex = &_textureUyvy) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _textureUyvy);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        _glViewport(0, 0, viewportWidth, viewportHeight);
        _windowWidth  = viewportWidth;
        _windowHeight = viewportHeight;
        _glClearColor(0f, 0f, 0f, 1f);
    }

    // ── Per-frame rendering ───────────────────────────────────────────────

    public void UploadAndDraw(VideoFrame frame)
    {
        if (frame.PixelFormat == PixelFormat.Nv12)
        {
            UploadAndDrawNv12(frame);
            return;
        }

        if (frame.PixelFormat == PixelFormat.Yuv420p)
        {
            UploadAndDrawI420(frame);
            return;
        }

        if (frame.PixelFormat == PixelFormat.Yuv422p10)
        {
            UploadAndDrawI422P10(frame);
            return;
        }

        if (frame.PixelFormat == PixelFormat.Uyvy422)
        {
            UploadAndDrawUyvy422(frame);
            return;
        }

        if (frame.PixelFormat != PixelFormat.Bgra32 && frame.PixelFormat != PixelFormat.Rgba32)
        {
            DrawBlack();
            return;
        }

        int w = frame.Width;
        int h = frame.Height;
        uint uploadFormat = frame.PixelFormat == PixelFormat.Rgba32 ? GL_RGBA : GL_BGRA;

        _glBindTexture(GL_TEXTURE_2D, _texture);

        // Pin the managed byte array for GPU upload.
        using var pin = frame.Data.Pin();

        if (w == _texWidth && h == _texHeight)
        {
            // Same resolution → fast sub-image update (no GPU realloc).
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, uploadFormat, GL_UNSIGNED_BYTE, pin.Pointer);
        }
        else
        {
            // Resolution changed → re-allocate the texture.
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, uploadFormat, GL_UNSIGNED_BYTE, pin.Pointer);
            _texWidth  = w;
            _texHeight = h;
        }

        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_program);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    private void UploadAndDrawNv12(VideoFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        if (w <= 0 || h <= 0)
            return;

        int ySize = w * h;
        int uvSize = w * ((h + 1) / 2);
        int required = ySize + uvSize;
        if (frame.Data.Length < required)
        {
            DrawBlack();
            return;
        }

        using var pin = frame.Data.Pin();
        nint yPtr = (nint)pin.Pointer;
        nint uvPtr = yPtr + ySize;

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureY);
        if (w == _texWidthNv12 && h == _texHeightNv12)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr);

        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureUv);
        if (w == _texWidthNv12 && h == _texHeightNv12)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, Math.Max(1, w / 2), Math.Max(1, h / 2), GL_RG, GL_UNSIGNED_BYTE, (void*)uvPtr);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RG8, Math.Max(1, w / 2), Math.Max(1, h / 2), 0, GL_RG, GL_UNSIGNED_BYTE, (void*)uvPtr);

        _texWidthNv12 = w;
        _texHeightNv12 = h;

        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programNv12);
        if (_uNv12LimitedRangeLoc >= 0)
            _glUniform1i(_uNv12LimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uNv12ColorMatrixLoc >= 0)
            _glUniform1i(_uNv12ColorMatrixLoc, ShouldUseBt709MatrixForYuv(w, h) ? 1 : 0);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    private void UploadAndDrawI420(VideoFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        if (w <= 0 || h <= 0)
            return;

        int cw = Math.Max(1, (w + 1) / 2);
        int ch = Math.Max(1, (h + 1) / 2);
        int ySize = w * h;
        int uSize = cw * ch;
        int required = ySize + uSize + uSize;
        if (frame.Data.Length < required)
        {
            DrawBlack();
            return;
        }

        using var pin = frame.Data.Pin();
        nint yPtr = (nint)pin.Pointer;
        nint uPtr = yPtr + ySize;
        nint vPtr = uPtr + uSize;

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureY);
        if (w == _texWidthI420 && h == _texHeightI420)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr);

        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureU);
        if (w == _texWidthI420 && h == _texHeightI420)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, cw, ch, GL_RED, GL_UNSIGNED_BYTE, (void*)uPtr);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, cw, ch, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)uPtr);

        _glActiveTexture(GL_TEXTURE2);
        _glBindTexture(GL_TEXTURE_2D, _textureV);
        if (w == _texWidthI420 && h == _texHeightI420)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, cw, ch, GL_RED, GL_UNSIGNED_BYTE, (void*)vPtr);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, cw, ch, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)vPtr);

        _texWidthI420 = w;
        _texHeightI420 = h;

        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programI420);
        if (_uI420LimitedRangeLoc >= 0)
            _glUniform1i(_uI420LimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uI420ColorMatrixLoc >= 0)
            _glUniform1i(_uI420ColorMatrixLoc, ShouldUseBt709MatrixForYuv(w, h) ? 1 : 0);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    private void UploadAndDrawI422P10(VideoFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        if (w <= 0 || h <= 0)
            return;

        int cw = Math.Max(1, (w + 1) / 2);
        int ySamples = w * h;
        int uvSamples = cw * h;
        int required = (ySamples + uvSamples + uvSamples) * sizeof(ushort);
        if (frame.Data.Length < required)
        {
            DrawBlack();
            return;
        }

        using var pin = frame.Data.Pin();
        nint yPtr = (nint)pin.Pointer;
        nint uPtr = yPtr + (ySamples * sizeof(ushort));
        nint vPtr = uPtr + (uvSamples * sizeof(ushort));

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureY422P10);
        if (w == _texWidthI422P10 && h == _texHeightI422P10)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)yPtr);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R16UI, w, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)yPtr);

        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureU422P10);
        if (w == _texWidthI422P10 && h == _texHeightI422P10)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, cw, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)uPtr);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R16UI, cw, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)uPtr);

        _glActiveTexture(GL_TEXTURE2);
        _glBindTexture(GL_TEXTURE_2D, _textureV422P10);
        if (w == _texWidthI422P10 && h == _texHeightI422P10)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, cw, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)vPtr);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R16UI, cw, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)vPtr);

        _texWidthI422P10 = w;
        _texHeightI422P10 = h;

        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programI422P10);
        if (_uI422P10LimitedRangeLoc >= 0)
            _glUniform1i(_uI422P10LimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uI422P10ColorMatrixLoc >= 0)
            _glUniform1i(_uI422P10ColorMatrixLoc, ShouldUseBt709MatrixForYuv(w, h) ? 1 : 0);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    private void UploadAndDrawUyvy422(VideoFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        if (w <= 0 || h <= 0)
            return;

        // UYVY: 2 bytes per pixel → w*h*2 bytes total.
        // Uploaded as an RGBA8 texture at (w/2) × h: each texel packs one pixel pair
        // [U, Y0, V, Y1] into R, G, B, A channels.
        int halfW = Math.Max(1, w / 2);
        int required = w * h * 2;
        if (frame.Data.Length < required)
        {
            DrawBlack();
            return;
        }

        using var pin = frame.Data.Pin();

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureUyvy);
        if (w == _texWidthUyvy && h == _texHeightUyvy)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, halfW, h, GL_RGBA, GL_UNSIGNED_BYTE, pin.Pointer);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, halfW, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, pin.Pointer);

        _texWidthUyvy = w;
        _texHeightUyvy = h;

        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programUyvy422);
        if (_uUyvyVideoWidthLoc >= 0)
            _glUniform1i(_uUyvyVideoWidthLoc, w);
        if (_uUyvyLimitedRangeLoc >= 0)
            _glUniform1i(_uUyvyLimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uUyvyColorMatrixLoc >= 0)
            _glUniform1i(_uUyvyColorMatrixLoc, ShouldUseBt709MatrixForYuv(w, h) ? 1 : 0);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    private bool ShouldUseBt709MatrixForYuv(int width, int height)
    {
        return YuvAutoPolicy.ResolveMatrix(_i422P10ColorMatrix, width, height) == YuvColorMatrix.Bt709;
    }

    private bool ShouldUseLimitedRangeForYuv()
    {
        return YuvAutoPolicy.ResolveRange(_i422P10ColorRange) == YuvColorRange.Limited;
    }

    private static YuvColorRange NormalizeColorRange(YuvColorRange value)
    {
        return value is YuvColorRange.Auto or YuvColorRange.Full or YuvColorRange.Limited
            ? value
            : YuvColorRange.Auto;
    }

    private static YuvColorMatrix NormalizeColorMatrix(YuvColorMatrix value)
    {
        return value is YuvColorMatrix.Auto or YuvColorMatrix.Bt601 or YuvColorMatrix.Bt709
            ? value
            : YuvColorMatrix.Auto;
    }

    public void DrawBlack()
    {
        _glClear(GL_COLOR_BUFFER_BIT);
    }

    /// <summary>
    /// Called when the SDL window is resized.  Recomputes the letterbox/pillarbox
    /// viewport so the video is always displayed with its correct aspect ratio.
    /// </summary>
    public void SetViewport(int windowWidth, int windowHeight)
    {
        _windowWidth  = windowWidth;
        _windowHeight = windowHeight;
        UpdateViewportLetterbox();
    }

    /// <summary>
    /// Informs the renderer of the video's native pixel dimensions so that it can
    /// compute the correct letterbox/pillarbox viewport on every window resize.
    /// Call this once after the output format is determined (before the first frame).
    /// </summary>
    public void SetVideoSize(int videoWidth, int videoHeight)
    {
        if (_videoWidth == videoWidth && _videoHeight == videoHeight)
            return;
        _videoWidth  = videoWidth;
        _videoHeight = videoHeight;
        UpdateViewportLetterbox();
    }

    /// <summary>
    /// Computes a letterboxed/pillarboxed GL viewport that preserves the video aspect ratio
    /// within the current window, with black bars filling any remaining space.
    /// </summary>
    private void UpdateViewportLetterbox()
    {
        int winW = _windowWidth  > 0 ? _windowWidth  : 1;
        int winH = _windowHeight > 0 ? _windowHeight : 1;

        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            _glViewport(0, 0, winW, winH);
            return;
        }

        double vidAspect = (double)_videoWidth  / _videoHeight;
        double winAspect = (double)winW / winH;

        int vpX, vpY, vpW, vpH;
        if (winAspect > vidAspect)
        {
            // Window is wider than the video: pillarbox (black bars on left/right)
            vpH = winH;
            vpW = (int)Math.Round(vpH * vidAspect);
            vpX = (winW - vpW) / 2;
            vpY = 0;
        }
        else
        {
            // Window is taller than the video (or exactly equal): letterbox (black bars top/bottom)
            vpW = winW;
            vpH = (int)Math.Round(vpW / vidAspect);
            vpX = 0;
            vpY = (winH - vpH) / 2;
        }

        _glViewport(vpX, vpY, Math.Max(1, vpW), Math.Max(1, vpH));
    }

    // ── GL loading helpers ────────────────────────────────────────────────

    private T LoadGL<T>(string name) where T : Delegate
    {
        var ptr = (nint)global::SDL3.SDL.GLGetProcAddress(name);
        if (ptr == nint.Zero)
            throw new InvalidOperationException($"Failed to load GL function: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private void LoadGLFunctions()
    {
        _glViewport                = LoadGL<GlViewport>("glViewport");
        _glClearColor              = LoadGL<GlClearColor>("glClearColor");
        _glClear                   = LoadGL<GlClear>("glClear");
        _glGenTextures             = LoadGL<GlGenTextures>("glGenTextures");
        _glDeleteTextures          = LoadGL<GlDeleteTextures>("glDeleteTextures");
        _glBindTexture             = LoadGL<GlBindTexture>("glBindTexture");
        _glTexImage2D              = LoadGL<GlTexImage2D>("glTexImage2D");
        _glTexSubImage2D           = LoadGL<GlTexSubImage2D>("glTexSubImage2D");
        _glTexParameteri           = LoadGL<GlTexParameteri>("glTexParameteri");
        _glCreateShader            = LoadGL<GlCreateShader>("glCreateShader");
        _glShaderSource            = LoadGL<GlShaderSource>("glShaderSource");
        _glCompileShader           = LoadGL<GlCompileShader>("glCompileShader");
        _glGetShaderiv             = LoadGL<GlGetShaderiv>("glGetShaderiv");
        _glGetShaderInfoLog        = LoadGL<GlGetShaderInfoLog>("glGetShaderInfoLog");
        _glCreateProgram           = LoadGL<GlCreateProgram>("glCreateProgram");
        _glAttachShader            = LoadGL<GlAttachShader>("glAttachShader");
        _glLinkProgram             = LoadGL<GlLinkProgram>("glLinkProgram");
        _glGetProgramiv            = LoadGL<GlGetProgramiv>("glGetProgramiv");
        _glGetProgramInfoLog       = LoadGL<GlGetProgramInfoLog>("glGetProgramInfoLog");
        _glUseProgram              = LoadGL<GlUseProgram>("glUseProgram");
        _glDeleteShader            = LoadGL<GlDeleteShader>("glDeleteShader");
        _glDeleteProgram           = LoadGL<GlDeleteProgram>("glDeleteProgram");
        _glGetUniformLocation      = LoadGL<GlGetUniformLocation>("glGetUniformLocation");
        _glUniform1i               = LoadGL<GlUniform1i>("glUniform1i");
        _glGenVertexArrays         = LoadGL<GlGenVertexArrays>("glGenVertexArrays");
        _glDeleteVertexArrays      = LoadGL<GlDeleteVertexArrays>("glDeleteVertexArrays");
        _glBindVertexArray         = LoadGL<GlBindVertexArray>("glBindVertexArray");
        _glGenBuffers              = LoadGL<GlGenBuffers>("glGenBuffers");
        _glDeleteBuffers           = LoadGL<GlDeleteBuffers>("glDeleteBuffers");
        _glBindBuffer              = LoadGL<GlBindBuffer>("glBindBuffer");
        _glBufferData              = LoadGL<GlBufferData>("glBufferData");
        _glEnableVertexAttribArray = LoadGL<GlEnableVertexAttribArray>("glEnableVertexAttribArray");
        _glVertexAttribPointer     = LoadGL<GlVertexAttribPointer>("glVertexAttribPointer");
        _glDrawArrays              = LoadGL<GlDrawArrays>("glDrawArrays");
        _glActiveTexture           = LoadGL<GlActiveTexture>("glActiveTexture");
    }

    private uint CompileShader(uint type, string source)
    {
        uint shader = _glCreateShader(type);
        byte[] srcBytes = Encoding.UTF8.GetBytes(source + '\0');
        fixed (byte* pSrc = srcBytes)
        {
            byte* pSrcPtr = pSrc;
            _glShaderSource(shader, 1, &pSrcPtr, null);
        }
        _glCompileShader(shader);

        int status;
        _glGetShaderiv(shader, GL_COMPILE_STATUS, &status);
        if (status == 0)
        {
            byte* log = stackalloc byte[1024];
            int len;
            _glGetShaderInfoLog(shader, 1024, &len, log);
            string msg = Encoding.UTF8.GetString(log, len);
            throw new InvalidOperationException($"Shader compilation failed: {msg}");
        }
        return shader;
    }

    private void CheckProgram(uint program)
    {
        int status;
        _glGetProgramiv(program, GL_LINK_STATUS, &status);
        if (status == 0)
        {
            byte* log = stackalloc byte[1024];
            int len;
            _glGetProgramInfoLog(program, 1024, &len, log);
            string msg = Encoding.UTF8.GetString(log, len);
            throw new InvalidOperationException($"Program link failed: {msg}");
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        fixed (uint* p = &_texture)      _glDeleteTextures(1, p);
        fixed (uint* p = &_textureY)     _glDeleteTextures(1, p);
        fixed (uint* p = &_textureUv)    _glDeleteTextures(1, p);
        fixed (uint* p = &_textureU)     _glDeleteTextures(1, p);
        fixed (uint* p = &_textureV)     _glDeleteTextures(1, p);
        fixed (uint* p = &_textureY422P10) _glDeleteTextures(1, p);
        fixed (uint* p = &_textureU422P10) _glDeleteTextures(1, p);
        fixed (uint* p = &_textureV422P10) _glDeleteTextures(1, p);
        fixed (uint* p = &_textureUyvy)   _glDeleteTextures(1, p);
        fixed (uint* p = &_vbo)          _glDeleteBuffers(1, p);
        fixed (uint* p = &_vao)          _glDeleteVertexArrays(1, p);
        _glDeleteProgram(_program);
        _glDeleteProgram(_programNv12);
        _glDeleteProgram(_programI420);
        _glDeleteProgram(_programI422P10);
        _glDeleteProgram(_programUyvy422);
    }
}

