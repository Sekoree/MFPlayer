using S.Media.Core.Errors;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL.SDL3;

public sealed class SDL3VideoView : IVideoOutput, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, SDL3VideoView> _clones = [];
    private readonly OpenGLVideoOutput _output = new();
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

    public Guid Id => _output.Id;

    public bool EnableHudOverlay { get; set; }

    public SDL3HudRenderer HudRenderer => _hudRenderer;

    public bool IsClone { get; private set; }

    public Guid? CloneParentOutputId { get; private set; }

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
            _platformHandle = new nint(Id.GetHashCode());
            _platformDescriptor = string.IsNullOrWhiteSpace(options.PreferredDescriptor)
                ? "x11-window"
                : options.PreferredDescriptor;

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
            if (!_initialized)
            {
                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            if (_parentLost)
            {
                return (int)MediaErrorCode.SDL3EmbedParentLost;
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
            if (!_initialized)
            {
                return (int)MediaErrorCode.SDL3EmbedNotInitialized;
            }

            if (_parentLost)
            {
                return (int)MediaErrorCode.SDL3EmbedParentLost;
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
        return (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
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

            cloneView = new SDL3VideoView
            {
                IsClone = true,
                CloneParentOutputId = Id,
            };

            var init = _embedded
                ? cloneView.InitializeEmbedded(_platformHandle, 1, 1)
                : cloneView.Initialize(new SDL3VideoViewOptions());
            if (init != MediaResult.Success)
            {
                cloneView = null;
                return init;
            }

            _clones[cloneView.Id] = cloneView;
            return MediaResult.Success;
        }
    }

    public int AttachClone(SDL3VideoView cloneView, in SDL3CloneOptions options)
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

            if (cloneView.CloneParentOutputId.HasValue && cloneView.CloneParentOutputId.Value != Id)
            {
                return (int)MediaErrorCode.OpenGLCloneChildAlreadyAttached;
            }

            if (_clones.ContainsKey(cloneView.Id))
            {
                return (int)MediaErrorCode.OpenGLCloneAlreadyAttached;
            }

            cloneView.CloneParentOutputId = Id;
            cloneView.IsClone = true;
            _clones[cloneView.Id] = cloneView;
            return MediaResult.Success;
        }
    }

    public int DetachClone(Guid cloneViewId)
    {
        lock (_gate)
        {
            if (!_clones.Remove(cloneViewId, out var clone))
            {
                return (int)MediaErrorCode.OpenGLCloneNotAttached;
            }

            clone.CloneParentOutputId = null;
            return MediaResult.Success;
        }
    }

    public void SimulateEmbeddedParentLost()
    {
        lock (_gate)
        {
            if (_embedded)
            {
                _parentLost = true;
            }
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
            foreach (var clone in _clones.Values)
            {
                clone.CloneParentOutputId = null;
            }

            _clones.Clear();
            _platformHandle = nint.Zero;
            _platformDescriptor = string.Empty;
            _initialized = false;
            _pipelineReady = false;
            _lastPresentedGeneration = -1;
            _hudDirty = false;
        }

        _shaderPipeline.Dispose();
        _output.Dispose();
    }
}

