using System.Runtime.InteropServices;
using S.Media.Core.Errors;
using S.Media.OpenGL.Output;
using S.Media.OpenGL.Upload;
using SDL3;

namespace S.Media.OpenGL.SDL3;

// Partial: GL function loading, shader compilation, resource init/dispose, GL constants.
public sealed partial class SDL3VideoView
{
    // ── Named GL constants (C4: replaces inline hex literals) ─────────────────
    private static class Gl
    {
        public const int TextureTarget2D    = 0x0DE1; // GL_TEXTURE_2D
        public const int Texture0           = 0x84C0; // GL_TEXTURE0
        public const int Texture1           = 0x84C1; // GL_TEXTURE1
        public const int Texture2           = 0x84C2; // GL_TEXTURE2
        public const int TextureMinFilter   = 0x2801; // GL_TEXTURE_MIN_FILTER
        public const int TextureMagFilter   = 0x2800; // GL_TEXTURE_MAG_FILTER
        public const int TextureWrapS       = 0x2802; // GL_TEXTURE_WRAP_S
        public const int TextureWrapT       = 0x2803; // GL_TEXTURE_WRAP_T
        public const int Linear             = 0x2601; // GL_LINEAR
        public const int ClampToEdge        = 0x812F; // GL_CLAMP_TO_EDGE
        public const int Rgba8              = 0x8058; // GL_RGBA8
        public const int Rgba               = 0x1908; // GL_RGBA
        public const int Bgra               = 0x80E1; // GL_BGRA
        public const int UnsignedByte       = 0x1401; // GL_UNSIGNED_BYTE
        public const int Float              = 0x1406; // GL_FLOAT
        public const int ArrayBuffer        = 0x8892; // GL_ARRAY_BUFFER
        public const int StaticDraw         = 0x88E4; // GL_STATIC_DRAW
        public const int ColorBufferBit     = 0x00004000; // GL_COLOR_BUFFER_BIT
        public const int Triangles          = 0x0004; // GL_TRIANGLES
        public const int VertexShader       = 0x8B31; // GL_VERTEX_SHADER
        public const int FragmentShader     = 0x8B30; // GL_FRAGMENT_SHADER
        public const int CompileStatus      = 0x8B81; // GL_COMPILE_STATUS
        public const int LinkStatus         = 0x8B82; // GL_LINK_STATUS
        public const int UnpackAlignment    = 0x0CF5; // GL_UNPACK_ALIGNMENT
    }


    // ── GL resource initialisation ───────────────────────────────────────────

