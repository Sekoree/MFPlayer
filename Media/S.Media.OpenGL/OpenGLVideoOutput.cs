using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.Diagnostics;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL;

public sealed class OpenGLVideoOutput : IVideoOutput
{
    private readonly Lock _gate = new();
    private readonly List<Guid> _cloneOutputIds = [];
    private bool _disposed;
    private bool _running;
    private OpenGLDiagnosticsEvents? _diagnostics;
    private long _framesPresented;
    private long _framesDropped;
    private long _framesCloned;

    internal OpenGLVideoOutput(Guid id, bool isClone)
    {
        Id = id;
        IsClone = isClone;
        Surface = OpenGLSurfaceMetadata.Empty;
    }

    public OpenGLVideoOutput() : this(Guid.NewGuid(), isClone: false)
    {
    }

    public Guid Id { get; }

    public bool IsClone { get; }

    public Guid? CloneParentOutputId { get; internal set; }

    public IReadOnlyList<Guid> CloneOutputIds
    {
        get
        {
            lock (_gate)
            {
                return _cloneOutputIds.ToArray();
            }
        }
    }

    public long LastPresentedFrameGeneration { get; private set; }

    public OpenGLSurfaceMetadata Surface { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public int Start(VideoOutputConfig config)
    {
        OpenGLDiagnosticsEvents? diagnostics;
        OpenGLOutputDebugInfo snapshot;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            var validate = config.Validate(hasEffectiveFrameDuration: false);
            if (validate != MediaResult.Success)
            {
                return validate;
            }

            _running = true;
            diagnostics = _diagnostics;
            snapshot = BuildDiagnosticsSnapshotLocked();
        }

        diagnostics?.PublishDiagnosticsUpdated(Id, snapshot);
        return MediaResult.Success;
    }

    internal void SetDiagnostics(OpenGLDiagnosticsEvents diagnostics)
    {
        lock (_gate)
        {
            _diagnostics = diagnostics;
        }
    }

    public int Stop()
    {
        OpenGLDiagnosticsEvents? diagnostics;
        OpenGLOutputDebugInfo snapshot;

        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            _running = false;
            diagnostics = _diagnostics;
            snapshot = BuildDiagnosticsSnapshotLocked();
        }

        diagnostics?.PublishDiagnosticsUpdated(Id, snapshot);
        return MediaResult.Success;
    }

    public int PushFrame(VideoFrame frame)
    {
        return PushFrame(frame, frame.PresentationTime);
    }

    public int PushFrame(VideoFrame frame, TimeSpan presentationTime)
    {
        OpenGLDiagnosticsEvents? diagnostics;
        OpenGLSurfaceMetadata surface;
        OpenGLOutputDebugInfo snapshot;
        int resultCode;

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            if (!_running)
            {
                return (int)MediaErrorCode.OpenGLCloneParentNotInitialized;
            }

            var frameValidation = frame.ValidateForPush();
            if (frameValidation != MediaResult.Success)
            {
                _framesDropped++;
                diagnostics = _diagnostics;
                snapshot = BuildDiagnosticsSnapshotLocked();
                surface = Surface;
                resultCode = frameValidation;
            }
            else
            {
                _framesPresented++;
                LastPresentedFrameGeneration++;
                Surface = BuildSurfaceMetadata(frame, LastPresentedFrameGeneration);
                diagnostics = _diagnostics;
                surface = Surface;
                snapshot = BuildDiagnosticsSnapshotLocked();
                resultCode = MediaResult.Success;
            }
        }

        diagnostics?.PublishSurfaceChanged(surface);
        diagnostics?.PublishDiagnosticsUpdated(Id, snapshot);
        return resultCode;
    }

    public void Dispose()
    {
        OpenGLDiagnosticsEvents? diagnostics;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _running = false;
            _cloneOutputIds.Clear();
            CloneParentOutputId = null;
            _disposed = true;
            Surface = OpenGLSurfaceMetadata.Empty;
            diagnostics = _diagnostics;
            _diagnostics = null;
        }

        diagnostics?.PublishSurfaceChanged(OpenGLSurfaceMetadata.Empty);
    }

    internal int AddClone(Guid cloneId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            if (_cloneOutputIds.Contains(cloneId))
            {
                return (int)MediaErrorCode.OpenGLCloneAlreadyAttached;
            }

            _cloneOutputIds.Add(cloneId);
            return MediaResult.Success;
        }
    }

    internal int RemoveClone(Guid cloneId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }

            return _cloneOutputIds.Remove(cloneId)
                ? MediaResult.Success
                : (int)MediaErrorCode.OpenGLCloneNotAttached;
        }
    }

    internal void PresentClonedFrame(OpenGLSurfaceMetadata parentSurface)
    {
        OpenGLDiagnosticsEvents? diagnostics;
        OpenGLOutputDebugInfo snapshot;

        lock (_gate)
        {
            if (_disposed || !_running)
            {
                return;
            }

            _framesCloned++;
            LastPresentedFrameGeneration = parentSurface.LastPresentedFrameGeneration;
            Surface = parentSurface;
            diagnostics = _diagnostics;
            snapshot = BuildDiagnosticsSnapshotLocked();
        }

        diagnostics?.PublishSurfaceChanged(parentSurface);
        diagnostics?.PublishDiagnosticsUpdated(Id, snapshot);
    }

    private OpenGLOutputDebugInfo BuildDiagnosticsSnapshotLocked()
    {
        return new OpenGLOutputDebugInfo(
            FramesPresented: _framesPresented,
            FramesDropped: _framesDropped,
            FramesCloned: _framesCloned,
            LastUploadMs: 0,
            LastPresentMs: 0,
            Surface: Surface);
    }

    private static OpenGLSurfaceMetadata BuildSurfaceMetadata(VideoFrame frame, long generation)
    {
        var strides = new List<int>(4);
        if (frame.Plane0.Length > 0)
        {
            strides.Add(frame.Plane0Stride);
        }

        if (frame.Plane1.Length > 0)
        {
            strides.Add(frame.Plane1Stride);
        }

        if (frame.Plane2.Length > 0)
        {
            strides.Add(frame.Plane2Stride);
        }

        if (frame.Plane3.Length > 0)
        {
            strides.Add(frame.Plane3Stride);
        }

        return new OpenGLSurfaceMetadata(
            SurfaceWidth: frame.Width,
            SurfaceHeight: frame.Height,
            RenderWidth: frame.Width,
            RenderHeight: frame.Height,
            PixelFormat: frame.PixelFormat,
            PlaneCount: strides.Count,
            PlaneStrides: strides,
            LastPresentedFrameGeneration: generation);
    }
}
