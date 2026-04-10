using System.Runtime.InteropServices;
using System.Text;
using Avalonia.OpenGL;
using S.Media.Core.Media;

namespace S.Media.Avalonia;

internal sealed unsafe class AvaloniaGlRenderer : IDisposable
{
    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    private const uint GL_TEXTURE_WRAP_S = 0x2802;
    private const uint GL_TEXTURE_WRAP_T = 0x2803;
    private const uint GL_LINEAR = 0x2601;
    private const uint GL_CLAMP_TO_EDGE = 0x812F;
    private const uint GL_RGBA8 = 0x8058;
    private const uint GL_RGBA = 0x1908;
    private const uint GL_BGRA = 0x80E1;
    private const uint GL_UNSIGNED_BYTE = 0x1401;
    private const uint GL_FLOAT = 0x1406;
    private const uint GL_TRIANGLES = 0x0004;
    private const uint GL_ARRAY_BUFFER = 0x8892;
    private const uint GL_STATIC_DRAW = 0x88E4;
    private const uint GL_FRAGMENT_SHADER = 0x8B30;
    private const uint GL_VERTEX_SHADER = 0x8B31;
    private const uint GL_COMPILE_STATUS = 0x8B81;
    private const uint GL_LINK_STATUS = 0x8B82;
    private const uint GL_COLOR_BUFFER_BIT = 0x00004000;
    private const uint GL_FALSE = 0;
    private const uint GL_FRAMEBUFFER = 0x8D40;
    private const uint GL_BLEND = 0x0BE2;
    private const uint GL_UNPACK_ALIGNMENT = 0x0CF5;
    private const uint GL_UNPACK_ROW_LENGTH = 0x0CF2;
    private const uint GL_TEXTURE0 = 0x84C0;

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

    private uint _texture;
    private uint _program;
    private uint _vao;
    private uint _vbo;
    private int _texWidth;
    private int _texHeight;
    private bool _initialised;
    private bool _disposed;

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

    public void Initialise(GlInterface gl)
    {
        if (_initialised) return;

        _glViewport = LoadGL<GlViewport>(gl, "glViewport");
        _glClearColor = LoadGL<GlClearColor>(gl, "glClearColor");
        _glClear = LoadGL<GlClear>(gl, "glClear");
        _glGenTextures = LoadGL<GlGenTextures>(gl, "glGenTextures");
        _glDeleteTextures = LoadGL<GlDeleteTextures>(gl, "glDeleteTextures");
        _glBindTexture = LoadGL<GlBindTexture>(gl, "glBindTexture");
        _glTexImage2D = LoadGL<GlTexImage2D>(gl, "glTexImage2D");
        _glTexSubImage2D = LoadGL<GlTexSubImage2D>(gl, "glTexSubImage2D");
        _glTexParameteri = LoadGL<GlTexParameteri>(gl, "glTexParameteri");
        _glCreateShader = LoadGL<GlCreateShader>(gl, "glCreateShader");
        _glShaderSource = LoadGL<GlShaderSource>(gl, "glShaderSource");
        _glCompileShader = LoadGL<GlCompileShader>(gl, "glCompileShader");
        _glGetShaderiv = LoadGL<GlGetShaderiv>(gl, "glGetShaderiv");
        _glGetShaderInfoLog = LoadGL<GlGetShaderInfoLog>(gl, "glGetShaderInfoLog");
        _glCreateProgram = LoadGL<GlCreateProgram>(gl, "glCreateProgram");
        _glAttachShader = LoadGL<GlAttachShader>(gl, "glAttachShader");
        _glLinkProgram = LoadGL<GlLinkProgram>(gl, "glLinkProgram");
        _glGetProgramiv = LoadGL<GlGetProgramiv>(gl, "glGetProgramiv");
        _glGetProgramInfoLog = LoadGL<GlGetProgramInfoLog>(gl, "glGetProgramInfoLog");
        _glUseProgram = LoadGL<GlUseProgram>(gl, "glUseProgram");
        _glDeleteShader = LoadGL<GlDeleteShader>(gl, "glDeleteShader");
        _glDeleteProgram = LoadGL<GlDeleteProgram>(gl, "glDeleteProgram");
        _glGetUniformLocation = LoadGL<GlGetUniformLocation>(gl, "glGetUniformLocation");
        _glUniform1i = LoadGL<GlUniform1i>(gl, "glUniform1i");
        _glGenVertexArrays = LoadGL<GlGenVertexArrays>(gl, "glGenVertexArrays");
        _glDeleteVertexArrays = LoadGL<GlDeleteVertexArrays>(gl, "glDeleteVertexArrays");
        _glBindVertexArray = LoadGL<GlBindVertexArray>(gl, "glBindVertexArray");
        _glGenBuffers = LoadGL<GlGenBuffers>(gl, "glGenBuffers");
        _glDeleteBuffers = LoadGL<GlDeleteBuffers>(gl, "glDeleteBuffers");
        _glBindBuffer = LoadGL<GlBindBuffer>(gl, "glBindBuffer");
        _glBufferData = LoadGL<GlBufferData>(gl, "glBufferData");
        _glEnableVertexAttribArray = LoadGL<GlEnableVertexAttribArray>(gl, "glEnableVertexAttribArray");
        _glVertexAttribPointer = LoadGL<GlVertexAttribPointer>(gl, "glVertexAttribPointer");
        _glDrawArrays = LoadGL<GlDrawArrays>(gl, "glDrawArrays");
        _glBindFramebuffer = LoadGL<GlBindFramebuffer>(gl, "glBindFramebuffer");
        _glPixelStorei = LoadGL<GlPixelStorei>(gl, "glPixelStorei");
        _glActiveTexture = LoadGL<GlActiveTexture>(gl, "glActiveTexture");
        _glDisable = LoadGL<GlDisable>(gl, "glDisable");

        uint vs = CompileShader(GL_VERTEX_SHADER, VertexShaderSource);
        uint fs = CompileShader(GL_FRAGMENT_SHADER, FragmentShaderSource);

        _program = _glCreateProgram();
        _glAttachShader(_program, vs);
        _glAttachShader(_program, fs);
        _glLinkProgram(_program);
        CheckProgram(_program);
        _glDeleteShader(vs);
        _glDeleteShader(fs);

        _glUseProgram(_program);
        fixed (byte* name = "uTexture\0"u8)
        {
            int loc = _glGetUniformLocation(_program, name);
            _glUniform1i(loc, 0);
        }

        float[] quadVerts =
        [
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
             1f,  1f, 1f, 0f,
            -1f, -1f, 0f, 1f,
             1f,  1f, 1f, 0f,
            -1f,  1f, 0f, 0f,
        ];

        fixed (uint* pVao = &_vao)
            _glGenVertexArrays(1, pVao);
        fixed (uint* pVbo = &_vbo)
            _glGenBuffers(1, pVbo);

        _glBindVertexArray(_vao);
        _glBindBuffer(GL_ARRAY_BUFFER, _vbo);
        fixed (float* pData = quadVerts)
            _glBufferData(GL_ARRAY_BUFFER, quadVerts.Length * sizeof(float), pData, GL_STATIC_DRAW);

        _glEnableVertexAttribArray(0);
        _glVertexAttribPointer(0, 2, GL_FLOAT, (byte)GL_FALSE, 4 * sizeof(float), (void*)0);
        _glEnableVertexAttribArray(1);
        _glVertexAttribPointer(1, 2, GL_FLOAT, (byte)GL_FALSE, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        _glBindVertexArray(0);

        fixed (uint* pTex = &_texture)
            _glGenTextures(1, pTex);

        _glBindTexture(GL_TEXTURE_2D, _texture);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, (int)GL_CLAMP_TO_EDGE);
        _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, (int)GL_CLAMP_TO_EDGE);
        _glActiveTexture(GL_TEXTURE0);
        _glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        _glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
        _glClearColor(0f, 0f, 0f, 1f);

