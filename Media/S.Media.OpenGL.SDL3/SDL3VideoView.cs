using System.Runtime.InteropServices;
using S.Media.Core.Errors;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.OpenGL.Output;
using SDL3;

namespace S.Media.OpenGL.SDL3;

public sealed class SDL3VideoView : OpenGLWrapperVideoOutput
{
    private const int MinStandaloneWindowWidth = 320;
    private const int MinStandaloneWindowHeight = 180;

    private readonly Lock _gate = new();
    // _clones removed (3.1): topology is now owned exclusively by OpenGLVideoEngine.
    private readonly SDL3HudRenderer _hudRenderer = new();
    private readonly SDL3ShaderPipeline _shaderPipeline = new();
    private bool _disposed;
    private bool _initialized;
    private bool _pipelineReady;
    private bool _embedded;
    private bool _parentLost;
    private bool _hudDirty;
    private bool _ownsSdlVideoSubsystem;
    private bool _bringToFrontOnShow;
    private bool _preserveAspectRatio = true;
    private SDL.WindowFlags _windowFlags = SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable;
    private long _lastPresentedGeneration = -1;
    private nint _windowHandle;
    private nint _glContextHandle;
    private bool _glInitialized;
    private int _glProgram;
    private int _glYuvProgram;
    private int _glYuvPixelFormatLocation = -1;
    private int _glYuvFullRangeLocation = -1;   // B6
    private int _glVao;
    private int _glVbo;
    private int _glTexture;
    private int _glTextureY;
    private int _glTextureU;
    private int _glTextureV;
    private TextureUploadState _rgbaUploadState;
    private TextureUploadState _yUploadState;
    private TextureUploadState _uUploadState;
    private TextureUploadState _vUploadState;
    private byte[]? _packedRgbaScratch;
    private byte[]? _plane0Scratch;
    private byte[]? _plane1Scratch;
    private byte[]? _plane2Scratch;
    private nint _platformHandle;
    private string _platformDescriptor = string.Empty;

    // ── Render thread (standalone window) ─────────────────────────────────────
    // All OpenGL calls for standalone windows happen exclusively on this thread.
    // PushFrame() is non-blocking: it enqueues frames here; the render thread
    // dequeues them, sleeps for PTS-based timing, uploads textures, and swaps.
    private Thread? _renderThread;
    private volatile bool _renderStopRequested;
    private readonly Queue<(VideoFrame Frame, TimeSpan Pts)> _renderQueue = new();
    private readonly Lock _renderQueueLock = new();
    private VideoOutputConfig _renderConfig = new();

