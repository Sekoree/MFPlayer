using System.Diagnostics;
using System.Runtime.InteropServices;
using Seko.OwnAudioNET.Video;
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
public sealed partial class VideoSDL : IDisposable
{
    public event Action<SDL.Keycode>? KeyDown;

    // ── GL constants ────────────────────────────────────────────────────────────
    private const int GlArrayBuffer      = 0x8892;
    private const int GlStaticDraw       = 0x88E4;
    private const int GlFloat            = 0x1406;
    private const int GlTriangles        = 0x0004;
    private const int GlTexture2D        = 0x0DE1;
    private const int GlTexture0         = 0x84C0;
    private const int GlTexture1         = GlTexture0 + 1;
    private const int GlTexture2         = GlTexture0 + 2;
    private const int GlColorBufferBit   = 0x00004000;
    private const int GlVertexShader     = 0x8B31;
    private const int GlFragmentShader   = 0x8B30;
    private const int GlCompileStatus    = 0x8B81;
    private const int GlLinkStatus       = 0x8B82;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlLinear           = 0x2601;
    private const int GlRgba8            = 0x8058;
    private const int GlRgba             = 0x1908;
    private const int GlUnsignedByte     = 0x1401;
    private const int GlR8               = 0x8229;
    private const int GlR16              = 0x822A;
    private const int GlRg8              = 0x822B;
    private const int GlRg16             = 0x822C;
    private const int GlRed              = 0x1903;
    private const int GlRg               = 0x8227;
    private const int GlUnsignedShort    = 0x1403;

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
    private int _yuvPixelFormat;
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

    // ── Render loop state ─────────────────────────────────────────────────────────
    private Thread?   _renderThread;
    private volatile bool _stopRequested;
    private readonly Lock _frameLock = new();
    private VideoFrame? _latestFrame;
    private bool        _hasFrame;
    private long        _latestFrameVersion;
    private long        _lastRenderedVersion = -1;

    // ── Geometry ─────────────────────────────────────────────────────────────────
    private static readonly float[] QuadVertices =
    [
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
         1f,  1f, 1f, 0f,
        -1f, -1f, 0f, 1f,
         1f,  1f, 1f, 0f,
        -1f,  1f, 0f, 0f
    ];

