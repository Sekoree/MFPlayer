using System.Diagnostics;
using System.Runtime.InteropServices;
using Seko.OwnAudioNET.Video.OpenGL;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.Sources;
using SDL3;

namespace Seko.OwnAudioNET.Video.SDL3;

/// <summary>
/// SDL3 / OpenGL 3.3 video output control.
/// <para>
/// Renders decoded <see cref="VideoFrame"/> objects (all <see cref="VideoPixelFormat"/> variants)
/// into its own SDL window via a dedicated background render thread.
/// </para>
/// <para>
/// The window can be used stand-alone, or embedded into a host such as Avalonia's
/// <c>NativeControlHost</c> by using <see cref="GetPlatformWindowHandle"/> /
/// <see cref="GetPlatformHandleDescriptor"/> after <see cref="Initialize"/> returns.
/// For Avalonia embedding, prefer <see cref="InitializeEmbedded"/> which creates a
/// borderless child window directly.
/// </para>
/// </summary>
public sealed partial class VideoSDL : IVideoOutput, IVideoPresentationSyncAwareOutput
{
    public event Action<SDL.Keycode>? KeyDown;
    private readonly Action<VideoFrame, double> _attachedFrameHandler;
    private IVideoSource? _attachedSource;

    // ── GL constants (sourced from VideoGlConstants) ─────────────────────────
    private const int GlArrayBuffer      = VideoGlConstants.ArrayBuffer;
    private const int GlStaticDraw       = VideoGlConstants.StaticDraw;
    private const int GlFloat            = VideoGlConstants.Float;
    private const int GlTriangles        = VideoGlConstants.Triangles;
    private const int GlTexture2D        = VideoGlConstants.Texture2D;
    private const int GlTexture0         = VideoGlConstants.Texture0;
    private const int GlTexture1         = VideoGlConstants.Texture1;
    private const int GlTexture2         = VideoGlConstants.Texture2;
    private const int GlColorBufferBit   = VideoGlConstants.ColorBufferBit;
    private const int GlVertexShader     = VideoGlConstants.VertexShader;
    private const int GlFragmentShader   = VideoGlConstants.FragmentShader;
    private const int GlCompileStatus    = VideoGlConstants.CompileStatus;
    private const int GlLinkStatus       = VideoGlConstants.LinkStatus;
    private const int GlTextureMinFilter = VideoGlConstants.TextureMinFilter;
    private const int GlTextureMagFilter = VideoGlConstants.TextureMagFilter;
    private const int GlLinear           = VideoGlConstants.Linear;
    private const int GlRgba8            = VideoGlConstants.Rgba8;
    private const int GlRgba             = VideoGlConstants.Rgba;
    private const int GlUnsignedByte     = VideoGlConstants.UnsignedByte;

    // ── GL delegate type declarations ────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportProc(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClearColorProc(float r, float g, float b, float a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClearProc(int mask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateShaderProc(int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ShaderSourceProc(int shader, int count, nint strings, nint lengths);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CompileShaderProc(int shader);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetShaderIvProc(int shader, int pname, out int param);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetShaderInfoLogProc(int shader, int maxLength, out int length, nint infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeleteShaderProc(int shader);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateProgramProc();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AttachShaderProc(int program, int shader);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LinkProgramProc(int program);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetProgramIvProc(int program, int pname, out int param);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetProgramInfoLogProc(int program, int maxLength, out int length, nint infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UseProgramProc(int program);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeleteProgramProc(int program);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BindAttribLocationProc(int program, int index, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetUniformLocationProc(int program, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Uniform1IProc(int location, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GenVertexArraysProc(int n, out int arrays);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BindVertexArrayProc(int array);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeleteVertexArraysProc(int n, in int arrays);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GenBuffersProc(int n, out int buffers);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BindBufferProc(int target, int buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BufferDataProc(int target, nint size, nint data, int usage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeleteBuffersProc(int n, in int buffers);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EnableVertexAttribArrayProc(int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VertexAttribPointerProc(int index, int size, int type, int normalized, int stride, nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GenTexturesProc(int n, out int textures);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActiveTextureProc(int texture);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BindTextureProc(int target, int texture);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TexParameteriProc(int target, int pname, int param);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TexImage2DProc(int target, int level, int internalFormat, int width, int height, int border, int format, int type, nint pixels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TexSubImage2DProc(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, nint pixels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeleteTexturesProc(int n, in int textures);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DrawArraysProc(int mode, int first, int count);

    // ── GL delegate instances ────────────────────────────────────────────────────
    private ViewportProc?              _glViewport;
    private ClearColorProc?            _glClearColor;
    private ClearProc?                 _glClear;
    private CreateShaderProc?          _glCreateShader;
    private ShaderSourceProc?          _glShaderSource;
    private CompileShaderProc?         _glCompileShader;
    private GetShaderIvProc?           _glGetShaderIv;
    private GetShaderInfoLogProc?      _glGetShaderInfoLog;
    private DeleteShaderProc?          _glDeleteShader;
    private CreateProgramProc?         _glCreateProgram;
    private AttachShaderProc?          _glAttachShader;
    private LinkProgramProc?           _glLinkProgram;
    private GetProgramIvProc?          _glGetProgramIv;
    private GetProgramInfoLogProc?     _glGetProgramInfoLog;
    private UseProgramProc?            _glUseProgram;
    private DeleteProgramProc?         _glDeleteProgram;
    private BindAttribLocationProc?    _glBindAttribLocation;
    private GetUniformLocationProc?    _glGetUniformLocation;
    private Uniform1IProc?             _glUniform1I;
    private GenVertexArraysProc?       _glGenVertexArrays;
    private BindVertexArrayProc?       _glBindVertexArray;
    private DeleteVertexArraysProc?    _glDeleteVertexArrays;
    private GenBuffersProc?            _glGenBuffers;
    private BindBufferProc?            _glBindBuffer;
    private BufferDataProc?            _glBufferData;
    private DeleteBuffersProc?         _glDeleteBuffers;
    private EnableVertexAttribArrayProc? _glEnableVertexAttribArray;
    private VertexAttribPointerProc?   _glVertexAttribPointer;
    private GenTexturesProc?           _glGenTextures;
    private ActiveTextureProc?         _glActiveTexture;
    private BindTextureProc?           _glBindTexture;
    private TexParameteriProc?         _glTexParameteri;
    private TexImage2DProc?            _glTexImage2D;
    private TexSubImage2DProc?         _glTexSubImage2D;
    private DeleteTexturesProc?        _glDeleteTextures;
    private DrawArraysProc?            _glDrawArrays;

