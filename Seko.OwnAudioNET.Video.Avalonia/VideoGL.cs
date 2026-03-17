using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Avalonia;

public partial class VideoGL : OpenGlControlBase, IDisposable
{
    private const int GlR8 = 0x8229;
    private const int GlR16 = 0x822A;
    private const int GlRg8 = 0x822B;
    private const int GlRg16 = 0x822C;
    private const int GlRed = 0x1903;
    private const int GlRg = 0x8227;
    private const int GlTexture1 = GlConsts.GL_TEXTURE0 + 1;
    private const int GlTexture2 = GlConsts.GL_TEXTURE0 + 2;
    private const int GlUnsignedShort = 0x1403;

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
    private TextureUploadState _rgbaState;
    private TextureUploadState _yState;
    private TextureUploadState _uvState;
    private TextureUploadState _uState;
    private TextureUploadState _vState;
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
    private bool _can16BitTextures;
    private byte[]? _plane0Scratch;
    private byte[]? _plane1Scratch;
    private byte[]? _plane2Scratch;
    private byte[]? _plane0Scratch8;
    private byte[]? _plane1Scratch8;
    private byte[]? _plane2Scratch8;
    private byte[]? _rgbaConvertedScratch;

    private bool _glReady;
    private bool _disposed;
    private bool _masterLoopStarted;
    private int _renderRequestQueued;
    private readonly Action _requestRenderAction;
    private Timer? _frameAdvanceTimer;
    private long _diagAdvanceTickCount;
    private long _diagAdvanceSuccessCount;
    private long _diagFrameReadyCount;
    private long _diagRenderCount;
    private long _diagRenderRequestPostedCount;
    private long _diagRenderRequestCoalescedCount;

    private struct TextureUploadState
    {
        public bool IsInitialized;
        public int Width;
        public int Height;
        public int InternalFormat;
        public int Format;
        public int Type;
    }

    public readonly record struct VideoGlDiagnostics(
        long AdvanceTicks,
        long AdvanceSuccess,
        long FrameReadyEvents,
        long RenderCalls,
        long RenderRequestPosted,
        long RenderRequestCoalesced);

    public VideoGlDiagnostics GetDiagnosticsSnapshot()
    {
        return new VideoGlDiagnostics(
            Interlocked.Read(ref _diagAdvanceTickCount),
            Interlocked.Read(ref _diagAdvanceSuccessCount),
            Interlocked.Read(ref _diagFrameReadyCount),
            Interlocked.Read(ref _diagRenderCount),
            Interlocked.Read(ref _diagRenderRequestPostedCount),
            Interlocked.Read(ref _diagRenderRequestCoalescedCount));
    }

    public bool KeepAspectRatio { get; set; } = true;

    public VideoGL(IVideoSource source, bool master = true)
    {
        _source = source;
        _master = master;
        _requestRenderAction = RequestNextFrameRendering;
        _source.FrameReadyFast += SourceFrameReadyFast;
        _source.StreamInfoChanged += OnSourceStreamInfoChanged;
    }

    private void OnSourceStreamInfoChanged(object? sender, Events.VideoStreamInfoChangedEventArgs e)
    {
        if (_frameAdvanceTimer == null)
            return;

        var intervalMs = ComputeAdvanceIntervalMs(e.StreamInfo.FrameRate);
        var period = TimeSpan.FromMilliseconds(intervalMs);
        _frameAdvanceTimer.Change(period, period);
    }