        _initialised = true;
    }

    public void DrawBlack(int framebuffer, int width, int height)
    {
        if (!_initialised) return;
        _glBindFramebuffer(GL_FRAMEBUFFER, (uint)framebuffer);
        _glViewport(0, 0, width, height);
        _glClear(GL_COLOR_BUFFER_BIT);
    }

    public void UploadAndDraw(in VideoFrame frame, int framebuffer, int viewportWidth, int viewportHeight)
    {
        if (!_initialised) return;

        _glBindFramebuffer(GL_FRAMEBUFFER, (uint)framebuffer);
        _glViewport(0, 0, viewportWidth, viewportHeight);
        _glActiveTexture(GL_TEXTURE0);
        _glBindTexture(GL_TEXTURE_2D, _texture);
        _glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
        _glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);

        using var pin = frame.Data.Pin();
        if (frame.Width == _texWidth && frame.Height == _texHeight)
        {
            _glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, frame.Width, frame.Height, GL_RGBA, GL_UNSIGNED_BYTE, pin.Pointer);
        }
        else
        {
            _glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, frame.Width, frame.Height, 0, GL_RGBA, GL_UNSIGNED_BYTE, pin.Pointer);
            _texWidth = frame.Width;
            _texHeight = frame.Height;
        }

        _glClear(GL_COLOR_BUFFER_BIT);
        _glDisable(GL_BLEND);
        _glUseProgram(_program);
        _glBindVertexArray(_vao);
        _glDrawArrays(GL_TRIANGLES, 0, 6);
        _glBindVertexArray(0);
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
        if (status != 0)
            return shader;

        byte* log = stackalloc byte[1024];
        int len;
        _glGetShaderInfoLog(shader, 1024, &len, log);
        string msg = Encoding.UTF8.GetString(log, Math.Max(0, len));
        throw new InvalidOperationException($"Shader compilation failed: {msg}");
    }

    private void CheckProgram(uint program)
    {
        int status;
        _glGetProgramiv(program, GL_LINK_STATUS, &status);
        if (status != 0)
            return;

        byte* log = stackalloc byte[1024];
        int len;
        _glGetProgramInfoLog(program, 1024, &len, log);
        string msg = Encoding.UTF8.GetString(log, Math.Max(0, len));
        throw new InvalidOperationException($"Program link failed: {msg}");
    }

    private static T LoadGL<T>(GlInterface gl, string name) where T : Delegate
    {
        var ptr = gl.GetProcAddress(name);
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load GL function: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initialised)
        {
            if (_texture != 0)
            {
                fixed (uint* pTex = &_texture)
                    _glDeleteTextures(1, pTex);
                _texture = 0;
            }

            if (_vbo != 0)
            {
                fixed (uint* pVbo = &_vbo)
                    _glDeleteBuffers(1, pVbo);
                _vbo = 0;
            }

            if (_vao != 0)
            {
                fixed (uint* pVao = &_vao)
                    _glDeleteVertexArrays(1, pVao);
                _vao = 0;
            }

            if (_program != 0)
            {
                _glDeleteProgram(_program);
                _program = 0;
            }
        }
    }
}
