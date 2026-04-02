using System.Runtime.InteropServices;
using S.Media.Core.Errors;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.OpenGL.Output;
using S.Media.OpenGL.Upload;
using SDL3;

namespace S.Media.OpenGL.SDL3;

public sealed partial class SDL3VideoView : OpenGLWrapperVideoOutput
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
    private readonly GlTextureUploader _uploader = new();
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
    private readonly ManualResetEventSlim _renderQueueReady = new(false);
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

        if (_renderConfig.BackpressureMode == VideoOutputBackpressureMode.Wait)
        {
            // Bounded wait: block until space is available or timeout expires.
            var timeout = _renderConfig.BackpressureTimeout ?? TimeSpan.FromMilliseconds(33);
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

            while (true)
            {
                lock (_renderQueueLock)
                {
                    if (_renderQueue.Count < capacity)
                    {
                        try { _renderQueue.Enqueue((frame.AddRef(), presentationTime)); }
                        catch (ObjectDisposedException) { return (int)MediaErrorCode.VideoFrameDisposed; }
                        _renderQueueReady.Set();
                        return MediaResult.Success;
                    }
                }

                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    return (int)MediaErrorCode.VideoOutputBackpressureTimeout;

                Thread.Sleep(Math.Min(1, (int)remaining));
            }
        }

        lock (_renderQueueLock)
        {
            if (_renderQueue.Count >= capacity)
            {
                if (_renderConfig.BackpressureMode == VideoOutputBackpressureMode.DropOldest)
                {
                    if (_renderQueue.TryDequeue(out var old)) toDrop = old.Frame;
                }
                else // DropNewest
                {
                    return (int)MediaErrorCode.VideoOutputBackpressureQueueFull;
                }
            }

            try { _renderQueue.Enqueue((frame.AddRef(), presentationTime)); }
            catch (ObjectDisposedException) { return (int)MediaErrorCode.VideoFrameDisposed; }
            _renderQueueReady.Set();
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
        _uploader.Reset();

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


        if (_windowHandle == nint.Zero)
        {
            return;
        }

        SDL.DestroyWindow(_windowHandle);
        _windowHandle = nint.Zero;
    }

    // ── Rendering methods are in SDL3VideoView.Rendering.cs ────────────────────

    // ── GL resource management is in SDL3VideoView.GlResources.cs ─────────────

    // Shader sources are in GlslShaders.cs (S.Media.OpenGL) — single source of truth.

    // ── Platform events + viewport are in SDL3VideoView.Rendering.cs ───────────
    // ── GL constants + TextureUploadState are in SDL3VideoView.GlResources.cs ──
    // ── Render thread loop is in SDL3VideoView.RenderLoop.cs ─────────────────
}

