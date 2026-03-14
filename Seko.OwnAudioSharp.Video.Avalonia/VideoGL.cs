using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Seko.OwnAudioSharp.Video;
using Seko.OwnAudioSharp.Video.Sources;

namespace VideoTest;

public class VideoGL : OpenGlControlBase, IDisposable
{
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

    private readonly FFVideoSource _source;
    private readonly bool _master;
    private readonly Lock _frameLock = new();

    private VideoFrame? _latestFrame;
    private bool _hasFrame;

    private int _program;
    private int _vbo;
    private int _vao;
    private int _texture;
    private int _textureWidth;
    private int _textureHeight;
    private bool _textureInitialized;
    private TexSubImage2DProc? _texSubImage2D;

    private bool _glReady;
    private bool _disposed;
    private bool _masterLoopStarted;

    public bool KeepAspectRatio { get; set; } = true;

    public VideoGL(FFVideoSource source, bool master = true)
    {
        _source = source;
        _master = master;
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

        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_disposed)
            return;

        if (change.Property == BoundsProperty || change.Property == Visual.IsVisibleProperty)
            RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        var texSubProc = gl.GetProcAddress("glTexSubImage2D");
        if (texSubProc != nint.Zero)
            _texSubImage2D = Marshal.GetDelegateForFunctionPointer<TexSubImage2DProc>(texSubProc);

        var vertexShaderSource = BuildVertexShader(gl.ContextInfo.Version.Type);
        var fragmentShaderSource = BuildFragmentShader(gl.ContextInfo.Version.Type);

        _program = BuildProgram(gl, vertexShaderSource, fragmentShaderSource);
        if (_program == 0)
            return;

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();

        gl.BindVertexArray(_vao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vbo);

        float[] vertices =
        [
            // pos.xy, uv.xy
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
             1f,  1f, 1f, 0f,
            -1f, -1f, 0f, 1f,
             1f,  1f, 1f, 0f,
            -1f,  1f, 0f, 0f
        ];

        var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
        try
        {
            gl.BufferData(
                GlConsts.GL_ARRAY_BUFFER,
                vertices.Length * sizeof(float),
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

        _texture = gl.GenTexture();
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _texture);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, GlConsts.GL_LINEAR);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, GlConsts.GL_LINEAR);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

        _glReady = true;

        if (_master)
            StartMasterLoop();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
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
            gl.ActiveTexture(GlConsts.GL_TEXTURE0);
            gl.BindTexture(GlConsts.GL_TEXTURE_2D, _texture);

            var internalFormat = GlConsts.GL_RGBA8;
                var format = GlConsts.GL_RGBA;

                unsafe
                {
                    var rgbaData = frame.RgbaData;
                    fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(rgbaData))
                    {
                        var pixels = (nint)ptr;

                        if (!_textureInitialized || _textureWidth != frame.Width || _textureHeight != frame.Height)
                        {
                            _textureWidth = frame.Width;
                            _textureHeight = frame.Height;
                            _textureInitialized = true;

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
                                frame.Width,
                                frame.Height,
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
                                frame.Width,
                                frame.Height,
                                0,
                                format,
                                GlConsts.GL_UNSIGNED_BYTE,
                                pixels);
                        }
                    }
                }
        }
        finally
        {
            frame.Dispose();
        }


        var drawViewport = GetDrawViewport(pixelSize, _textureWidth, _textureHeight, KeepAspectRatio);
        gl.Viewport(drawViewport.X, drawViewport.Y, drawViewport.Width, drawViewport.Height);

        gl.UseProgram(_program);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(GlConsts.GL_TRIANGLES, 0, 6);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, 0);

        if (_master && !_disposed && IsVisible)
            RequestNextFrameRendering();
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

        if (_texture != 0)
        {
            gl.DeleteTexture(_texture);
            _texture = 0;
        }

        if (_program != 0)
        {
            gl.DeleteProgram(_program);
            _program = 0;
        }

        _glReady = false;
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
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
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