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
    private const uint GL_FRAGMENT_SHADER   = 0x8B30;
    private const uint GL_VERTEX_SHADER     = 0x8B31;
    private const uint GL_COMPILE_STATUS    = 0x8B81;
    private const uint GL_LINK_STATUS       = 0x8B82;
    private const uint GL_COLOR_BUFFER_BIT  = 0x00004000;
    private const uint GL_FALSE             = 0;
    private const uint GL_FRAMEBUFFER       = 0x8D40;
    private const uint GL_BLEND             = 0x0BE2;
    private const uint GL_UNPACK_ALIGNMENT  = 0x0CF5;
    private const uint GL_UNPACK_ROW_LENGTH = 0x0CF2;
    private const uint GL_TEXTURE0          = 0x84C0;
    private const uint GL_TEXTURE1          = 0x84C1;
    private const uint GL_TEXTURE2          = 0x84C2;
    private const uint GL_RG16UI            = 0x823A;
    private const uint GL_RG_INTEGER        = 0x8228;

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
    private delegate void GlDisable(uint cap);

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
    private GlDisable _glDisable = null!;

    // ── Programs ──────────────────────────────────────────────────────────
    private uint _programRgba;
    private uint _programNv12;
    private uint _programI420;
    private uint _programI422P10;
    private uint _programUyvy422;
    private uint _programP010;
    private uint _programYuv444p;
    private uint _programGray8;
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
        _glDisable                = LoadGL<GlDisable>(gl, "glDisable");

        _programRgba    = BuildProgram(GlShaderSources.FragmentPassthrough);
        _programNv12    = BuildProgram(GlShaderSources.FragmentNv12);
        _programI420    = BuildProgram(GlShaderSources.FragmentI420);
        _programI422P10 = BuildProgram(GlShaderSources.FragmentI422P10);
        _programUyvy422 = BuildProgram(GlShaderSources.FragmentUyvy422);
        _programP010    = BuildProgram(GlShaderSources.FragmentP010);
        _programYuv444p = BuildProgram(GlShaderSources.FragmentYuv444p);
        _programGray8   = BuildProgram(GlShaderSources.FragmentGray8);

        BindSamplerSingle(_programRgba,    "uTexture\0"u8);
        BindSamplerSingle(_programUyvy422, "uTexUYVY\0"u8);  // fixed: shader uses uTexUYVY
        BindSamplers2(_programNv12,    "uTexY\0"u8, "uTexUV\0"u8);
        BindSamplers2(_programP010,    "uTexY\0"u8, "uTexUV\0"u8);
        BindSamplers3(_programI420,    "uTexY\0"u8, "uTexU\0"u8, "uTexV\0"u8);
        BindSamplers3(_programI422P10, "uTexY\0"u8, "uTexU\0"u8, "uTexV\0"u8);
        BindSamplers3(_programYuv444p, "uTexY\0"u8, "uTexU\0"u8, "uTexV\0"u8);
        BindSamplerSingle(_programGray8,   "uTexY\0"u8);

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
        _initialised = true;
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
        _glBindFramebuffer(GL_FRAMEBUFFER, (uint)framebuffer);
        _glViewport(0, 0, vpW, vpH);
        _glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        _glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);

        using var pin = frame.Data.Pin();
        byte* data = (byte*)pin.Pointer;
        int   w    = frame.Width, h = frame.Height;
        _lastW = w; _lastH = h;

        switch (frame.PixelFormat)
        {
            case PixelFormat.Rgba32:
                UploadRgba(data, w, h, GL_RGBA);   DrawWith(_programRgba);  break;
            case PixelFormat.Bgra32:
                UploadRgba(data, w, h, GL_BGRA);   DrawWith(_programRgba);  break;
            case PixelFormat.Nv12:
                UploadNv12(data, w, h);            DrawYuv(_programNv12, h); break;
            case PixelFormat.Yuv420p:
                UploadI420(data, w, h);            DrawYuv(_programI420, h); break;
            case PixelFormat.Yuv422p10:
                UploadI422P10(data, w, h);         DrawYuv(_programI422P10, h); break;
            case PixelFormat.Uyvy422:
                UploadUyvy422(data, w, h);         DrawUyvy422(_programUyvy422, w, h); break;
            case PixelFormat.P010:
                UploadP010(data, w, h);            DrawYuv(_programP010, h); break;
            case PixelFormat.Yuv444p:
                UploadYuv444p(data, w, h);         DrawYuv(_programYuv444p, h); break;
            case PixelFormat.Gray8:
                UploadGray8(data, w, h);           DrawWith(_programGray8); break;
            default:
                _glClear(GL_COLOR_BUFFER_BIT); return;
        }

        _lastFormat = frame.PixelFormat;
    }

    public void DrawLastTexture(int framebuffer, int vpW, int vpH)
    {
        if (!_initialised || _texY == 0) return;
        _glBindFramebuffer(GL_FRAMEBUFFER, (uint)framebuffer);
        _glViewport(0, 0, vpW, vpH);

        switch (_lastFormat)
        {
            case PixelFormat.Rgba32: case PixelFormat.Bgra32:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                DrawWith(_programRgba); break;
            case PixelFormat.Nv12:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
                DrawWith(_programNv12); break;
            case PixelFormat.Yuv420p:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
                _glActiveTexture(GL_TEXTURE2); _glBindTexture(GL_TEXTURE_2D, _texV);
                DrawWith(_programI420); break;
            case PixelFormat.Yuv422p10:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
                _glActiveTexture(GL_TEXTURE2); _glBindTexture(GL_TEXTURE_2D, _texV);
                DrawWith(_programI422P10); break;
            case PixelFormat.Uyvy422:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                DrawUyvy422(_programUyvy422, _lastW, _lastH); break;
            case PixelFormat.P010:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
                DrawWith(_programP010); break;
            case PixelFormat.Yuv444p:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
                _glActiveTexture(GL_TEXTURE2); _glBindTexture(GL_TEXTURE_2D, _texV);
                DrawWith(_programYuv444p); break;
            case PixelFormat.Gray8:
                _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
                DrawWith(_programGray8); break;
            default:
                DrawWith(_currentProgram != 0 ? _currentProgram : _programRgba); break;
        }
    }

    // ── Upload helpers ────────────────────────────────────────────────────

    private void UploadRgba(byte* data, int w, int h, uint fmt)
    {
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _texY);
        if (w == _texWidth && h == _texHeight)
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, fmt, GL_UNSIGNED_BYTE, data);
        else
        {
            _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, fmt, GL_UNSIGNED_BYTE, data);
            _texWidth = w; _texHeight = h;
        }
    }

    private void UploadNv12(byte* data, int w, int h)
    {
        int ySize = w * h;
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _texY);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, data);
        _glActiveTexture(GL_TEXTURE1);
        _glBindTexture(GL_TEXTURE_2D, _texU);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RG8, w / 2, h / 2, 0, GL_RG, GL_UNSIGNED_BYTE, data + ySize);
    }

    private void UploadI420(byte* data, int w, int h)
    {
        int ySize = w * h, uvSize = (w / 2) * (h / 2);
        _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, data);
        _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R8, w / 2, h / 2, 0, GL_RED, GL_UNSIGNED_BYTE, data + ySize);
        _glActiveTexture(GL_TEXTURE2); _glBindTexture(GL_TEXTURE_2D, _texV);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R8, w / 2, h / 2, 0, GL_RED, GL_UNSIGNED_BYTE, data + ySize + uvSize);
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
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R16UI, w, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, data);
        _glActiveTexture(GL_TEXTURE1); SetNearest(_texU);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R16UI, w / 2, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, data + yBytes);
        _glActiveTexture(GL_TEXTURE2); SetNearest(_texV);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R16UI, w / 2, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, data + yBytes + uvBytes);
    }

    private void UploadUyvy422(byte* data, int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w / 2, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, data);
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
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R16UI, w, h, 0, GL_RED_INTEGER, GL_UNSIGNED_SHORT, data);
        _glActiveTexture(GL_TEXTURE1); SetNearest(_texU);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RG16UI, Math.Max(1, w / 2), Math.Max(1, h / 2),
            0, GL_RG_INTEGER, GL_UNSIGNED_SHORT, data + yBytes);
    }

    private void UploadYuv444p(byte* data, int w, int h)
    {
        // 3 full-resolution R8 planes: Y, U, V each w×h
        int planeSize = w * h;
        _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, data);
        _glActiveTexture(GL_TEXTURE1); _glBindTexture(GL_TEXTURE_2D, _texU);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, data + planeSize);
        _glActiveTexture(GL_TEXTURE2); _glBindTexture(GL_TEXTURE_2D, _texV);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, data + planeSize * 2);
    }

    private void UploadGray8(byte* data, int w, int h)
    {
        _glActiveTexture(GL_TEXTURE0); _glBindTexture(GL_TEXTURE_2D, _texY);
        _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, data);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_initialised) return;

        void DelTex(ref uint t) { if (t == 0) return; fixed (uint* p = &t) _glDeleteTextures(1, p);     t = 0; }
        void DelBuf(ref uint b) { if (b == 0) return; fixed (uint* p = &b) _glDeleteBuffers(1, p);      b = 0; }
        void DelVao(ref uint v) { if (v == 0) return; fixed (uint* p = &v) _glDeleteVertexArrays(1, p); v = 0; }
        void DelPrg(ref uint p) { if (p == 0) return; _glDeleteProgram(p); p = 0; }

        DelTex(ref _texY); DelTex(ref _texU); DelTex(ref _texV);
        DelBuf(ref _vbo);  DelVao(ref _vao);
        DelPrg(ref _programRgba); DelPrg(ref _programNv12); DelPrg(ref _programI420);
        DelPrg(ref _programI422P10); DelPrg(ref _programUyvy422);
        DelPrg(ref _programP010); DelPrg(ref _programYuv444p); DelPrg(ref _programGray8);
    }
}