    private int EnsureGlResourcesLocked()
    {
        if (_glInitialized)
        {
            return MediaResult.Success;
        }

        if (!LoadGlFunctions())
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        var vertexShader = CompileShader(Gl.VertexShader, GlslShaders.VertexCore);
        if (vertexShader == 0)
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        var fragmentShader = CompileShader(Gl.FragmentShader, GlslShaders.FragmentRgbaCore);
        if (fragmentShader == 0)
        {
            _glDeleteShader!(vertexShader);
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        var yuvFragmentShader = CompileShader(Gl.FragmentShader, GlslShaders.FragmentYuvCore);
        if (yuvFragmentShader == 0)
        {
            _glDeleteShader!(vertexShader);
            _glDeleteShader!(fragmentShader);
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        _glProgram = _glCreateProgram!();
        _glAttachShader!(_glProgram, vertexShader);
        _glAttachShader!(_glProgram, fragmentShader);
        _glBindAttribLocation!(_glProgram, 0, "aPosition");
        _glBindAttribLocation!(_glProgram, 1, "aTexCoord");
        _glLinkProgram!(_glProgram);

        _glGetProgramIv!(_glProgram, Gl.LinkStatus, out var linked);
        if (linked == 0)
        {
            _glDeleteShader!(vertexShader);
            _glDeleteShader!(fragmentShader);
            _glDeleteShader!(yuvFragmentShader);
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        _glYuvProgram = _glCreateProgram!();
        _glAttachShader!(_glYuvProgram, vertexShader);
        _glAttachShader!(_glYuvProgram, yuvFragmentShader);
        _glBindAttribLocation!(_glYuvProgram, 0, "aPosition");
        _glBindAttribLocation!(_glYuvProgram, 1, "aTexCoord");
        _glLinkProgram!(_glYuvProgram);

        _glDeleteShader!(vertexShader);
        _glDeleteShader!(fragmentShader);
        _glDeleteShader!(yuvFragmentShader);

        _glGetProgramIv!(_glYuvProgram, Gl.LinkStatus, out var yuvLinked);
        if (yuvLinked == 0)
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        var uTexture = _glGetUniformLocation!(_glProgram, "uTexture");
        _glUseProgram!(_glProgram);
        if (uTexture >= 0)
        {
            _glUniform1I!(uTexture, 0);
        }
        _glUseProgram!(0);

        _glUseProgram!(_glYuvProgram);
        var uTextureY = _glGetUniformLocation!(_glYuvProgram, "uTextureY");
        var uTextureU = _glGetUniformLocation!(_glYuvProgram, "uTextureU");
        var uTextureV = _glGetUniformLocation!(_glYuvProgram, "uTextureV");
        _glYuvPixelFormatLocation = _glGetUniformLocation!(_glYuvProgram, "uPixelFormat");
        _glYuvFullRangeLocation   = _glGetUniformLocation!(_glYuvProgram, "uFullRange"); // B6
        if (uTextureY >= 0) { _glUniform1I!(uTextureY, 0); }
        if (uTextureU >= 0) { _glUniform1I!(uTextureU, 1); }
        if (uTextureV >= 0) { _glUniform1I!(uTextureV, 2); }
        _glUseProgram!(0);

        _glGenVertexArrays!(1, out _glVao);
        _glGenBuffers!(1, out _glVbo);
        _glBindVertexArray!(_glVao);
        _glBindBuffer!(Gl.ArrayBuffer, _glVbo);

        var quad = new[]
        {
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
             1f,  1f, 1f, 0f,
            -1f, -1f, 0f, 1f,
             1f,  1f, 1f, 0f,
            -1f,  1f, 0f, 0f,
        };

        unsafe
        {
            fixed (float* ptr = quad)
            {
                _glBufferData!(Gl.ArrayBuffer, quad.Length * sizeof(float), (nint)ptr, Gl.StaticDraw);
            }
        }

        _glEnableVertexAttribArray!(0);
        _glVertexAttribPointer!(0, 2, Gl.Float, 0, 4 * sizeof(float), nint.Zero);
        _glEnableVertexAttribArray!(1);
        _glVertexAttribPointer!(1, 2, Gl.Float, 0, 4 * sizeof(float), new nint(2 * sizeof(float)));
        _glBindBuffer!(Gl.ArrayBuffer, 0);
        _glBindVertexArray!(0);

        _glGenTextures!(1, out _glTexture);
        InitializeTextureParameters(_glTexture);

        _glGenTextures!(1, out _glTextureY);
        InitializeTextureParameters(_glTextureY);

        _glGenTextures!(1, out _glTextureU);
        InitializeTextureParameters(_glTextureU);

        _glGenTextures!(1, out _glTextureV);
        InitializeTextureParameters(_glTextureV);

        // P4.7: Wire shared texture uploader with SDL3 GL function delegates.
        _uploader.SetFunctions(new GlUploadFunctions
        {
            BindTexture = (target, tex) => _glBindTexture!(target, tex),
            PixelStorei = (pname, param) => _glPixelStoreI!(pname, param),
            TexImage2D = (target, level, internalFmt, w, h, border, fmt, type, pixels) =>
                _glTexImage2D!(target, level, internalFmt, w, h, border, fmt, type, pixels),
            TexSubImage2D = (target, level, xoff, yoff, w, h, fmt, type, pixels) =>
                _glTexSubImage2D!(target, level, xoff, yoff, w, h, fmt, type, pixels),
        });
        _uploader.SetTextureIds(_glTexture, _glTextureY, _glTextureU, _glTextureV);

        _glInitialized = true;
        return MediaResult.Success;
    }

    private void InitializeTextureParameters(int textureId)
    {
        _glBindTexture!(Gl.TextureTarget2D, textureId);
        _glTexParameteri!(Gl.TextureTarget2D, Gl.TextureMinFilter, Gl.Linear);
        _glTexParameteri!(Gl.TextureTarget2D, Gl.TextureMagFilter, Gl.Linear);
        _glTexParameteri!(Gl.TextureTarget2D, Gl.TextureWrapS, Gl.ClampToEdge);
        _glTexParameteri!(Gl.TextureTarget2D, Gl.TextureWrapT, Gl.ClampToEdge);
        _glBindTexture!(Gl.TextureTarget2D, 0);
    }

    // ── GL function loading ──────────────────────────────────────────────────

    private bool LoadGlFunctions()
    {
        T? Load<T>(string name) where T : Delegate
        {
            var pointer = SDL.GLGetProcAddress(name);
            return pointer == nint.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        _glViewport = Load<GlViewport>("glViewport");
        _glClearColor = Load<GlClearColor>("glClearColor");
        _glClear = Load<GlClear>("glClear");
        _glCreateShader = Load<GlCreateShader>("glCreateShader");
        _glShaderSource = Load<GlShaderSource>("glShaderSource");
        _glCompileShader = Load<GlCompileShader>("glCompileShader");
        _glGetShaderIv = Load<GlGetShaderIv>("glGetShaderiv");
        _glGetShaderInfoLog = Load<GlGetShaderInfoLog>("glGetShaderInfoLog");
        _glCreateProgram = Load<GlCreateProgram>("glCreateProgram");
        _glAttachShader = Load<GlAttachShader>("glAttachShader");
        _glBindAttribLocation = Load<GlBindAttribLocation>("glBindAttribLocation");
        _glLinkProgram = Load<GlLinkProgram>("glLinkProgram");
        _glGetProgramIv = Load<GlGetProgramIv>("glGetProgramiv");
        _glGetProgramInfoLog = Load<GlGetProgramInfoLog>("glGetProgramInfoLog");
        _glUseProgram = Load<GlUseProgram>("glUseProgram");
        _glDeleteShader = Load<GlDeleteShader>("glDeleteShader");
        _glDeleteProgram = Load<GlDeleteProgram>("glDeleteProgram");
        _glGetUniformLocation = Load<GlGetUniformLocation>("glGetUniformLocation");
        _glUniform1I = Load<GlUniform1I>("glUniform1i");
        _glGenVertexArrays = Load<GlGenVertexArrays>("glGenVertexArrays");
        _glBindVertexArray = Load<GlBindVertexArray>("glBindVertexArray");
        _glDeleteVertexArrays = Load<GlDeleteVertexArrays>("glDeleteVertexArrays");
        _glGenBuffers = Load<GlGenBuffers>("glGenBuffers");
        _glBindBuffer = Load<GlBindBuffer>("glBindBuffer");
        _glBufferData = Load<GlBufferData>("glBufferData");
        _glDeleteBuffers = Load<GlDeleteBuffers>("glDeleteBuffers");
        _glEnableVertexAttribArray = Load<GlEnableVertexAttribArray>("glEnableVertexAttribArray");
        _glVertexAttribPointer = Load<GlVertexAttribPointer>("glVertexAttribPointer");
        _glGenTextures = Load<GlGenTextures>("glGenTextures");
        _glBindTexture = Load<GlBindTexture>("glBindTexture");
        _glTexParameteri = Load<GlTexParameteri>("glTexParameteri");
        _glTexImage2D = Load<GlTexImage2D>("glTexImage2D");
        _glTexSubImage2D = Load<GlTexSubImage2D>("glTexSubImage2D");
        _glPixelStoreI = Load<GlPixelStoreI>("glPixelStorei");
        _glActiveTexture = Load<GlActiveTexture>("glActiveTexture");
        _glDeleteTextures = Load<GlDeleteTextures>("glDeleteTextures");
        _glDrawArrays = Load<GlDrawArrays>("glDrawArrays");

        return _glViewport is not null
            && _glClearColor is not null
            && _glClear is not null
            && _glCreateShader is not null
            && _glShaderSource is not null
            && _glCompileShader is not null
            && _glGetShaderIv is not null
            && _glGetShaderInfoLog is not null
            && _glCreateProgram is not null
            && _glAttachShader is not null
            && _glBindAttribLocation is not null
            && _glLinkProgram is not null
            && _glGetProgramIv is not null
            && _glGetProgramInfoLog is not null
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

    // ── Shader compilation ───────────────────────────────────────────────────

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

        _glGetShaderIv!(shader, Gl.CompileStatus, out var compiled);
        if (compiled != 0)
        {
            return shader;
        }

        _glDeleteShader!(shader);
        return 0;
    }

    // ── GL resource disposal ─────────────────────────────────────────────────

    private void DisposeGlResourcesLocked()
    {
        if (_glTextureY != 0)
        {
            var texture = _glTextureY;
            _glDeleteTextures?.Invoke(1, in texture);
            _glTextureY = 0;
        }

        if (_glTextureU != 0)
        {
            var texture = _glTextureU;
            _glDeleteTextures?.Invoke(1, in texture);
            _glTextureU = 0;
        }

        if (_glTextureV != 0)
        {
            var texture = _glTextureV;
            _glDeleteTextures?.Invoke(1, in texture);
            _glTextureV = 0;
        }

        if (_glTexture != 0)
        {
            var texture = _glTexture;
            _glDeleteTextures?.Invoke(1, in texture);
            _glTexture = 0;
        }

        if (_glVbo != 0)
        {
            var vbo = _glVbo;
            _glDeleteBuffers?.Invoke(1, in vbo);
            _glVbo = 0;
        }

        if (_glVao != 0)
        {
            var vao = _glVao;
            _glDeleteVertexArrays?.Invoke(1, in vao);
            _glVao = 0;
        }

        if (_glProgram != 0)
        {
            _glDeleteProgram?.Invoke(_glProgram);
            _glProgram = 0;
        }

        if (_glYuvProgram != 0)
        {
            _glDeleteProgram?.Invoke(_glYuvProgram);
            _glYuvProgram = 0;
        }

        _glYuvPixelFormatLocation = -1;
        _glYuvFullRangeLocation = -1;
        _uploader.Reset();

        _glInitialized = false;
    }
}