    // ── GL state ─────────────────────────────────────────────────────────────────
    private bool _glInitialized;
    private int _program;
    private int _yuvProgram;
    private int _vao;
    private int _vbo;
    private int _textureRgba;
    private int _textureY;
    private int _textureUv;
    private int _textureU;
    private int _textureV;
    private int _textureWidth;
    private int _textureHeight;
    private bool _useYuvProgram;
    private VideoGlUploadPlanner.VideoGlYuvMode _yuvPixelFormat = VideoGlUploadPlanner.VideoGlYuvMode.None;
    private int _yuvTextureYLocation  = -1;
    private int _yuvTextureULocation  = -1;
    private int _yuvTextureVLocation  = -1;
    private int _yuvPixelFormatLocation = -1;
    private byte[]? _plane0Scratch;
    private byte[]? _plane1Scratch;
    private byte[]? _plane2Scratch;
    private TextureUploadState _rgbaState;
    private TextureUploadState _yState;
    private TextureUploadState _uvState;
    private TextureUploadState _uState;
    private TextureUploadState _vState;

    // ── HUD / diagnostics ────────────────────────────────────────────────────────
    private long   _renderedFrameCount;
    private long   _lastHudUpdateTime;
    private double _currentRenderFps;
    private string _currentPixelFormatInfo = "---";
    private double _currentVideoFps;
    private int    _currentQueueDepth;
    private double _currentUploadMsPerFrame;
    private double _currentAvDriftMs;
    private bool   _currentHardwareDecoding;
    private long   _currentDroppedFrames;
    private bool   _enableHudOverlay;
    private long   _diagRenderCalls;
    private long   _diagFramesSubmitted;
    private long   _diagFramesRendered;

    // ── SDL state ─────────────────────────────────────────────────────────────────
    private nint _sdlWindow;
    private nint _sdlGlContext;
    private bool _sdlOwnsSubsystem;
    private bool _disposed;
    private int _presentationSyncMode = (int)VideoTransportPresentationSyncMode.PreferVSync;
    private int _appliedSwapInterval = int.MinValue;

    // ── Render loop state ─────────────────────────────────────────────────────────
    private Thread?   _renderThread;
    private volatile bool _stopRequested;
    private readonly Lock _frameLock = new();
    private VideoFrame? _latestFrame;
    private bool        _hasFrame;
    private long        _latestFrameVersion;
            public VideoTransportPresentationSyncMode PresentationSyncMode
            {
                get => (VideoTransportPresentationSyncMode)Volatile.Read(ref _presentationSyncMode);
                set => Volatile.Write(ref _presentationSyncMode, (int)value);
            }

    private long        _lastRenderedVersion = -1;

    // ── Geometry / texture state (shared via Seko.OwnaudioNET.OpenGL) ────────────
    // QuadVertices  → VideoGlGeometry.QuadVertices
    // TextureUploadState struct → Seko.OwnaudioNET.OpenGL.TextureUploadState

    // ── Properties ───────────────────────────────────────────────────────────────

    /// <summary>The raw SDL3 window pointer. Available after <see cref="Initialize"/> succeeds.</summary>
    public nint SdlWindowPtr => _sdlWindow;

    /// <summary>True while the render thread is running and stop was not requested.</summary>
    public bool IsRunning => _renderThread is { IsAlive: true } && !_stopRequested;

    /// <summary>Current render frame rate (frames per second).</summary>
    public double RenderFps => _currentRenderFps;

    /// <summary>Video stream frame rate.</summary>
    public double VideoFps => _currentVideoFps;

    /// <summary>Pixel format info string shown in the HUD (e.g. <c>"yuv420p"</c> or <c>"yuv420p→nv12"</c>).</summary>
    public string PixelFormatInfo => _currentPixelFormatInfo;

    /// <summary>
    /// Enable or disable the on-screen diagnostic HUD overlay.
    /// Disabled by default for normal playback.
    /// </summary>
    public bool EnableHudOverlay
    {
        get => _enableHudOverlay;
        set
        {
            _enableHudOverlay = value;
            _hudTextDirty = true;
        }
    }

    /// <summary>Convenience helper to update the HUD visibility.</summary>
    public void SetHudOverlayEnabled(bool enabled) => EnableHudOverlay = enabled;

    /// <summary>Preserve source aspect ratio while rendering.</summary>
    public bool KeepAspectRatio { get; set; } = true;

    public Guid Id { get; } = Guid.NewGuid();

    public IVideoSource? Source => _attachedSource;

    public bool IsAttached => _attachedSource != null;

    public VideoSDL()
    {
        _attachedFrameHandler = OnSourceFrameReady;
    }

    public readonly record struct VideoSdlDiagnostics(long RenderCalls, long FramesSubmitted, long FramesRendered);

    public VideoSdlDiagnostics GetDiagnosticsSnapshot() =>
        new(Interlocked.Read(ref _diagRenderCalls), Interlocked.Read(ref _diagFramesSubmitted), Interlocked.Read(ref _diagFramesRendered));