    private void SourceFrameReadyFast(VideoFrame frame, double _)
    {
        Interlocked.Increment(ref _diagFrameReadyCount);

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
        var glVersion = gl.ContextInfo.Version;
        _can16BitTextures = glVersion.Type == GlProfileType.OpenGL
            ? glVersion.Major >= 3
            : (glVersion.Major > 3 || (glVersion.Major == 3 && glVersion.Minor >= 2));
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
        Interlocked.Increment(ref _diagRenderCount);
        Interlocked.Exchange(ref _renderRequestQueued, 0);


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
                VideoPixelFormat.Yuv422p => UploadYuv422pFrame(gl, frame),
                VideoPixelFormat.Yuv444p => UploadYuv444pFrame(gl, frame),
                VideoPixelFormat.Yuv422p10le => UploadYuv422p10leFrame(gl, frame),
                VideoPixelFormat.P010le => UploadP010leFrame(gl, frame),
                VideoPixelFormat.Yuv420p10le => UploadYuv420p10leFrame(gl, frame),
                VideoPixelFormat.Yuv444p10le => UploadYuv444p10leFrame(gl, frame),
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

        // Rendering is triggered by frame promotions (FrameReadyFast) and property changes.
        // Avoid a continuous self-queued render loop that can monopolize UI/GPU time.
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
        _rgbaState = default;
        _yState = default;
        _uvState = default;
        _uState = default;
        _vState = default;
        _useYuvProgramThisFrame = false;
        base.OnOpenGlDeinit(gl);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _frameAdvanceTimer?.Dispose();
        _frameAdvanceTimer = null;
        _source.FrameReadyFast -= SourceFrameReadyFast;
        _source.StreamInfoChanged -= OnSourceStreamInfoChanged;

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

        // Drive frame advancement at ~125 Hz independently of the GL render cadence.
        // This decouples clock-based frame consumption from Avalonia compositor scheduling,
        // preventing the decode queue from stalling when renders are delayed.
        var intervalMs = ComputeAdvanceIntervalMs(_source.StreamInfo.FrameRate);
        _frameAdvanceTimer = new Timer(
            static state => ((VideoGL)state!).AdvanceFrameCallback(),
            this,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(intervalMs));

        // Seed the render loop so the control paints its initial state.
        QueueRenderRequest();
    }

    /// <summary>
    /// Computes the frame-advance polling interval in milliseconds.
    /// Targets ~80% of the stream's frame interval so we never miss a frame's presentation
    /// window; falls back to 8 ms (~125 Hz) when the frame rate is unknown.
    /// </summary>
    private static int ComputeAdvanceIntervalMs(double fps)
    {
        if (fps > 0 && !double.IsNaN(fps) && !double.IsInfinity(fps))
            return Math.Max(1, (int)(800.0 / fps));   // 80% of frame period in ms

        return 8; // default ~125 Hz
    }

    private void AdvanceFrameCallback()
    {
        if (_disposed || !_glReady)
            return;

        Interlocked.Increment(ref _diagAdvanceTickCount);
        if (_source.RequestNextFrame(out _))
            Interlocked.Increment(ref _diagAdvanceSuccessCount);

        // FrameReadyFast → SourceFrameReadyFast → QueueRenderRequest will fire on promotion.
    }

