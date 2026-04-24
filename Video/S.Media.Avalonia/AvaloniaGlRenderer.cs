using System.Runtime.InteropServices;
using System.Text;
using Avalonia.OpenGL;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.Avalonia;

/// <summary>
/// Multi-format OpenGL renderer for Avalonia.
/// Supports native GPU upload for Rgba32, Bgra32, Nv12, Yuv420p, Yuv422p10, and Uyvy422
/// using the same shader programs as the SDL3 GLRenderer — no CPU pixel conversion required.
/// </summary>
internal sealed unsafe class AvaloniaGlRenderer : IDisposable
{
    // ── GL constants ──────────────────────────────────────────────────────
    private const uint GL_TEXTURE_2D        = 0x0DE1;
    private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    private const uint GL_TEXTURE_WRAP_S    = 0x2802;
    private const uint GL_TEXTURE_WRAP_T    = 0x2803;
    private const uint GL_LINEAR            = 0x2601;
    private const uint GL_NEAREST           = 0x2600;
    private const uint GL_CLAMP_TO_EDGE     = 0x812F;
    private const uint GL_RGBA8             = 0x8058;
    private const uint GL_RGBA              = 0x1908;
    private const uint GL_BGRA              = 0x80E1;
    private const uint GL_RED               = 0x1903;
    private const uint GL_RG                = 0x8227;
    private const uint GL_R8                = 0x8229;
    private const uint GL_RG8               = 0x822B;
    private const uint GL_R16UI             = 0x8234;
    private const uint GL_RED_INTEGER       = 0x8D94;
    private const uint GL_UNSIGNED_BYTE     = 0x1401;
    private const uint GL_UNSIGNED_SHORT    = 0x1403;
    private const uint GL_FLOAT             = 0x1406;
    private const uint GL_TRIANGLES         = 0x0004;
    private const uint GL_ARRAY_BUFFER      = 0x8892;
    private const uint GL_STATIC_DRAW       = 0x88E4;
    private const uint GL_DYNAMIC_DRAW      = 0x88E8;
    private const uint GL_FRAGMENT_SHADER   = 0x8B30;
    private const uint GL_VERTEX_SHADER     = 0x8B31;
    private const uint GL_COMPILE_STATUS    = 0x8B81;
    private const uint GL_LINK_STATUS       = 0x8B82;
    private const uint GL_COLOR_BUFFER_BIT  = 0x00004000;
    private const uint GL_FALSE             = 0;
    private const uint GL_FRAMEBUFFER       = 0x8D40;
    private const uint GL_BLEND             = 0x0BE2;
    private const uint GL_SRC_ALPHA         = 0x0302;
    private const uint GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const uint GL_UNPACK_ALIGNMENT  = 0x0CF5;
    private const uint GL_UNPACK_ROW_LENGTH = 0x0CF2;
    private const uint GL_TEXTURE0          = 0x84C0;
    private const uint GL_TEXTURE1          = 0x84C1;
    private const uint GL_TEXTURE2          = 0x84C2;
    private const uint GL_RG16UI            = 0x823A;
    private const uint GL_RG_INTEGER        = 0x8228;
    private const uint GL_COLOR_ATTACHMENT0  = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
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