    // ── Lifecycle ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a resizable stand-alone SDL window with an OpenGL 3.3 core context.
    /// Call <see cref="Start"/> afterwards to begin the render loop.
    /// </summary>
    public bool Initialize(int width, int height, string title, out string error)
    {
        error = string.Empty;
        if (_sdlWindow != nint.Zero) return true;

        if (!EnsureSdlVideoInit(out error)) return false;

        SDL.GLResetAttributes();
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);

        _sdlWindow = SDL.CreateWindow(title, width, height,
                                      SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable);
        if (_sdlWindow == nint.Zero)
        {
            error = $"SDL_CreateWindow failed: {SDL.GetError()}";
            TeardownSdlSubsystem();
            return false;
        }

        return CreateGlContext(out error);
    }

    /// <summary>
    /// Creates a borderless child SDL window attached to an existing native parent window,
    /// suitable for embedding in Avalonia via <c>NativeControlHost</c>.
    /// <para>
    /// Pass the handle provided by <c>IPlatformHandle.Handle</c> (HWND on Windows,
    /// X11 Window XID on Linux/X11, wl_surface* on Wayland).
    /// </para>
    /// </summary>
    public bool InitializeEmbedded(nint parentHandle, int width, int height, out string error)
    {
        error = string.Empty;
        if (_sdlWindow != nint.Zero) return true;

        if (!EnsureSdlVideoInit(out error)) return false;

        SDL.GLResetAttributes();
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);

        var createProps = SDL.CreateProperties();
        try
        {
            SDL.SetBooleanProperty(createProps, SDL.Props.WindowCreateOpenGLBoolean, true);
            SDL.SetBooleanProperty(createProps, SDL.Props.WindowCreateBorderlessBoolean, true);
            SDL.SetNumberProperty(createProps, SDL.Props.WindowCreateWidthNumber, width);
            SDL.SetNumberProperty(createProps, SDL.Props.WindowCreateHeightNumber, height);

            if (OperatingSystem.IsWindows())
                SDL.SetPointerProperty(createProps, SDL.Props.WindowCreateWin32HWNDPointer, parentHandle);
            else if (OperatingSystem.IsLinux())
                SDL.SetNumberProperty(createProps, SDL.Props.WindowCreateX11WindowNumber, parentHandle.ToInt64());
            else if (OperatingSystem.IsMacOS())
                SDL.SetPointerProperty(createProps, SDL.Props.WindowCreateCocoaWindowPointer, parentHandle);

            _sdlWindow = SDL.CreateWindowWithProperties(createProps);
        }
        finally
        {
            SDL.DestroyProperties(createProps);
        }

        if (_sdlWindow == nint.Zero)
        {
            error = $"SDL_CreateWindowWithProperties failed: {SDL.GetError()}";
            TeardownSdlSubsystem();
            return false;
        }

        return CreateGlContext(out error);
    }

    /// <summary>Start the background render loop thread.</summary>
    public void Start()
    {
        if (_renderThread != null || _sdlWindow == nint.Zero) return;
        _stopRequested = false;
        _renderThread = new Thread(RenderLoop)
        {
            Name = "VideoSDL.RenderLoop",
            IsBackground = true
        };
        _renderThread.Start();
    }

    /// <summary>
    /// Signal the render loop to stop and wait up to 3 seconds for the thread to exit.
    /// Safe to call even if <see cref="Start"/> was never called.
    /// </summary>
    public void Stop()
    {
        _stopRequested = true;
        var renderThread = _renderThread;
        if (renderThread != null && !ReferenceEquals(Thread.CurrentThread, renderThread))
            renderThread.Join(TimeSpan.FromSeconds(3));

        _renderThread = null;
    }

    /// <summary>
    /// Queues a decoded frame for display. Frame delivery is expected to come from
    /// <see cref="AttachSource(IVideoSource)"/> (typically via <see cref="VideoMixer"/> or explicit caller wiring).
    /// </summary>
    public bool PushFrame(VideoFrame frame, double masterTimestamp)
    {
        if (_disposed)
            return false;

        Interlocked.Increment(ref _diagFramesSubmitted);
        VideoFrame? previous;
        lock (_frameLock)
        {
            previous = _latestFrame;
            _latestFrame = frame.AddRef();
            _hasFrame = true;
            _latestFrameVersion++;
        }
        previous?.Dispose();
        return true;
    }

    /// <summary>Update the pixel-format string shown in the HUD. Thread-safe.</summary>
    public void UpdateFormatInfo(string sourceFormatName, string outputFormatName, double videoFrameRate)
    {
        var src = FmtName(sourceFormatName);
        var dst = FmtName(outputFormatName);
        _currentPixelFormatInfo = string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)
            ? src : $"{src}→{dst}";
        _currentVideoFps = videoFrameRate;
        _hudTextDirty = true;
    }

    /// <summary>Update per-second diagnostics shown in the HUD. Thread-safe.</summary>
    public void UpdateHudDiagnostics(int queueDepth, double uploadMsPerFrame, double avDriftMs,
                                      bool isHardwareDecoding, long droppedFrames)
    {
        _currentQueueDepth       = Math.Max(0, queueDepth);
        _currentUploadMsPerFrame = Math.Max(0, uploadMsPerFrame);
        _currentAvDriftMs        = avDriftMs;
        _currentHardwareDecoding = isHardwareDecoding;
        _currentDroppedFrames    = Math.Max(0, droppedFrames);
        _hudTextDirty = true;
    }

    /// <summary>
    /// Returns the OS-level native handle of the SDL window, suitable for passing to
    /// Avalonia's <c>NativeControlHost</c> as <c>IPlatformHandle.Handle</c>.
    /// <list type="table">
    ///   <item><term>Windows</term><description>HWND</description></item>
    ///   <item><term>Linux/X11</term><description>X11 Window (XID)</description></item>
    ///   <item><term>Linux/Wayland</term><description>wl_surface*</description></item>
    ///   <item><term>macOS</term><description>NSWindow*</description></item>
    /// </list>
    /// Returns <see cref="nint.Zero"/> if the window has not been created yet.
    /// </summary>
    public nint GetPlatformWindowHandle()
    {
        if (_sdlWindow == nint.Zero) return nint.Zero;
        var props = SDL.GetWindowProperties(_sdlWindow);
        if (props == 0) return nint.Zero;

        if (OperatingSystem.IsWindows())
            return SDL.GetPointerProperty(props, SDL.Props.WindowWin32HWNDPointer, nint.Zero);

        if (OperatingSystem.IsLinux())
        {
            var x11 = SDL.GetNumberProperty(props, SDL.Props.WindowX11WindowNumber, 0);
            if (x11 != 0) return (nint)x11;
            return SDL.GetPointerProperty(props, SDL.Props.WindowWaylandSurfacePointer, nint.Zero);
        }

        if (OperatingSystem.IsMacOS())
            return SDL.GetPointerProperty(props, SDL.Props.WindowCocoaWindowPointer, nint.Zero);

        return nint.Zero;
    }

    /// <summary>
    /// Returns the handle-descriptor string for <see cref="GetPlatformWindowHandle"/>,
    /// usable as <c>IPlatformHandle.HandleDescriptor</c> in Avalonia
    /// (<c>"HWND"</c>, <c>"XID"</c>, <c>"NSView"</c>, or <c>"wayland-surface"</c>).
    /// </summary>
    public string GetPlatformHandleDescriptor()
    {
        if (OperatingSystem.IsWindows()) return "HWND";
        if (OperatingSystem.IsMacOS())  return "NSView";
        if (_sdlWindow != nint.Zero)
        {
            var props = SDL.GetWindowProperties(_sdlWindow);
            if (props != 0 && SDL.GetNumberProperty(props, SDL.Props.WindowX11WindowNumber, 0) != 0)
                return "XID";
        }
        return "wayland-surface";
    }

    /// <summary>Show or hide the SDL window (no-op in embedded mode).</summary>
    public void SetVisible(bool visible)
    {
        if (_sdlWindow == nint.Zero) return;
        if (visible) SDL.ShowWindow(_sdlWindow);
        else         SDL.HideWindow(_sdlWindow);
    }

    /// <summary>
    /// Resize the SDL window. Call this when the host container changes size
    /// in embedded mode so the viewport stays correct.
    /// </summary>
    public void Resize(int width, int height)
    {
        if (_sdlWindow != nint.Zero)
            SDL.SetWindowSize(_sdlWindow, Math.Max(1, width), Math.Max(1, height));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DetachSource();

        Stop();

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        if (_sdlGlContext != nint.Zero)
        {
            SDL.GLDestroyContext(_sdlGlContext);
            _sdlGlContext = nint.Zero;
        }
        if (_sdlWindow != nint.Zero)
        {
            SDL.DestroyWindow(_sdlWindow);
            _sdlWindow = nint.Zero;
        }
        if (_sdlOwnsSubsystem)
            SDL.Quit();
    }

    public bool AttachSource(IVideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ReferenceEquals(_attachedSource, source))
            return true;

        DetachSource();
        source.FrameReadyFast += _attachedFrameHandler;
        _attachedSource = source;
        return true;
    }

    public void DetachSource()
    {
        if (_attachedSource == null)
            return;

        _attachedSource.FrameReadyFast -= _attachedFrameHandler;
        _attachedSource = null;
    }

    private void OnSourceFrameReady(VideoFrame frame, double _)
    {
        PushFrame(frame, _);
    }

    // ── Render loop (background thread) ──────────────────────────────────────────

    private void RenderLoop()
    {
        if (!SDL.GLMakeCurrent(_sdlWindow, _sdlGlContext))
        {
            Console.Error.WriteLine($"[VideoSDL] GLMakeCurrent failed: {SDL.GetError()}");
            return;
        }
        ApplyPresentationSyncModeIfNeeded();

        if (!InitializeGl(out var glError))
        {
            Console.Error.WriteLine($"[VideoSDL] OpenGL init failed: {glError}");
            SDL.GLMakeCurrent(_sdlWindow, nint.Zero);
            return;
        }

        _renderedFrameCount = 0;
        _currentRenderFps   = 0;
        _lastHudUpdateTime  = Stopwatch.GetTimestamp();

        while (!_stopRequested)
        {
            ApplyPresentationSyncModeIfNeeded();

            while (SDL.PollEvent(out var sdlEvent))
            {
                switch ((SDL.EventType)sdlEvent.Type)
                {
                    case SDL.EventType.Quit:
                    case SDL.EventType.WindowCloseRequested:
                        _stopRequested = true;
                        break;
                    case SDL.EventType.KeyDown:
                        KeyDown?.Invoke(sdlEvent.Key.Key);
                        if (sdlEvent.Key.Key == SDL.Keycode.Escape)
                            _stopRequested = true;
                        break;
                }
            }

            if (_stopRequested) break;

            SDL.GetWindowSizeInPixels(_sdlWindow, out var pixW, out var pixH);
            var w = Math.Max(1, pixW);
            var h = Math.Max(1, pixH);

            VideoFrame? frame = null;
            long version = -1;
            lock (_frameLock)
            {
                if (_hasFrame)
                {
                    frame   = _latestFrame?.AddRef();
                    version = _latestFrameVersion;
                }
            }

            if (frame != null)
            {
                try
                {
                    if (version != _lastRenderedVersion)
                    {
                        if (RenderFrame(frame, w, h))
                            _lastRenderedVersion = version;
                    }
                    else
                    {
                        RenderLastFrame(w, h);
                    }
                }
                finally { frame.Dispose(); }
            }
            else
            {
                RenderLastFrame(w, h);
            }

            SDL.GLSwapWindow(_sdlWindow);
        }

        DisposeGlResources();
        SDL.GLMakeCurrent(_sdlWindow, nint.Zero);
    }

    private void ApplyPresentationSyncModeIfNeeded()
    {
        // SDL exposes swap interval as an on/off preference, so Prefer/Require both map to a
        // best-effort VSync request while None explicitly disables it.
        var desiredSwapInterval = PresentationSyncMode == VideoTransportPresentationSyncMode.None ? 0 : 1;
        if (desiredSwapInterval == _appliedSwapInterval)
            return;

        if (!SDL.GLSetSwapInterval(desiredSwapInterval) && desiredSwapInterval != 0)
        {
            if (SDL.GLSetSwapInterval(0))
                desiredSwapInterval = 0;
            else
                return;
        }

        _appliedSwapInterval = desiredSwapInterval;
    }

    // ── GL initialisation (runs on render thread) ─────────────────────────────────

    private bool InitializeGl(out string error)
    {
        error = string.Empty;
        if (!LoadGlFunctions(out error)) return false;

        var vs = BuildVertexShader();
        _program    = BuildProgram(vs, BuildFragmentShader(),    out error); if (_program    == 0) return false;
        _yuvProgram = BuildProgram(vs, BuildYuvFragmentShader(), out error); if (_yuvProgram == 0) return false;

        _yuvTextureYLocation    = _glGetUniformLocation!(_yuvProgram, "uTextureY");
        _yuvTextureULocation    = _glGetUniformLocation!(_yuvProgram, "uTextureU");
        _yuvTextureVLocation    = _glGetUniformLocation!(_yuvProgram, "uTextureV");
        _yuvPixelFormatLocation = _glGetUniformLocation!(_yuvProgram, "uPixelFormat");

        _glUseProgram!(_yuvProgram);
        if (_yuvTextureYLocation    >= 0) _glUniform1I!(_yuvTextureYLocation,    0);
        if (_yuvTextureULocation    >= 0) _glUniform1I!(_yuvTextureULocation,    1);
        if (_yuvTextureVLocation    >= 0) _glUniform1I!(_yuvTextureVLocation,    2);
        _glUseProgram!(0);

        _glUseProgram!(_program);
        var uTex = _glGetUniformLocation!(_program, "uTexture");
        if (uTex >= 0) _glUniform1I!(uTex, 0);
        _glUseProgram!(0);

        _glGenVertexArrays!(1, out _vao);
        _glGenBuffers!(1, out _vbo);
        _glBindVertexArray!(_vao);
        _glBindBuffer!(GlArrayBuffer, _vbo);

        var h = GCHandle.Alloc(VideoGlGeometry.QuadVertices, GCHandleType.Pinned);
        try   { _glBufferData!(GlArrayBuffer, VideoGlGeometry.QuadVertices.Length * sizeof(float), h.AddrOfPinnedObject(), GlStaticDraw); }
        finally { h.Free(); }

        var stride = 4 * sizeof(float);
        _glEnableVertexAttribArray!(0); _glVertexAttribPointer!(0, 2, GlFloat, 0, stride, nint.Zero);
        _glEnableVertexAttribArray!(1); _glVertexAttribPointer!(1, 2, GlFloat, 0, stride, 2 * sizeof(float));
        _glBindBuffer!(GlArrayBuffer, 0);
        _glBindVertexArray!(0);

        _glGenTextures!(1, out _textureRgba);
        _glGenTextures!(1, out _textureY);
        _glGenTextures!(1, out _textureUv);
        _glGenTextures!(1, out _textureU);
        _glGenTextures!(1, out _textureV);
        ConfigureTexture(_textureRgba);
        ConfigureTexture(_textureY);
        ConfigureTexture(_textureUv);
        ConfigureTexture(_textureU);
        ConfigureTexture(_textureV);

        LoadHudGlFunctions();
        InitializeHudRendering();

        _glInitialized = true;
        return true;
    }

    // ── Frame rendering ───────────────────────────────────────────────────────────

    private bool RenderFrame(VideoFrame frame, int surfaceW, int surfaceH)
    {
        Interlocked.Increment(ref _diagRenderCalls);
        if (!_glInitialized) return false;

        _glViewport!(0, 0, surfaceW, surfaceH);
        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(GlColorBufferBit);

        var uploaded = frame.PixelFormat switch
        {
            VideoPixelFormat.Rgba32      => UploadRgbaFrame(frame),
            VideoPixelFormat.Nv12        => UploadNv12Frame(frame),
            VideoPixelFormat.Yuv420p     => UploadYuv420pFrame(frame),
            VideoPixelFormat.Yuv422p     => UploadYuv422pFrame(frame),
            VideoPixelFormat.Yuv444p     => UploadYuv444pFrame(frame),
            VideoPixelFormat.Yuv422p10le => UploadYuv422p10leFrame(frame),
            VideoPixelFormat.Yuv420p10le => UploadYuv420p10leFrame(frame),
            VideoPixelFormat.Yuv444p10le => UploadYuv444p10leFrame(frame),
            VideoPixelFormat.P010le      => UploadP010leFrame(frame),
            _                            => false
        };

        if (!uploaded)
        {
            if (_textureWidth > 0 && _textureHeight > 0)
                DrawCurrentFrame(surfaceW, surfaceH);
            return false;
        }

        DrawCurrentFrame(surfaceW, surfaceH);
        RenderHudOverlay(surfaceW, surfaceH);
        UpdateRenderFps();
        Interlocked.Increment(ref _diagFramesRendered);
        return true;
    }

    private void RenderLastFrame(int surfaceW, int surfaceH)
    {
        Interlocked.Increment(ref _diagRenderCalls);
        if (!_glInitialized)
            return;

        _glViewport!(0, 0, surfaceW, surfaceH);
        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(GlColorBufferBit);

        if (_textureWidth <= 0 || _textureHeight <= 0)
            return;

        DrawCurrentFrame(surfaceW, surfaceH);
        RenderHudOverlay(surfaceW, surfaceH);
        UpdateRenderFps();
    }

    private void DrawCurrentFrame(int surfaceW, int surfaceH)
    {
        var vp = KeepAspectRatio
            ? GetAspectFitRect(surfaceW, surfaceH, _textureWidth, _textureHeight)
            : new VideoGlViewport(0, 0, Math.Max(1, surfaceW), Math.Max(1, surfaceH));
        _glViewport!(vp.X, vp.Y, vp.Width, vp.Height);

        if (!_useYuvProgram)
        {
            _glActiveTexture!(GlTexture0);
            _glBindTexture!(GlTexture2D, _textureRgba);
        }
        else
        {
            _glActiveTexture!(GlTexture0); _glBindTexture!(GlTexture2D, _textureY);
            var semiPlanar = VideoGlUploadPlanner.IsSemiPlanar(_yuvPixelFormat);
            _glActiveTexture!(GlTexture1); _glBindTexture!(GlTexture2D, semiPlanar ? _textureUv : _textureU);
            if (!semiPlanar) { _glActiveTexture!(GlTexture2); _glBindTexture!(GlTexture2D, _textureV); }
            _glActiveTexture!(GlTexture0);
        }

        if (_useYuvProgram)
        {
            _glUseProgram!(_yuvProgram);
            if (_yuvPixelFormatLocation >= 0)
                _glUniform1I!(_yuvPixelFormatLocation, (int)_yuvPixelFormat);
        }
        else
        {
            _glUseProgram!(_program);
        }

        _glBindVertexArray!(_vao);
        _glDrawArrays!(GlTriangles, 0, 6);
        _glBindVertexArray!(0);
        _glUseProgram!(0);
    }

    // ── GL resource cleanup ───────────────────────────────────────────────────────

    private void DisposeGlResources()
    {
        DisposeHudResources();

        if (_textureRgba != 0) { var t = _textureRgba; _glDeleteTextures?.Invoke(1, in t); }
        if (_textureY    != 0) { var t = _textureY;    _glDeleteTextures?.Invoke(1, in t); }
        if (_textureUv   != 0) { var t = _textureUv;   _glDeleteTextures?.Invoke(1, in t); }
        if (_textureU    != 0) { var t = _textureU;    _glDeleteTextures?.Invoke(1, in t); }
        if (_textureV    != 0) { var t = _textureV;    _glDeleteTextures?.Invoke(1, in t); }
        if (_vbo != 0) { var b = _vbo; _glDeleteBuffers?.Invoke(1, in b); }
        if (_vao != 0) { var a = _vao; _glDeleteVertexArrays?.Invoke(1, in a); }
        if (_program    != 0) _glDeleteProgram?.Invoke(_program);
        if (_yuvProgram != 0) _glDeleteProgram?.Invoke(_yuvProgram);

        _glInitialized = false;
    }

    // ── GL function loading ───────────────────────────────────────────────────────

    private bool LoadGlFunctions(out string error)
    {
        error = string.Empty;

        bool Load<T>(string name, out T? d) where T : Delegate
        {
            var p = SDL.GLGetProcAddress(name);
            if (p == nint.Zero) { d = null; return false; }
            d = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }

        if (!Load("glViewport",                out _glViewport)               ||
            !Load("glClearColor",              out _glClearColor)             ||
            !Load("glClear",                   out _glClear)                  ||
            !Load("glCreateShader",            out _glCreateShader)           ||
            !Load("glShaderSource",            out _glShaderSource)           ||
            !Load("glCompileShader",           out _glCompileShader)          ||
            !Load("glGetShaderiv",             out _glGetShaderIv)            ||
            !Load("glGetShaderInfoLog",        out _glGetShaderInfoLog)       ||
            !Load("glDeleteShader",            out _glDeleteShader)           ||
            !Load("glCreateProgram",           out _glCreateProgram)          ||
            !Load("glAttachShader",            out _glAttachShader)           ||
            !Load("glLinkProgram",             out _glLinkProgram)            ||
            !Load("glGetProgramiv",            out _glGetProgramIv)           ||
            !Load("glGetProgramInfoLog",       out _glGetProgramInfoLog)      ||
            !Load("glUseProgram",              out _glUseProgram)             ||
            !Load("glDeleteProgram",           out _glDeleteProgram)          ||
            !Load("glBindAttribLocation",      out _glBindAttribLocation)     ||
            !Load("glGetUniformLocation",      out _glGetUniformLocation)     ||
            !Load("glUniform1i",               out _glUniform1I)              ||
            !Load("glGenVertexArrays",         out _glGenVertexArrays)        ||
            !Load("glBindVertexArray",         out _glBindVertexArray)        ||
            !Load("glDeleteVertexArrays",      out _glDeleteVertexArrays)     ||
            !Load("glGenBuffers",              out _glGenBuffers)             ||
            !Load("glBindBuffer",              out _glBindBuffer)             ||
            !Load("glBufferData",              out _glBufferData)             ||
            !Load("glDeleteBuffers",           out _glDeleteBuffers)          ||
            !Load("glEnableVertexAttribArray", out _glEnableVertexAttribArray)||
            !Load("glVertexAttribPointer",     out _glVertexAttribPointer)    ||
            !Load("glGenTextures",             out _glGenTextures)            ||
            !Load("glActiveTexture",           out _glActiveTexture)          ||
            !Load("glBindTexture",             out _glBindTexture)            ||
            !Load("glTexParameteri",           out _glTexParameteri)          ||
            !Load("glTexImage2D",              out _glTexImage2D)             ||
            !Load("glDeleteTextures",          out _glDeleteTextures)         ||
            !Load("glDrawArrays",              out _glDrawArrays))
        {
            error = "Failed to load required OpenGL functions.";
            return false;
        }

        // Optional fast-path for sub-image updates
        Load("glTexSubImage2D", out _glTexSubImage2D);
        return true;
    }

    private void ConfigureTexture(int texture)
    {
        _glBindTexture!(GlTexture2D, texture);
        _glTexParameteri!(GlTexture2D, GlTextureMinFilter, GlLinear);
        _glTexParameteri!(GlTexture2D, GlTextureMagFilter, GlLinear);
        _glBindTexture!(GlTexture2D, 0);
    }

    private int BuildProgram(string vertexSrc, string fragmentSrc, out string error)
    {
        error = string.Empty;
        var vs = CompileShader(GlVertexShader,   vertexSrc,   out error); if (vs == 0) return 0;
        var fs = CompileShader(GlFragmentShader, fragmentSrc, out error);
        if (fs == 0) { _glDeleteShader!(vs); return 0; }

        var prog = _glCreateProgram!();
        _glAttachShader!(prog, vs); _glAttachShader!(prog, fs);
        _glBindAttribLocation!(prog, 0, "aPosition");
        _glBindAttribLocation!(prog, 1, "aTexCoord");
        _glLinkProgram!(prog);
        _glDeleteShader!(vs); _glDeleteShader!(fs);

        _glGetProgramIv!(prog, GlLinkStatus, out var linked);
        if (linked == 0) { error = GetProgramLog(prog); _glDeleteProgram!(prog); return 0; }
        return prog;
    }

    private int CompileShader(int shaderType, string source, out string error)
    {
        error = string.Empty;
        var shader = _glCreateShader!(shaderType);
        var srcPtr = Marshal.StringToHGlobalAnsi(source);
        try
        {
            var arr = Marshal.AllocHGlobal(nint.Size);
            try { Marshal.WriteIntPtr(arr, srcPtr); _glShaderSource!(shader, 1, arr, nint.Zero); }
            finally { Marshal.FreeHGlobal(arr); }
        }
        finally { Marshal.FreeHGlobal(srcPtr); }

        _glCompileShader!(shader);
        _glGetShaderIv!(shader, GlCompileStatus, out var ok);
        if (ok == 0) { error = GetShaderLog(shader); _glDeleteShader!(shader); return 0; }
        return shader;
    }

    private string GetShaderLog(int shader)
    {
        var buf = Marshal.AllocHGlobal(4096);
        try
        {
            _glGetShaderInfoLog!(shader, 4096, out var len, buf);
            if (len <= 0)
                return "Shader compile failed.";

            var message = Marshal.PtrToStringAnsi(buf, len);
            return string.IsNullOrWhiteSpace(message) ? "Shader compile failed." : message;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private string GetProgramLog(int program)
    {
        var buf = Marshal.AllocHGlobal(4096);
        try
        {
            _glGetProgramInfoLog!(program, 4096, out var len, buf);
            if (len <= 0)
                return "Program link failed.";

            var message = Marshal.PtrToStringAnsi(buf, len);
            return string.IsNullOrWhiteSpace(message) ? "Program link failed." : message;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── Texture upload ────────────────────────────────────────────────────────────

    private void UploadTexture2D(ref TextureUploadState state,
        int width, int height, int internalFormat, int format, int type, byte[] data)
    {
        VideoGlTextureUploadOrchestrator.UploadTexture2D(
            ref state,
            width,
            height,
            internalFormat,
            format,
            type,
            data,
            (ifmt, w, h, fmt, t, pixels) => _glTexImage2D!(GlTexture2D, 0, ifmt, w, h, 0, fmt, t, pixels),
            _glTexSubImage2D == null
                ? null
                : (w, h, fmt, t, pixels) => _glTexSubImage2D(GlTexture2D, 0, 0, 0, w, h, fmt, t, pixels));
    }

    // ── Upload methods ────────────────────────────────────────────────────────────

    private bool UploadRgbaFrame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.Rgba32);

    private bool UploadNv12Frame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.Nv12);

    private bool UploadYuv420pFrame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.Yuv420p);

    private bool UploadYuv422pFrame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.Yuv422p);

    private bool UploadYuv444pFrame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.Yuv444p);

    private bool UploadYuv420p10leFrame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.Yuv420p10le);

    private bool UploadYuv422p10leFrame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.Yuv422p10le);

    private bool UploadYuv444p10leFrame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.Yuv444p10le);

    private bool UploadP010leFrame(VideoFrame frame)
        => UploadFrameWithGpuPlan(frame, VideoPixelFormat.P010le);

    private bool UploadFrameWithGpuPlan(VideoFrame frame, VideoPixelFormat pixelFormat)
    {
        var w = frame.Width;
        var h = frame.Height;
        if (w <= 0 || h <= 0)
            return false;

        var plan = VideoGlUploadPlanner.CreateGpuUploadPlan(pixelFormat, w, h);
        if (!plan.IsSupported)
            return false;

        byte[]? p0 = null;
        byte[]? p1 = null;
        byte[]? p2 = null;

        for (var i = 0; i < plan.PlaneCount; i++)
        {
            var descriptor = GetPlaneDescriptor(plan, i);
            ref var scratch = ref GetPlaneScratch(descriptor.PlaneIndex);
            var packed = GetTightlyPackedPlane(
                frame,
                descriptor.PlaneIndex,
                descriptor.RowBytes,
                descriptor.Height,
                ref scratch);
            if (packed == null)
                return false;

            if (i == 0) p0 = packed;
            else if (i == 1) p1 = packed;
            else p2 = packed;
        }

        for (var i = 0; i < plan.PlaneCount; i++)
        {
            var descriptor = GetPlaneDescriptor(plan, i);
            var data = i == 0 ? p0 : i == 1 ? p1 : p2;
            if (data == null)
                return false;

            var textureUnit = GetTextureUnit(descriptor.Slot);
            var textureId = GetTextureId(descriptor.Slot);

            _glActiveTexture!(textureUnit);
            _glBindTexture!(GlTexture2D, textureId);

            ref var state = ref GetTextureState(descriptor.Slot);
            UploadTexture2D(
                ref state,
                descriptor.Width,
                descriptor.Height,
                descriptor.InternalFormat,
                descriptor.Format,
                descriptor.Type,
                data);
        }

        if (plan.IsYuv && VideoGlUploadPlanner.IsSemiPlanar(plan.YuvMode))
        {
            _glActiveTexture!(GlTexture2);
            _glBindTexture!(GlTexture2D, _textureUv);
        }

        _glActiveTexture!(GlTexture0);
        SetCurrentFrameState(w, h, plan.IsYuv, plan.YuvMode);
        return true;
    }

    private static VideoGlUploadPlanner.VideoGlPlanePlan GetPlaneDescriptor(
        in VideoGlUploadPlanner.VideoGlGpuPlan plan,
        int index)
        => index switch
        {
            0 => plan.Plane0,
            1 => plan.Plane1,
            2 => plan.Plane2,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

    private ref byte[]? GetPlaneScratch(int planeIndex)
    {
        switch (planeIndex)
        {
            case 0: return ref _plane0Scratch;
            case 1: return ref _plane1Scratch;
            case 2: return ref _plane2Scratch;
            default: throw new ArgumentOutOfRangeException(nameof(planeIndex));
        }
    }

    private int GetTextureUnit(VideoGlUploadPlanner.VideoGlPlaneSlot slot)
        => slot switch
        {
            VideoGlUploadPlanner.VideoGlPlaneSlot.Rgba => GlTexture0,
            VideoGlUploadPlanner.VideoGlPlaneSlot.Y => GlTexture0,
            VideoGlUploadPlanner.VideoGlPlaneSlot.Uv => GlTexture1,
            VideoGlUploadPlanner.VideoGlPlaneSlot.U => GlTexture1,
            VideoGlUploadPlanner.VideoGlPlaneSlot.V => GlTexture2,
            _ => GlTexture0
        };

    private int GetTextureId(VideoGlUploadPlanner.VideoGlPlaneSlot slot)
        => slot switch
        {
            VideoGlUploadPlanner.VideoGlPlaneSlot.Rgba => _textureRgba,
            VideoGlUploadPlanner.VideoGlPlaneSlot.Y => _textureY,
            VideoGlUploadPlanner.VideoGlPlaneSlot.Uv => _textureUv,
            VideoGlUploadPlanner.VideoGlPlaneSlot.U => _textureU,
            VideoGlUploadPlanner.VideoGlPlaneSlot.V => _textureV,
            _ => _textureRgba
        };

    private ref TextureUploadState GetTextureState(VideoGlUploadPlanner.VideoGlPlaneSlot slot)
    {
        switch (slot)
        {
            case VideoGlUploadPlanner.VideoGlPlaneSlot.Rgba: return ref _rgbaState;
            case VideoGlUploadPlanner.VideoGlPlaneSlot.Y: return ref _yState;
            case VideoGlUploadPlanner.VideoGlPlaneSlot.Uv: return ref _uvState;
            case VideoGlUploadPlanner.VideoGlPlaneSlot.U: return ref _uState;
            case VideoGlUploadPlanner.VideoGlPlaneSlot.V: return ref _vState;
            default: throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private void SetCurrentFrameState(
        int width,
        int height,
        bool useYuv,
        VideoGlUploadPlanner.VideoGlYuvMode yuvMode)
    {
        _textureWidth  = width;
        _textureHeight = height;
        _useYuvProgram = useYuv;
        _yuvPixelFormat = yuvMode;
    }

    // ── SDL helpers ───────────────────────────────────────────────────────────────

    private bool EnsureSdlVideoInit(out string error)
    {
        error = string.Empty;
        if ((SDL.WasInit(SDL.InitFlags.Video) & SDL.InitFlags.Video) != 0)
            return true;

        if (!SDL.Init(SDL.InitFlags.Video))
        {
            error = $"SDL_Init(Video) failed: {SDL.GetError()}";
            return false;
        }
        _sdlOwnsSubsystem = true;
        return true;
    }

    private bool CreateGlContext(out string error)
    {
        error = string.Empty;
        _sdlGlContext = SDL.GLCreateContext(_sdlWindow);
        if (_sdlGlContext == nint.Zero)
        {
            error = $"SDL_GLCreateContext failed: {SDL.GetError()}";
            SDL.DestroyWindow(_sdlWindow);
            _sdlWindow = nint.Zero;
            TeardownSdlSubsystem();
            return false;
        }
        // Release from the calling thread so the render thread can own it.
        SDL.GLMakeCurrent(_sdlWindow, nint.Zero);
        return true;
    }

    private void TeardownSdlSubsystem()
    {
        if (_sdlOwnsSubsystem)
        {
            SDL.Quit();
            _sdlOwnsSubsystem = false;
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────────

    private static string FmtName(string name)
    {
        const string prefix = "AV_PIX_FMT_";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..].ToLowerInvariant()
            : name.ToLowerInvariant();
    }
}