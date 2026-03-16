using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Avalonia;

public class VideoGL : OpenGlControlBase, IDisposable
{
    private const int GlR8 = 0x8229;
    private const int GlRg8 = 0x822B;
    private const int GlRed = 0x1903;
    private const int GlRg = 0x8227;
    private const int GlTexture1 = GlConsts.GL_TEXTURE0 + 1;
    private const int GlTexture2 = GlConsts.GL_TEXTURE0 + 2;

    private static readonly float[] QuadVertices =
    [
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
         1f,  1f, 1f, 0f,
        -1f, -1f, 0f, 1f,
         1f,  1f, 1f, 0f,
        -1f,  1f, 0f, 0f
    ];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TexSubImage2DProc(
        int target,
        int level,
        int xoffset,
        int yoffset,
        int width,
        int height,
        int format,
        int type,
        nint pixels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetUniformLocationProc(int program, [MarshalAs(UnmanagedType.LPStr)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Uniform1iProc(int location, int value);

    private readonly IVideoSource _source;
    private readonly bool _master;
    private readonly Lock _frameLock = new();

    private VideoFrame? _latestFrame;
    private bool _hasFrame;

    private int _program;
    private int _yuvProgram;
    private int _vbo;
    private int _vao;
    private int _textureRgba;
    private int _textureY;
    private int _textureUv;
    private int _textureU;
    private int _textureV;
    private int _textureWidth;
    private int _textureHeight;
    private bool _rgbaTextureInitialized;
    private TexSubImage2DProc? _texSubImage2D;
    private GetUniformLocationProc? _getUniformLocation;
    private Uniform1iProc? _uniform1i;
    private bool _canUseGpuYuvPath;
    private int _yuvTextureYLocation = -1;
    private int _yuvTextureULocation = -1;
    private int _yuvTextureVLocation = -1;
    private int _yuvPixelFormatLocation = -1;
    private bool _useYuvProgramThisFrame;
    private int _yuvPixelFormatThisFrame;
    private byte[]? _plane0Scratch;
    private byte[]? _plane1Scratch;
    private byte[]? _plane2Scratch;
    private byte[]? _rgbaConvertedScratch;

    private bool _glReady;
    private bool _disposed;
    private bool _masterLoopStarted;
    private int _renderRequestQueued;
    private readonly Action _requestRenderAction;

    public bool KeepAspectRatio { get; set; } = true;

    public VideoGL(IVideoSource source, bool master = true)
    {
        _source = source;
        _master = master;
        _requestRenderAction = RequestNextFrameRendering;
        _source.FrameReadyFast += SourceFrameReadyFast;
    }

    private void SourceFrameReadyFast(VideoFrame frame, double _)
    {
        VideoFrame? previous;
        lock (_frameLock)
        {
            previous = _latestFrame;
            _latestFrame = frame.AddRef();
            _hasFrame = true;
        }

        previous?.Dispose();

        QueueRenderRequest();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_disposed)
            return;

        if (change.Property == BoundsProperty || change.Property == Visual.IsVisibleProperty)
            QueueRenderRequest();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        var texSubProc = gl.GetProcAddress("glTexSubImage2D");
        if (texSubProc != nint.Zero)
            _texSubImage2D = Marshal.GetDelegateForFunctionPointer<TexSubImage2DProc>(texSubProc);

        var getUniformLocationProc = gl.GetProcAddress("glGetUniformLocation");
        if (getUniformLocationProc != nint.Zero)
            _getUniformLocation = Marshal.GetDelegateForFunctionPointer<GetUniformLocationProc>(getUniformLocationProc);

        var uniform1IProc = gl.GetProcAddress("glUniform1i");
        if (uniform1IProc != nint.Zero)
            _uniform1i = Marshal.GetDelegateForFunctionPointer<Uniform1iProc>(uniform1IProc);

        var vertexShaderSource = BuildVertexShader(gl.ContextInfo.Version.Type);
        var fragmentShaderSource = BuildFragmentShader(gl.ContextInfo.Version.Type);
        var yuvFragmentShaderSource = BuildYuvFragmentShader(gl.ContextInfo.Version.Type);

        _program = BuildProgram(gl, vertexShaderSource, fragmentShaderSource);
        if (_program == 0)
            return;

        _yuvProgram = BuildProgram(gl, vertexShaderSource, yuvFragmentShaderSource);
        _canUseGpuYuvPath = _yuvProgram != 0 && _getUniformLocation != null && _uniform1i != null;
        if (_canUseGpuYuvPath)
            InitializeYuvUniformLocations(gl);

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();

        gl.BindVertexArray(_vao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vbo);

        var handle = GCHandle.Alloc(QuadVertices, GCHandleType.Pinned);
        try
        {
            gl.BufferData(
                GlConsts.GL_ARRAY_BUFFER,
                QuadVertices.Length * sizeof(float),
                handle.AddrOfPinnedObject(),
                GlConsts.GL_STATIC_DRAW);
        }
        finally
        {
            handle.Free();
        }

        var stride = 4 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, stride, nint.Zero);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, GlConsts.GL_FLOAT, 0, stride, 2 * sizeof(float));

        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, 0);
        gl.BindVertexArray(0);

