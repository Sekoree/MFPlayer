using System.Diagnostics;
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
    private VideoOutputConfig _config = new();
    private bool _hasTimelineAnchor;
    private double _anchorPtsSeconds;
    private long _anchorTicks;
    private double _lastNormalizedPtsSeconds = double.NaN;
    private long _lastPresentTicks;

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

    public VideoOutputState State => _running ? VideoOutputState.Running : VideoOutputState.Stopped;

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

            _config = config;
            _hasTimelineAnchor = false;
            _anchorPtsSeconds = 0;
            _anchorTicks = 0;
            _lastNormalizedPtsSeconds = double.NaN;
            _lastPresentTicks = 0;
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
            _hasTimelineAnchor = false;
            _anchorPtsSeconds = 0;
            _anchorTicks = 0;
            _lastNormalizedPtsSeconds = double.NaN;
            _lastPresentTicks = 0;
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
        TimeSpan delay = TimeSpan.Zero;
        var dropAsStale = false;

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

            var timing = ComputePresentationTimingLocked(presentationTime);
            delay = timing.Delay;
            dropAsStale = timing.DropAsStale;
        }

        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                _framesDropped++;
                diagnostics = _diagnostics;
                snapshot = BuildDiagnosticsSnapshotLocked();
                surface = Surface;
                resultCode = (int)MediaErrorCode.OpenGLCloneParentDisposed;
            }
            else if (!_running)
            {
                _framesDropped++;
                diagnostics = _diagnostics;
                snapshot = BuildDiagnosticsSnapshotLocked();
                surface = Surface;
                resultCode = (int)MediaErrorCode.OpenGLCloneParentNotInitialized;
            }
            else
            {
                var frameValidation = frame.ValidateForPush();
                if (frameValidation != MediaResult.Success)
                {
                    _framesDropped++;
                    diagnostics = _diagnostics;
                    snapshot = BuildDiagnosticsSnapshotLocked();
                    surface = Surface;
                    resultCode = frameValidation;
                }
                else if (dropAsStale)
                {
                    _framesDropped++;
                    diagnostics = _diagnostics;
                    snapshot = BuildDiagnosticsSnapshotLocked();
                    surface = Surface;
                    resultCode = MediaResult.Success;
                }
                else
                {
                    _framesPresented++;
                    LastPresentedFrameGeneration++;
                    Surface = BuildSurfaceMetadata(frame, LastPresentedFrameGeneration);
                    _lastPresentTicks = Stopwatch.GetTimestamp();
                    diagnostics = _diagnostics;
                    surface = Surface;
                    snapshot = BuildDiagnosticsSnapshotLocked();
                    resultCode = MediaResult.Success;
                }
            }
        }

        diagnostics?.PublishSurfaceChanged(surface);
        diagnostics?.PublishDiagnosticsUpdated(Id, snapshot);
        return resultCode;
    }

    private (TimeSpan Delay, bool DropAsStale) ComputePresentationTimingLocked(TimeSpan presentationTime)
    {
        var nowTicks = Stopwatch.GetTimestamp();

        if (_config.PresentationMode is VideoOutputPresentationMode.Unlimited or VideoOutputPresentationMode.VSync)
        {
            return (TimeSpan.Zero, false);
        }

        if (_config.PresentationMode == VideoOutputPresentationMode.MaxFps)
        {
            if (!_config.MaxFps.HasValue || !double.IsFinite(_config.MaxFps.Value) || _config.MaxFps.Value <= 0)
            {
                return (TimeSpan.Zero, false);
            }

            if (_lastPresentTicks <= 0)
            {
                return (TimeSpan.Zero, false);
            }

            var frameIntervalTicks = (long)(Stopwatch.Frequency / _config.MaxFps.Value);
            var maxFpsTargetTicks = _lastPresentTicks + Math.Max(1, frameIntervalTicks);
            if (maxFpsTargetTicks <= nowTicks)
            {
                return (TimeSpan.Zero, false);
            }

            var wait = TimeSpan.FromSeconds((maxFpsTargetTicks - nowTicks) / (double)Stopwatch.Frequency);
            return (ClampWait(wait, _config.MaxSchedulingWait), false);
        }

        if (presentationTime < TimeSpan.Zero)
        {
            return (TimeSpan.Zero, false);
        }

        var ptsSeconds = NormalizePtsLocked(presentationTime.TotalSeconds, nowTicks);
        if (!_hasTimelineAnchor)
        {
            _hasTimelineAnchor = true;
            _anchorPtsSeconds = ptsSeconds;
            _anchorTicks = nowTicks;
            return (TimeSpan.Zero, false);
        }

        var targetTicks = _anchorTicks + (long)((ptsSeconds - _anchorPtsSeconds) * Stopwatch.Frequency);
        var lagTicks = nowTicks - targetTicks;

        if (_config.StaleFrameDropThreshold.HasValue)
        {
            var staleTicks = (long)(_config.StaleFrameDropThreshold.Value.TotalSeconds * Stopwatch.Frequency);
            if (lagTicks > staleTicks)
            {
                return (TimeSpan.Zero, true);
            }
        }

        if (targetTicks <= nowTicks)
        {
            return (TimeSpan.Zero, false);
        }

        var delay = TimeSpan.FromSeconds((targetTicks - nowTicks) / (double)Stopwatch.Frequency);
        return (ClampWait(delay, _config.MaxSchedulingWait), false);
    }

    private double NormalizePtsLocked(double ptsSeconds, long nowTicks)
    {
        if (double.IsNaN(_lastNormalizedPtsSeconds))
        {
            _lastNormalizedPtsSeconds = ptsSeconds;
            return ptsSeconds;
        }

        var previous = _lastNormalizedPtsSeconds;
        var thresholdSeconds = _config.TimestampDiscontinuityThreshold.TotalSeconds;

        switch (_config.TimestampMode)
        {
            case VideoTimestampMode.ClampForward:
                if (ptsSeconds < previous)
                {
                    ptsSeconds = previous;
                }
                break;

            case VideoTimestampMode.RebaseOnDiscontinuity:
                if (thresholdSeconds >= 0 && Math.Abs(ptsSeconds - previous) > thresholdSeconds)
                {
                    _anchorPtsSeconds = ptsSeconds;
                    _anchorTicks = nowTicks;
                }
                break;
        }

        _lastNormalizedPtsSeconds = ptsSeconds;
        return ptsSeconds;
    }

    private static TimeSpan ClampWait(TimeSpan wait, TimeSpan maxWait)
    {
        if (wait <= TimeSpan.Zero || maxWait <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return wait > maxWait ? maxWait : wait;
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
