using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.OpenGL;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Avalonia;

public partial class VideoGL : OpenGlControlBase, IVideoOutput, IVideoPresentationSyncAwareOutput
{
    // Constants not in Avalonia's GlConsts — sourced from VideoGlConstants
    private const int GlTexture1      = VideoGlConstants.Texture1;
    private const int GlTexture2      = VideoGlConstants.Texture2;

    // QuadVertices → VideoGlGeometry.QuadVertices (shared)
    // TextureUploadState struct → Seko.OwnaudioNET.OpenGL.TextureUploadState (shared)

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

    private readonly IVideoOutputEngine? _sinkEngine;
    private readonly Lock _frameLock = new();
    private readonly Lock _mirrorLock = new();
    private readonly Action<VideoFrame, double> _mirrorFrameHandler;

    private VideoFrame? _latestFrame;
    private bool _hasFrame;
    private double _latestMasterTimestamp;
    private bool _hasLatestMasterTimestamp;
    private Action<VideoFrame, double>? _mirrorFrameSubscribers;
    private VideoGL? _mirrorParent;

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
    private VideoGlUploadPlanner.VideoGlYuvMode _yuvPixelFormatThisFrame = VideoGlUploadPlanner.VideoGlYuvMode.None;
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
    private int _renderRequestQueued;
    private readonly Action _requestRenderAction;
    private long _diagAdvanceTickCount;
    private long _diagAdvanceSuccessCount;
    private long _diagFrameReadyCount;
    private long _diagRenderCount;
    private long _diagRenderRequestPostedCount;
    private long _diagRenderRequestCoalescedCount;
    private int _presentationSyncMode = (int)VideoTransportPresentationSyncMode.PreferVSync;

    // TextureUploadState is defined in Seko.OwnaudioNET.OpenGL.VideoGlGeometry

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

    public Guid Id { get; } = Guid.NewGuid();

    public IVideoSource? Source { get; private set; }

    public bool IsAttached => Source != null;

    public VideoGL? MirrorParent
    {
        get
        {
            lock (_mirrorLock)
                return _mirrorParent;
        }
    }

    public bool IsMirroring => MirrorParent != null;

    public VideoTransportPresentationSyncMode PresentationSyncMode
    {
        get => (VideoTransportPresentationSyncMode)Volatile.Read(ref _presentationSyncMode);
        set
        {
            Volatile.Write(ref _presentationSyncMode, (int)value);
            PropagatePresentationSyncModeToMirrors(value);
        }
    }

    public VideoGL()
    {
        _mirrorFrameHandler = OnMirrorParentFrameReady;
        _requestRenderAction = RequestNextFrameRendering;
    }

    public VideoGL(IVideoOutputEngine engine)
    {
        _sinkEngine = engine ?? throw new ArgumentNullException(nameof(engine));
        _mirrorFrameHandler = OnMirrorParentFrameReady;
        _requestRenderAction = RequestNextFrameRendering;
        _sinkEngine.AddOutput(this);
    }

    public VideoGL(VideoGL parent)
        : this()
    {
        AttachMirrorParent(parent);
    }

    public static VideoGL CreateMirror(VideoGL parent)
    {
        return new VideoGL(parent);
    }

    private void SourceFrameReadyFast(VideoFrame frame, double _)
    {
        PushFrame(frame, _);
    }