    private void QueueRenderRequest()
    {
        if (_disposed)
            return;

        if (Interlocked.Exchange(ref _renderRequestQueued, 1) == 1)
        {
            Interlocked.Increment(ref _diagRenderRequestCoalescedCount);
            return;
        }

        Interlocked.Increment(ref _diagRenderRequestPostedCount);

        // Use Render priority so the invalidation isn't delayed behind normal UI work,
        // keeping the GL surface updated within the same compositor frame.
        Dispatcher.UIThread.Post(_requestRenderAction, DispatcherPriority.Render);
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
                       // uPixelFormat:
                       //  1 = NV12         (semi-planar,  8-bit)
                       //  2 = YUV planar   (8-bit:  420p / 422p / 444p)
                       //  3 = P010LE       (semi-planar, 10-bit MSB-aligned)
                       //  4 = YUV10 planar (10-bit LSB-packed: 420p10le / 422p10le / 444p10le)
                       float scale = (uPixelFormat == 4) ? (65535.0 / 1023.0) : 1.0;

                       float y = texture(uTextureY, vTexCoord).r * scale;
                       float u;
                       float v;
                       if (uPixelFormat == 1 || uPixelFormat == 3)
                       {
                           vec2 uv = texture(uTextureU, vTexCoord).rg * scale;
                           u = uv.r - 0.5;
                           v = uv.g - 0.5;
                       }
                       else
                       {
                           u = texture(uTextureU, vTexCoord).r * scale - 0.5;
                           v = texture(uTextureV, vTexCoord).r * scale - 0.5;
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
                   // uPixelFormat:
                   //  1 = NV12         (semi-planar,  8-bit)
                   //  2 = YUV planar   (8-bit:  420p / 422p / 444p)
                   //  3 = P010LE       (semi-planar, 10-bit MSB-aligned)
                   //  4 = YUV10 planar (10-bit LSB-packed: 420p10le / 422p10le / 444p10le)
                   float scale = (uPixelFormat == 4) ? (65535.0 / 1023.0) : 1.0;

                   float y = texture(uTextureY, vTexCoord).r * scale;
                   float u;
                   float v;
                   if (uPixelFormat == 1 || uPixelFormat == 3)
                   {
                       vec2 uv = texture(uTextureU, vTexCoord).rg * scale;
                       u = uv.r - 0.5;
                       v = uv.g - 0.5;
                   }
                   else
                   {
                       u = texture(uTextureU, vTexCoord).r * scale - 0.5;
                       v = texture(uTextureV, vTexCoord).r * scale - 0.5;
                   }

                   FragColor = vec4(yuvToRgb(y, u, v), 1.0);
               }
               """;
    }

    // Upload helpers are defined in VideoGL.Uploads.cs

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

    private static void ConvertYuv422pToRgba(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height, byte[] destination)
    {
        var chromaWidth = (width + 1) / 2;
        for (var y = 0; y < height; y++)
        {
            var yRowOffset = y * width;
            var uvRowOffset = y * chromaWidth;
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

    private static void ConvertYuv444pToRgba(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height, byte[] destination)
    {
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            var dstRowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var idx = rowOffset + x;
                WriteRgbaPixel(destination, dstRowOffset + x * 4, yPlane[idx], uPlane[idx], vPlane[idx]);
            }
        }
    }

    private static byte[]? Downscale10BitTo8Bit(byte[] source16, int width, int height, ref byte[]? scratch)
    {
        var pixelCount = width * height;
        if (source16.Length < pixelCount * 2)
            return null;

        if (scratch == null || scratch.Length < pixelCount)
            scratch = new byte[pixelCount];

        for (var i = 0; i < pixelCount; i++)
        {
            var lo = source16[i * 2];
            var hi = source16[i * 2 + 1];
            var value = (ushort)(lo | (hi << 8));
            scratch[i] = (byte)(value >> 2);
        }

        return scratch;
    }

    private static byte[]? Downscale10BitMsbTo8Bit(byte[] source16, int width, int height, ref byte[]? scratch)
    {
        var pixelCount = width * height;
        if (source16.Length < pixelCount * 2)
            return null;

        if (scratch == null || scratch.Length < pixelCount)
            scratch = new byte[pixelCount];

        for (var i = 0; i < pixelCount; i++)
            scratch[i] = source16[i * 2 + 1];

        return scratch;
    }

    private static byte[]? Downscale10BitMsbDualTo8Bit(byte[] source16, int chromaWidth, int chromaHeight, ref byte[]? scratch)
    {
        var texelCount = chromaWidth * chromaHeight;
        var srcBytes = texelCount * 4;
        if (source16.Length < srcBytes)
            return null;

        var dstBytes = texelCount * 2;
        if (scratch == null || scratch.Length < dstBytes)
            scratch = new byte[dstBytes];

        for (var i = 0; i < texelCount; i++)
        {
            scratch[i * 2] = source16[i * 4 + 1];
            scratch[i * 2 + 1] = source16[i * 4 + 3];
        }

        return scratch;
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

