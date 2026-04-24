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
    private const uint GL_RG16UI              = 0x823A;
    private const uint GL_RG_INTEGER          = 0x8228;
    private const uint GL_UNPACK_ALIGNMENT    = 0x0CF5;
    private const uint GL_FRAMEBUFFER         = 0x8D40;
    private const uint GL_COLOR_ATTACHMENT0   = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    private const uint GL_LINEAR_MIPMAP_LINEAR = 0x2703;
    private const uint GL_BLEND               = 0x0BE2;
    private const uint GL_SRC_ALPHA           = 0x0302;
    private const uint GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const uint GL_DYNAMIC_DRAW        = 0x88E8;
    private const uint GL_PIXEL_UNPACK_BUFFER = 0x88EC;
    private const uint GL_MAP_WRITE_BIT       = 0x0002;
    private const uint GL_MAP_PERSISTENT_BIT  = 0x0040;
    private const uint GL_MAP_COHERENT_BIT    = 0x0080;
    private const uint GL_SYNC_GPU_COMMANDS_COMPLETE = 0x9117;
    private const uint GL_ALREADY_SIGNALED    = 0x911A;
    private const uint GL_TIMEOUT_EXPIRED     = 0x911B;
    private const uint GL_CONDITION_SATISFIED = 0x911C;
    private const uint GL_WAIT_FAILED         = 0x911D;

    private const int PersistentPboSlotCount = 6;
    private const int PersistentPboSlotSizeBytes = 32 * 1024 * 1024;

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
    private delegate void   GlPixelStorei(uint pname, int param);
    private delegate void   GlGenFramebuffers(int n, uint* framebuffers);
    private delegate void   GlDeleteFramebuffers(int n, uint* framebuffers);
    private delegate void   GlBindFramebuffer(uint target, uint framebuffer);
    private delegate void   GlFramebufferTexture2D(uint target, uint attachment, uint textarget, uint texture, int level);
    private delegate uint   GlCheckFramebufferStatus(uint target);
    private delegate void   GlGenerateMipmap(uint target);
    private delegate void   GlEnable(uint cap);
    private delegate void   GlDisable(uint cap);
    private delegate void   GlBlendFunc(uint sfactor, uint dfactor);
    private delegate void   GlUniform2f(int location, float v0, float v1);
    private delegate void   GlUniform4f(int location, float v0, float v1, float v2, float v3);
    private delegate void   GlBufferStorage(uint target, nint size, void* data, uint flags);
    private delegate void*  GlMapBufferRange(uint target, nint offset, nint length, uint access);
    private delegate byte   GlUnmapBuffer(uint target);
    private delegate nint   GlFenceSync(uint condition, uint flags);
    private delegate uint   GlClientWaitSync(nint sync, uint flags, ulong timeout);
    private delegate void   GlDeleteSync(nint sync);

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
    private GlPixelStorei             _glPixelStorei = null!;
    private GlGenFramebuffers         _glGenFramebuffers = null!;
    private GlDeleteFramebuffers      _glDeleteFramebuffers = null!;
    private GlBindFramebuffer         _glBindFramebuffer = null!;
    private GlFramebufferTexture2D    _glFramebufferTexture2D = null!;
    private GlCheckFramebufferStatus  _glCheckFramebufferStatus = null!;
    private GlGenerateMipmap          _glGenerateMipmap = null!;
    private GlEnable                  _glEnable = null!;
    private GlDisable                 _glDisable = null!;
    private GlBlendFunc               _glBlendFunc = null!;
    private GlUniform2f               _glUniform2f = null!;
    private GlUniform4f               _glUniform4f = null!;
    private GlBufferStorage?          _glBufferStorage;
    private GlMapBufferRange?         _glMapBufferRange;
    private GlUnmapBuffer?            _glUnmapBuffer;
    private GlFenceSync?              _glFenceSync;
    private GlClientWaitSync?         _glClientWaitSync;
    private GlDeleteSync?             _glDeleteSync;

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
    private uint _programP010;
    private uint _programYuv444p;
    private uint _programGray8;
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
    // P010 textures and uniforms
    private uint _textureP010Y;
    private uint _textureP010UV;
    private int  _texWidthP010;
    private int  _texHeightP010;
    private int  _uP010LimitedRangeLoc = -1;
    private int  _uP010ColorMatrixLoc = -1;
    // Yuv444p textures and uniforms
    private uint _textureY444p;
    private uint _textureU444p;
    private uint _textureV444p;
    private int  _texWidthYuv444p;
    private int  _texHeightYuv444p;
    private int  _uYuv444pLimitedRangeLoc = -1;
    private int  _uYuv444pColorMatrixLoc = -1;
    // Gray8 texture
    private uint _textureGray8;
    private int  _texWidthGray8;
    private int  _texHeightGray8;
    private YuvColorRange _yuvColorRange = YuvColorRange.Auto;
    private YuvColorMatrix _yuvColorMatrix = YuvColorMatrix.Auto;
    private bool _disposed;
    private int _windowWidth;
    private int _windowHeight;
    private int _videoWidth;
    private int _videoHeight;
    // Stored letterbox viewport — restored after FBO pass 1 in two-pass rendering.
    private int _vpX, _vpY, _vpW, _vpH;
    // FBO for two-pass UYVY rendering: decode at native resolution, then scale with mipmaps.
    private uint _fboUyvy;
    private uint _fboUyvyTexture;
    private int  _fboUyvyWidth;
    private int  _fboUyvyHeight;
    private uint _programFbo;
    // Bicubic (Catmull-Rom) blit program — compiled from FragmentBicubicBlit.
    // Used in place of _programFbo when ScalingFilter == Bicubic.
    private uint _programBicubic;
    // General-purpose FBO for non-UYVY formats when ScalingFilter != Bilinear.
    private uint _fboGeneral;
    private uint _fboGeneralTexture;
    private int  _fboGeneralWidth;
    private int  _fboGeneralHeight;
    // Scaling filter — bicubic by default for broadcast-quality monitoring.
    // Set via ScalingFilter property (render thread reads, any thread writes).
    private volatile int _scalingFilter = (int)ScalingFilter.Bicubic;
    // §8.6 — persistent mapped PBO ring for non-blocking texture uploads.
    private bool _persistentPboEnabled;
    private uint[]? _persistentPboIds;
    private nint[]? _persistentPboMappings;
    private nint[]? _persistentPboFences;
    private int _persistentPboNextSlot;

    // ── Last-drawn frame tracking (for DrawLastFrame / texture reuse) ─────
    // Set at the tail of each UploadAndDrawXxx. Used by DrawLastFrame() to
    // re-run only the draw portion of the pipeline (without re-uploading
    // texture data) when the video output decides the frame did not change.
    private PixelFormat _lastDrawnFormat;
    private int         _lastDrawnW;
    private int         _lastDrawnH;
    private bool        _hasLastDrawn;

    // ── HUD state ──────────────────────────────────────────────────────────
    private uint _hudProgram;
    private uint _hudVao;
    private uint _hudVbo;
    private uint _hudFontTexture;
    private int  _hudUScreenSize = -1;
    private int  _hudUColor = -1;
    private int  _hudUBgColor = -1;
    private bool _hudInitialised;
    // §3.40f / §8.7 — persistent CPU-side scratch for HUD glyph vertices.
    // Avoids a per-frame `new float[]` allocation in DrawHud.
    private float[] _hudScratchVerts = Array.Empty<float>();

    public ScalingFilter ScalingFilter
    {
        get => (ScalingFilter)_scalingFilter;
        set => _scalingFilter = (int)value;
    }

    public YuvColorRange YuvColorRange
    {
        get => _yuvColorRange;
        set => _yuvColorRange = NormalizeColorRange(value);
    }

    public YuvColorMatrix YuvColorMatrix
    {
        get => _yuvColorMatrix;
        set => _yuvColorMatrix = NormalizeColorMatrix(value);
    }

    public bool I422P10LimitedRange
    {
        get => _yuvColorRange == YuvColorRange.Limited;
        set => _yuvColorRange = value ? YuvColorRange.Limited : YuvColorRange.Full;
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
        get => _yuvColorMatrix == YuvColorMatrix.Bt709;
        set => _yuvColorMatrix = value ? YuvColorMatrix.Bt709 : YuvColorMatrix.Bt601;
    }

    // ── Shaders ───────────────────────────────────────────────────────────

    private const string VertexShaderSource = GlShaderSources.VertexPassthrough;
    private const string FragmentShaderSource = GlShaderSources.FragmentPassthrough;
    private const string FragmentShaderSourceNv12 = GlShaderSources.FragmentNv12;
    private const string FragmentShaderSourceI420 = GlShaderSources.FragmentI420;
    private const string FragmentShaderSourceI422P10 = GlShaderSources.FragmentI422P10;
    private const string FragmentShaderSourceUyvy422 = GlShaderSources.FragmentUyvy422;
    private const string FragmentShaderSourceP010    = GlShaderSources.FragmentP010;
    private const string FragmentShaderSourceYuv444p = GlShaderSources.FragmentYuv444p;
    private const string FragmentShaderSourceGray8   = GlShaderSources.FragmentGray8;

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

        uint fsP010 = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSourceP010);
        _programP010 = _glCreateProgram();
        _glAttachShader(_programP010, vs);
        _glAttachShader(_programP010, fsP010);
        _glLinkProgram(_programP010);
        CheckProgram(_programP010);

        uint fsYuv444p = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSourceYuv444p);
        _programYuv444p = _glCreateProgram();
        _glAttachShader(_programYuv444p, vs);
        _glAttachShader(_programYuv444p, fsYuv444p);
        _glLinkProgram(_programYuv444p);
        CheckProgram(_programYuv444p);

        uint fsGray8 = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSourceGray8);
        _programGray8 = _glCreateProgram();
        _glAttachShader(_programGray8, vs);
        _glAttachShader(_programGray8, fsGray8);
        _glLinkProgram(_programGray8);
        CheckProgram(_programGray8);

        // FBO passthrough program for two-pass rendering (hardware bilinear + Y-flip)
        uint fsFbo = CompileShader(GL_FRAGMENT_SHADER, GlShaderSources.FragmentPassthroughFbo);
        _programFbo = _glCreateProgram();
        _glAttachShader(_programFbo, vs);
        _glAttachShader(_programFbo, fsFbo);
        _glLinkProgram(_programFbo);
        CheckProgram(_programFbo);

        // Bicubic (Catmull-Rom) blit program for high-quality scaling.
        // Used for all formats when ScalingFilter == Bicubic.
        uint fsBicubic = CompileShader(GL_FRAGMENT_SHADER, GlShaderSources.FragmentBicubicBlit);
        _programBicubic = _glCreateProgram();
        _glAttachShader(_programBicubic, vs);
        _glAttachShader(_programBicubic, fsBicubic);
        _glLinkProgram(_programBicubic);
        CheckProgram(_programBicubic);

        _glDeleteShader(vs);
        _glDeleteShader(fs);
        _glDeleteShader(fsNv12);
        _glDeleteShader(fsI420);
        _glDeleteShader(fsI422P10);
        _glDeleteShader(fsUyvy422);
        _glDeleteShader(fsP010);
        _glDeleteShader(fsYuv444p);
        _glDeleteShader(fsGray8);
        _glDeleteShader(fsFbo);
        _glDeleteShader(fsBicubic);

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

        // ── P010 uniforms ──────────────────────────────────────────────────
        _glUseProgram(_programP010);
        fixed (byte* nameY = "uTexY\0"u8)
        {
            int locY = _glGetUniformLocation(_programP010, nameY);
            _glUniform1i(locY, 0);
        }
        fixed (byte* nameUv = "uTexUV\0"u8)
        {
            int locUv = _glGetUniformLocation(_programP010, nameUv);
            _glUniform1i(locUv, 1);
        }
        fixed (byte* nameLimited = "uLimitedRange\0"u8)
        {
            _uP010LimitedRangeLoc = _glGetUniformLocation(_programP010, nameLimited);
            if (_uP010LimitedRangeLoc >= 0) _glUniform1i(_uP010LimitedRangeLoc, 0);
        }
        fixed (byte* nameMatrix = "uColorMatrix\0"u8)
        {
            _uP010ColorMatrixLoc = _glGetUniformLocation(_programP010, nameMatrix);
            if (_uP010ColorMatrixLoc >= 0) _glUniform1i(_uP010ColorMatrixLoc, 0);
        }

        // ── Yuv444p uniforms ───────────────────────────────────────────────
        _glUseProgram(_programYuv444p);
        fixed (byte* nameY = "uTexY\0"u8)
        { int l = _glGetUniformLocation(_programYuv444p, nameY); _glUniform1i(l, 0); }
        fixed (byte* nameU = "uTexU\0"u8)
        { int l = _glGetUniformLocation(_programYuv444p, nameU); _glUniform1i(l, 1); }
        fixed (byte* nameV = "uTexV\0"u8)
        { int l = _glGetUniformLocation(_programYuv444p, nameV); _glUniform1i(l, 2); }
        fixed (byte* nameLimited = "uLimitedRange\0"u8)
        {
            _uYuv444pLimitedRangeLoc = _glGetUniformLocation(_programYuv444p, nameLimited);
            if (_uYuv444pLimitedRangeLoc >= 0) _glUniform1i(_uYuv444pLimitedRangeLoc, 0);
        }
        fixed (byte* nameMatrix = "uColorMatrix\0"u8)
        {
            _uYuv444pColorMatrixLoc = _glGetUniformLocation(_programYuv444p, nameMatrix);
            if (_uYuv444pColorMatrixLoc >= 0) _glUniform1i(_uYuv444pColorMatrixLoc, 0);
        }

        // ── Gray8 uniforms ─────────────────────────────────────────────────
        _glUseProgram(_programGray8);
        fixed (byte* nameY = "uTexY\0"u8)
        { int l = _glGetUniformLocation(_programGray8, nameY); _glUniform1i(l, 0); }

        // ── FBO passthrough uniforms ──────────────────────────────────────
        _glUseProgram(_programFbo);
        fixed (byte* name = "uTexture\0"u8)
        { int l = _glGetUniformLocation(_programFbo, name); _glUniform1i(l, 0); }

        // ── Bicubic blit uniforms ─────────────────────────────────────────
        _glUseProgram(_programBicubic);
        fixed (byte* name = "uTexture\0"u8)
        { int l = _glGetUniformLocation(_programBicubic, name); _glUniform1i(l, 0); }

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

        // Byte-aligned unpacking — required for R8/RG8 texture planes where the row
        // byte count may not be a multiple of 4 (the GL default alignment).
        _glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        InitialisePersistentPboUpload();

        // Textures — initialised via helper to eliminate repetition (§6.3).
        InitTexture(ref _texture,        GL_LINEAR);
        InitTexture(ref _textureY,       GL_LINEAR);
        InitTexture(ref _textureUv,      GL_LINEAR);
        InitTexture(ref _textureU,       GL_LINEAR);
        InitTexture(ref _textureV,       GL_LINEAR);
        InitTexture(ref _textureY422P10, GL_NEAREST);
        InitTexture(ref _textureU422P10, GL_NEAREST);
        InitTexture(ref _textureV422P10, GL_NEAREST);
        InitTexture(ref _textureUyvy,    GL_NEAREST);

        // P010 textures (R16UI / RG16UI — NEAREST filter for integer sampling)
        InitTexture(ref _textureP010Y,  GL_NEAREST);
        InitTexture(ref _textureP010UV, GL_NEAREST);

        // Yuv444p textures (R8 × 3, full resolution — LINEAR filter)
        InitTexture(ref _textureY444p, GL_LINEAR);
        InitTexture(ref _textureU444p, GL_LINEAR);
        InitTexture(ref _textureV444p, GL_LINEAR);

        // Gray8 texture (R8 — LINEAR filter)
        InitTexture(ref _textureGray8, GL_LINEAR);

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

        if (frame.PixelFormat == PixelFormat.P010)
        {
            UploadAndDrawP010(frame);
            return;
        }

        if (frame.PixelFormat == PixelFormat.Yuv444p)
        {
            UploadAndDrawYuv444p(frame);
            return;
        }

        if (frame.PixelFormat == PixelFormat.Gray8)
        {
            UploadAndDrawGray8(frame);
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
            UploadSubImage2D(w, h, uploadFormat, GL_UNSIGNED_BYTE, pin.Pointer, w * h * 4);
        }
        else
        {
            // Resolution changed → re-allocate the texture.
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, uploadFormat, GL_UNSIGNED_BYTE, pin.Pointer);
            _texWidth  = w;
            _texHeight = h;
        }

        DrawRgbaFromTextures(w, h);
        _lastDrawnFormat = frame.PixelFormat;
        _lastDrawnW = w;
        _lastDrawnH = h;
        _hasLastDrawn = true;
    }

    private void DrawRgbaFromTextures(int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _texture);

        bool useFbo = BeginFboIfNeeded(w, h);
        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_program);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
        if (useFbo) BlitFboToScreen();
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
            UploadSubImage2D(w, h, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr, ySize);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr);

        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureUv);
        if (w == _texWidthNv12 && h == _texHeightNv12)
            UploadSubImage2D(Math.Max(1, w / 2), Math.Max(1, h / 2), GL_RG, GL_UNSIGNED_BYTE, (void*)uvPtr, uvSize);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RG8, Math.Max(1, w / 2), Math.Max(1, h / 2), 0, GL_RG, GL_UNSIGNED_BYTE, (void*)uvPtr);

        _texWidthNv12 = w;
        _texHeightNv12 = h;

        DrawNv12FromTextures(w, h);
        _lastDrawnFormat = PixelFormat.Nv12;
        _lastDrawnW = w;
        _lastDrawnH = h;
        _hasLastDrawn = true;
    }

    private void DrawNv12FromTextures(int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureY);
        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureUv);

        bool useFbo = BeginFboIfNeeded(w, h);
        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programNv12);
        if (_uNv12LimitedRangeLoc >= 0)
            _glUniform1i(_uNv12LimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uNv12ColorMatrixLoc >= 0)
            _glUniform1i(_uNv12ColorMatrixLoc, GetColorMatrixValue(w, h));
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
        if (useFbo) BlitFboToScreen();
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
            UploadSubImage2D(w, h, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr, ySize);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr);

        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureU);
        if (w == _texWidthI420 && h == _texHeightI420)
            UploadSubImage2D(cw, ch, GL_RED, GL_UNSIGNED_BYTE, (void*)uPtr, uSize);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, cw, ch, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)uPtr);

        _glActiveTexture(GL_TEXTURE2);
        _glBindTexture(GL_TEXTURE_2D, _textureV);
        if (w == _texWidthI420 && h == _texHeightI420)
            UploadSubImage2D(cw, ch, GL_RED, GL_UNSIGNED_BYTE, (void*)vPtr, uSize);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, cw, ch, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)vPtr);

        _texWidthI420 = w;
        _texHeightI420 = h;

        DrawI420FromTextures(w, h);
        _lastDrawnFormat = PixelFormat.Yuv420p;
        _lastDrawnW = w;
        _lastDrawnH = h;
        _hasLastDrawn = true;
    }

    private void DrawI420FromTextures(int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureY);
        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureU);
        _glActiveTexture(GL_TEXTURE2);
        _glBindTexture(GL_TEXTURE_2D, _textureV);

        bool useFbo = BeginFboIfNeeded(w, h);
        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programI420);
        if (_uI420LimitedRangeLoc >= 0)
            _glUniform1i(_uI420LimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uI420ColorMatrixLoc >= 0)
            _glUniform1i(_uI420ColorMatrixLoc, GetColorMatrixValue(w, h));
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
        if (useFbo) BlitFboToScreen();
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
            UploadSubImage2D(w, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)yPtr, ySamples * sizeof(ushort));
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R16UI, w, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)yPtr);

        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureU422P10);
        if (w == _texWidthI422P10 && h == _texHeightI422P10)
            UploadSubImage2D(cw, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)uPtr, uvSamples * sizeof(ushort));
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R16UI, cw, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)uPtr);

        _glActiveTexture(GL_TEXTURE2);
        _glBindTexture(GL_TEXTURE_2D, _textureV422P10);
        if (w == _texWidthI422P10 && h == _texHeightI422P10)
            UploadSubImage2D(cw, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)vPtr, uvSamples * sizeof(ushort));
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R16UI, cw, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)vPtr);

        _texWidthI422P10 = w;
        _texHeightI422P10 = h;

        DrawI422P10FromTextures(w, h);
        _lastDrawnFormat = PixelFormat.Yuv422p10;
        _lastDrawnW = w;
        _lastDrawnH = h;
        _hasLastDrawn = true;
    }

    private void DrawI422P10FromTextures(int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureY422P10);
        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureU422P10);
        _glActiveTexture(GL_TEXTURE2);
        _glBindTexture(GL_TEXTURE_2D, _textureV422P10);

        bool useFbo = BeginFboIfNeeded(w, h);
        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programI422P10);
        if (_uI422P10LimitedRangeLoc >= 0)
            _glUniform1i(_uI422P10LimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uI422P10ColorMatrixLoc >= 0)
            _glUniform1i(_uI422P10ColorMatrixLoc, GetColorMatrixValue(w, h));
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
        if (useFbo) BlitFboToScreen();
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
            UploadSubImage2D(halfW, h, GL_RGBA, GL_UNSIGNED_BYTE, pin.Pointer, required);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, halfW, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, pin.Pointer);

        _texWidthUyvy = w;
        _texHeightUyvy = h;

        // Ensure FBO exists at native video resolution for the two-pass decode.
        if (!EnsureFboUyvy(w, h))
            return; // FBO unavailable — skip this frame

        DrawUyvy422FromTextures(w, h);
        _lastDrawnFormat = PixelFormat.Uyvy422;
        _lastDrawnW = w;
        _lastDrawnH = h;
        _hasLastDrawn = true;
    }

    private void DrawUyvy422FromTextures(int w, int h)
    {
        // ── Pass 1: Decode UYVY → RGB at native resolution into FBO ──────
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureUyvy);
        _glBindFramebuffer(GL_FRAMEBUFFER, _fboUyvy);
        _glViewport(0, 0, w, h);

        _glUseProgram(_programUyvy422);
        if (_uUyvyVideoWidthLoc >= 0)
            _glUniform1i(_uUyvyVideoWidthLoc, w);
        if (_uUyvyLimitedRangeLoc >= 0)
            _glUniform1i(_uUyvyLimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uUyvyColorMatrixLoc >= 0)
            _glUniform1i(_uUyvyColorMatrixLoc, GetColorMatrixValue(w, h));
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);

        // ── Pass 2: Display FBO texture on screen with selected scaling ──
        _glBindFramebuffer(GL_FRAMEBUFFER, 0);
        _glViewport(_vpX, _vpY, _vpW, _vpH);
        _glClear(GL_COLOR_BUFFER_BIT);
        uint pass2Prog = (ScalingFilter)_scalingFilter == ScalingFilter.Bicubic
            ? _programBicubic
            : _programFbo;
        _glUseProgram(pass2Prog);
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _fboUyvyTexture);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    /// <summary>
    /// When <see cref="ScalingFilter"/> is <see cref="ScalingFilter.Bicubic"/>, ensures the
    /// general FBO exists at the given native video resolution, binds it, and sets the GL
    /// viewport to cover the full FBO.  The caller's draw call then renders into the FBO
    /// rather than directly to the window.
    /// </summary>
    /// <returns><c>true</c> if FBO was bound (bicubic path); <c>false</c> for direct rendering.</returns>
    private bool BeginFboIfNeeded(int w, int h)
    {
        if ((ScalingFilter)_scalingFilter == ScalingFilter.Bilinear) return false;
        if (!EnsureFboGeneral(w, h)) return false; // FBO unavailable, fall back to direct
        _glBindFramebuffer(GL_FRAMEBUFFER, _fboGeneral);
        _glViewport(0, 0, w, h);
        return true;
    }

    /// <summary>
    /// Unbinds the general FBO, restores the letterbox viewport, and blits the FBO
    /// texture to the window using the shader selected by <see cref="ScalingFilter"/>.
    /// </summary>
    private void BlitFboToScreen()
    {
        _glBindFramebuffer(GL_FRAMEBUFFER, 0);
        _glViewport(_vpX, _vpY, _vpW, _vpH);
        _glClear(GL_COLOR_BUFFER_BIT);

        uint prog = (ScalingFilter)_scalingFilter == ScalingFilter.Bicubic
            ? _programBicubic
            : _programFbo;
        _glUseProgram(prog);
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _fboGeneralTexture);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    /// <summary>Creates (or recreates on resolution change) a general-purpose RGBA8 FBO.</summary>
    /// <returns><c>true</c> if the FBO is ready; <c>false</c> if creation failed (frame should be skipped).</returns>
    private bool EnsureFboGeneral(int w, int h)
    {
        if (_fboGeneralWidth == w && _fboGeneralHeight == h && _fboGeneral != 0)
            return true;

        if (_fboGeneral != 0)
        { fixed (uint* p = &_fboGeneral) _glDeleteFramebuffers(1, p); _fboGeneral = 0; }
        if (_fboGeneralTexture != 0)
        { fixed (uint* p = &_fboGeneralTexture) _glDeleteTextures(1, p); _fboGeneralTexture = 0; }

        fixed (uint* pTex = &_fboGeneralTexture) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _fboGeneralTexture);
        _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        // GL_NEAREST on the FBO texture: the bicubic shader uses texelFetch (ignores filter),
        // and GL_NEAREST avoids any hardware bilinear blending during the decode pass.
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        fixed (uint* pFbo = &_fboGeneral) _glGenFramebuffers(1, pFbo);
        _glBindFramebuffer(GL_FRAMEBUFFER, _fboGeneral);
        _glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _fboGeneralTexture, 0);
        uint status = _glCheckFramebufferStatus(GL_FRAMEBUFFER);
        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            // FBO incomplete — delete it and signal failure. Will retry next frame.
            fixed (uint* p = &_fboGeneral) _glDeleteFramebuffers(1, p); _fboGeneral = 0;
            fixed (uint* p = &_fboGeneralTexture) _glDeleteTextures(1, p); _fboGeneralTexture = 0;
            _glBindFramebuffer(GL_FRAMEBUFFER, 0);
            _fboGeneralWidth = 0;
            _fboGeneralHeight = 0;
            return false;
        }
        _glBindFramebuffer(GL_FRAMEBUFFER, 0);

        _fboGeneralWidth  = w;
        _fboGeneralHeight = h;
        return true;
    }

    /// <summary>
    /// Creates (or recreates on resolution change) an FBO + RGBA8 texture at the given video
    /// resolution.  Pass 2 uses a simple passthrough shader with <c>texture()</c> and the
    /// GPU's native <c>GL_LINEAR</c> bilinear filter — the sharpest standard filter for
    /// moderate scaling and what reference video players typically use.
    /// </summary>
    private bool EnsureFboUyvy(int w, int h)
    {
        if (_fboUyvyWidth == w && _fboUyvyHeight == h && _fboUyvy != 0)
            return true;

        // Delete old resources if they exist.
        if (_fboUyvy != 0)
        {
            fixed (uint* p = &_fboUyvy) _glDeleteFramebuffers(1, p);
            _fboUyvy = 0;
        }
        if (_fboUyvyTexture != 0)
        {
            fixed (uint* p = &_fboUyvyTexture) _glDeleteTextures(1, p);
            _fboUyvyTexture = 0;
        }

        // Create FBO color attachment: RGBA8 at native video resolution, GL_LINEAR for
        // hardware bilinear in Pass 2 (texture() respects the filter mode).
        fixed (uint* pTex = &_fboUyvyTexture) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _fboUyvyTexture);
        _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        // Create and validate the framebuffer object.
        fixed (uint* pFbo = &_fboUyvy) _glGenFramebuffers(1, pFbo);
        _glBindFramebuffer(GL_FRAMEBUFFER, _fboUyvy);
        _glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _fboUyvyTexture, 0);
        uint status = _glCheckFramebufferStatus(GL_FRAMEBUFFER);
        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            fixed (uint* p = &_fboUyvy) _glDeleteFramebuffers(1, p); _fboUyvy = 0;
            fixed (uint* p = &_fboUyvyTexture) _glDeleteTextures(1, p); _fboUyvyTexture = 0;
            _glBindFramebuffer(GL_FRAMEBUFFER, 0);
            _fboUyvyWidth = 0;
            _fboUyvyHeight = 0;
            return false;
        }
        _glBindFramebuffer(GL_FRAMEBUFFER, 0);

        _fboUyvyWidth  = w;
        _fboUyvyHeight = h;
        return true;
    }

    private void UploadAndDrawP010(VideoFrame frame)
    {
        int w = frame.Width, h = frame.Height;
        if (w <= 0 || h <= 0) return;

        // Y: w×h × uint16 (R16UI); UV: (w/2)×(h/2) × uvec2 (RG16UI)
        int yBytes  = w * h * 2;
        int uvW     = Math.Max(1, w / 2);
        int uvH     = Math.Max(1, h / 2);
        int required = yBytes + uvW * uvH * 4;
        if (frame.Data.Length < required) { DrawBlack(); return; }

        using var pin = frame.Data.Pin();
        nint yPtr  = (nint)pin.Pointer;
        nint uvPtr = yPtr + yBytes;

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureP010Y);
        if (w == _texWidthP010 && h == _texHeightP010)
            UploadSubImage2D(w, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)yPtr, yBytes);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R16UI, w, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, (void*)yPtr);

        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureP010UV);
        if (w == _texWidthP010 && h == _texHeightP010)
            UploadSubImage2D(uvW, uvH, GL_RG_INTEGER, GL_UNSIGNED_SHORT, (void*)uvPtr, uvW * uvH * 4);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RG16UI, uvW, uvH, 0, GL_RG_INTEGER, GL_UNSIGNED_SHORT, (void*)uvPtr);

        _texWidthP010  = w;
        _texHeightP010 = h;

        DrawP010FromTextures(w, h);
        _lastDrawnFormat = PixelFormat.P010;
        _lastDrawnW = w;
        _lastDrawnH = h;
        _hasLastDrawn = true;
    }

    private void DrawP010FromTextures(int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureP010Y);
        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureP010UV);

        bool useFbo = BeginFboIfNeeded(w, h);
        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programP010);
        if (_uP010LimitedRangeLoc >= 0)
            _glUniform1i(_uP010LimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uP010ColorMatrixLoc >= 0)
            _glUniform1i(_uP010ColorMatrixLoc, GetColorMatrixValue(w, h));
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
        if (useFbo) BlitFboToScreen();
    }

    private void UploadAndDrawYuv444p(VideoFrame frame)
    {
        int w = frame.Width, h = frame.Height;
        if (w <= 0 || h <= 0) return;

        int planeSize = w * h;
        int required  = planeSize * 3;
        if (frame.Data.Length < required) { DrawBlack(); return; }

        using var pin = frame.Data.Pin();
        nint yPtr = (nint)pin.Pointer;
        nint uPtr = yPtr + planeSize;
        nint vPtr = uPtr + planeSize;

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureY444p);
        if (w == _texWidthYuv444p && h == _texHeightYuv444p)
            UploadSubImage2D(w, h, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr, planeSize);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)yPtr);

        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureU444p);
        if (w == _texWidthYuv444p && h == _texHeightYuv444p)
            UploadSubImage2D(w, h, GL_RED, GL_UNSIGNED_BYTE, (void*)uPtr, planeSize);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)uPtr);

        _glActiveTexture(GL_TEXTURE2);
        _glBindTexture(GL_TEXTURE_2D, _textureV444p);
        if (w == _texWidthYuv444p && h == _texHeightYuv444p)
            UploadSubImage2D(w, h, GL_RED, GL_UNSIGNED_BYTE, (void*)vPtr, planeSize);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, (void*)vPtr);

        _texWidthYuv444p  = w;
        _texHeightYuv444p = h;

        DrawYuv444pFromTextures(w, h);
        _lastDrawnFormat = PixelFormat.Yuv444p;
        _lastDrawnW = w;
        _lastDrawnH = h;
        _hasLastDrawn = true;
    }

    private void DrawYuv444pFromTextures(int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureY444p);
        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _textureU444p);
        _glActiveTexture(GL_TEXTURE2);
        _glBindTexture(GL_TEXTURE_2D, _textureV444p);

        bool useFbo = BeginFboIfNeeded(w, h);
        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programYuv444p);
        if (_uYuv444pLimitedRangeLoc >= 0)
            _glUniform1i(_uYuv444pLimitedRangeLoc, ShouldUseLimitedRangeForYuv() ? 1 : 0);
        if (_uYuv444pColorMatrixLoc >= 0)
            _glUniform1i(_uYuv444pColorMatrixLoc, GetColorMatrixValue(w, h));
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
        if (useFbo) BlitFboToScreen();
    }

    private void UploadAndDrawGray8(VideoFrame frame)
    {
        int w = frame.Width, h = frame.Height;
        if (w <= 0 || h <= 0) return;

        int required = w * h;
        if (frame.Data.Length < required) { DrawBlack(); return; }

        using var pin = frame.Data.Pin();

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureGray8);
        if (w == _texWidthGray8 && h == _texHeightGray8)
            UploadSubImage2D(w, h, GL_RED, GL_UNSIGNED_BYTE, pin.Pointer, required);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, pin.Pointer);

        _texWidthGray8  = w;
        _texHeightGray8 = h;

        DrawGray8FromTextures(w, h);
        _lastDrawnFormat = PixelFormat.Gray8;
        _lastDrawnW = w;
        _lastDrawnH = h;
        _hasLastDrawn = true;
    }

    private void DrawGray8FromTextures(int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _textureGray8);

        bool useFbo = BeginFboIfNeeded(w, h);
        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_programGray8);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
        if (useFbo) BlitFboToScreen();
    }

    private void UploadSubImage2D(int width, int height, uint format, uint type, void* source, int sourceBytes)
    {
        if (TryUploadSubImageWithPersistentPbo(width, height, format, type, source, sourceBytes))
            return;
        _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, format, type, source);
    }

    private void InitialisePersistentPboUpload()
    {
        _persistentPboEnabled = false;
        var glBufferStorage = _glBufferStorage;
        var glMapBufferRange = _glMapBufferRange;
        if (glBufferStorage is null || glMapBufferRange is null || _glFenceSync is null ||
            _glClientWaitSync is null || _glDeleteSync is null)
            return;

        var pboIds      = new uint[PersistentPboSlotCount];
        var pboMappings = new nint[PersistentPboSlotCount];
        var pboFences   = new nint[PersistentPboSlotCount];
        uint storageFlags = GL_MAP_WRITE_BIT | GL_MAP_PERSISTENT_BIT | GL_MAP_COHERENT_BIT;
        bool ok = false;

        try
        {
            for (int i = 0; i < pboIds.Length; i++)
            {
                fixed (uint* p = &pboIds[i]) _glGenBuffers(1, p);
                if (pboIds[i] == 0)
                    return;

                _glBindBuffer(GL_PIXEL_UNPACK_BUFFER, pboIds[i]);
                glBufferStorage(GL_PIXEL_UNPACK_BUFFER, PersistentPboSlotSizeBytes, null, storageFlags);
                void* mapped = glMapBufferRange(
                    GL_PIXEL_UNPACK_BUFFER, 0, PersistentPboSlotSizeBytes, storageFlags);
                if (mapped is null)
                    return;
                pboMappings[i] = (nint)mapped;
            }

            ok = true;
        }
        finally
        {
            _glBindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
            if (!ok)
            {
                for (int i = 0; i < pboIds.Length; i++)
                {
                    if (pboIds[i] == 0) continue;
                    fixed (uint* p = &pboIds[i]) _glDeleteBuffers(1, p);
                }
            }
        }

        _persistentPboIds = pboIds;
        _persistentPboMappings = pboMappings;
        _persistentPboFences = pboFences;
        _persistentPboNextSlot = 0;
        _persistentPboEnabled = true;
    }

    private bool TryUploadSubImageWithPersistentPbo(
        int width, int height, uint format, uint type, void* source, int sourceBytes)
    {
        if (!_persistentPboEnabled || sourceBytes <= 0 || sourceBytes > PersistentPboSlotSizeBytes)
            return false;
        var glFenceSync = _glFenceSync;
        if (_persistentPboIds is null || _persistentPboMappings is null || _persistentPboFences is null ||
            glFenceSync is null || _glClientWaitSync is null || _glDeleteSync is null)
            return false;

        if (!TryAcquirePersistentPboSlot(out int slot))
            return false;

        Buffer.MemoryCopy(source, (void*)_persistentPboMappings[slot], PersistentPboSlotSizeBytes, sourceBytes);

        _glBindBuffer(GL_PIXEL_UNPACK_BUFFER, _persistentPboIds[slot]);
        _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, format, type, null);
        _glBindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
        _persistentPboFences[slot] = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        return true;
    }

    private bool TryAcquirePersistentPboSlot(out int slot)
    {
        slot = -1;
        var glClientWaitSync = _glClientWaitSync;
        var glDeleteSync = _glDeleteSync;
        if (_persistentPboIds is null || _persistentPboFences is null ||
            glClientWaitSync is null || glDeleteSync is null)
            return false;

        int count = _persistentPboIds.Length;
        int start = _persistentPboNextSlot;
        for (int i = 0; i < count; i++)
        {
            int idx = (start + i) % count;
            nint fence = _persistentPboFences[idx];
            if (fence == nint.Zero)
            {
                slot = idx;
                _persistentPboNextSlot = (idx + 1) % count;
                return true;
            }

            uint wait = glClientWaitSync(fence, 0, 0);
            if (wait == GL_ALREADY_SIGNALED || wait == GL_CONDITION_SATISFIED || wait == GL_WAIT_FAILED)
            {
                glDeleteSync(fence);
                _persistentPboFences[idx] = nint.Zero;
                slot = idx;
                _persistentPboNextSlot = (idx + 1) % count;
                return true;
            }
            if (wait == GL_TIMEOUT_EXPIRED)
                continue;
        }

        return false;
    }

    private void DisposePersistentPboUpload()
    {
        _persistentPboEnabled = false;
        if (_persistentPboIds is null)
            return;

        var glDeleteSync = _glDeleteSync;
        if (_persistentPboFences is not null && glDeleteSync is not null)
        {
            for (int i = 0; i < _persistentPboFences.Length; i++)
            {
                if (_persistentPboFences[i] == nint.Zero)
                    continue;
                glDeleteSync(_persistentPboFences[i]);
                _persistentPboFences[i] = nint.Zero;
            }
        }

        for (int i = 0; i < _persistentPboIds.Length; i++)
        {
            if (_persistentPboIds[i] == 0)
                continue;
            if (_glUnmapBuffer is not null && _persistentPboMappings is not null &&
                _persistentPboMappings[i] != nint.Zero)
            {
                _glBindBuffer(GL_PIXEL_UNPACK_BUFFER, _persistentPboIds[i]);
                _glUnmapBuffer(GL_PIXEL_UNPACK_BUFFER);
                _persistentPboMappings[i] = nint.Zero;
            }
            fixed (uint* p = &_persistentPboIds[i]) _glDeleteBuffers(1, p);
        }

        _glBindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
        _persistentPboIds = null;
        _persistentPboMappings = null;
        _persistentPboFences = null;
        _persistentPboNextSlot = 0;
    }

    /// <summary>
    /// Re-runs the draw pipeline for the last successfully uploaded frame, reusing
    /// the GPU-resident textures without any PCIe upload. Callers (e.g. the SDL3
    /// render loop) should call this instead of <see cref="UploadAndDraw"/> when the
    /// pull callback returns a frame identical to the previously presented one, so
    /// we avoid redundant <c>glTexSubImage2D</c> calls on every vsync.
    /// </summary>
    public void DrawLastFrame()
    {
        if (!_hasLastDrawn || _lastDrawnW <= 0 || _lastDrawnH <= 0)
        {
            DrawBlack();
            return;
        }

        switch (_lastDrawnFormat)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Rgba32:
                DrawRgbaFromTextures(_lastDrawnW, _lastDrawnH);
                break;
            case PixelFormat.Nv12:
                DrawNv12FromTextures(_lastDrawnW, _lastDrawnH);
                break;
            case PixelFormat.Yuv420p:
                DrawI420FromTextures(_lastDrawnW, _lastDrawnH);
                break;
            case PixelFormat.Yuv422p10:
                DrawI422P10FromTextures(_lastDrawnW, _lastDrawnH);
                break;
            case PixelFormat.Uyvy422:
                DrawUyvy422FromTextures(_lastDrawnW, _lastDrawnH);
                break;
            case PixelFormat.P010:
                DrawP010FromTextures(_lastDrawnW, _lastDrawnH);
                break;
            case PixelFormat.Yuv444p:
                DrawYuv444pFromTextures(_lastDrawnW, _lastDrawnH);
                break;
            case PixelFormat.Gray8:
                DrawGray8FromTextures(_lastDrawnW, _lastDrawnH);
                break;
            default:
                DrawBlack();
                break;
        }
    }

    private int GetColorMatrixValue(int width, int height)
    {
        var resolved = YuvAutoPolicy.ResolveMatrix(_yuvColorMatrix, width, height);
        return YuvAutoPolicy.ToShaderValue(resolved);
    }

    private bool ShouldUseLimitedRangeForYuv()
    {
        return YuvAutoPolicy.ResolveRange(_yuvColorRange) == YuvColorRange.Limited;
    }

    private static YuvColorRange NormalizeColorRange(YuvColorRange value)
    {
        return value is YuvColorRange.Auto or YuvColorRange.Full or YuvColorRange.Limited
            ? value
            : YuvColorRange.Auto;
    }

    private static YuvColorMatrix NormalizeColorMatrix(YuvColorMatrix value)
    {
        return value is YuvColorMatrix.Auto or YuvColorMatrix.Bt601 or YuvColorMatrix.Bt709 or YuvColorMatrix.Bt2020
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
            _vpX = 0; _vpY = 0; _vpW = winW; _vpH = winH;
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

        _vpX = vpX; _vpY = vpY;
        _vpW = Math.Max(1, vpW); _vpH = Math.Max(1, vpH);
        _glViewport(_vpX, _vpY, _vpW, _vpH);
    }

    // ── GL loading helpers ────────────────────────────────────────────────

    private T LoadGL<T>(string name) where T : Delegate
    {
        var ptr = (nint)global::SDL3.SDL.GLGetProcAddress(name);
        if (ptr == nint.Zero)
            throw new InvalidOperationException($"Failed to load GL function: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private T? TryLoadGL<T>(string name) where T : Delegate
    {
        var ptr = (nint)global::SDL3.SDL.GLGetProcAddress(name);
        if (ptr == nint.Zero)
            return null;
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
        _glPixelStorei             = LoadGL<GlPixelStorei>("glPixelStorei");
        _glGenFramebuffers         = LoadGL<GlGenFramebuffers>("glGenFramebuffers");
        _glDeleteFramebuffers      = LoadGL<GlDeleteFramebuffers>("glDeleteFramebuffers");
        _glBindFramebuffer         = LoadGL<GlBindFramebuffer>("glBindFramebuffer");
        _glFramebufferTexture2D    = LoadGL<GlFramebufferTexture2D>("glFramebufferTexture2D");
        _glCheckFramebufferStatus  = LoadGL<GlCheckFramebufferStatus>("glCheckFramebufferStatus");
        _glGenerateMipmap          = LoadGL<GlGenerateMipmap>("glGenerateMipmap");
        _glEnable                  = LoadGL<GlEnable>("glEnable");
        _glDisable                 = LoadGL<GlDisable>("glDisable");
        _glBlendFunc               = LoadGL<GlBlendFunc>("glBlendFunc");
        _glUniform2f               = LoadGL<GlUniform2f>("glUniform2f");
        _glUniform4f               = LoadGL<GlUniform4f>("glUniform4f");

        // §8.6 optional upload path for drivers exposing ARB_buffer_storage / GL 4.4+.
        _glBufferStorage = TryLoadGL<GlBufferStorage>("glBufferStorage");
        _glMapBufferRange = TryLoadGL<GlMapBufferRange>("glMapBufferRange");
        _glUnmapBuffer = TryLoadGL<GlUnmapBuffer>("glUnmapBuffer");
        _glFenceSync = TryLoadGL<GlFenceSync>("glFenceSync");
        _glClientWaitSync = TryLoadGL<GlClientWaitSync>("glClientWaitSync");
        _glDeleteSync = TryLoadGL<GlDeleteSync>("glDeleteSync");
    }

    /// <summary>
    /// Generates a single GL texture, binds it, and sets min/mag filter + clamp-to-edge wrap.
    /// Consolidates the 6-line texture init sequence that was previously repeated 15+ times
    /// across all <c>EnsureTextures*</c> methods (§6.3).
    /// </summary>
    /// <param name="texture">Field reference that receives the new texture name.</param>
    /// <param name="filterMode"><c>GL_NEAREST</c> for pixel-exact formats, <c>GL_LINEAR</c> for sub-sampled chroma.</param>
    private void InitTexture(ref uint texture, uint filterMode)
    {
        fixed (uint* pTex = &texture) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, texture);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filterMode);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filterMode);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
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

    // ── HUD rendering ──────────────────────────────────────────────────────

    /// <summary>
    /// Draws a multi-line text overlay in the top-left corner of the window.
    /// Uses a bitmap font atlas and a dedicated shader program.
    /// Must be called AFTER the video frame has been rendered and BEFORE SwapWindow.
    /// </summary>
    public void DrawHud(string[] lines)
    {
        if (lines.Length == 0 || _windowWidth <= 0 || _windowHeight <= 0) return;

        EnsureHudInitialised();

        // Build vertex data for all glyphs: 6 verts per glyph (2 triangles), 4 floats per vert (pos.xy + uv.xy)
        const int glyphW = BitmapFont.GlyphWidth;
        const int glyphH = BitmapFont.GlyphHeight;
        const int atlasW = 128;
        const int atlasH = 128;
        const int colCount = 16; // atlas cols
        const int padding = 4;
        int scale = Math.Max(1, _windowHeight / 500); // auto-scale HUD for HiDPI

        int totalGlyphs = 0;
        foreach (var line in lines) totalGlyphs += line.Length;

        int requiredFloats = totalGlyphs * 6 * 4;
        EnsureHudScratchCapacity(requiredFloats);
        var verts = _hudScratchVerts;
        int vi = 0;
        int cursorY = padding;

        foreach (var line in lines)
        {
            int cursorX = padding;
            foreach (char c in line)
            {
                int idx = c - BitmapFont.FirstChar;
                if (idx < 0 || idx >= BitmapFont.GlyphCount) idx = 0; // space

                int atlasCol = idx % colCount;
                int atlasRow = idx / colCount;

                float u0 = (float)(atlasCol * glyphW) / atlasW;
                float v0 = (float)(atlasRow * glyphH) / atlasH;
                float u1 = (float)((atlasCol + 1) * glyphW) / atlasW;
                float v1 = (float)((atlasRow + 1) * glyphH) / atlasH;

                float x0 = cursorX;
                float y0 = cursorY;
                float x1 = cursorX + glyphW * scale;
                float y1 = cursorY + glyphH * scale;

                // Triangle 1
                verts[vi++] = x0; verts[vi++] = y0; verts[vi++] = u0; verts[vi++] = v0;
                verts[vi++] = x1; verts[vi++] = y0; verts[vi++] = u1; verts[vi++] = v0;
                verts[vi++] = x1; verts[vi++] = y1; verts[vi++] = u1; verts[vi++] = v1;
                // Triangle 2
                verts[vi++] = x0; verts[vi++] = y0; verts[vi++] = u0; verts[vi++] = v0;
                verts[vi++] = x1; verts[vi++] = y1; verts[vi++] = u1; verts[vi++] = v1;
                verts[vi++] = x0; verts[vi++] = y1; verts[vi++] = u0; verts[vi++] = v1;

                cursorX += glyphW * scale;
            }
            cursorY += (glyphH + 2) * scale;
        }

        int vertCount = vi / 4;
        if (vertCount == 0) return;

        // Upload vertex data
        _glBindBuffer(GL_ARRAY_BUFFER, _hudVbo);
        fixed (float* pData = verts)
            _glBufferData(GL_ARRAY_BUFFER, (nint)(vi * sizeof(float)), pData, GL_DYNAMIC_DRAW);

        // Render with blending
        _glBindFramebuffer(GL_FRAMEBUFFER, 0);
        _glViewport(0, 0, _windowWidth, _windowHeight);
        _glEnable(GL_BLEND);
        _glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

        _glUseProgram(_hudProgram);
        _glUniform2f(_hudUScreenSize, _windowWidth, _windowHeight);
        _glUniform4f(_hudUColor, 1f, 1f, 1f, 1f); // white text
        _glUniform4f(_hudUBgColor, 0f, 0f, 0f, 0.65f); // semi-transparent black bg

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _hudFontTexture);

        _glBindVertexArray(_hudVao);
        _glDrawArrays(GL_TRIANGLES, 0, (int)(uint)vertCount);
        _glBindVertexArray(0);

        _glDisable(GL_BLEND);

        // Restore video viewport
        _glViewport(_vpX, _vpY, _vpW, _vpH);
    }

    private void EnsureHudScratchCapacity(int requiredFloats)
    {
        if (_hudScratchVerts.Length >= requiredFloats)
            return;

        int newSize = _hudScratchVerts.Length == 0 ? 1024 : _hudScratchVerts.Length;
        while (newSize < requiredFloats)
            newSize <<= 1;
        _hudScratchVerts = new float[newSize];
    }

    private void EnsureHudInitialised()
    {
        if (_hudInitialised) return;
        _hudInitialised = true;

        // Compile HUD shader
        uint vs = CompileShader(GL_VERTEX_SHADER, GlShaderSources.VertexHud);
        uint fs = CompileShader(GL_FRAGMENT_SHADER, GlShaderSources.FragmentHud);
        _hudProgram = _glCreateProgram();
        _glAttachShader(_hudProgram, vs);
        _glAttachShader(_hudProgram, fs);
        _glLinkProgram(_hudProgram);
        CheckProgram(_hudProgram);
        _glDeleteShader(vs);
        _glDeleteShader(fs);

        _glUseProgram(_hudProgram);
        fixed (byte* name = "uFontAtlas\0"u8)
        { int l = _glGetUniformLocation(_hudProgram, name); _glUniform1i(l, 0); }
        fixed (byte* name = "uScreenSize\0"u8)
            _hudUScreenSize = _glGetUniformLocation(_hudProgram, name);
        fixed (byte* name = "uColor\0"u8)
            _hudUColor = _glGetUniformLocation(_hudProgram, name);
        fixed (byte* name = "uBgColor\0"u8)
            _hudUBgColor = _glGetUniformLocation(_hudProgram, name);

        // Create font atlas texture
        var atlas = BitmapFont.BuildAtlasRgba(out int aw, out int ah, out _);
        fixed (uint* pTex = &_hudFontTexture) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _hudFontTexture);
        fixed (byte* pData = atlas)
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, aw, ah, 0, GL_RGBA, GL_UNSIGNED_BYTE, pData);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        // Create VAO/VBO for dynamic glyph quads
        fixed (uint* pVao = &_hudVao) _glGenVertexArrays(1, pVao);
        fixed (uint* pVbo = &_hudVbo) _glGenBuffers(1, pVbo);

        _glBindVertexArray(_hudVao);
        _glBindBuffer(GL_ARRAY_BUFFER, _hudVbo);
        _glEnableVertexAttribArray(0);
        _glVertexAttribPointer(0, 2, GL_FLOAT, (byte)GL_FALSE, 4 * sizeof(float), (void*)0);
        _glEnableVertexAttribArray(1);
        _glVertexAttribPointer(1, 2, GL_FLOAT, (byte)GL_FALSE, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        _glBindVertexArray(0);
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePersistentPboUpload();

        fixed (uint* p = &_texture)        _glDeleteTextures(1, p);
        fixed (uint* p = &_textureY)       _glDeleteTextures(1, p);
        fixed (uint* p = &_textureUv)      _glDeleteTextures(1, p);
        fixed (uint* p = &_textureU)       _glDeleteTextures(1, p);
        fixed (uint* p = &_textureV)       _glDeleteTextures(1, p);
        fixed (uint* p = &_textureY422P10) _glDeleteTextures(1, p);
        fixed (uint* p = &_textureU422P10) _glDeleteTextures(1, p);
        fixed (uint* p = &_textureV422P10) _glDeleteTextures(1, p);
        fixed (uint* p = &_textureUyvy)    _glDeleteTextures(1, p);
        fixed (uint* p = &_textureP010Y)   _glDeleteTextures(1, p);
        fixed (uint* p = &_textureP010UV)  _glDeleteTextures(1, p);
        fixed (uint* p = &_textureY444p)   _glDeleteTextures(1, p);
        fixed (uint* p = &_textureU444p)   _glDeleteTextures(1, p);
        fixed (uint* p = &_textureV444p)   _glDeleteTextures(1, p);
        fixed (uint* p = &_textureGray8)   _glDeleteTextures(1, p);
        if (_fboUyvyTexture != 0)
        { fixed (uint* p = &_fboUyvyTexture) _glDeleteTextures(1, p); }
        if (_fboUyvy != 0)
        { fixed (uint* p = &_fboUyvy) _glDeleteFramebuffers(1, p); }
        if (_fboGeneralTexture != 0)
        { fixed (uint* p = &_fboGeneralTexture) _glDeleteTextures(1, p); }
        if (_fboGeneral != 0)
        { fixed (uint* p = &_fboGeneral) _glDeleteFramebuffers(1, p); }
        fixed (uint* p = &_vbo)            _glDeleteBuffers(1, p);
        fixed (uint* p = &_vao)            _glDeleteVertexArrays(1, p);
        _glDeleteProgram(_program);
        _glDeleteProgram(_programNv12);
        _glDeleteProgram(_programI420);
        _glDeleteProgram(_programI422P10);
        _glDeleteProgram(_programUyvy422);
        _glDeleteProgram(_programP010);
        _glDeleteProgram(_programYuv444p);
        _glDeleteProgram(_programGray8);
        _glDeleteProgram(_programFbo);
        if (_programBicubic != 0) _glDeleteProgram(_programBicubic);
        if (_hudProgram != 0) _glDeleteProgram(_hudProgram);
        if (_hudFontTexture != 0) { fixed (uint* p = &_hudFontTexture) _glDeleteTextures(1, p); }
        if (_hudVbo != 0) { fixed (uint* p = &_hudVbo) _glDeleteBuffers(1, p); }
        if (_hudVao != 0) { fixed (uint* p = &_hudVao) _glDeleteVertexArrays(1, p); }
    }
}