        _textureRgba = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureRgba);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);

        _textureY = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);

        _textureUv = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureUv);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);

        _textureU = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);

        _textureV = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

        _glReady = true;

        if (_master)
            StartMasterLoop();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        Interlocked.Exchange(ref _renderRequestQueued, 0);

        if (_master && _glReady)
            _source.RequestNextFrame(out _);

        var pixelSize = GetPixelSize();
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
        gl.Viewport(0, 0, pixelSize.Width, pixelSize.Height);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

        if (!_glReady)
            return;

        VideoFrame frame;
        lock (_frameLock)
        {
            if (!_hasFrame || _latestFrame == null)
                return;

            frame = _latestFrame.AddRef();
        }

        try
        {
            _useYuvProgramThisFrame = false;
            _yuvPixelFormatThisFrame = 0;

            var uploaded = frame.PixelFormat switch
            {
                VideoPixelFormat.Rgba32 => UploadRgbaFrame(gl, frame),
                VideoPixelFormat.Nv12 => UploadNv12Frame(gl, frame),
                VideoPixelFormat.Yuv420p => UploadYuv420pFrame(gl, frame),
                _ => false
            };

            if (!uploaded)
                return;
        }
        finally
        {
            frame.Dispose();
        }


        var drawViewport = GetDrawViewport(pixelSize, _textureWidth, _textureHeight, KeepAspectRatio);
        gl.Viewport(drawViewport.X, drawViewport.Y, drawViewport.Width, drawViewport.Height);

        if (_useYuvProgramThisFrame)
        {
            gl.UseProgram(_yuvProgram);
            if (_uniform1i != null && _yuvPixelFormatLocation >= 0)
                _uniform1i(_yuvPixelFormatLocation, _yuvPixelFormatThisFrame);
        }
        else
        {
            gl.UseProgram(_program);
        }
        gl.BindVertexArray(_vao);
        gl.DrawArrays(GlConsts.GL_TRIANGLES, 0, 6);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

        if (_master && !_disposed && IsVisible)
            QueueRenderRequest();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_vbo != 0)
        {
            gl.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        if (_vao != 0)
        {
            gl.DeleteVertexArray(_vao);
            _vao = 0;
        }

        if (_textureRgba != 0)
        {
            gl.DeleteTexture(_textureRgba);
            _textureRgba = 0;
        }

        if (_textureY != 0)
        {
            gl.DeleteTexture(_textureY);
            _textureY = 0;
        }

        if (_textureUv != 0)
        {
            gl.DeleteTexture(_textureUv);
            _textureUv = 0;
        }

        if (_textureU != 0)
        {
            gl.DeleteTexture(_textureU);
            _textureU = 0;
        }

        if (_textureV != 0)
        {
            gl.DeleteTexture(_textureV);
            _textureV = 0;
        }

        if (_program != 0)
        {
            gl.DeleteProgram(_program);
            _program = 0;
        }

        if (_yuvProgram != 0)
        {
            gl.DeleteProgram(_yuvProgram);
            _yuvProgram = 0;
        }

        _glReady = false;
        _rgbaTextureInitialized = false;
        _useYuvProgramThisFrame = false;
        base.OnOpenGlDeinit(gl);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _source.FrameReadyFast -= SourceFrameReadyFast;

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
            _hasFrame = false;
        }
    }

    private void StartMasterLoop()
    {
        if (_masterLoopStarted || _disposed)
            return;

        _masterLoopStarted = true;
        QueueRenderRequest();
    }

    private void QueueRenderRequest()
    {
        if (_disposed)
            return;

        if (Interlocked.Exchange(ref _renderRequestQueued, 1) == 1)
            return;

        Dispatcher.UIThread.Post(_requestRenderAction, DispatcherPriority.Background);
    }

    private static int BuildProgram(GlInterface gl, string vertexSource, string fragmentSource)
    {
        var vertexShader = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        var vertexError = gl.CompileShaderAndGetError(vertexShader, vertexSource);
        if (!string.IsNullOrWhiteSpace(vertexError))
            return 0;

        var fragmentShader = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        var fragmentError = gl.CompileShaderAndGetError(fragmentShader, fragmentSource);
        if (!string.IsNullOrWhiteSpace(fragmentError))
            return 0;

        var program = gl.CreateProgram();
        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);
        gl.BindAttribLocationString(program, 0, "aPosition");
        gl.BindAttribLocationString(program, 1, "aTexCoord");

        var linkError = gl.LinkProgramAndGetError(program);

        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        if (!string.IsNullOrWhiteSpace(linkError))
        {
            gl.DeleteProgram(program);
            return 0;
        }

        return program;
    }

    private static string BuildVertexShader(GlProfileType profileType)
    {
        if (profileType == GlProfileType.OpenGLES)
        {
            return """
                   #version 300 es
                   layout(location = 0) in vec2 aPosition;
                   layout(location = 1) in vec2 aTexCoord;
                   out vec2 vTexCoord;
                   void main()
                   {
                       gl_Position = vec4(aPosition, 0.0, 1.0);
                       vTexCoord = aTexCoord;
                   }
                   """;
        }

        return """
               #version 330 core
               layout(location = 0) in vec2 aPosition;
               layout(location = 1) in vec2 aTexCoord;
               out vec2 vTexCoord;
               void main()
               {
                   gl_Position = vec4(aPosition, 0.0, 1.0);
                   vTexCoord = aTexCoord;
               }
               """;
    }

    private static string BuildFragmentShader(GlProfileType profileType)
    {
        if (profileType == GlProfileType.OpenGLES)
        {
            return """
                   #version 300 es
                   precision mediump float;
                   in vec2 vTexCoord;
                   uniform sampler2D uTexture;
                   out vec4 FragColor;
                   void main()
                   {
                       FragColor = texture(uTexture, vTexCoord);
                   }
                   """;
        }

        return """
               #version 330 core
               in vec2 vTexCoord;
               uniform sampler2D uTexture;
               out vec4 FragColor;
               void main()
               {
                   FragColor = texture(uTexture, vTexCoord);
               }
               """;
    }

    private static string BuildYuvFragmentShader(GlProfileType profileType)
    {
        if (profileType == GlProfileType.OpenGLES)
        {
            return """
                   #version 300 es
                   precision mediump float;
                   in vec2 vTexCoord;
                   uniform sampler2D uTextureY;
                   uniform sampler2D uTextureU;
                   uniform sampler2D uTextureV;
                   uniform int uPixelFormat;
                   out vec4 FragColor;
                   
                   vec3 yuvToRgb(float y, float u, float v)
                   {
                       float r = y + 1.5748 * v;
                       float g = y - 0.1873 * u - 0.4681 * v;
                       float b = y + 1.8556 * u;
                       return clamp(vec3(r, g, b), 0.0, 1.0);
                   }
                   
                   void main()
                   {
                       float y = texture(uTextureY, vTexCoord).r;
                       float u;
                       float v;
                       if (uPixelFormat == 1)
                       {
                           vec2 uv = texture(uTextureU, vTexCoord).rg;
                           u = uv.r - 0.5;
                           v = uv.g - 0.5;
                       }
                       else
                       {
                           u = texture(uTextureU, vTexCoord).r - 0.5;
                           v = texture(uTextureV, vTexCoord).r - 0.5;
                       }

                       FragColor = vec4(yuvToRgb(y, u, v), 1.0);
                   }
                   """;
        }

        return """
               #version 330 core
               in vec2 vTexCoord;
               uniform sampler2D uTextureY;
               uniform sampler2D uTextureU;
               uniform sampler2D uTextureV;
               uniform int uPixelFormat;
               out vec4 FragColor;

               vec3 yuvToRgb(float y, float u, float v)
               {
                   float r = y + 1.5748 * v;
                   float g = y - 0.1873 * u - 0.4681 * v;
                   float b = y + 1.8556 * u;
                   return clamp(vec3(r, g, b), 0.0, 1.0);
               }

               void main()
               {
                   float y = texture(uTextureY, vTexCoord).r;
                   float u;
                   float v;
                   if (uPixelFormat == 1)
                   {
                       vec2 uv = texture(uTextureU, vTexCoord).rg;
                       u = uv.r - 0.5;
                       v = uv.g - 0.5;
                   }
                   else
                   {
                       u = texture(uTextureU, vTexCoord).r - 0.5;
                       v = texture(uTextureV, vTexCoord).r - 0.5;
                   }

                   FragColor = vec4(yuvToRgb(y, u, v), 1.0);
               }
               """;
    }

    private unsafe bool UploadRgbaFrame(GlInterface gl, VideoFrame frame)
    {
        if (frame.GetPlaneLength(0) <= 0 || frame.Width <= 0 || frame.Height <= 0)
            return false;

        var rgbaData = GetTightlyPackedPlane(frame, 0, frame.Width * 4, frame.Height, ref _plane0Scratch);
        if (rgbaData == null)
            return false;

        return UploadRgbaPixels(gl, frame.Width, frame.Height, rgbaData);
    }

    private unsafe bool UploadRgbaPixels(GlInterface gl, int width, int height, byte[] rgbaData)
    {
        gl.ActiveTexture(GlConsts.GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureRgba);

        var internalFormat = GlConsts.GL_RGBA8;
        var format = GlConsts.GL_RGBA;

        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(rgbaData))
        {
            var pixels = (nint)ptr;

            if (!_rgbaTextureInitialized || _textureWidth != width || _textureHeight != height)
            {
                _textureWidth = width;
                _textureHeight = height;
                _rgbaTextureInitialized = true;

                gl.TexImage2D(
                    GlConsts.GL_TEXTURE_2D,
                    0,
                    internalFormat,
                    _textureWidth,
                    _textureHeight,
                    0,
                    format,
                    GlConsts.GL_UNSIGNED_BYTE,
                    nint.Zero);
            }

            if (_texSubImage2D != null)
            {
                _texSubImage2D(
                    GlConsts.GL_TEXTURE_2D,
                    0,
                    0,
                    0,
                    width,
                    height,
                    format,
                    GlConsts.GL_UNSIGNED_BYTE,
                    pixels);
            }
            else
            {
                gl.TexImage2D(
                    GlConsts.GL_TEXTURE_2D,
                    0,
                    internalFormat,
                    width,
                    height,
                    0,
                    format,
                    GlConsts.GL_UNSIGNED_BYTE,
                    pixels);
            }
        }

        return true;
    }

    private unsafe bool UploadNv12Frame(GlInterface gl, VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        var width = frame.Width;
        var height = frame.Height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uvPlane = GetTightlyPackedPlane(frame, 1, chromaWidth * 2, chromaHeight, ref _plane1Scratch);
        if (yPlane == null || uvPlane == null)
            return false;

        if (_canUseGpuYuvPath)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadSingleChannelTexture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureUv);
            UploadDualChannelTexture(gl, chromaWidth, chromaHeight, uvPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureUv);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 1;
            _rgbaTextureInitialized = false;
            return true;
        }

        var pixelCount = checked(width * height);
        var rgbaLength = checked(pixelCount * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];

        ConvertNv12ToRgba(yPlane, uvPlane, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe bool UploadYuv420pFrame(GlInterface gl, VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        var width = frame.Width;
        var height = frame.Height;
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var yPlane = GetTightlyPackedPlane(frame, 0, width, height, ref _plane0Scratch);
        var uPlane = GetTightlyPackedPlane(frame, 1, chromaWidth, chromaHeight, ref _plane1Scratch);
        var vPlane = GetTightlyPackedPlane(frame, 2, chromaWidth, chromaHeight, ref _plane2Scratch);
        if (yPlane == null || uPlane == null || vPlane == null)
            return false;

        if (_canUseGpuYuvPath)
        {
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureY);
            UploadSingleChannelTexture(gl, width, height, yPlane);

            gl.ActiveTexture(GlTexture1);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureU);
            UploadSingleChannelTexture(gl, chromaWidth, chromaHeight, uPlane);

            gl.ActiveTexture(GlTexture2);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _textureV);
            UploadSingleChannelTexture(gl, chromaWidth, chromaHeight, vPlane);
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);

            _textureWidth = width;
            _textureHeight = height;
            _useYuvProgramThisFrame = true;
            _yuvPixelFormatThisFrame = 2;
            _rgbaTextureInitialized = false;
            return true;
        }

        var pixelCount = checked(width * height);
        var rgbaLength = checked(pixelCount * 4);
        if (_rgbaConvertedScratch == null || _rgbaConvertedScratch.Length < rgbaLength)
            _rgbaConvertedScratch = new byte[rgbaLength];

        ConvertYuv420pToRgba(yPlane, uPlane, vPlane, width, height, _rgbaConvertedScratch);
        return UploadRgbaPixels(gl, width, height, _rgbaConvertedScratch);
    }

    private unsafe void UploadSingleChannelTexture(GlInterface gl, int width, int height, byte[] data)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlR8, width, height, 0, GlRed, GlConsts.GL_UNSIGNED_BYTE, (nint)ptr);
        }
    }

    private unsafe void UploadDualChannelTexture(GlInterface gl, int width, int height, byte[] data)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GlRg8, width, height, 0, GlRg, GlConsts.GL_UNSIGNED_BYTE, (nint)ptr);
        }
    }

    private void InitializeYuvUniformLocations(GlInterface gl)
    {
        if (_getUniformLocation == null || _uniform1i == null || _yuvProgram == 0)
            return;

        gl.UseProgram(_yuvProgram);
        _yuvTextureYLocation = _getUniformLocation(_yuvProgram, "uTextureY");
        _yuvTextureULocation = _getUniformLocation(_yuvProgram, "uTextureU");
        _yuvTextureVLocation = _getUniformLocation(_yuvProgram, "uTextureV");
        _yuvPixelFormatLocation = _getUniformLocation(_yuvProgram, "uPixelFormat");

        if (_yuvTextureYLocation >= 0)
            _uniform1i(_yuvTextureYLocation, 0);
        if (_yuvTextureULocation >= 0)
            _uniform1i(_yuvTextureULocation, 1);
        if (_yuvTextureVLocation >= 0)
            _uniform1i(_yuvTextureVLocation, 2);

        gl.UseProgram(0);
    }

    private static byte[]? GetTightlyPackedPlane(VideoFrame frame, int planeIndex, int rowBytes, int rows, ref byte[]? scratch)
    {
        if (rowBytes <= 0 || rows <= 0)
            return null;

        var stride = frame.GetPlaneStride(planeIndex);
        var source = frame.GetPlaneData(planeIndex);
        if (source.Length == 0 || stride <= 0)
            return null;

        var tightLength = checked(rowBytes * rows);
        if (stride == rowBytes && source.Length >= tightLength)
            return source;

        if (scratch == null || scratch.Length < tightLength)
            scratch = new byte[tightLength];

        var destinationOffset = 0;
        var sourceOffset = 0;
        for (var row = 0; row < rows; row++)
        {
            if (sourceOffset + rowBytes > source.Length)
                return null;

            Buffer.BlockCopy(source, sourceOffset, scratch, destinationOffset, rowBytes);
            sourceOffset += stride;
            destinationOffset += rowBytes;
        }

        return scratch;
    }

    private static void ConvertNv12ToRgba(byte[] yPlane, byte[] uvPlane, int width, int height, byte[] destination)
    {
        var uvWidth = (width + 1) / 2;
        for (var y = 0; y < height; y++)
        {
            var yRowOffset = y * width;
            var uvRowOffset = (y / 2) * uvWidth * 2;
            var dstRowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var yValue = yPlane[yRowOffset + x];
                var uvOffset = uvRowOffset + (x / 2) * 2;
                var uValue = uvPlane[uvOffset];
                var vValue = uvPlane[uvOffset + 1];
                WriteRgbaPixel(destination, dstRowOffset + x * 4, yValue, uValue, vValue);
            }
        }
    }

    private static void ConvertYuv420pToRgba(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height, byte[] destination)
    {
        var chromaWidth = (width + 1) / 2;
        for (var y = 0; y < height; y++)
        {
            var yRowOffset = y * width;
            var uvRowOffset = (y / 2) * chromaWidth;
            var dstRowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var yValue = yPlane[yRowOffset + x];
                var uvOffset = uvRowOffset + (x / 2);
                var uValue = uPlane[uvOffset];
                var vValue = vPlane[uvOffset];
                WriteRgbaPixel(destination, dstRowOffset + x * 4, yValue, uValue, vValue);
            }
        }
    }

    private static void WriteRgbaPixel(byte[] destination, int destinationOffset, int y, int u, int v)
    {
        var c = y - 16;
        var d = u - 128;
        var e = v - 128;

        var r = (298 * c + 409 * e + 128) >> 8;
        var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
        var b = (298 * c + 516 * d + 128) >> 8;

        destination[destinationOffset] = (byte)Math.Clamp(r, 0, 255);
        destination[destinationOffset + 1] = (byte)Math.Clamp(g, 0, 255);
        destination[destinationOffset + 2] = (byte)Math.Clamp(b, 0, 255);
        destination[destinationOffset + 3] = 255;
    }

    private PixelSize GetPixelSize()
    {
        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));
        return new PixelSize(width, height);
    }

    private static PixelRect GetDrawViewport(PixelSize surface, int videoWidth, int videoHeight, bool keepAspectRatio)
    {
        if (!keepAspectRatio || videoWidth <= 0 || videoHeight <= 0)
            return new PixelRect(0, 0, surface.Width, surface.Height);

        var surfaceAspect = surface.Width / (double)surface.Height;
        var videoAspect = videoWidth / (double)videoHeight;

        if (videoAspect > surfaceAspect)
        {
            var targetHeight = Math.Max(1, (int)Math.Round(surface.Width / videoAspect));
            var y = (surface.Height - targetHeight) / 2;
            return new PixelRect(0, y, surface.Width, targetHeight);
        }

        var targetWidth = Math.Max(1, (int)Math.Round(surface.Height * videoAspect));
        var x = (surface.Width - targetWidth) / 2;
        return new PixelRect(x, 0, targetWidth, surface.Height);
    }
}