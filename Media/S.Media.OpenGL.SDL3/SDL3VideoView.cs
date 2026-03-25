using S.Media.Core.Errors;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.SDL3;

public sealed class SDL3VideoView : IVideoOutput
{
    private static long _nextSyntheticHandle = 1;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, SDL3VideoView> _clones = [];
    private readonly OpenGLVideoOutput _output;
    private readonly OpenGLVideoEngine _engine;
    private readonly SDL3HudRenderer _hudRenderer = new();
    private readonly SDL3ShaderPipeline _shaderPipeline = new();
    private bool _disposed;
    private bool _initialized;
    private bool _pipelineReady;
    private bool _embedded;
    private bool _parentLost;
    private bool _hudDirty;
    private long _lastPresentedGeneration = -1;
    private nint _platformHandle;
    private string _platformDescriptor = string.Empty;

    public SDL3VideoView() : this(new OpenGLVideoOutput(), new OpenGLVideoEngine(), isClone: false)
    {
    }

    private SDL3VideoView(OpenGLVideoOutput output, OpenGLVideoEngine engine, bool isClone)
    {
        _output = output;
        _engine = engine;
        IsClone = isClone;

        var add = _engine.AddOutput(_output);
        if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
        {
            throw new InvalidOperationException($"Failed to register SDL3 output in OpenGL engine. Code={add}.");
        }
    }

    public Guid Id => _output.Id;

    public bool EnableHudOverlay { get; set; }

    public SDL3HudRenderer HudRenderer => _hudRenderer;

    public bool IsClone { get; private set; }