    // ── Delegates ─────────────────────────────────────────────────────────
    private delegate void GlViewport(int x, int y, int w, int h);
    private delegate void GlClearColor(float r, float g, float b, float a);
    private delegate void GlClear(uint mask);
    private delegate void GlGenTextures(int n, uint* textures);
    private delegate void GlDeleteTextures(int n, uint* textures);
    private delegate void GlBindTexture(uint target, uint texture);
    private delegate void GlTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, void* data);
    private delegate void GlTexSubImage2D(uint target, int level, int x, int y, int w, int h, uint format, uint type, void* data);
    private delegate void GlTexParameteri(uint target, uint pname, int param);
    private delegate uint GlCreateShader(uint type);
    private delegate void GlShaderSource(uint shader, int count, byte** strings, int* lengths);
    private delegate void GlCompileShader(uint shader);
    private delegate void GlGetShaderiv(uint shader, uint pname, int* result);
    private delegate void GlGetShaderInfoLog(uint shader, int maxLen, int* length, byte* infoLog);
    private delegate uint GlCreateProgram();
    private delegate void GlAttachShader(uint program, uint shader);
    private delegate void GlLinkProgram(uint program);
    private delegate void GlGetProgramiv(uint program, uint pname, int* result);
    private delegate void GlGetProgramInfoLog(uint program, int maxLen, int* length, byte* infoLog);
    private delegate void GlUseProgram(uint program);
    private delegate void GlDeleteShader(uint shader);
    private delegate void GlDeleteProgram(uint program);
    private delegate int GlGetUniformLocation(uint program, byte* name);
    private delegate void GlUniform1i(int location, int value);
    private delegate void GlGenVertexArrays(int n, uint* arrays);
    private delegate void GlDeleteVertexArrays(int n, uint* arrays);
    private delegate void GlBindVertexArray(uint array);
    private delegate void GlGenBuffers(int n, uint* buffers);
    private delegate void GlDeleteBuffers(int n, uint* buffers);
    private delegate void GlBindBuffer(uint target, uint buffer);
    private delegate void GlBufferData(uint target, nint size, void* data, uint usage);
    private delegate void GlEnableVertexAttribArray(uint index);
    private delegate void GlVertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, void* pointer);
    private delegate void GlDrawArrays(uint mode, int first, int count);
    private delegate void GlBindFramebuffer(uint target, uint framebuffer);
    private delegate void GlPixelStorei(uint pname, int param);
    private delegate void GlActiveTexture(uint texture);
    private delegate void GlEnable(uint cap);
    private delegate void GlDisable(uint cap);
    private delegate void GlBlendFunc(uint sfactor, uint dfactor);
    private delegate void GlGenFramebuffers(int n, uint* framebuffers);
    private delegate void GlDeleteFramebuffers(int n, uint* framebuffers);
    private delegate void GlFramebufferTexture2D(uint target, uint attachment, uint textarget, uint texture, int level);
    private delegate uint GlCheckFramebufferStatus(uint target);
    private delegate void GlUniform2f(int location, float v0, float v1);
    private delegate void GlUniform4f(int location, float v0, float v1, float v2, float v3);
    private delegate void GlBufferStorage(uint target, nint size, void* data, uint flags);
    private delegate void* GlMapBufferRange(uint target, nint offset, nint length, uint access);
    private delegate byte GlUnmapBuffer(uint target);
    private delegate nint GlFenceSync(uint condition, uint flags);
    private delegate uint GlClientWaitSync(nint sync, uint flags, ulong timeout);
    private delegate void GlDeleteSync(nint sync);

    // ── Function pointers ─────────────────────────────────────────────────
    private GlViewport _glViewport = null!;
    private GlClearColor _glClearColor = null!;
    private GlClear _glClear = null!;
    private GlGenTextures _glGenTextures = null!;
    private GlDeleteTextures _glDeleteTextures = null!;
    private GlBindTexture _glBindTexture = null!;
    private GlTexImage2D _glTexImage2D = null!;
    private GlTexSubImage2D _glTexSubImage2D = null!;
    private GlTexParameteri _glTexParameteri = null!;
    private GlCreateShader _glCreateShader = null!;
    private GlShaderSource _glShaderSource = null!;
    private GlCompileShader _glCompileShader = null!;
    private GlGetShaderiv _glGetShaderiv = null!;
    private GlGetShaderInfoLog _glGetShaderInfoLog = null!;
    private GlCreateProgram _glCreateProgram = null!;
    private GlAttachShader _glAttachShader = null!;
    private GlLinkProgram _glLinkProgram = null!;
    private GlGetProgramiv _glGetProgramiv = null!;
    private GlGetProgramInfoLog _glGetProgramInfoLog = null!;
    private GlUseProgram _glUseProgram = null!;
    private GlDeleteShader _glDeleteShader = null!;
    private GlDeleteProgram _glDeleteProgram = null!;
    private GlGetUniformLocation _glGetUniformLocation = null!;
    private GlUniform1i _glUniform1i = null!;
    private GlGenVertexArrays _glGenVertexArrays = null!;
    private GlDeleteVertexArrays _glDeleteVertexArrays = null!;
    private GlBindVertexArray _glBindVertexArray = null!;
    private GlGenBuffers _glGenBuffers = null!;
    private GlDeleteBuffers _glDeleteBuffers = null!;
    private GlBindBuffer _glBindBuffer = null!;
    private GlBufferData _glBufferData = null!;
    private GlEnableVertexAttribArray _glEnableVertexAttribArray = null!;
    private GlVertexAttribPointer _glVertexAttribPointer = null!;
    private GlDrawArrays _glDrawArrays = null!;
    private GlBindFramebuffer _glBindFramebuffer = null!;
    private GlPixelStorei _glPixelStorei = null!;
    private GlActiveTexture _glActiveTexture = null!;
    private GlEnable _glEnable = null!;
    private GlDisable _glDisable = null!;
    private GlBlendFunc _glBlendFunc = null!;
    private GlGenFramebuffers        _glGenFramebuffers        = null!;
    private GlDeleteFramebuffers     _glDeleteFramebuffers     = null!;
    private GlFramebufferTexture2D   _glFramebufferTexture2D   = null!;
    private GlCheckFramebufferStatus _glCheckFramebufferStatus = null!;
    private GlUniform2f _glUniform2f = null!;
    private GlUniform4f _glUniform4f = null!;
    private GlBufferStorage? _glBufferStorage;
    private GlMapBufferRange? _glMapBufferRange;
    private GlUnmapBuffer? _glUnmapBuffer;
    private GlFenceSync? _glFenceSync;
    private GlClientWaitSync? _glClientWaitSync;
    private GlDeleteSync? _glDeleteSync;

    // ── Programs ──────────────────────────────────────────────────────────
    private uint _programRgba;
    private uint _programNv12;
    private uint _programI420;
    private uint _programI422P10;
    private uint _programUyvy422;
    private uint _programP010;
    private uint _programYuv444p;
    private uint _programGray8;
    private uint _programFbo;
    private uint _programBicubic;
    private uint _currentProgram;

    // ── Textures (Y/single, U/UV, V) ──────────────────────────────────────
    private uint _texY, _texU, _texV;
    private int  _texWidth, _texHeight;

    // ── Geometry ──────────────────────────────────────────────────────────
    private uint _vao, _vbo;

    // ── State ─────────────────────────────────────────────────────────────
    private bool        _initialised;
    private bool        _disposed;
    private PixelFormat _lastFormat;
    private int         _lastW, _lastH;  // last uploaded frame dimensions

    // YUV hints: -1 = auto-detect from resolution
    private int _yuvColorMatrix  = -1;
    private int _yuvLimitedRange = 0;

    // FBO for bicubic/nearest scaling
    private uint _fboGeneral;
    private uint _fboGeneralTexture;
    private int  _fboGeneralWidth;
    private int  _fboGeneralHeight;

    // Scaling filter — bicubic by default for broadcast-monitoring quality.
    private volatile int _scalingFilter = (int)ScalingFilter.Bicubic;
    // §8.6 — persistent mapped PBO ring for non-blocking texture uploads.
    private bool _persistentPboEnabled;
    private uint[]? _persistentPboIds;
    private nint[]? _persistentPboMappings;
    private nint[]? _persistentPboFences;
    private int _persistentPboNextSlot;

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

    /// <summary>
    /// Scaling filter applied during final presentation.
    /// Defaults to <see cref="ScalingFilter.Bicubic"/> (Catmull-Rom via FBO) for sharp edges.
    /// Thread-safe via a volatile backing field; the render thread reads on each frame.
    /// </summary>
    public ScalingFilter ScalingFilter
    {
        get => (ScalingFilter)_scalingFilter;
        set => _scalingFilter = (int)value;
    }

    /// <summary>Overrides YUV color matrix and range used by GPU shaders (legacy boolean API).</summary>
    public void SetYuvHints(bool bt709, bool limitedRange)
    {
        _yuvColorMatrix  = bt709        ? 1 : 0;
        _yuvLimitedRange = limitedRange ? 1 : 0;
    }

    /// <summary>Overrides YUV color matrix and range used by GPU shaders.</summary>
    public void SetYuvHints(YuvColorMatrix matrix, bool limitedRange)
    {
        _yuvColorMatrix  = YuvAutoPolicy.ToShaderValue(matrix);
        _yuvLimitedRange = limitedRange ? 1 : 0;
    }

    public void ResetYuvHintsToAuto() { _yuvColorMatrix = -1; _yuvLimitedRange = 0; }

    public void Initialise(GlInterface gl)
    {
        if (_initialised) return;

        _glViewport               = LoadGL<GlViewport>(gl, "glViewport");
        _glClearColor             = LoadGL<GlClearColor>(gl, "glClearColor");
        _glClear                  = LoadGL<GlClear>(gl, "glClear");
        _glGenTextures            = LoadGL<GlGenTextures>(gl, "glGenTextures");
        _glDeleteTextures         = LoadGL<GlDeleteTextures>(gl, "glDeleteTextures");
        _glBindTexture            = LoadGL<GlBindTexture>(gl, "glBindTexture");
        _glTexImage2D             = LoadGL<GlTexImage2D>(gl, "glTexImage2D");
        _glTexSubImage2D          = LoadGL<GlTexSubImage2D>(gl, "glTexSubImage2D");
        _glTexParameteri          = LoadGL<GlTexParameteri>(gl, "glTexParameteri");
        _glCreateShader           = LoadGL<GlCreateShader>(gl, "glCreateShader");
        _glShaderSource           = LoadGL<GlShaderSource>(gl, "glShaderSource");
        _glCompileShader          = LoadGL<GlCompileShader>(gl, "glCompileShader");
        _glGetShaderiv            = LoadGL<GlGetShaderiv>(gl, "glGetShaderiv");
        _glGetShaderInfoLog       = LoadGL<GlGetShaderInfoLog>(gl, "glGetShaderInfoLog");
        _glCreateProgram          = LoadGL<GlCreateProgram>(gl, "glCreateProgram");
        _glAttachShader           = LoadGL<GlAttachShader>(gl, "glAttachShader");
        _glLinkProgram            = LoadGL<GlLinkProgram>(gl, "glLinkProgram");
        _glGetProgramiv           = LoadGL<GlGetProgramiv>(gl, "glGetProgramiv");
        _glGetProgramInfoLog      = LoadGL<GlGetProgramInfoLog>(gl, "glGetProgramInfoLog");
        _glUseProgram             = LoadGL<GlUseProgram>(gl, "glUseProgram");
        _glDeleteShader           = LoadGL<GlDeleteShader>(gl, "glDeleteShader");
        _glDeleteProgram          = LoadGL<GlDeleteProgram>(gl, "glDeleteProgram");
        _glGetUniformLocation     = LoadGL<GlGetUniformLocation>(gl, "glGetUniformLocation");
        _glUniform1i              = LoadGL<GlUniform1i>(gl, "glUniform1i");
        _glGenVertexArrays        = LoadGL<GlGenVertexArrays>(gl, "glGenVertexArrays");
        _glDeleteVertexArrays     = LoadGL<GlDeleteVertexArrays>(gl, "glDeleteVertexArrays");
        _glBindVertexArray        = LoadGL<GlBindVertexArray>(gl, "glBindVertexArray");
        _glGenBuffers             = LoadGL<GlGenBuffers>(gl, "glGenBuffers");
        _glDeleteBuffers          = LoadGL<GlDeleteBuffers>(gl, "glDeleteBuffers");
        _glBindBuffer             = LoadGL<GlBindBuffer>(gl, "glBindBuffer");
        _glBufferData             = LoadGL<GlBufferData>(gl, "glBufferData");
        _glEnableVertexAttribArray= LoadGL<GlEnableVertexAttribArray>(gl, "glEnableVertexAttribArray");
        _glVertexAttribPointer    = LoadGL<GlVertexAttribPointer>(gl, "glVertexAttribPointer");
        _glDrawArrays             = LoadGL<GlDrawArrays>(gl, "glDrawArrays");
        _glBindFramebuffer        = LoadGL<GlBindFramebuffer>(gl, "glBindFramebuffer");
        _glPixelStorei            = LoadGL<GlPixelStorei>(gl, "glPixelStorei");
        _glActiveTexture          = LoadGL<GlActiveTexture>(gl, "glActiveTexture");
        _glEnable                 = LoadGL<GlEnable>(gl, "glEnable");
        _glDisable                = LoadGL<GlDisable>(gl, "glDisable");
        _glBlendFunc              = LoadGL<GlBlendFunc>(gl, "glBlendFunc");
        _glGenFramebuffers        = LoadGL<GlGenFramebuffers>(gl, "glGenFramebuffers");
        _glDeleteFramebuffers     = LoadGL<GlDeleteFramebuffers>(gl, "glDeleteFramebuffers");
        _glFramebufferTexture2D   = LoadGL<GlFramebufferTexture2D>(gl, "glFramebufferTexture2D");
        _glCheckFramebufferStatus = LoadGL<GlCheckFramebufferStatus>(gl, "glCheckFramebufferStatus");
        _glUniform2f              = LoadGL<GlUniform2f>(gl, "glUniform2f");
        _glUniform4f              = LoadGL<GlUniform4f>(gl, "glUniform4f");
        _glBufferStorage          = TryLoadGL<GlBufferStorage>(gl, "glBufferStorage");
        _glMapBufferRange         = TryLoadGL<GlMapBufferRange>(gl, "glMapBufferRange");
        _glUnmapBuffer            = TryLoadGL<GlUnmapBuffer>(gl, "glUnmapBuffer");
        _glFenceSync              = TryLoadGL<GlFenceSync>(gl, "glFenceSync");
        _glClientWaitSync         = TryLoadGL<GlClientWaitSync>(gl, "glClientWaitSync");
        _glDeleteSync             = TryLoadGL<GlDeleteSync>(gl, "glDeleteSync");

        _programRgba    = BuildProgram(GlShaderSources.FragmentPassthrough);
        _programNv12    = BuildProgram(GlShaderSources.FragmentNv12);
        _programI420    = BuildProgram(GlShaderSources.FragmentI420);
        _programI422P10 = BuildProgram(GlShaderSources.FragmentI422P10);
        _programUyvy422 = BuildProgram(GlShaderSources.FragmentUyvy422);
        _programP010    = BuildProgram(GlShaderSources.FragmentP010);
        _programYuv444p = BuildProgram(GlShaderSources.FragmentYuv444p);
        _programGray8   = BuildProgram(GlShaderSources.FragmentGray8);
        _programFbo     = BuildProgram(GlShaderSources.FragmentPassthroughFbo);
        _programBicubic = BuildProgram(GlShaderSources.FragmentBicubicBlit);

        BindSamplerSingle(_programRgba,    "uTexture\0"u8);
        BindSamplerSingle(_programUyvy422, "uTexUYVY\0"u8);  // fixed: shader uses uTexUYVY
        BindSamplers2(_programNv12,    "uTexY\0"u8, "uTexUV\0"u8);
        BindSamplers2(_programP010,    "uTexY\0"u8, "uTexUV\0"u8);
        BindSamplers3(_programI420,    "uTexY\0"u8, "uTexU\0"u8, "uTexV\0"u8);
        BindSamplers3(_programI422P10, "uTexY\0"u8, "uTexU\0"u8, "uTexV\0"u8);
        BindSamplers3(_programYuv444p, "uTexY\0"u8, "uTexU\0"u8, "uTexV\0"u8);
        BindSamplerSingle(_programGray8,   "uTexY\0"u8);
        BindSamplerSingle(_programFbo,     "uTexture\0"u8);
        BindSamplerSingle(_programBicubic, "uTexture\0"u8);

        var quad = GlShaderSources.FullscreenQuadVerts;
        fixed (uint* p = &_vao) _glGenVertexArrays(1, p);
        fixed (uint* p = &_vbo) _glGenBuffers(1, p);
        _glBindVertexArray(_vao);
        _glBindBuffer(GL_ARRAY_BUFFER, _vbo);
        fixed (float* p = quad)
            _glBufferData(GL_ARRAY_BUFFER, quad.Length * sizeof(float), p, GL_STATIC_DRAW);
        _glEnableVertexAttribArray(0);
        _glVertexAttribPointer(0, 2, GL_FLOAT, (byte)GL_FALSE, 4 * sizeof(float), (void*)0);
        _glEnableVertexAttribArray(1);
        _glVertexAttribPointer(1, 2, GL_FLOAT, (byte)GL_FALSE, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        _glBindVertexArray(0);

        fixed (uint* p = &_texY) _glGenTextures(1, p);
        fixed (uint* p = &_texU) _glGenTextures(1, p);
        fixed (uint* p = &_texV) _glGenTextures(1, p);
        foreach (var tex in new[] { _texY, _texU, _texV })
        {
            _glBindTexture(GL_TEXTURE_2D, tex);
            _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
            _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
            _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S,     (int)GL_CLAMP_TO_EDGE);
            _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T,     (int)GL_CLAMP_TO_EDGE);
        }

        _glClearColor(0f, 0f, 0f, 1f);
        _glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        _glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
        InitialisePersistentPboUpload();
        _initialised = true;
    }

    // ── FBO helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// If <see cref="ScalingFilter"/> is not <see cref="ScalingFilter.Bilinear"/>, ensures the
    /// general FBO exists at native video resolution, binds it, and sets the viewport.
    /// Otherwise binds the Avalonia framebuffer directly at viewport size.
    /// Returns <c>true</c> if FBO was bound; caller must then call <see cref="BlitFboToScreen"/>.
    /// </summary>
    private bool BeginFboIfNeeded(int w, int h, int fallbackFb, int fallbackVpW, int fallbackVpH)
    {
        if ((ScalingFilter)_scalingFilter == ScalingFilter.Bilinear)
        {
            _glBindFramebuffer(GL_FRAMEBUFFER, (uint)fallbackFb);
            _glViewport(0, 0, fallbackVpW, fallbackVpH);
            _glClear(GL_COLOR_BUFFER_BIT);
            var target = ComputeAspectViewport(fallbackVpW, fallbackVpH, w, h);
            _glViewport(target.X, target.Y, target.Width, target.Height);
            return false;
        }
        EnsureFboGeneral(w, h);
        _glBindFramebuffer(GL_FRAMEBUFFER, _fboGeneral);
        _glViewport(0, 0, w, h);
        return true;
    }

    /// <summary>
    /// Binds the Avalonia framebuffer, restores the viewport, and blits the general FBO texture
    /// using the shader selected by <see cref="ScalingFilter"/>.
    /// </summary>
    private void BlitFboToScreen(int fb, int vpW, int vpH, int videoW, int videoH)
    {
        _glBindFramebuffer(GL_FRAMEBUFFER, (uint)fb);
        _glViewport(0, 0, vpW, vpH);
        _glClear(GL_COLOR_BUFFER_BIT);
        var target = ComputeAspectViewport(vpW, vpH, videoW, videoH);
        _glViewport(target.X, target.Y, target.Width, target.Height);
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

    private static (int X, int Y, int Width, int Height) ComputeAspectViewport(
        int viewportWidth,
        int viewportHeight,
        int videoWidth,
        int videoHeight)
    {
        int vpW = viewportWidth > 0 ? viewportWidth : 1;
        int vpH = viewportHeight > 0 ? viewportHeight : 1;
        if (videoWidth <= 0 || videoHeight <= 0)
            return (0, 0, vpW, vpH);

        double videoAspect = (double)videoWidth / videoHeight;
        double viewportAspect = (double)vpW / vpH;
        if (viewportAspect > videoAspect)
        {
            int drawH = vpH;
            int drawW = Math.Min(vpW, Math.Max(1, (int)Math.Round(drawH * videoAspect)));
            int drawX = Math.Max(0, (vpW - drawW) / 2);
            return (drawX, 0, drawW, drawH);
        }

        int height = Math.Min(vpH, Math.Max(1, (int)Math.Round(vpW / videoAspect)));
        int y = Math.Max(0, (vpH - height) / 2);
        return (0, y, vpW, height);
    }

    /// <summary>Creates (or recreates on resolution change) a general-purpose RGBA8 FBO.</summary>
    private void EnsureFboGeneral(int w, int h)
    {
        if (_fboGeneralWidth == w && _fboGeneralHeight == h && _fboGeneral != 0)
            return;

        if (_fboGeneral != 0)
        { fixed (uint* p = &_fboGeneral) _glDeleteFramebuffers(1, p); _fboGeneral = 0; }
        if (_fboGeneralTexture != 0)
        { fixed (uint* p = &_fboGeneralTexture) _glDeleteTextures(1, p); _fboGeneralTexture = 0; }

        fixed (uint* pTex = &_fboGeneralTexture) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _fboGeneralTexture);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        // GL_NEAREST: bicubic uses texelFetch (bypasses filter), avoids double-bilinear blending.
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S,     (int)GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T,     (int)GL_CLAMP_TO_EDGE);

        fixed (uint* pFbo = &_fboGeneral) _glGenFramebuffers(1, pFbo);
        _glBindFramebuffer(GL_FRAMEBUFFER, _fboGeneral);
        _glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _fboGeneralTexture, 0);
        uint status = _glCheckFramebufferStatus(GL_FRAMEBUFFER);
        if (status != GL_FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException($"General FBO incomplete: 0x{status:X}");
        _glBindFramebuffer(GL_FRAMEBUFFER, 0);

        _fboGeneralWidth  = w;
        _fboGeneralHeight = h;
    }

    public void DrawBlack(int framebuffer, int width, int height)
    {
        if (!_initialised) return;
        _glBindFramebuffer(GL_FRAMEBUFFER, (uint)framebuffer);
        _glViewport(0, 0, width, height);
        _glClear(GL_COLOR_BUFFER_BIT);
    }

    public void UploadAndDraw(in VideoFrame frame, int framebuffer, int vpW, int vpH)
    {
        if (!_initialised) return;
        _glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        _glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);

        using var pin = frame.Data.Pin();
        byte* data = (byte*)pin.Pointer;
        int   w    = frame.Width, h = frame.Height;
        _lastW = w; _lastH = h;

        // ── Upload phase (framebuffer-independent texture ops) ───────────────
        switch (frame.PixelFormat)
        {
            case PixelFormat.Rgba32:    UploadRgba(data, w, h, GL_RGBA);  break;
            case PixelFormat.Bgra32:    UploadRgba(data, w, h, GL_BGRA);  break;
            case PixelFormat.Nv12:      UploadNv12(data, w, h);           break;
            case PixelFormat.Yuv420p:   UploadI420(data, w, h);           break;
            case PixelFormat.Yuv422p10: UploadI422P10(data, w, h);        break;
            case PixelFormat.Uyvy422:   UploadUyvy422(data, w, h);        break;
            case PixelFormat.P010:      UploadP010(data, w, h);           break;
            case PixelFormat.Yuv444p:   UploadYuv444p(data, w, h);        break;
            case PixelFormat.Gray8:     UploadGray8(data, w, h);          break;
            default:
                _glBindFramebuffer(GL_FRAMEBUFFER, (uint)framebuffer);
                _glViewport(0, 0, vpW, vpH);
                _glClear(GL_COLOR_BUFFER_BIT);
                return;
        }

        // ── Draw phase — bind FBO or direct framebuffer, then blit ──────────
        bool useFbo = BeginFboIfNeeded(w, h, framebuffer, vpW, vpH);
        switch (frame.PixelFormat)
        {
            case PixelFormat.Rgba32: case PixelFormat.Bgra32:
                DrawWith(_programRgba);             break;
            case PixelFormat.Nv12:
                DrawYuv(_programNv12, h);           break;
            case PixelFormat.Yuv420p:
                DrawYuv(_programI420, h);           break;
            case PixelFormat.Yuv422p10:
                DrawYuv(_programI422P10, h);        break;
            case PixelFormat.Uyvy422:
                DrawUyvy422(_programUyvy422, w, h); break;
            case PixelFormat.P010:
                DrawYuv(_programP010, h);           break;
            case PixelFormat.Yuv444p:
                DrawYuv(_programYuv444p, h);        break;
            case PixelFormat.Gray8:
                DrawWith(_programGray8);            break;
        }
        if (useFbo) BlitFboToScreen(framebuffer, vpW, vpH, w, h);

        _lastFormat = frame.PixelFormat;
    }

    public void DrawLastTexture(int framebuffer, int vpW, int vpH)
    {
        if (!_initialised || _texY == 0) return;

        // ── Re-bind textures (no framebuffer dependency) ──────────────────
        switch (_lastFormat)
        {
            case PixelFormat.Rgba32: case PixelFormat.Bgra32: case PixelFormat.Uyvy422: case PixelFormat.Gray8:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY); break;
            case PixelFormat.Nv12: case PixelFormat.P010:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU); break;
            case PixelFormat.Yuv420p: case PixelFormat.Yuv422p10: case PixelFormat.Yuv444p:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
                _glActiveTexture(GL_TEXTURE2); _glBindTexture(GL_TEXTURE_2D, _texV); break;
        }

        // ── Draw with optional FBO for bicubic/nearest scaling ──────────
        bool useFbo = BeginFboIfNeeded(_lastW, _lastH, framebuffer, vpW, vpH);
        switch (_lastFormat)
        {
            case PixelFormat.Rgba32: case PixelFormat.Bgra32:
                DrawWith(_programRgba);                       break;
            case PixelFormat.Nv12:
                DrawWith(_programNv12);                       break;
            case PixelFormat.Yuv420p:
                DrawWith(_programI420);                       break;
            case PixelFormat.Yuv422p10:
                DrawWith(_programI422P10);                    break;
            case PixelFormat.Uyvy422:
                DrawUyvy422(_programUyvy422, _lastW, _lastH); break;
            case PixelFormat.P010:
                DrawWith(_programP010);                       break;
            case PixelFormat.Yuv444p:
                DrawWith(_programYuv444p);                    break;
            case PixelFormat.Gray8:
                DrawWith(_programGray8);                      break;
            default:
                DrawWith(_currentProgram != 0 ? _currentProgram : _programRgba); break;
        }
        if (useFbo) BlitFboToScreen(framebuffer, vpW, vpH, _lastW, _lastH);
    }

    /// <summary>
    /// Draws the diagnostics HUD over the already-rendered video frame.
    /// Must be called after <see cref="UploadAndDraw"/>/<see cref="DrawLastTexture"/>
    /// and before returning from the render tick.
    /// </summary>
    public void DrawHud(string[] lines, int framebuffer, int vpW, int vpH)
    {
        if (lines.Length == 0 || vpW <= 0 || vpH <= 0) return;

        EnsureHudInitialised();

        const int glyphW = BitmapFont.GlyphWidth;
        const int glyphH = BitmapFont.GlyphHeight;
        const int atlasW = 128;
        const int atlasH = 128;
        const int colCount = 16;
        const int padding = 4;
        int scale = Math.Max(1, vpH / 500);

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
                if (idx < 0 || idx >= BitmapFont.GlyphCount) idx = 0;

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

                verts[vi++] = x0; verts[vi++] = y0; verts[vi++] = u0; verts[vi++] = v0;
                verts[vi++] = x1; verts[vi++] = y0; verts[vi++] = u1; verts[vi++] = v0;
                verts[vi++] = x1; verts[vi++] = y1; verts[vi++] = u1; verts[vi++] = v1;
                verts[vi++] = x0; verts[vi++] = y0; verts[vi++] = u0; verts[vi++] = v0;
                verts[vi++] = x1; verts[vi++] = y1; verts[vi++] = u1; verts[vi++] = v1;
                verts[vi++] = x0; verts[vi++] = y1; verts[vi++] = u0; verts[vi++] = v1;

                cursorX += glyphW * scale;
            }
            cursorY += (glyphH + 2) * scale;
        }

        int vertCount = vi / 4;
        if (vertCount == 0) return;

        _glBindBuffer(GL_ARRAY_BUFFER, _hudVbo);
        fixed (float* pData = verts)
            _glBufferData(GL_ARRAY_BUFFER, (nint)(vi * sizeof(float)), pData, GL_DYNAMIC_DRAW);

        _glBindFramebuffer(GL_FRAMEBUFFER, (uint)framebuffer);
        _glViewport(0, 0, vpW, vpH);
        _glEnable(GL_BLEND);
        _glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);

        _glUseProgram(_hudProgram);
        _glUniform2f(_hudUScreenSize, vpW, vpH);
        _glUniform4f(_hudUColor, 1f, 1f, 1f, 1f);
        _glUniform4f(_hudUBgColor, 0f, 0f, 0f, 0.65f);

        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _hudFontTexture);

        _glBindVertexArray(_hudVao);
        _glDrawArrays(GL_TRIANGLES, 0, vertCount);
        _glBindVertexArray(0);

        _glDisable(GL_BLEND);
        _glViewport(0, 0, vpW, vpH);
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
        {
            int l = _glGetUniformLocation(_hudProgram, name);
            _glUniform1i(l, 0);
        }
        fixed (byte* name = "uScreenSize\0"u8)
            _hudUScreenSize = _glGetUniformLocation(_hudProgram, name);
        fixed (byte* name = "uColor\0"u8)
            _hudUColor = _glGetUniformLocation(_hudProgram, name);
        fixed (byte* name = "uBgColor\0"u8)
            _hudUBgColor = _glGetUniformLocation(_hudProgram, name);

        var atlas = BitmapFont.BuildAtlasRgba(out int aw, out int ah, out _);
        fixed (uint* pTex = &_hudFontTexture) _glGenTextures(1, pTex);
        _glBindTexture(GL_TEXTURE_2D, _hudFontTexture);
        fixed (byte* pData = atlas)
            _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, aw, ah, 0, GL_RGBA, GL_UNSIGNED_BYTE, pData);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);

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

    // ── Upload helpers ────────────────────────────────────────────────────

    private void UploadTexImage2D(
        int internalFormat, int width, int height, uint format, uint type, void* source, int sourceBytes)
    {
        if (TryUploadWithPersistentPbo(
                width, height, format, type, source, sourceBytes,
                static (r, w, h, fmt, ty) => r._glTexImage2D(GL_TEXTURE_2D, 0, 0, w, h, 0, fmt, ty, null),
                internalFormat))
            return;
        _glTexImage2D(GL_TEXTURE_2D, 0, internalFormat, width, height, 0, format, type, source);
    }

    private void UploadTexSubImage2D(int width, int height, uint format, uint type, void* source, int sourceBytes)
    {
        if (TryUploadWithPersistentPbo(
                width, height, format, type, source, sourceBytes,
                static (r, w, h, fmt, ty) => r._glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, fmt, ty, null),
                0))
            return;
        _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, width, height, format, type, source);
    }

    private delegate void PboUploadGlCall(AvaloniaGlRenderer renderer, int width, int height, uint format, uint type);

    private bool TryUploadWithPersistentPbo(
        int width, int height, uint format, uint type, void* source, int sourceBytes,
        PboUploadGlCall uploadCall, int internalFormat)
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
        if (internalFormat == 0)
            uploadCall(this, width, height, format, type);
        else
            _glTexImage2D(GL_TEXTURE_2D, 0, internalFormat, width, height, 0, format, type, null);
        _glBindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);
        _persistentPboFences[slot] = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        return true;
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

    private void UploadRgba(byte* data, int w, int h, uint fmt)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _texY);
        if (w == _texWidth && h == _texHeight)
            UploadTexSubImage2D(w, h, fmt, GL_UNSIGNED_BYTE, data, w * h * 4);
        else
        {
            UploadTexImage2D((int)GL_RGBA8, w, h, fmt, GL_UNSIGNED_BYTE, data, w * h * 4);
            _texWidth = w; _texHeight = h;
        }
    }

    private void UploadNv12(byte* data, int w, int h)
    {
        int ySize = w * h;
        int uvW = Math.Max(1, w / 2);
        int uvH = Math.Max(1, h / 2);
        int uvSize = uvW * uvH * 2;
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _texY);
        UploadTexImage2D((int)GL_R8, w, h, GL_RED, GL_UNSIGNED_BYTE, data, ySize);
        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _texU);
        UploadTexImage2D((int)GL_RG8, uvW, uvH, GL_RG, GL_UNSIGNED_BYTE, data + ySize, uvSize);
    }

    private void UploadI420(byte* data, int w, int h)
    {
        int cw = Math.Max(1, w / 2);
        int ch = Math.Max(1, h / 2);
        int ySize = w * h;
        int uvSize = cw * ch;
        _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
        UploadTexImage2D((int)GL_R8, w, h, GL_RED, GL_UNSIGNED_BYTE, data, ySize);
        _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
        UploadTexImage2D((int)GL_R8, cw, ch, GL_RED, GL_UNSIGNED_BYTE, data + ySize, uvSize);
        _glActiveTexture(GL_TEXTURE2); _glBindTexture(GL_TEXTURE_2D, _texV);
        UploadTexImage2D((int)GL_R8, cw, ch, GL_RED, GL_UNSIGNED_BYTE, data + ySize + uvSize, uvSize);
    }

    private void UploadI422P10(byte* data, int w, int h)
    {
        int yBytes = w * h * 2, uvBytes = (w / 2) * h * 2;
        void SetNearest(uint tex)
        {
            _glBindTexture(GL_TEXTURE_2D, tex);
            _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
            _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
        }
        _glActiveTexture(GL_TEXTURE0); SetNearest(_texY);
        UploadTexImage2D((int)GL_R16UI, w, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, data, yBytes);
        _glActiveTexture(GL_TEXTURE1); SetNearest(_texU);
        UploadTexImage2D((int)GL_R16UI, Math.Max(1, w / 2), h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, data + yBytes, uvBytes);
        _glActiveTexture(GL_TEXTURE2); SetNearest(_texV);
        UploadTexImage2D((int)GL_R16UI, Math.Max(1, w / 2), h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, data + yBytes + uvBytes, uvBytes);
    }

    private void UploadUyvy422(byte* data, int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
        int halfW = Math.Max(1, w / 2);
        UploadTexImage2D((int)GL_RGBA8, halfW, h, GL_RGBA, GL_UNSIGNED_BYTE, data, w * h * 2);
    }

    private void UploadP010(byte* data, int w, int h)
    {
        // Y plane: w×h × uint16 (R16UI); UV plane: (w/2)×(h/2) × uvec2 (RG16UI)
        int yBytes = w * h * 2;
        void SetNearest(uint tex)
        {
            _glBindTexture(GL_TEXTURE_2D, tex);
            _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_NEAREST);
            _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_NEAREST);
        }
        _glActiveTexture(GL_TEXTURE0); SetNearest(_texY);
        UploadTexImage2D((int)GL_R16UI, w, h, GL_RED_INTEGER, GL_UNSIGNED_SHORT, data, yBytes);
        _glActiveTexture(GL_TEXTURE1); SetNearest(_texU);
        int uvW = Math.Max(1, w / 2);
        int uvH = Math.Max(1, h / 2);
        UploadTexImage2D((int)GL_RG16UI, uvW, uvH, GL_RG_INTEGER, GL_UNSIGNED_SHORT, data + yBytes, uvW * uvH * 4);
    }

    private void UploadYuv444p(byte* data, int w, int h)
    {
        // 3 full-resolution R8 planes: Y, U, V each w×h
        int planeSize = w * h;
        _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
        UploadTexImage2D((int)GL_R8, w, h, GL_RED, GL_UNSIGNED_BYTE, data, planeSize);
        _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
        UploadTexImage2D((int)GL_R8, w, h, GL_RED, GL_UNSIGNED_BYTE, data + planeSize, planeSize);
        _glActiveTexture(GL_TEXTURE2); _glBindTexture(GL_TEXTURE_2D, _texV);
        UploadTexImage2D((int)GL_R8, w, h, GL_RED, GL_UNSIGNED_BYTE, data + planeSize * 2, planeSize);
    }

    private void UploadGray8(byte* data, int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
        UploadTexImage2D((int)GL_R8, w, h, GL_RED, GL_UNSIGNED_BYTE, data, w * h);
    }

    // ── Draw helpers ──────────────────────────────────────────────────────

    private void DrawWith(uint prog)
    {
        _currentProgram = prog;
        _glClear(GL_COLOR_BUFFER_BIT);
        _glDisable(GL_BLEND);
        _glUseProgram(prog);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    private void DrawYuv(uint prog, int height)
    {
        // -1 = auto: pick BT.601 for SD (≤720 lines) or BT.709 for HD
        int matrix = _yuvColorMatrix >= 0
            ? _yuvColorMatrix
            : YuvAutoPolicy.ToShaderValue(YuvAutoPolicy.ResolveMatrix(YuvColorMatrix.Auto, 0, height));
        _glUseProgram(prog);
        SetUniform1i(prog, "uColorMatrix\0"u8,  matrix);
        SetUniform1i(prog, "uLimitedRange\0"u8, _yuvLimitedRange);
        DrawWith(prog);
    }

    private void DrawUyvy422(uint prog, int videoWidth, int videoHeight)
    {
        int matrix = _yuvColorMatrix >= 0
            ? _yuvColorMatrix
            : YuvAutoPolicy.ToShaderValue(YuvAutoPolicy.ResolveMatrix(YuvColorMatrix.Auto, 0, videoHeight));
        _glUseProgram(prog);
        SetUniform1i(prog, "uVideoWidth\0"u8,   videoWidth);
        SetUniform1i(prog, "uColorMatrix\0"u8,  matrix);
        SetUniform1i(prog, "uLimitedRange\0"u8, _yuvLimitedRange);
        DrawWith(prog);
    }

    // ── Shader helpers ────────────────────────────────────────────────────

    private uint BuildProgram(string frag)
    {
        uint vs = CompileShader(GL_VERTEX_SHADER,   GlShaderSources.VertexPassthrough);
        uint fs = CompileShader(GL_FRAGMENT_SHADER, frag);
        uint p  = _glCreateProgram();
        _glAttachShader(p, vs); _glAttachShader(p, fs);
        _glLinkProgram(p); CheckProgram(p);
        _glDeleteShader(vs); _glDeleteShader(fs);
        return p;
    }

    private void BindSamplerSingle(uint prog, ReadOnlySpan<byte> n0)
    {
        _glUseProgram(prog);
        fixed (byte* p = n0) { int l = _glGetUniformLocation(prog, p); if (l >= 0) _glUniform1i(l, 0); }
    }

    private void BindSamplers2(uint prog, ReadOnlySpan<byte> n0, ReadOnlySpan<byte> n1)
    {
        _glUseProgram(prog);
        fixed (byte* p = n0) { int l = _glGetUniformLocation(prog, p); if (l >= 0) _glUniform1i(l, 0); }
        fixed (byte* p = n1) { int l = _glGetUniformLocation(prog, p); if (l >= 0) _glUniform1i(l, 1); }
    }

    private void BindSamplers3(uint prog, ReadOnlySpan<byte> n0, ReadOnlySpan<byte> n1, ReadOnlySpan<byte> n2)
    {
        BindSamplers2(prog, n0, n1);
        fixed (byte* p = n2) { int l = _glGetUniformLocation(prog, p); if (l >= 0) _glUniform1i(l, 2); }
    }

    private void SetUniform1i(uint prog, ReadOnlySpan<byte> name, int v)
    {
        fixed (byte* p = name) { int l = _glGetUniformLocation(prog, p); if (l >= 0) _glUniform1i(l, v); }
    }

    private uint CompileShader(uint type, string src)
    {
        uint sh = _glCreateShader(type);
        byte[] b = Encoding.UTF8.GetBytes(src + '\0');
        fixed (byte* p = b) { byte* pp = p; _glShaderSource(sh, 1, &pp, null); }
        _glCompileShader(sh);
        int ok; _glGetShaderiv(sh, GL_COMPILE_STATUS, &ok);
        if (ok != 0) return sh;
        byte* log = stackalloc byte[1024]; int len;
        _glGetShaderInfoLog(sh, 1024, &len, log);
        throw new InvalidOperationException($"Shader compile error: {Encoding.UTF8.GetString(log, Math.Max(0, len))}");
    }

    private void CheckProgram(uint p)
    {
        int ok; _glGetProgramiv(p, GL_LINK_STATUS, &ok);
        if (ok != 0) return;
        byte* log = stackalloc byte[1024]; int len;
        _glGetProgramInfoLog(p, 1024, &len, log);
        throw new InvalidOperationException($"Program link error: {Encoding.UTF8.GetString(log, Math.Max(0, len))}");
    }

    private static T LoadGL<T>(GlInterface gl, string name) where T : Delegate
    {
        var ptr = gl.GetProcAddress(name);
        if (ptr == IntPtr.Zero) throw new InvalidOperationException($"Failed to load GL function: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static T? TryLoadGL<T>(GlInterface gl, string name) where T : Delegate
    {
        var ptr = gl.GetProcAddress(name);
        if (ptr == IntPtr.Zero)
            return null;
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_initialised) return;
        DisposePersistentPboUpload();

        void DelTex(ref uint t) { if (t == 0) return; fixed (uint* p = &t) _glDeleteTextures(1, p);     t = 0; }
        void DelBuf(ref uint b) { if (b == 0) return; fixed (uint* p = &b) _glDeleteBuffers(1, p);      b = 0; }
        void DelVao(ref uint v) { if (v == 0) return; fixed (uint* p = &v) _glDeleteVertexArrays(1, p); v = 0; }
        void DelPrg(ref uint p) { if (p == 0) return; _glDeleteProgram(p); p = 0; }
        void DelFbo(ref uint f) { if (f == 0) return; fixed (uint* p = &f) _glDeleteFramebuffers(1, p); f = 0; }

        DelTex(ref _texY); DelTex(ref _texU); DelTex(ref _texV);
        DelTex(ref _fboGeneralTexture);
        DelFbo(ref _fboGeneral);
        DelBuf(ref _vbo);  DelVao(ref _vao);
        DelPrg(ref _programRgba); DelPrg(ref _programNv12); DelPrg(ref _programI420);
        DelPrg(ref _programI422P10); DelPrg(ref _programUyvy422);
        DelPrg(ref _programP010); DelPrg(ref _programYuv444p); DelPrg(ref _programGray8);
        DelPrg(ref _programFbo); DelPrg(ref _programBicubic);
        DelPrg(ref _hudProgram);
        DelTex(ref _hudFontTexture);
        DelBuf(ref _hudVbo);
        DelVao(ref _hudVao);
    }
}