    private delegate void GlViewport(int x, int y, int width, int height);
    private delegate void GlClearColor(float r, float g, float b, float a);
    private delegate void GlClear(int mask);
    private delegate int GlCreateShader(int type);
    private delegate void GlShaderSource(int shader, int count, nint strings, nint lengths);
    private delegate void GlCompileShader(int shader);
    private delegate void GlGetShaderIv(int shader, int pname, out int param);
    private delegate void GlGetShaderInfoLog(int shader, int maxLength, out int length, nint infoLog);
    private delegate int GlCreateProgram();
    private delegate void GlAttachShader(int program, int shader);
    private delegate void GlBindAttribLocation(int program, int index, string name);
    private delegate void GlLinkProgram(int program);
    private delegate void GlGetProgramIv(int program, int pname, out int param);
    private delegate void GlGetProgramInfoLog(int program, int maxLength, out int length, nint infoLog);
    private delegate void GlUseProgram(int program);
    private delegate void GlDeleteShader(int shader);
    private delegate void GlDeleteProgram(int program);
    private delegate int GlGetUniformLocation(int program, string name);
    private delegate void GlUniform1I(int location, int value);
    private delegate void GlGenVertexArrays(int n, out int arrays);
    private delegate void GlBindVertexArray(int array);
    private delegate void GlDeleteVertexArrays(int n, in int arrays);
    private delegate void GlGenBuffers(int n, out int buffers);
    private delegate void GlBindBuffer(int target, int buffer);
    private delegate void GlBufferData(int target, nint size, nint data, int usage);
    private delegate void GlDeleteBuffers(int n, in int buffers);
    private delegate void GlEnableVertexAttribArray(int index);
    private delegate void GlVertexAttribPointer(int index, int size, int type, int normalized, int stride, nint pointer);
    private delegate void GlGenTextures(int n, out int textures);
    private delegate void GlBindTexture(int target, int texture);
    private delegate void GlTexParameteri(int target, int pname, int param);
    private delegate void GlTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, nint pixels);
    private delegate void GlTexSubImage2D(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, nint pixels);
    private delegate void GlPixelStoreI(int pname, int param);
    private delegate void GlActiveTexture(int texture);
    private delegate void GlDeleteTextures(int n, in int textures);
    private delegate void GlDrawArrays(int mode, int first, int count);

    private GlViewport? _glViewport;
    private GlClearColor? _glClearColor;
    private GlClear? _glClear;
    private GlCreateShader? _glCreateShader;
    private GlShaderSource? _glShaderSource;
    private GlCompileShader? _glCompileShader;
    private GlGetShaderIv? _glGetShaderIv;
    private GlGetShaderInfoLog? _glGetShaderInfoLog;
    private GlCreateProgram? _glCreateProgram;
    private GlAttachShader? _glAttachShader;
    private GlBindAttribLocation? _glBindAttribLocation;
    private GlLinkProgram? _glLinkProgram;
    private GlGetProgramIv? _glGetProgramIv;
    private GlGetProgramInfoLog? _glGetProgramInfoLog;
    private GlUseProgram? _glUseProgram;
    private GlDeleteShader? _glDeleteShader;
    private GlDeleteProgram? _glDeleteProgram;
    private GlGetUniformLocation? _glGetUniformLocation;
    private GlUniform1I? _glUniform1I;
    private GlGenVertexArrays? _glGenVertexArrays;
    private GlBindVertexArray? _glBindVertexArray;
    private GlDeleteVertexArrays? _glDeleteVertexArrays;
    private GlGenBuffers? _glGenBuffers;
    private GlBindBuffer? _glBindBuffer;
    private GlBufferData? _glBufferData;
    private GlDeleteBuffers? _glDeleteBuffers;
    private GlEnableVertexAttribArray? _glEnableVertexAttribArray;
    private GlVertexAttribPointer? _glVertexAttribPointer;
    private GlGenTextures? _glGenTextures;
    private GlBindTexture? _glBindTexture;
    private GlTexParameteri? _glTexParameteri;
    private GlTexImage2D? _glTexImage2D;
    private GlTexSubImage2D? _glTexSubImage2D;
    private GlPixelStoreI? _glPixelStoreI;
    private GlActiveTexture? _glActiveTexture;
    private GlDeleteTextures? _glDeleteTextures;
    private GlDrawArrays? _glDrawArrays;

    public SDL3VideoView() : this(new OpenGLVideoOutput(), new OpenGLVideoEngine(), isClone: false)
    {
    }

    /// <summary>
    /// Creates a new standalone <see cref="SDL3VideoView"/> without throwing on failure.
    /// Preferred over the public constructor for code that follows the framework's
    /// integer-return-code convention.
    /// </summary>
    public static int Create(out SDL3VideoView? result)
    {
        result = null;
        var output = new OpenGLVideoOutput();
        var engine = new OpenGLVideoEngine();
        var add = engine.AddOutput(output);
        if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
        {
            output.Dispose();
            engine.Dispose();
            return add;
        }

        result = new SDL3VideoView(output, engine, isClone: false);
        return MediaResult.Success;
    }

    private SDL3VideoView(OpenGLVideoOutput output, OpenGLVideoEngine engine, bool isClone)
        : base(output, engine, isClone)
    {
    }

    /// <summary>
    /// The underlying <see cref="OpenGLVideoOutput"/> managed by this view.
    /// Intended for diagnostics only — do not call <c>Start</c>, <c>Stop</c>, or
    /// <c>PushFrame</c> directly on this object.
    /// </summary>
    public OpenGLVideoOutput InternalOutput => Output;

    public bool EnableHudOverlay { get; set; }

    public SDL3HudRenderer HudRenderer => _hudRenderer;


    public int Initialize(SDL3VideoViewOptions options)
    {
        if (options.Width <= 0 || options.Height <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.SDL3EmbedTeardownFailed;
            }

            _initialized = true;
            _pipelineReady = false;
            _embedded = false;
            _parentLost = false;
            _lastPresentedGeneration = -1;
            _hudDirty = true;
            _platformDescriptor = NormalizeDescriptor(options.PreferredDescriptor);
            if (string.IsNullOrEmpty(_platformDescriptor))
            {
                return (int)MediaErrorCode.SDL3EmbedUnsupportedDescriptor;
            }

            _windowFlags = NormalizeWindowFlags(options.WindowFlags);
            _bringToFrontOnShow = options.BringToFrontOnShow;
            _preserveAspectRatio = options.PreserveAspectRatio;

            var startupWidth = Math.Max(MinStandaloneWindowWidth, options.Width);
            var startupHeight = Math.Max(MinStandaloneWindowHeight, options.Height);

            if (!EnsureStandaloneWindowCreatedLocked(startupWidth, startupHeight, options.WindowTitle))
            {
                return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
            }

            _platformHandle = ResolvePlatformHandleLocked(_platformDescriptor);
            if (_platformHandle == nint.Zero)
            {
                _platformHandle = _windowHandle;
            }

            if (options.ShowOnInitialize)
            {
                var show = ShowAndBringToFrontLocked();
                if (show != MediaResult.Success)
                {
                    return show;
                }
            }

            return MediaResult.Success;
        }
    }

    public int InitializeEmbedded(nint parentHandle, int width, int height)
    {
        if (parentHandle == nint.Zero)
        {
            return (int)MediaErrorCode.SDL3EmbedInvalidParentHandle;
        }

        if (width <= 0 || height <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.SDL3EmbedTeardownFailed;
            }

            _initialized = true;
            _pipelineReady = false;
            _embedded = true;
            _parentLost = false;
            _lastPresentedGeneration = -1;
            _hudDirty = true;
            _windowFlags = SDL.WindowFlags.OpenGL;
            _bringToFrontOnShow = false;
            DestroyStandaloneWindowLocked();
            _platformHandle = parentHandle;
            _platformDescriptor = "x11-window";
            return MediaResult.Success;
        }
    }

    public override int Start(VideoOutputConfig config)
    {
        lock (_gate)
        {
            if (_parentLost)   return (int)MediaErrorCode.SDL3EmbedParentLost;
            if (!_initialized) return (int)MediaErrorCode.SDL3EmbedNotInitialized;

            var outStart = Output.Start(config);
            if (outStart != MediaResult.Success) return outStart;

            _renderConfig = config;

            // Embedded path: pipeline must be ready on the calling thread.
            if (_embedded)
            {
                if (!_pipelineReady)
                {
                    var init = _shaderPipeline.EnsureInitialized();
                    if (init != MediaResult.Success) return init;
                    _pipelineReady = true;
                }
                return MediaResult.Success;
            }

            // Standalone path: start dedicated render thread which owns the GL context.
            if (_renderThread is null && _windowHandle != nint.Zero)
            {
                _renderStopRequested = false;
                _renderThread = new Thread(RenderLoop)
                { Name = "SDL3VideoView.RenderLoop", IsBackground = true };
                _renderThread.Start();
            }

            return MediaResult.Success;
        }
    }

    public int Start()
    {
        return Start(new VideoOutputConfig());
    }

    public override int Stop()
    {
        Thread? renderThread;
        lock (_gate)
        {
            _renderStopRequested = true;
            renderThread = _renderThread;
            _renderThread = null;
        }

        if (renderThread is not null && !ReferenceEquals(Thread.CurrentThread, renderThread))
            renderThread.Join(TimeSpan.FromSeconds(3));

        lock (_renderQueueLock)
        {
            while (_renderQueue.TryDequeue(out var item))
                item.Frame.Dispose();
        }

        return Output.Stop();
    }

    public override int PushFrame(VideoFrame frame)
    {
        return PushFrame(frame, frame.PresentationTime);
    }

    public override int PushFrame(VideoFrame frame, TimeSpan presentationTime)
    {
        bool embedded;
        lock (_gate)
        {
            if (_parentLost)   return (int)MediaErrorCode.SDL3EmbedParentLost;
            if (!_initialized) return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            embedded = _embedded;
        }

        if (embedded)
        {
            var push = Output.PushFrame(frame, presentationTime);
            if (push != MediaResult.Success) return push;

            lock (_gate)
            {
                if (_disposed)   return (int)MediaErrorCode.SDL3EmbedTeardownFailed;
                if (!_initialized) return (int)MediaErrorCode.SDL3EmbedNotInitialized;

                var generation = Output.Surface.LastPresentedFrameGeneration;
                if (generation == _lastPresentedGeneration)
                {
                    if (EnableHudOverlay && _hudDirty) { _ = _hudRenderer.Render(); _hudDirty = false; }
                    return MediaResult.Success;
                }
                _lastPresentedGeneration = generation;

                if (_pipelineReady)
                {
                    var upload = _shaderPipeline.Upload(frame);
                    if (upload != MediaResult.Success) return upload;
                    var draw = _shaderPipeline.Draw();
                    if (draw != MediaResult.Success) return draw;
                }

                if (EnableHudOverlay) { _ = _hudRenderer.Render(); _hudDirty = false; }
                return MediaResult.Success;
            }
        }

        // ── Standalone path: enqueue for the render thread ─────────────────────
        var capacity = Math.Max(1, _renderConfig.QueueCapacity);
        VideoFrame? toDrop = null;

        lock (_renderQueueLock)
        {
            if (_renderQueue.Count >= capacity)
            {
                if (_renderConfig.BackpressureMode == VideoOutputBackpressureMode.DropOldest)
                {
                    if (_renderQueue.TryDequeue(out var old)) toDrop = old.Frame;
                }
                else
                {
                    // Busy – caller should back off.
                    return (int)MediaErrorCode.VideoOutputBackpressureQueueFull;
                }
            }

            try { _renderQueue.Enqueue((frame.AddRef(), presentationTime)); }
            catch (ObjectDisposedException) { return (int)MediaErrorCode.VideoFrameDisposed; }
        }

        toDrop?.Dispose();
        return MediaResult.Success;
    }

    public int PushAudio(in AudioFrame frame, TimeSpan presentationTime)
    {
        _ = frame;
        _ = presentationTime;
        return (int)MediaErrorCode.MediaInvalidArgument;
    }

    public int UpdateHud(HudEntry entry)
    {
        var code = _hudRenderer.Update(entry);
        if (code == MediaResult.Success)
        {
            lock (_gate)
            {
                _hudDirty = true;
            }
        }

        return code;
    }

    public int Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (_parentLost)
            {
                return (int)MediaErrorCode.SDL3EmbedParentLost;
            }

            if (!_initialized)
            {
                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            if (!_embedded && _windowHandle != nint.Zero)
            {
                var resizedWidth = Math.Max(MinStandaloneWindowWidth, width);
                var resizedHeight = Math.Max(MinStandaloneWindowHeight, height);
                _ = SDL.SetWindowSize(_windowHandle, resizedWidth, resizedHeight);
            }

            return MediaResult.Success;
        }
    }

    public nint GetPlatformWindowHandle()
    {
        return TryGetPlatformWindowHandle(out var handle) == MediaResult.Success ? handle : nint.Zero;
    }

    public string GetPlatformHandleDescriptor()
    {
        return TryGetPlatformHandleDescriptor(out var descriptor) == MediaResult.Success ? descriptor : string.Empty;
    }

    public int TryGetPlatformWindowHandle(out nint handle)
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                handle = nint.Zero;
                return (int)MediaErrorCode.SDL3EmbedHandleUnavailable;
            }

            if (_parentLost)
            {
                handle = nint.Zero;
                return (int)MediaErrorCode.SDL3EmbedParentLost;
            }

            handle = _platformHandle;
            return MediaResult.Success;
        }
    }

    public int TryGetPlatformHandleDescriptor(out string descriptor)
    {
        lock (_gate)
        {
            descriptor = string.Empty;

            if (!_initialized)
            {
                return (int)MediaErrorCode.SDL3EmbedDescriptorUnavailable;
            }

            if (_parentLost)
            {
                return (int)MediaErrorCode.SDL3EmbedParentLost;
            }

            if (string.IsNullOrWhiteSpace(_platformDescriptor))
            {
                return (int)MediaErrorCode.SDL3EmbedUnsupportedDescriptor;
            }

            descriptor = _platformDescriptor;
            return MediaResult.Success;
        }
    }

    public int TryGetWindowFlags(out SDL.WindowFlags windowFlags)
    {
        lock (_gate)
        {
            if (_parentLost)
            {
                windowFlags = default;
                return (int)MediaErrorCode.SDL3EmbedParentLost;
            }

            if (!_initialized)
            {
                windowFlags = default;
                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            windowFlags = _windowFlags;
            return MediaResult.Success;
        }
    }

    public int ShowAndBringToFront()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.SDL3EmbedTeardownFailed;
            }

            if (!_initialized)
            {
                if (_parentLost)
                {
                    return (int)MediaErrorCode.SDL3EmbedParentLost;
                }

                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            if (_parentLost)
            {
                return (int)MediaErrorCode.SDL3EmbedParentLost;
            }

            if (_embedded)
            {
                return MediaResult.Success;
            }

            return ShowAndBringToFrontLocked();
        }
    }

    public int CreateClone(in SDL3CloneOptions options, out SDL3VideoView? cloneView)
    {
        cloneView = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            var create = Engine.CreateCloneOutput(Id, ToOpenGlCloneOptions(options), out var cloneBaseOutput);
            if (create != MediaResult.Success || cloneBaseOutput is not OpenGLVideoOutput glClone)
            {
                return create != MediaResult.Success ? create : (int)MediaErrorCode.OpenGLCloneCreationFailed;
            }

            cloneView = new SDL3VideoView(glClone, Engine, isClone: true);
            var cloneInit = _embedded
                ? cloneView.InitializeEmbedded(_platformHandle, 1, 1)
                : cloneView.Initialize(new SDL3VideoViewOptions
                {
                    PreferredDescriptor = _platformDescriptor,
                    WindowFlags = _windowFlags,
                    PreserveAspectRatio = _preserveAspectRatio,
                });
            if (cloneInit != MediaResult.Success)
            {
                _ = Engine.RemoveOutput(cloneView.Id);
                cloneView.Dispose();
                cloneView = null;
                return cloneInit;
            }

            // Clone registered; topology tracked exclusively in the engine (3.1).
            return MediaResult.Success;
        }
    }

    public int AttachClone(SDL3VideoView? cloneView, in SDL3CloneOptions options)
    {
        if (cloneView is null)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (cloneView.Id == Id)
            {
                return (int)MediaErrorCode.OpenGLCloneSelfAttachRejected;
            }

            var add = Engine.AddOutput(cloneView.Output);
            if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
            {
                return add;
            }

            // B7 fix: forward options to the engine rather than discarding them.
            var attachCode = Engine.AttachCloneOutput(Id, cloneView.Id, ToOpenGlCloneOptions(options));
            if (attachCode != MediaResult.Success)
            {
                return attachCode;
            }

            cloneView.IsClone = true;
            // Topology tracked exclusively in the engine (3.1).
            return MediaResult.Success;
        }
    }

    public int DetachClone(Guid cloneViewId)
    {
        return Engine.DetachCloneOutput(Id, cloneViewId);
    }

    public void SimulateEmbeddedParentLost()
    {
        Guid[] cloneIds;

        lock (_gate)
        {
            if (!_embedded)
                return;
            cloneIds = Engine.GetCloneIds(Id).ToArray();
            ApplyParentLossTeardownLocked();
        }

        // Detach all clone outputs from the engine; their SDL3VideoView instances
        // become orphaned and will fail on the next operation (3.1 — no _clones dict).
        foreach (var id in cloneIds)
            _ = Engine.RemoveOutput(id);
    }

    protected override void OnBeforeDispose()
    {
        // Stop the render thread first so it releases the GL context before we destroy the window.
        _ = Stop();

        var releaseSdl = false;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _platformHandle = nint.Zero;
            _platformDescriptor = string.Empty;
            _initialized = false;
            _pipelineReady = false;
            _lastPresentedGeneration = -1;
            _hudDirty = false;
            _bringToFrontOnShow = false;
            _windowFlags = SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable;
            _preserveAspectRatio = true;

            DestroyStandaloneWindowLocked();
            releaseSdl = _ownsSdlVideoSubsystem;
            _ownsSdlVideoSubsystem = false;
        }

        if (releaseSdl)
            SDL.Quit();

        _shaderPipeline.Dispose();
        // base.Dispose() will call Engine.RemoveOutput(Id) and Engine.Dispose() (if !IsClone).
    }

    private static OpenGLCloneOptions ToOpenGlCloneOptions(in SDL3CloneOptions options)
    {
        return new OpenGLCloneOptions
        {
            Mode = options.CloneMode,
            AutoResizeToParent = options.AutoTrackParentSize,
            HudMode = options.HudMode,
            MaxCloneDepth = options.MaxCloneDepth,
        };
    }

    private static string NormalizeDescriptor(string? descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return ResolveDefaultDescriptor();
        }

        var normalized = descriptor.Trim().ToLowerInvariant();
        return normalized switch
        {
            "x11-window" => "x11-window",
            "wayland-surface" => "wayland-surface",
            "win32-hwnd" => "win32-hwnd",
            "cocoa-nsview" => "cocoa-nsview",
            _ => string.Empty,
        };
    }

    private static string ResolveDefaultDescriptor()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "win32-hwnd";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "cocoa-nsview";

        // Linux: prefer Wayland when the compositor is running, otherwise X11.
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? "wayland-surface"
            : "x11-window";
    }

    private void ApplyParentLossTeardownLocked()
    {
        if (_parentLost)
            return;

        _parentLost = true;
        _initialized = false;
        _pipelineReady = false;
        _platformHandle = nint.Zero;
        _platformDescriptor = string.Empty;
        _lastPresentedGeneration = -1;
        _hudDirty = false;
        _windowFlags = SDL.WindowFlags.OpenGL;
        _bringToFrontOnShow = false;
        // Clone topology cleanup is handled in SimulateEmbeddedParentLost via engine (3.1).
    }

    private static SDL.WindowFlags NormalizeWindowFlags(SDL.WindowFlags requestedFlags)
    {
        if ((requestedFlags & SDL.WindowFlags.OpenGL) == 0)
        {
            requestedFlags |= SDL.WindowFlags.OpenGL;
        }

        if ((requestedFlags & SDL.WindowFlags.Borderless) != 0)
        {
            requestedFlags &= ~SDL.WindowFlags.Borderless;
        }

        return requestedFlags;
    }

    private bool EnsureStandaloneWindowCreatedLocked(int width, int height, string title)
    {
        if (_windowHandle != nint.Zero)
        {
            DestroyStandaloneWindowLocked();
        }

        if (!EnsureSdlVideoInitializedLocked())
        {
            return false;
        }

        SDL.GLResetAttributes();
        _ = SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        _ = SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        _ = SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        _ = SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);

        var windowTitle = string.IsNullOrWhiteSpace(title) ? "MFPlayer SDL3 Preview" : title;
        _windowHandle = SDL.CreateWindow(windowTitle, width, height, _windowFlags);
        if (_windowHandle == nint.Zero)
        {
            if (_ownsSdlVideoSubsystem)
            {
                SDL.Quit();
                _ownsSdlVideoSubsystem = false;
            }

            return false;
        }

        _ = SDL.SetWindowMinimumSize(_windowHandle, MinStandaloneWindowWidth, MinStandaloneWindowHeight);

        _glContextHandle = SDL.GLCreateContext(_windowHandle);
        if (_glContextHandle == nint.Zero)
        {
            DestroyStandaloneWindowLocked();
            if (_ownsSdlVideoSubsystem)
            {
                SDL.Quit();
                _ownsSdlVideoSubsystem = false;
            }

            return false;
        }

        if (!SDL.GLMakeCurrent(_windowHandle, _glContextHandle))
        {
            DestroyStandaloneWindowLocked();
            if (_ownsSdlVideoSubsystem)
            {
                SDL.Quit();
                _ownsSdlVideoSubsystem = false;
            }

            return false;
        }

        // Swap interval is applied at the start of RenderLoop based on VideoOutputConfig.PresentationMode.

        // Release the context from the calling thread so the render thread can acquire it.
        _ = SDL.GLMakeCurrent(_windowHandle, nint.Zero);
        _glInitialized = false;
        _glTexture = 0;
        _rgbaUploadState = default;
        _yUploadState = default;
        _uUploadState = default;
        _vUploadState = default;

        return true;
    }

    private bool EnsureSdlVideoInitializedLocked()
    {
        if ((SDL.WasInit(SDL.InitFlags.Video) & SDL.InitFlags.Video) != 0)
        {
            return true;
        }

        if (!SDL.Init(SDL.InitFlags.Video))
        {
            return false;
        }

        _ownsSdlVideoSubsystem = true;
        return true;
    }

    private nint ResolvePlatformHandleLocked(string descriptor)
    {
        if (_windowHandle == nint.Zero)
        {
            return nint.Zero;
        }

        var properties = SDL.GetWindowProperties(_windowHandle);
        if (properties == 0)
        {
            return nint.Zero;
        }

        return descriptor switch
        {
            "win32-hwnd" => SDL.GetPointerProperty(properties, SDL.Props.WindowWin32HWNDPointer, nint.Zero),
            "x11-window" => (nint)SDL.GetNumberProperty(properties, SDL.Props.WindowX11WindowNumber, 0),
            "wayland-surface" => SDL.GetPointerProperty(properties, SDL.Props.WindowWaylandSurfacePointer, nint.Zero),
            "cocoa-nsview" => SDL.GetPointerProperty(properties, SDL.Props.WindowCocoaWindowPointer, nint.Zero),
            _ => nint.Zero,
        };
    }

    private int ShowAndBringToFrontLocked()
    {
        if (_windowHandle == nint.Zero)
        {
            return (int)MediaErrorCode.SDL3EmbedHandleUnavailable;
        }

        _ = SDL.SetHint(SDL.Hints.ForceRaiseWindow, "1");
        SDL.ShowWindow(_windowHandle);
        if (_bringToFrontOnShow)
        {
            SDL.RaiseWindow(_windowHandle);
        }

        PumpSdlEvents();

        return MediaResult.Success;
    }

    private void DestroyStandaloneWindowLocked()
    {
        if (_windowHandle != nint.Zero && _glContextHandle != nint.Zero)
        {
            _ = SDL.GLMakeCurrent(_windowHandle, _glContextHandle);
            DisposeGlResourcesLocked();
            SDL.GLDestroyContext(_glContextHandle);
            _glContextHandle = nint.Zero;
        }

        _packedRgbaScratch = null;

        if (_windowHandle == nint.Zero)
        {
            return;
        }

        SDL.DestroyWindow(_windowHandle);
        _windowHandle = nint.Zero;
    }

    private int PresentStandaloneFrameLocked(VideoFrame frame)
    {
        if (_windowHandle == nint.Zero || _glContextHandle == nint.Zero)
        {
            return (int)MediaErrorCode.SDL3EmbedHandleUnavailable;
        }

        if (!SDL.GLMakeCurrent(_windowHandle, _glContextHandle))
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        PumpSdlEvents();

        var ensureGl = EnsureGlResourcesLocked();
        if (ensureGl != MediaResult.Success)
        {
            return ensureGl;
        }

        if (SDL.GetWindowSizeInPixels(_windowHandle, out var pixelWidth, out var pixelHeight))
        {
            ApplyViewportForFrame(frame.Width, frame.Height, pixelWidth, pixelHeight);
        }

        int renderCode;
        if (frame.PixelFormat is VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32)
        {
            renderCode = RenderRgbaFrameLocked(frame);
        }
        else
        {
            renderCode = RenderYuvFrameLocked(frame);
        }

        if (renderCode != MediaResult.Success)
        {
            return renderCode;
        }

        if (!SDL.GLSwapWindow(_windowHandle))
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        return MediaResult.Success;
    }

    private int RenderRgbaFrameLocked(VideoFrame frame)
    {
        if (!TryGetPackedRgbaBytes(frame, out var contiguous, out var uploadFormat))
        {
            return (int)MediaErrorCode.SDL3EmbedInitializeFailed;
        }

        UploadTexture(ref _rgbaUploadState, _glTexture, frame.Width, frame.Height, Gl.Rgba8, uploadFormat, Gl.UnsignedByte, contiguous);

        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(Gl.ColorBufferBit);
        _glUseProgram!(_glProgram);
        _glActiveTexture!(Gl.Texture0);
        _glBindTexture!(Gl.TextureTarget2D, _glTexture);
        _glBindVertexArray!(_glVao);
        _glDrawArrays!(Gl.Triangles, 0, 6);
        _glBindVertexArray!(0);
        _glUseProgram!(0);
        return MediaResult.Success;
    }

    private int RenderYuvFrameLocked(VideoFrame frame)
    {
        if (!TryBuildYuvPlan(frame, out var plan))
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        var yData = PackPlane(frame.Plane0, frame.Plane0Stride, plan.YRowBytes, plan.YHeight, ref _plane0Scratch);
        if (yData is null)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        UploadTexture(ref _yUploadState, _glTextureY, plan.YWidth, plan.YHeight, plan.YInternalFormat, plan.YFormat, plan.YType, yData);

        if (plan.IsSemiPlanar)
        {
            var uvData = PackPlane(frame.Plane1, frame.Plane1Stride, plan.UvRowBytes, plan.UvHeight, ref _plane1Scratch);
            if (uvData is null)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            UploadTexture(ref _uUploadState, _glTextureU, plan.UvWidth, plan.UvHeight, plan.UvInternalFormat, plan.UvFormat, plan.UvType, uvData);
        }
        else
        {
            var uData = PackPlane(frame.Plane1, frame.Plane1Stride, plan.URowBytes, plan.UHeight, ref _plane1Scratch);
            var vData = PackPlane(frame.Plane2, frame.Plane2Stride, plan.VRowBytes, plan.VHeight, ref _plane2Scratch);
            if (uData is null || vData is null)
            {
                return (int)MediaErrorCode.MediaInvalidArgument;
            }

            UploadTexture(ref _uUploadState, _glTextureU, plan.UWidth, plan.UHeight, plan.UInternalFormat, plan.UFormat, plan.UType, uData);
            UploadTexture(ref _vUploadState, _glTextureV, plan.VWidth, plan.VHeight, plan.VInternalFormat, plan.VFormat, plan.VType, vData);
        }

        _glClearColor!(0f, 0f, 0f, 1f);
        _glClear!(Gl.ColorBufferBit);
        _glUseProgram!(_glYuvProgram);
        if (_glYuvPixelFormatLocation >= 0)
        {
            _glUniform1I!(_glYuvPixelFormatLocation, plan.ModeId);
        }
        if (_glYuvFullRangeLocation >= 0)
        {
            _glUniform1I!(_glYuvFullRangeLocation, frame.IsFullRange ? 1 : 0); // B6
        }

        _glActiveTexture!(Gl.Texture0);
        _glBindTexture!(Gl.TextureTarget2D, _glTextureY);
        _glActiveTexture!(Gl.Texture1);
        _glBindTexture!(Gl.TextureTarget2D, _glTextureU);
        _glActiveTexture!(Gl.Texture2);
        _glBindTexture!(Gl.TextureTarget2D, plan.IsSemiPlanar ? _glTextureU : _glTextureV);

        _glBindVertexArray!(_glVao);
        _glDrawArrays!(Gl.Triangles, 0, 6);
        _glBindVertexArray!(0);
        _glUseProgram!(0);
        return MediaResult.Success;
    }

    private void UploadTexture(ref TextureUploadState state, int textureId, int width, int height, int internalFormat, int format, int type, ReadOnlySpan<byte> data)
    {
        _glBindTexture!(Gl.TextureTarget2D, textureId);
        _glPixelStoreI!(Gl.UnpackAlignment, 1);

        var reallocate = !state.IsInitialized
            || state.Width != width
            || state.Height != height
            || state.InternalFormat != internalFormat
            || state.Format != format
            || state.Type != type;

        unsafe
        {
            fixed (byte* ptr = data)
            {
                if (reallocate)
                {
                    _glTexImage2D!(0x0DE1, 0, internalFormat, width, height, 0, format, type, (nint)ptr);
                    state = new TextureUploadState
                    {
                        IsInitialized = true,
                        Width = width,
                        Height = height,
                        InternalFormat = internalFormat,
                        Format = format,
                        Type = type,
                    };
                }
                else
                {
                    _glTexSubImage2D!(0x0DE1, 0, 0, 0, width, height, format, type, (nint)ptr);
                }
            }
        }
    }

    private bool TryGetPackedRgbaBytes(VideoFrame frame, out ReadOnlySpan<byte> packed, out int glFormat)
    {
        glFormat = frame.PixelFormat == VideoPixelFormat.Bgra32 ? Gl.Bgra : Gl.Rgba;
        packed = default;

        var requiredStride = frame.Width * 4;
        var requiredLength = checked(requiredStride * frame.Height);
        if (frame.Plane0.Length < requiredLength)
        {
            return false;
        }

        if (frame.Plane0Stride == requiredStride)
        {
            packed = frame.Plane0.Span.Slice(0, requiredLength);
            return true;
        }

        if (_packedRgbaScratch is null || _packedRgbaScratch.Length < requiredLength)
        {
            _packedRgbaScratch = new byte[requiredLength];
        }

        var source = frame.Plane0.Span;
        for (var y = 0; y < frame.Height; y++)
        {
            var srcOffset = y * frame.Plane0Stride;
            var dstOffset = y * requiredStride;
            source.Slice(srcOffset, requiredStride).CopyTo(_packedRgbaScratch.AsSpan(dstOffset, requiredStride));
        }

        packed = _packedRgbaScratch.AsSpan(0, requiredLength);
        return true;
    }

    private byte[]? PackPlane(ReadOnlyMemory<byte> plane, int stride, int rowBytes, int height, ref byte[]? scratch)
    {
        if (rowBytes <= 0 || height <= 0 || stride < rowBytes)
        {
            return null;
        }

        var requiredLength = checked(rowBytes * height);
        if (plane.Length < checked(stride * height))
        {
            return null;
        }

        if (scratch is null || scratch.Length < requiredLength)
        {
            scratch = new byte[requiredLength];
        }

        var source = plane.Span;
        var destination = scratch.AsSpan(0, requiredLength);
        for (var y = 0; y < height; y++)
        {
            source.Slice(y * stride, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
        }

        return scratch;
    }

    // N6: YuvPlan record and TryBuildYuvPlan moved to YuvPlan.cs (shared with SDL3ShaderPipeline).
    private static bool TryBuildYuvPlan(VideoFrame frame, out YuvPlan plan)
        => YuvPlan.TryBuild(frame, out plan);

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
        _rgbaUploadState = default;
        _yUploadState = default;
        _uUploadState = default;
        _vUploadState = default;
        _plane0Scratch = null;
        _plane1Scratch = null;
        _plane2Scratch = null;

        _glInitialized = false;
    }

    // Shader sources are in GlslShaders.cs (S.Media.OpenGL) — single source of truth.

    private static void PumpSdlEvents()
    {
        while (SDL.PollEvent(out _))
        {
        }
    }

    private void ApplyViewportForFrame(int frameWidth, int frameHeight, int windowWidth, int windowHeight)
    {
        var safeWindowWidth = Math.Max(1, windowWidth);
        var safeWindowHeight = Math.Max(1, windowHeight);
        if (!_preserveAspectRatio || frameWidth <= 0 || frameHeight <= 0)
        {
            _glViewport!(0, 0, safeWindowWidth, safeWindowHeight);
            return;
        }

        var sourceAspect = (double)frameWidth / frameHeight;
        var targetAspect = (double)safeWindowWidth / safeWindowHeight;

        int viewportWidth;
        int viewportHeight;
        if (targetAspect > sourceAspect)
        {
            viewportHeight = safeWindowHeight;
            viewportWidth = Math.Max(1, (int)Math.Round(viewportHeight * sourceAspect));
        }
        else
        {
            viewportWidth = safeWindowWidth;
            viewportHeight = Math.Max(1, (int)Math.Round(viewportWidth / sourceAspect));
        }

        var viewportX = (safeWindowWidth - viewportWidth) / 2;
        var viewportY = (safeWindowHeight - viewportHeight) / 2;
        _glViewport!(viewportX, viewportY, viewportWidth, viewportHeight);
    }

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

    private struct TextureUploadState
    {
        public bool IsInitialized;
        public int Width;
        public int Height;
        public int InternalFormat;
        public int Format;
        public int Type;
    }

    // ── Render thread ─────────────────────────────────────────────────────────

    private void RenderLoop()
    {
        // Capture the handles once; they are immutable while the render thread lives
        // (Stop() joins before DestroyStandaloneWindowLocked() can run).
        nint wnd, ctx;
        lock (_gate) { wnd = _windowHandle; ctx = _glContextHandle; }

        if (wnd == nint.Zero || ctx == nint.Zero) return;

        if (!SDL.GLMakeCurrent(wnd, ctx))
        {
            Console.Error.WriteLine("[SDL3VideoView] RenderLoop: GLMakeCurrent failed – " + SDL.GetError());
            return;
        }

        // Apply hardware VSync based on the output config set at Start().
        // VSync mode → SwapInterval(1); all other modes → SwapInterval(0) so the
        // software timing in OpenGLVideoOutput is the sole pacing mechanism.
        var useHwVSync = _renderConfig.PresentationMode == VideoOutputPresentationMode.VSync;
        _ = SDL.GLSetSwapInterval(useHwVSync ? 1 : 0);

        // Initialise GL resources and the shader pipeline on this thread.
        lock (_gate)
        {
            var glInit = EnsureGlResourcesLocked();
            if (glInit != MediaResult.Success)
            {
                SDL.GLMakeCurrent(wnd, nint.Zero);
                return;
            }

            if (!_pipelineReady)
            {
                var pipeInit = _shaderPipeline.EnsureInitialized();
                if (pipeInit == MediaResult.Success)
                    _pipelineReady = true;
                // Non-fatal if pipeline fails – standalone rendering still works.
            }
        }

        while (!_renderStopRequested)
        {
            // TODO: on macOS SDL events must be pumped on the main thread.
            //       For now, pump them here (fine on Linux X11/Wayland).
            PumpSdlEvents();

            (VideoFrame Frame, TimeSpan Pts) item;
            bool hasItem;
            lock (_renderQueueLock) { hasItem = _renderQueue.TryDequeue(out item); }

            if (!hasItem)
            {
                Thread.Sleep(1);
                continue;
            }

            using (item.Frame)
            {
                RenderFrameOnRenderThread(item.Frame, item.Pts, wnd);
            }
        }

        // Release the GL context before returning so it can be destroyed on the main thread.
        SDL.GLMakeCurrent(wnd, nint.Zero);
    }

    private void RenderFrameOnRenderThread(VideoFrame frame, TimeSpan pts, nint wnd)
    {
        var push = Output.PushFrame(frame, pts);
        if (push != MediaResult.Success) return;

        var generation = Output.LastPresentedFrameGeneration;
        if (generation == _lastPresentedGeneration) return;
        _lastPresentedGeneration = generation;


        if (SDL.GetWindowSizeInPixels(wnd, out var pw, out var ph))
            ApplyViewportForFrame(frame.Width, frame.Height, pw, ph);

        var uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
        int renderCode;
        if (frame.PixelFormat is VideoPixelFormat.Rgba32 or VideoPixelFormat.Bgra32)
            renderCode = RenderRgbaFrameLocked(frame);
        else
            renderCode = RenderYuvFrameLocked(frame);
        var uploadMs = System.Diagnostics.Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;

        if (renderCode != MediaResult.Success) return;

        var swapStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!SDL.GLSwapWindow(wnd))
        {
            Console.Error.WriteLine("[SDL3VideoView] GLSwapWindow failed – " + SDL.GetError());
            return;
        }
        var presentMs = System.Diagnostics.Stopwatch.GetElapsedTime(swapStart).TotalMilliseconds;

        Output.UpdateTimings(uploadMs, presentMs);
    }
}