    public bool PushFrame(VideoFrame frame, double masterTimestamp)
    {
        if (_disposed)
            return false;

        Interlocked.Increment(ref _diagFrameReadyCount);

        VideoFrame? previous;
        lock (_frameLock)
        {
            previous = _latestFrame;
            _latestFrame = frame.AddRef();
            _hasFrame = true;
            _latestMasterTimestamp = masterTimestamp;
            _hasLatestMasterTimestamp = true;
        }

        previous?.Dispose();

        Interlocked.Increment(ref _diagAdvanceTickCount);
        Interlocked.Increment(ref _diagAdvanceSuccessCount);
        QueueRenderRequest();
        DispatchMirroredFrame(frame, masterTimestamp);
        return true;
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

        var handle = GCHandle.Alloc(VideoGlGeometry.QuadVertices, GCHandleType.Pinned);
        try
        {
            gl.BufferData(
                GlConsts.GL_ARRAY_BUFFER,
                VideoGlGeometry.QuadVertices.Length * sizeof(float),
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
        QueueRenderRequest();
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
            _yuvPixelFormatThisFrame = VideoGlUploadPlanner.VideoGlYuvMode.None;

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
                _uniform1i(_yuvPixelFormatLocation, (int)_yuvPixelFormatThisFrame);
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

        DetachMirrorParent();
        ClearMirrorSubscribers();

        try
        {
            _sinkEngine?.RemoveOutput(this);
        }
        catch
        {
            // Best effort during shutdown.
        }

        DetachSource();

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
            _hasFrame = false;
        }
    }

    public bool AttachSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ReferenceEquals(Source, source))
            return true;