    private struct TextureUploadState
    {
        public bool IsInitialized;
        public int  Width, Height, InternalFormat, Format, Type;
    }

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
        _renderThread?.Join(TimeSpan.FromSeconds(3));
        _renderThread = null;
    }

    /// <summary>
    /// Push a decoded video frame for display. Thread-safe; can be called from any thread.
    /// The class takes one extra reference and disposes it when no longer needed.
    /// </summary>
    public void PushFrame(VideoFrame frame)
    {
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

    // ── Render loop (background thread) ──────────────────────────────────────────

    private void RenderLoop()
    {
        if (!SDL.GLMakeCurrent(_sdlWindow, _sdlGlContext))
        {
            Console.Error.WriteLine($"[VideoSDL] GLMakeCurrent failed: {SDL.GetError()}");
            return;
        }
        SDL.GLSetSwapInterval(1);

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
            SDL.Delay(1);
        }

        DisposeGlResources();
        SDL.GLMakeCurrent(_sdlWindow, nint.Zero);
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

        var h = GCHandle.Alloc(QuadVertices, GCHandleType.Pinned);
        try   { _glBufferData!(GlArrayBuffer, QuadVertices.Length * sizeof(float), h.AddrOfPinnedObject(), GlStaticDraw); }
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

    private bool RenderLastFrame(int surfaceW, int surfaceH)
    {
        Interlocked.Increment(ref _diagRenderCalls);
        if (!_glInitialized) return false;

        _glViewport!(0, 0, surfaceW, surfaceH);
        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(GlColorBufferBit);

        if (_textureWidth <= 0 || _textureHeight <= 0) return false;

        DrawCurrentFrame(surfaceW, surfaceH);
        RenderHudOverlay(surfaceW, surfaceH);
        UpdateRenderFps();
        return true;
    }

    private void DrawCurrentFrame(int surfaceW, int surfaceH)
    {
        var vp = KeepAspectRatio
            ? GetAspectFitRect(surfaceW, surfaceH, _textureWidth, _textureHeight)
            : new ViewportRect(0, 0, Math.Max(1, surfaceW), Math.Max(1, surfaceH));
        _glViewport!(vp.X, vp.Y, vp.Width, vp.Height);

        if (!_useYuvProgram)
        {
            _glActiveTexture!(GlTexture0);
            _glBindTexture!(GlTexture2D, _textureRgba);
        }
        else
        {
            _glActiveTexture!(GlTexture0); _glBindTexture!(GlTexture2D, _textureY);
            var semiPlanar = _yuvPixelFormat == 1 || _yuvPixelFormat == 3;
            _glActiveTexture!(GlTexture1); _glBindTexture!(GlTexture2D, semiPlanar ? _textureUv : _textureU);
            if (!semiPlanar) { _glActiveTexture!(GlTexture2); _glBindTexture!(GlTexture2D, _textureV); }
            _glActiveTexture!(GlTexture0);
        }

        if (_useYuvProgram)
        {
            _glUseProgram!(_yuvProgram);
            if (_yuvPixelFormatLocation >= 0)
                _glUniform1I!(_yuvPixelFormatLocation, _yuvPixelFormat);
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
        try { _glGetShaderInfoLog!(shader, 4096, out var len, buf); return len > 0 ? Marshal.PtrToStringAnsi(buf, len) ?? "Shader compile failed." : "Shader compile failed."; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private string GetProgramLog(int program)
    {
        var buf = Marshal.AllocHGlobal(4096);
        try { _glGetProgramInfoLog!(program, 4096, out var len, buf); return len > 0 ? Marshal.PtrToStringAnsi(buf, len) ?? "Program link failed." : "Program link failed."; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── Texture upload ────────────────────────────────────────────────────────────

    private unsafe void UploadTexture2D(ref TextureUploadState state,
        int width, int height, int internalFormat, int format, int type, byte[] data)
    {
        fixed (byte* ptr = &MemoryMarshal.GetArrayDataReference(data))
        {
            var pixels = (nint)ptr;
            var realloc = !state.IsInitialized
                       || state.Width          != width
                       || state.Height         != height
                       || state.InternalFormat != internalFormat
                       || state.Format         != format
                       || state.Type           != type;

            if (realloc)
            {
                _glTexImage2D!(GlTexture2D, 0, internalFormat, width, height, 0, format, type, nint.Zero);
                state = new TextureUploadState { IsInitialized = true, Width = width, Height = height, InternalFormat = internalFormat, Format = format, Type = type };
            }

            if (_glTexSubImage2D != null)
                _glTexSubImage2D(GlTexture2D, 0, 0, 0, width, height, format, type, pixels);
            else
                _glTexImage2D!(GlTexture2D, 0, internalFormat, width, height, 0, format, type, pixels);
        }
    }

    // ── Upload methods ────────────────────────────────────────────────────────────

    private bool UploadRgbaFrame(VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return false;
        var rgba = GetTightlyPackedPlane(frame, 0, frame.Width * 4, frame.Height, ref _plane0Scratch);
        if (rgba == null) return false;
        _glActiveTexture!(GlTexture0); _glBindTexture!(GlTexture2D, _textureRgba);
        UploadTexture2D(ref _rgbaState, frame.Width, frame.Height, GlRgba8, GlRgba, GlUnsignedByte, rgba);
        SetCurrentFrameState(frame.Width, frame.Height, false, 0);
        return true;
    }

    private bool UploadNv12Frame(VideoFrame frame)
    {
        var (w, h, cw, ch) = (frame.Width, frame.Height, (frame.Width + 1) / 2, (frame.Height + 1) / 2);
        var y  = GetTightlyPackedPlane(frame, 0, w,      h,  ref _plane0Scratch);
        var uv = GetTightlyPackedPlane(frame, 1, cw * 2, ch, ref _plane1Scratch);
        if (y == null || uv == null) return false;

        _glActiveTexture!(GlTexture0); _glBindTexture!(GlTexture2D, _textureY);
        UploadTexture2D(ref _yState, w, h, GlR8, GlRed, GlUnsignedByte, y);
        _glActiveTexture!(GlTexture1); _glBindTexture!(GlTexture2D, _textureUv);
        UploadTexture2D(ref _uvState, cw, ch, GlRg8, GlRg, GlUnsignedByte, uv);
        _glActiveTexture!(GlTexture2); _glBindTexture!(GlTexture2D, _textureUv);
        _glActiveTexture!(GlTexture0);
        SetCurrentFrameState(w, h, true, 1);
        return true;
    }

    private bool UploadYuv420pFrame(VideoFrame f)  => UploadPlanar8Bit(f, (f.Width + 1)/2, (f.Height + 1)/2, 2);
    private bool UploadYuv422pFrame(VideoFrame f)  => UploadPlanar8Bit(f, (f.Width + 1)/2, f.Height,         2);
    private bool UploadYuv444pFrame(VideoFrame f)  => UploadPlanar8Bit(f, f.Width,          f.Height,         2);

    private bool UploadYuv420p10leFrame(VideoFrame f) => UploadPlanar16Bit(f, (f.Width + 1)/2, (f.Height + 1)/2, 4);
    private bool UploadYuv422p10leFrame(VideoFrame f) => UploadPlanar16Bit(f, (f.Width + 1)/2, f.Height,         4);
    private bool UploadYuv444p10leFrame(VideoFrame f) => UploadPlanar16Bit(f, f.Width,          f.Height,         4);

    private bool UploadP010leFrame(VideoFrame frame)
    {
        var (w, h, cw, ch) = (frame.Width, frame.Height, (frame.Width + 1) / 2, (frame.Height + 1) / 2);
        var y  = GetTightlyPackedPlane(frame, 0, w * 2,  h,  ref _plane0Scratch);
        var uv = GetTightlyPackedPlane(frame, 1, cw * 4, ch, ref _plane1Scratch);
        if (y == null || uv == null) return false;

        _glActiveTexture!(GlTexture0); _glBindTexture!(GlTexture2D, _textureY);
        UploadTexture2D(ref _yState, w, h, GlR16, GlRed, GlUnsignedShort, y);
        _glActiveTexture!(GlTexture1); _glBindTexture!(GlTexture2D, _textureUv);
        UploadTexture2D(ref _uvState, cw, ch, GlRg16, GlRg, GlUnsignedShort, uv);
        _glActiveTexture!(GlTexture2); _glBindTexture!(GlTexture2D, _textureUv);
        _glActiveTexture!(GlTexture0);
        SetCurrentFrameState(w, h, true, 3);
        return true;
    }

    private bool UploadPlanar8Bit(VideoFrame frame, int cw, int ch, int fmtCode)
    {
        var (w, h) = (frame.Width, frame.Height);
        var y = GetTightlyPackedPlane(frame, 0, w,  h,  ref _plane0Scratch);
        var u = GetTightlyPackedPlane(frame, 1, cw, ch, ref _plane1Scratch);
        var v = GetTightlyPackedPlane(frame, 2, cw, ch, ref _plane2Scratch);
        if (y == null || u == null || v == null) return false;

        _glActiveTexture!(GlTexture0); _glBindTexture!(GlTexture2D, _textureY);
        UploadTexture2D(ref _yState, w, h, GlR8, GlRed, GlUnsignedByte, y);
        _glActiveTexture!(GlTexture1); _glBindTexture!(GlTexture2D, _textureU);
        UploadTexture2D(ref _uState, cw, ch, GlR8, GlRed, GlUnsignedByte, u);
        _glActiveTexture!(GlTexture2); _glBindTexture!(GlTexture2D, _textureV);
        UploadTexture2D(ref _vState, cw, ch, GlR8, GlRed, GlUnsignedByte, v);
        _glActiveTexture!(GlTexture0);
        SetCurrentFrameState(w, h, true, fmtCode);
        return true;
    }

    private bool UploadPlanar16Bit(VideoFrame frame, int cw, int ch, int fmtCode)
    {
        var (w, h) = (frame.Width, frame.Height);
        var y = GetTightlyPackedPlane(frame, 0, w * 2,  h,  ref _plane0Scratch);
        var u = GetTightlyPackedPlane(frame, 1, cw * 2, ch, ref _plane1Scratch);
        var v = GetTightlyPackedPlane(frame, 2, cw * 2, ch, ref _plane2Scratch);
        if (y == null || u == null || v == null) return false;

        _glActiveTexture!(GlTexture0); _glBindTexture!(GlTexture2D, _textureY);
        UploadTexture2D(ref _yState, w, h, GlR16, GlRed, GlUnsignedShort, y);
        _glActiveTexture!(GlTexture1); _glBindTexture!(GlTexture2D, _textureU);
        UploadTexture2D(ref _uState, cw, ch, GlR16, GlRed, GlUnsignedShort, u);
        _glActiveTexture!(GlTexture2); _glBindTexture!(GlTexture2D, _textureV);
        UploadTexture2D(ref _vState, cw, ch, GlR16, GlRed, GlUnsignedShort, v);
        _glActiveTexture!(GlTexture0);
        SetCurrentFrameState(w, h, true, fmtCode);
        return true;
    }

    private void SetCurrentFrameState(int width, int height, bool useYuv, int fmtCode)
    {
        _textureWidth  = width;
        _textureHeight = height;
        _useYuvProgram = useYuv;
        _yuvPixelFormat = fmtCode;
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