    public Guid? CloneParentOutputId => _output.CloneParentOutputId;

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
            _platformHandle = AllocateSyntheticHandle();
            _platformDescriptor = NormalizeDescriptor(options.PreferredDescriptor);
            if (string.IsNullOrEmpty(_platformDescriptor))
            {
                return (int)MediaErrorCode.SDL3EmbedUnsupportedDescriptor;
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
            _platformHandle = parentHandle;
            _platformDescriptor = "x11-window";
            return MediaResult.Success;
        }
    }

    public int Start(VideoOutputConfig config)
    {
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

            if (!_pipelineReady)
            {
                var init = _shaderPipeline.EnsureInitialized();
                if (init != MediaResult.Success)
                {
                    return init;
                }

                _pipelineReady = true;
            }

            return _output.Start(config);
        }
    }

    public int Start()
    {
        return Start(new VideoOutputConfig());
    }

    public int Stop()
    {
        return _output.Stop();
    }

    public int PushFrame(VideoFrame frame)
    {
        return PushFrame(frame, frame.PresentationTime);
    }

    public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
    {
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

            var push = _output.PushFrame(frame, presentationTime);
            if (push != MediaResult.Success)
            {
                return push;
            }

            var generation = _output.Surface.LastPresentedFrameGeneration;
            if (generation == _lastPresentedGeneration)
            {
                if (EnableHudOverlay && _hudDirty)
                {
                    _ = _hudRenderer.Render();
                    _hudDirty = false;
                }

                return MediaResult.Success;
            }

            _lastPresentedGeneration = generation;

            if (_pipelineReady)
            {
                var upload = _shaderPipeline.Upload(frame);
                if (upload != MediaResult.Success)
                {
                    return upload;
                }

                var draw = _shaderPipeline.Draw();
                if (draw != MediaResult.Success)
                {
                    return draw;
                }
            }

            if (EnableHudOverlay)
            {
                _ = _hudRenderer.Render();
                _hudDirty = false;
            }

            return MediaResult.Success;
        }
    }

    public int PushAudio(in AudioFrame frame, TimeSpan presentationTime)
    {
        _ = frame;
        _ = presentationTime;
        return (int)MediaErrorCode.MediaInvalidArgument;
    }

    public int UpdateHud(DebugInfo debugInfo)
    {
        var code = _hudRenderer.Update(debugInfo);
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
            if (!_initialized)
            {
                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            if (_parentLost)
            {
                return (int)MediaErrorCode.SDL3EmbedParentLost;
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
            if (!_initialized)
            {
                descriptor = string.Empty;
                return (int)MediaErrorCode.SDL3EmbedDescriptorUnavailable;
            }

            if (_parentLost)
            {
                descriptor = string.Empty;
                return (int)MediaErrorCode.SDL3EmbedParentLost;
            }

            if (string.IsNullOrWhiteSpace(_platformDescriptor))
            {
                descriptor = string.Empty;
                return (int)MediaErrorCode.SDL3EmbedUnsupportedDescriptor;
            }

            descriptor = _platformDescriptor;
            return MediaResult.Success;
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

            var create = _engine.CreateCloneOutput(Id, ToOpenGlCloneOptions(options), out var cloneBaseOutput);
            if (create != MediaResult.Success || cloneBaseOutput is not OpenGLVideoOutput glClone)
            {
                return create != MediaResult.Success ? create : (int)MediaErrorCode.OpenGLCloneCreationFailed;
            }

            cloneView = new SDL3VideoView(glClone, _engine, isClone: true);
            var cloneInit = _embedded
                ? cloneView.InitializeEmbedded(_platformHandle, 1, 1)
                : cloneView.Initialize(new SDL3VideoViewOptions { PreferredDescriptor = _platformDescriptor });
            if (cloneInit != MediaResult.Success)
            {
                _ = _engine.RemoveOutput(cloneView.Id);
                cloneView.Dispose();
                cloneView = null;
                return cloneInit;
            }

            _clones[cloneView.Id] = cloneView;
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

            var add = _engine.AddOutput(cloneView._output);
            if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
            {
                return add;
            }

            var attach = _engine.AttachCloneOutput(Id, cloneView.Id);
            if (attach != MediaResult.Success)
            {
                return attach;
            }

            cloneView.IsClone = true;
            _clones[cloneView.Id] = cloneView;
            return MediaResult.Success;
        }
    }

    public int DetachClone(Guid cloneViewId)
    {
        lock (_gate)
        {
            var detach = _engine.DetachCloneOutput(Id, cloneViewId);
            if (detach != MediaResult.Success)
            {
                return detach;
            }

            _clones.Remove(cloneViewId, out _);

            return MediaResult.Success;
        }
    }

    public void SimulateEmbeddedParentLost()
    {
        SDL3VideoView[] clonesToDispose;

        lock (_gate)
        {
            if (_embedded)
            {
                clonesToDispose = ApplyParentLossTeardownLocked();
            }
            else
            {
                clonesToDispose = [];
            }
        }

        foreach (var clone in clonesToDispose)
        {
            clone.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _clones.Clear();
            _platformHandle = nint.Zero;
            _platformDescriptor = string.Empty;
            _initialized = false;
            _pipelineReady = false;
            _lastPresentedGeneration = -1;
            _hudDirty = false;
        }

        _shaderPipeline.Dispose();
        _ = _engine.RemoveOutput(Id);
        if (!IsClone)
        {
            _engine.Dispose();
        }

        _output.Dispose();
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
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return "wayland-surface";
        }

        return "x11-window";
    }

    private static nint AllocateSyntheticHandle()
    {
        var handle = Interlocked.Increment(ref _nextSyntheticHandle);
        return new nint(handle == 0 ? 1 : handle);
    }

    private SDL3VideoView[] ApplyParentLossTeardownLocked()
    {
        if (_parentLost)
        {
            return [];
        }

        _parentLost = true;
        _initialized = false;
        _pipelineReady = false;
        _platformHandle = nint.Zero;
        _platformDescriptor = string.Empty;
        _lastPresentedGeneration = -1;
        _hudDirty = false;

        var clones = _clones.Values.ToArray();
        _clones.Clear();
        return clones;
    }
}

