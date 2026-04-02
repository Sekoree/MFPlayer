using System.Diagnostics;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.Diagnostics;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL;

public sealed class OpenGLVideoOutput : IVideoOutput
{
    private readonly Lock _gate = new();
    private bool _disposed;
    private bool _running;
    private OpenGLDiagnosticsEvents? _diagnostics;
    private long _framesPresented;
    private long _framesDropped;
    private long _framesCloned;
    private double _lastUploadMs;
    private double _lastPresentMs;
    private VideoOutputConfig _config = new();
    private bool _hasTimelineAnchor;
    private double _anchorPtsSeconds;
    private long _anchorTicks;
    private double _lastNormalizedPtsSeconds = double.NaN;
    private long _lastPresentTicks;

    internal OpenGLVideoOutput(Guid id)
    {
        Id = id;
        Surface = OpenGLSurfaceMetadata.Empty;
    }

    public OpenGLVideoOutput() : this(Guid.NewGuid())
    {
    }

    public Guid Id { get; }

    public VideoOutputState State => _running ? VideoOutputState.Running : VideoOutputState.Stopped;

    /// <summary>
    /// The ID of the parent output this output was attached to as a clone, or
    /// <see langword="null"/> if not attached as a clone.
    /// Topology is managed by <see cref="OpenGLVideoEngine"/>; this field is
    /// set/cleared by the engine via <see cref="SetCloneParent"/>.
    /// </summary>
    public Guid? CloneParentOutputId { get; private set; }

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
        VideoOutputDiagnosticsSnapshot snapshot;

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
        VideoOutputDiagnosticsSnapshot snapshot;

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
        VideoOutputDiagnosticsSnapshot snapshot;
        int resultCode;
        TimeSpan delay = TimeSpan.Zero;
        bool dropAsStale = false;

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
            PrecisionWait(delay);
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

        if (_config.PresentationMode == VideoOutputPresentationMode.Unlimited)
        {
            return (TimeSpan.Zero, false);
        }

        if (_config.PresentationMode == VideoOutputPresentationMode.VSync)
        {
            // Software VSync approximation: cap to the configured (or default 60 Hz) refresh rate.
            // True hardware VSync is handled by the display backend (e.g. SDL3 GLSetSwapInterval).
            var vsyncFps = _config.VSyncRefreshRate is > 0 ? (double)_config.VSyncRefreshRate.Value : 60.0;
            if (_lastPresentTicks <= 0)
                return (TimeSpan.Zero, false);
            var frameIntervalTicks = (long)(Stopwatch.Frequency / vsyncFps);
            var vsyncTarget = _lastPresentTicks + Math.Max(1, frameIntervalTicks);
            if (vsyncTarget <= nowTicks)
                return (TimeSpan.Zero, false);
            var wait = TimeSpan.FromSeconds((vsyncTarget - nowTicks) / (double)Stopwatch.Frequency);
            return (ClampWait(wait, _config.MaxSchedulingWait), false);
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
            CloneParentOutputId = null;
            _disposed = true;
            Surface = OpenGLSurfaceMetadata.Empty;
            diagnostics = _diagnostics;
            _diagnostics = null;
        }

        diagnostics?.PublishSurfaceChanged(OpenGLSurfaceMetadata.Empty);
    }

    /// <summary>
    /// Sets or clears the parent clone ID. Called exclusively by <see cref="OpenGLVideoEngine"/>
    /// when attaching or detaching this output as a clone.
    /// </summary>
    internal void SetCloneParent(Guid? parentId)
    {
        lock (_gate)
        {
            CloneParentOutputId = parentId;
        }
    }

    /// <summary>
    /// Records the most recent GPU upload and present timings so
    /// <see cref="BuildDiagnosticsSnapshotLocked"/> can include real metrics.
    /// Called by the rendering backend (SDL3 render loop / Avalonia GL render pass)
    /// after each frame is uploaded and drawn.
    /// </summary>
    internal void UpdateTimings(double lastUploadMs, double lastPresentMs)
    {
        lock (_gate)
        {
            _lastUploadMs = lastUploadMs;
            _lastPresentMs = lastPresentMs;
        }
    }

    internal void PresentClonedFrame(OpenGLSurfaceMetadata parentSurface)
    {
        OpenGLDiagnosticsEvents? diagnostics;
        VideoOutputDiagnosticsSnapshot snapshot;

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

    private VideoOutputDiagnosticsSnapshot BuildDiagnosticsSnapshotLocked()
    {
        return new VideoOutputDiagnosticsSnapshot(
            FramesPresented: _framesPresented,
            FramesDropped: _framesDropped,
            FramesCloned: _framesCloned,
            LastUploadMs: _lastUploadMs,
            LastPresentMs: _lastPresentMs,
            Surface: Surface);
    }

    private static OpenGLSurfaceMetadata BuildSurfaceMetadata(VideoFrame frame, long generation)
    {
        var count = 0;
        var strides = new int[4];
        if (frame.Plane0.Length > 0)
        {
            strides[count++] = frame.Plane0Stride;
        }

        if (frame.Plane1.Length > 0)
        {
            strides[count++] = frame.Plane1Stride;
        }

        if (frame.Plane2.Length > 0)
        {
            strides[count++] = frame.Plane2Stride;
        }

        if (frame.Plane3.Length > 0)
        {
            strides[count++] = frame.Plane3Stride;
        }

        return new OpenGLSurfaceMetadata(
            SurfaceWidth: frame.Width,
            SurfaceHeight: frame.Height,
            PixelFormat: frame.PixelFormat,
            PlaneCount: count,
            PlaneStrides: count == strides.Length ? strides : strides[..count],
            LastPresentedFrameGeneration: generation);
    }

    /// <summary>
    /// Hybrid precision wait that avoids the ~15 ms <c>Thread.Sleep</c> floor on Windows.
    /// For delays above the spin threshold, yields the time slice with <c>Thread.Sleep(1)</c>;
    /// the remaining sub-threshold portion uses a <see cref="SpinWait"/> loop with
    /// <see cref="Stopwatch.GetTimestamp()"/> for microsecond-level accuracy.
    /// </summary>
    private static void PrecisionWait(TimeSpan delay)
    {
        // Spin threshold: on Windows Thread.Sleep(1) can overshoot by 10–15 ms,
        // so we switch to spinning when < 2 ms remains.  On Linux/macOS the
        // sleep resolution is already ~1 ms but spinning the last stretch still
        // improves jitter.
        const double spinThresholdSeconds = 0.002; // 2 ms

        var targetTicks = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);

        // Coarse phase: yield CPU while we have time to spare.
        while (true)
        {
            var remainingSeconds = (targetTicks - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
            if (remainingSeconds <= spinThresholdSeconds)
                break;
            Thread.Sleep(1);
        }

        // Fine phase: spin until the target time.
        var sw = new SpinWait();
        while (Stopwatch.GetTimestamp() < targetTicks)
        {
            sw.SpinOnce(sleep1Threshold: -1); // never degrade to Thread.Sleep inside SpinWait
        }
    }
}
