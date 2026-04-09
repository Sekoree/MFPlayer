using System.Runtime.InteropServices;
using System.Text;
using S.Media.Core.Media;

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
    private const uint GL_BGRA                = 0x80E1;
    private const uint GL_UNSIGNED_BYTE       = 0x1401;
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

    // ── GL state ──────────────────────────────────────────────────────────

    private uint _texture;
    private uint _program;
    private uint _vao;
    private uint _vbo;
    private int  _texWidth;
    private int  _texHeight;
    private bool _disposed;

    // ── Shaders ───────────────────────────────────────────────────────────

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 vUV;
        void main() {
            gl_Position = vec4(aPos, 0.0, 1.0);
            vUV = aUV;
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec2 vUV;
        out vec4 fragColor;
        uniform sampler2D uTexture;
        void main() {
            fragColor = texture(uTexture, vUV);
        }
        """;

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
        _glDeleteShader(vs);
        _glDeleteShader(fs);

        // Set texture uniform to unit 0
        _glUseProgram(_program);
        fixed (byte* name = "uTexture\0"u8)
        {
            int loc = _glGetUniformLocation(_program, name);
            _glUniform1i(loc, 0);
        }

        // Fullscreen quad (2 triangles): position (x,y) + UV (u,v)
        // Note: UV y is flipped (1→0) so the image isn't upside-down.
        float[] quadVerts =
        [
            // pos        uv
            -1f, -1f,   0f, 1f,
             1f, -1f,   1f, 1f,
             1f,  1f,   1f, 0f,
            -1f, -1f,   0f, 1f,
             1f,  1f,   1f, 0f,
            -1f,  1f,   0f, 0f,
        ];

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

        _glViewport(0, 0, viewportWidth, viewportHeight);
        _glClearColor(0f, 0f, 0f, 1f);
    }

    // ── Per-frame rendering ───────────────────────────────────────────────

    public void UploadAndDraw(VideoFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;

        _glBindTexture(GL_TEXTURE_2D, _texture);

        // Pin the managed byte array for GPU upload.
        using var pin = frame.Data.Pin();

        if (w == _texWidth && h == _texHeight)
        {
            // Same resolution → fast sub-image update (no GPU realloc).
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_BGRA, GL_UNSIGNED_BYTE, pin.Pointer);
        }
        else
        {
            // Resolution changed → re-allocate the texture.
            _glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_BGRA, GL_UNSIGNED_BYTE, pin.Pointer);
            _texWidth  = w;
            _texHeight = h;
        }

        _glClear(GL_COLOR_BUFFER_BIT);
        _glUseProgram(_program);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
    }

    public void DrawBlack()
    {
        _glClear(GL_COLOR_BUFFER_BIT);
    }

    public void SetViewport(int w, int h)
    {
        _glViewport(0, 0, w, h);
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
        fixed (uint* p = &_vbo)          _glDeleteBuffers(1, p);
        fixed (uint* p = &_vao)          _glDeleteVertexArrays(1, p);
        _glDeleteProgram(_program);
    }
}