        DetachMirrorParent();
        DetachSource();
        source.FrameReadyFast += SourceFrameReadyFast;
        Source = source;
        QueueRenderRequest();
        return true;
    }

    public void DetachSource()
    {
        if (Source == null)
            return;

        Source.FrameReadyFast -= SourceFrameReadyFast;
        Source = null;
    }

    private void AttachMirrorParent(VideoGL parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (ReferenceEquals(parent, this))
            throw new ArgumentException("A VideoGL instance cannot mirror itself.", nameof(parent));

        VideoGL? previousParent;
        lock (_mirrorLock)
        {
            previousParent = _mirrorParent;
            if (ReferenceEquals(previousParent, parent))
                return;

            _mirrorParent = parent;
        }

        if (previousParent != null)
            previousParent.RemoveMirrorSubscriber(_mirrorFrameHandler);

        DetachSource();
        parent.AddMirrorSubscriber(_mirrorFrameHandler);
        PresentationSyncMode = parent.PresentationSyncMode;

        if (parent.TryGetCurrentFrameSnapshot(out var frame, out var masterTimestamp))
        {
            try
            {
                PushFrame(frame, masterTimestamp);
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    private void DetachMirrorParent()
    {
        VideoGL? parent;
        lock (_mirrorLock)
        {
            parent = _mirrorParent;
            _mirrorParent = null;
        }

        parent?.RemoveMirrorSubscriber(_mirrorFrameHandler);
    }

    private void AddMirrorSubscriber(Action<VideoFrame, double> subscriber)
    {
        lock (_mirrorLock)
            _mirrorFrameSubscribers += subscriber;
    }

    private void RemoveMirrorSubscriber(Action<VideoFrame, double> subscriber)
    {
        lock (_mirrorLock)
            _mirrorFrameSubscribers -= subscriber;
    }

    private void ClearMirrorSubscribers()
    {
        lock (_mirrorLock)
            _mirrorFrameSubscribers = null;
    }

    private void DispatchMirroredFrame(VideoFrame frame, double masterTimestamp)
    {
        Action<VideoFrame, double>? subscribers;
        lock (_mirrorLock)
            subscribers = _mirrorFrameSubscribers;

        if (subscribers == null)
            return;

        foreach (Action<VideoFrame, double> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(frame, masterTimestamp);
            }
            catch
            {
                // Best effort mirror fan-out.
            }
        }
    }

    private void PropagatePresentationSyncModeToMirrors(VideoTransportPresentationSyncMode mode)
    {
        Action<VideoFrame, double>? subscribers;
        lock (_mirrorLock)
            subscribers = _mirrorFrameSubscribers;

        if (subscribers == null)
            return;

        foreach (Action<VideoFrame, double> subscriber in subscribers.GetInvocationList())
        {
            if (subscriber.Target is VideoGL mirrorView)
                mirrorView.PresentationSyncMode = mode;
        }
    }

    private void OnMirrorParentFrameReady(VideoFrame frame, double masterTimestamp)
    {
        PushFrame(frame, masterTimestamp);
    }

    private bool TryGetCurrentFrameSnapshot(out VideoFrame frame, out double masterTimestamp)
    {
        lock (_frameLock)
        {
            if (!_hasFrame || _latestFrame == null || !_hasLatestMasterTimestamp)
            {
                frame = default!;
                masterTimestamp = 0;
                return false;
            }

            frame = _latestFrame.AddRef();
            masterTimestamp = _latestMasterTimestamp;
            return true;
        }
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

        // Avalonia presentation remains compositor-driven either way. For transport modes that
        // prefer display synchronization we post at Render priority; otherwise we use Normal
        // priority for a softer clock-driven invalidation path.
        var priority = PresentationSyncMode == VideoTransportPresentationSyncMode.None
            ? DispatcherPriority.Normal
            : DispatcherPriority.Render;

        Dispatcher.UIThread.Post(_requestRenderAction, priority);
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
        => profileType == GlProfileType.OpenGLES
            ? VideoGlShaders.VertexShaderEs
            : VideoGlShaders.VertexShaderCore;

    private static string BuildFragmentShader(GlProfileType profileType)
        => profileType == GlProfileType.OpenGLES
            ? VideoGlShaders.FragmentShaderEs
            : VideoGlShaders.FragmentShaderCore;

    private static string BuildYuvFragmentShader(GlProfileType profileType)
        => profileType == GlProfileType.OpenGLES
            ? VideoGlShaders.YuvFragmentShaderEs
            : VideoGlShaders.YuvFragmentShaderCore;

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
        => VideoFramePacking.GetTightlyPackedPlane(frame, planeIndex, rowBytes, rows, ref scratch);

    private static void ConvertNv12ToRgba(byte[] yPlane, byte[] uvPlane, int width, int height, byte[] destination)
        => VideoGlYuvConverter.ConvertNv12ToRgba(yPlane, uvPlane, width, height, destination);

    private static void ConvertYuv420pToRgba(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height, byte[] destination)
        => VideoGlYuvConverter.ConvertYuv420pToRgba(yPlane, uPlane, vPlane, width, height, destination);

    private static void ConvertYuv422pToRgba(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height, byte[] destination)
        => VideoGlYuvConverter.ConvertYuv422pToRgba(yPlane, uPlane, vPlane, width, height, destination);

    private static void ConvertYuv444pToRgba(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height, byte[] destination)
        => VideoGlYuvConverter.ConvertYuv444pToRgba(yPlane, uPlane, vPlane, width, height, destination);

    private static byte[]? Downscale10BitTo8Bit(byte[] source16, int width, int height, ref byte[]? scratch)
        => VideoGlYuvConverter.Downscale10BitTo8Bit(source16, width, height, ref scratch);

    private static byte[]? Downscale10BitMsbTo8Bit(byte[] source16, int width, int height, ref byte[]? scratch)
        => VideoGlYuvConverter.Downscale10BitMsbTo8Bit(source16, width, height, ref scratch);

    private static byte[]? Downscale10BitMsbDualTo8Bit(byte[] source16, int chromaWidth, int chromaHeight, ref byte[]? scratch)
        => VideoGlYuvConverter.Downscale10BitMsbDualTo8Bit(source16, chromaWidth, chromaHeight, ref scratch);

    private static void WriteRgbaPixel(byte[] destination, int destinationOffset, int y, int u, int v)
        => VideoGlYuvConverter.WriteRgbaPixel(destination, destinationOffset, y, u, v);

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

        var vp = VideoFramePacking.GetAspectFitViewport(surface.Width, surface.Height, videoWidth, videoHeight);
        return new PixelRect(vp.X, vp.Y, vp.Width, vp.Height);
    }
}

