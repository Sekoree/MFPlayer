using NDILib;
using S.Media.Core.Errors;
using S.Media.Core.Runtime;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Input;
using S.Media.NDI.Media;
using S.Media.NDI.Output;

namespace S.Media.NDI.Runtime;

public sealed class NDIEngine : IMediaEngine
{
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _diagnosticsStopSignal = new(false);
    private readonly List<NDIMediaItem> _mediaItems = [];       // P2.14: track intermediate items
    private readonly List<NDIAudioSource> _audioSources = [];
    private readonly List<NDIVideoSource> _videoSources = [];
    private readonly List<NDIVideoOutput> _outputs = [];
    private readonly Dictionary<NDIReceiver, INDICaptureCoordinator> _captureCoordinators = new();
    private Thread? _diagnosticsThread;
    private bool _diagnosticsRunning;
    private bool _disposed;

    private NDIIntegrationOptions _integrationOptions = new();
    private NDILimitsOptions _limitsOptions = new();
    private NDIDiagnosticsOptions _diagnosticsOptions = new();

    public bool IsInitialized { get; private set; }

    public event EventHandler<NDIEngineDiagnostics>? DiagnosticsUpdated;

    /// <summary>
    /// Initializes the NDI engine with default options (Balanced limits, Default diagnostics).
    /// </summary>
    public int Initialize() =>
        Initialize(new NDIIntegrationOptions(), NDILimitsOptions.Balanced, NDIDiagnosticsOptions.Default);

    public int Initialize(NDIIntegrationOptions integrationOptions, NDILimitsOptions limitsOptions,
        NDIDiagnosticsOptions diagnosticsOptions)
    {
        ArgumentNullException.ThrowIfNull(integrationOptions);
        ArgumentNullException.ThrowIfNull(limitsOptions);
        ArgumentNullException.ThrowIfNull(diagnosticsOptions);

        var diagnosticsThreadToJoin = default(Thread);

        lock (_gate)
        {
            if (_disposed)
                return (int)MediaErrorCode.NDIInitializeFailed;

            diagnosticsThreadToJoin = _diagnosticsThread;
            _diagnosticsRunning = false;
            _diagnosticsThread = null;
            _diagnosticsStopSignal.Set();

            _integrationOptions = integrationOptions;
            _limitsOptions = limitsOptions.Normalize();
            _diagnosticsOptions = diagnosticsOptions.Normalize();
            IsInitialized = true;
        }

        diagnosticsThreadToJoin?.Join(TimeSpan.FromSeconds(1));

        var startCode = TryStartDiagnosticsThread();
        if (startCode != MediaResult.Success)
            lock (_gate) { IsInitialized = false; }

        return startCode;
    }

    /// <summary>
    /// Terminates the NDI engine, stopping all sources/outputs and disposing coordinators.
    /// <para><b>⚠️ Blocking:</b> Joins the diagnostics thread with a 1-second timeout.
    /// Avoid calling from a UI thread.</para>
    /// </summary>
    public int Terminate()
    {
        Thread? diagnosticsThread;

        lock (_gate)
        {
            if (_disposed)
                return MediaResult.Success;

            diagnosticsThread = _diagnosticsThread;
            _diagnosticsRunning = false;
            _diagnosticsThread = null;
            _diagnosticsStopSignal.Set();

            foreach (var source in _audioSources) { _ = source.Stop(); source.Dispose(); }
            foreach (var source in _videoSources) { _ = source.Stop(); source.Dispose(); }
            foreach (var output in _outputs) { _ = output.Stop(); output.Dispose(); }

            _audioSources.Clear();
            _videoSources.Clear();
            _outputs.Clear();
            _mediaItems.Clear();

            // Issue 5.9: dispose coordinators so their SemaphoreSlim is released.
            foreach (var coordinator in _captureCoordinators.Values)
                coordinator.Dispose();
            _captureCoordinators.Clear();

            IsInitialized = false;
        }

        diagnosticsThread?.Join(TimeSpan.FromSeconds(1));
        return MediaResult.Success;
    }

    // ── Factory: CreateMediaItem (Issue 4.2) ──────────────────────────────────

    /// <summary>
    /// Creates an <see cref="NDIMediaItem"/> backed by <paramref name="receiver"/> using the
    /// engine's shared coordinator for that receiver. Use <see cref="NDIMediaItem.CreateAudioSource"/>
    /// and <see cref="NDIMediaItem.CreateVideoSource"/> on the returned item to create sources
    /// that correctly share a single capture call.
    /// </summary>
    public int CreateMediaItem(NDIReceiver receiver, out NDIMediaItem? item)
    {
        item = null;
        ArgumentNullException.ThrowIfNull(receiver);

        lock (_gate)
        {
            if (_disposed || !IsInitialized)
                return (int)MediaErrorCode.NDIInitializeFailed;

            var coordinator = GetOrCreateCaptureCoordinatorLocked(receiver);
            item = new NDIMediaItem(receiver, _integrationOptions, coordinator);
            _mediaItems.Add(item);  // P2.14: track item
            return MediaResult.Success;
        }
    }

    // ── Factory: CreateAudioSource ────────────────────────────────────────────

    public int CreateAudioSource(NDIReceiver receiver, in NDISourceOptions sourceOptions, out NDIAudioSource? source)
    {
        source = null;
        ArgumentNullException.ThrowIfNull(receiver);

        lock (_gate)
        {
            if (_disposed || !IsInitialized)
                return (int)MediaErrorCode.NDIInitializeFailed;

            var normalized = sourceOptions.Normalize();
            // Issue 5.4: return the specific validation error, not NDIReceiverCreateFailed.
            var optionsValidation = normalized.Validate();
            if (optionsValidation != MediaResult.Success)
                return optionsValidation;

            var coordinator = GetOrCreateCaptureCoordinatorLocked(receiver);
            var item = new NDIMediaItem(receiver, _integrationOptions, coordinator);
            _mediaItems.Add(item);  // P2.14: track intermediate item
            _ = item.CreateAudioSource(normalized, out source);
            if (source is null)
                return (int)MediaErrorCode.NDIReceiverCreateFailed;

            _audioSources.Add(source);
            return MediaResult.Success;
        }
    }

    // ── Factory: CreateVideoSource ────────────────────────────────────────────

    public int CreateVideoSource(NDIReceiver receiver, in NDISourceOptions sourceOptions, out NDIVideoSource? source)
    {
        source = null;
        ArgumentNullException.ThrowIfNull(receiver);

        lock (_gate)
        {
            if (_disposed || !IsInitialized)
                return (int)MediaErrorCode.NDIInitializeFailed;

            var normalized = sourceOptions.Normalize();
            // Issue 5.4: return the specific validation error, not NDIReceiverCreateFailed.
            var optionsValidation = normalized.Validate();
            if (optionsValidation != MediaResult.Success)
                return optionsValidation;

            var coordinator = GetOrCreateCaptureCoordinatorLocked(receiver);
            var item = new NDIMediaItem(receiver, _integrationOptions, coordinator);
            _mediaItems.Add(item);  // P2.14: track intermediate item
            _ = item.CreateVideoSource(normalized, out source);
            if (source is null)
                return (int)MediaErrorCode.NDIReceiverCreateFailed;

            _videoSources.Add(source);
            return MediaResult.Success;
        }
    }

    // ── Factory: CreateOutput ─────────────────────────────────────────────────

    public int CreateOutput(string outputName, in NDIOutputOptions outputOptions, out NDIVideoOutput? output)
    {
        output = null;

        lock (_gate)
        {
            if (_disposed || !IsInitialized)
                return (int)MediaErrorCode.NDIInitializeFailed;

            // Issue 4.1: RequireAudioPathOnStart lives only on NDIOutputOptions now.
            var effective = outputOptions with
            {
                SendFormatOverride = outputOptions.SendFormatOverride ?? _integrationOptions.SendFormat,
            };

            var validate = effective.Validate();
            if (validate != MediaResult.Success)
                return validate;

            output = new NDIVideoOutput(outputName, effective);
            _outputs.Add(output);
            return MediaResult.Success;
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    public int GetDiagnosticsSnapshot(out NDIEngineDiagnostics snapshot)
    {
        lock (_gate)
        {
            if (_disposed || !IsInitialized)
            {
                snapshot = default;
                return (int)MediaErrorCode.NDIDiagnosticsSnapshotUnavailable;
            }

            snapshot = BuildDiagnosticsSnapshotLocked();
            return MediaResult.Success;
        }
    }

    public void Dispose()
    {
        Thread? diagnosticsThread;

        lock (_gate)
        {
            if (_disposed) return;

            // P2.2: Set _disposed immediately so concurrent callers see it
            // before we release the lock to join the diagnostics thread.
            _disposed = true;

            diagnosticsThread = _diagnosticsThread;
            _diagnosticsRunning = false;
            _diagnosticsThread = null;
            _diagnosticsStopSignal.Set();
        }

        diagnosticsThread?.Join(TimeSpan.FromSeconds(1));
        _ = Terminate();

        lock (_gate)
        {
            DiagnosticsUpdated = null;
        }

        _diagnosticsStopSignal.Dispose();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private int TryStartDiagnosticsThread()
    {
        Thread? thread;

        lock (_gate)
        {
            if (_disposed || !IsInitialized)
                return (int)MediaErrorCode.NDIDiagnosticsThreadStartFailed;

            if (!_diagnosticsOptions.EnableDedicatedDiagnosticsThread ||
                _diagnosticsOptions.PublishSnapshotsOnRequestOnly)
                return MediaResult.Success;

            if (_diagnosticsThread is { IsAlive: true })
                return MediaResult.Success;

            _diagnosticsRunning = true;
            thread = new Thread(DiagnosticsLoop)
            {
                IsBackground = true,
                Name = "S.Media.NDI.Diagnostics",
            };
            _diagnosticsThread = thread;
        }

        try
        {
            thread.Start();
            return MediaResult.Success;
        }
        catch
        {
            lock (_gate) { _diagnosticsRunning = false; _diagnosticsThread = null; }
            return (int)MediaErrorCode.NDIDiagnosticsThreadStartFailed;
        }
    }

    private void DiagnosticsLoop()
    {
        while (true)
        {
            TimeSpan tickInterval;
            EventHandler<NDIEngineDiagnostics>? handler;
            NDIEngineDiagnostics snapshot;

            lock (_gate)
            {
                if (!_diagnosticsRunning || _disposed || !IsInitialized)
                    return;

                tickInterval = _diagnosticsOptions.DiagnosticsTickInterval;
                handler = DiagnosticsUpdated;
                snapshot = BuildDiagnosticsSnapshotLocked();
            }

            handler?.Invoke(this, snapshot);

            if (_diagnosticsStopSignal.WaitOne(tickInterval))
                return;
        }
    }

    private NDIEngineDiagnostics BuildDiagnosticsSnapshotLocked()
    {
        long audioCaptured = 0, audioDropped = 0, audioRejected = 0;
        double maxAudioReadMs = 0;
        foreach (var source in _audioSources)
        {
            var d = source.Diagnostics;
            audioCaptured += d.FramesCaptured;
            audioDropped  += d.FramesDropped;
            audioRejected += d.RejectedReads;
            maxAudioReadMs = Math.Max(maxAudioReadMs, d.LastReadMs);
        }

        long videoCaptured = 0, videoDropped = 0, videoRejected = 0, repeatedFrames = 0, fallbackFrames = 0;
        int queueDepth = 0, jitterBufferFrames = 0;
        string incomingPixelFormat = "none", outputPixelFormat = "none", conversionPath = "none";
        double maxVideoReadMs = 0;
        foreach (var source in _videoSources)
        {
            var d = source.Diagnostics;
            videoCaptured   += d.FramesCaptured;
            videoDropped    += d.FramesDropped;
            videoRejected   += d.RejectedReads;
            repeatedFrames  += d.RepeatedTimestampFramesPresented;
            fallbackFrames  += d.FallbackFramesPresented;
            queueDepth       = Math.Max(queueDepth, d.QueueDepth);
            jitterBufferFrames = Math.Max(jitterBufferFrames, d.JitterBufferFrames);
            if (!string.Equals(d.IncomingPixelFormat, "none", StringComparison.OrdinalIgnoreCase))
                incomingPixelFormat = d.IncomingPixelFormat;
            if (!string.Equals(d.OutputPixelFormat, "none", StringComparison.OrdinalIgnoreCase))
                outputPixelFormat = d.OutputPixelFormat;
            if (!string.Equals(d.ConversionPath, "none", StringComparison.OrdinalIgnoreCase))
                conversionPath = d.ConversionPath;
            maxVideoReadMs = Math.Max(maxVideoReadMs, d.LastReadMs);
        }

        long vPushOk = 0, vPushFail = 0, aPushOk = 0, aPushFail = 0;
        double maxOutputPushMs = 0;
        foreach (var output in _outputs)
        {
            var d = output.Diagnostics;
            vPushOk   += d.VideoPushSuccesses;
            vPushFail += d.VideoPushFailures;
            aPushOk   += d.AudioPushSuccesses;
            aPushFail += d.AudioPushFailures;
            maxOutputPushMs = Math.Max(maxOutputPushMs, d.LastPushMs);
        }

        return new NDIEngineDiagnostics(
            Audio: new NDIAudioDiagnostics(audioCaptured, audioDropped, audioRejected, maxAudioReadMs),
            VideoSource: new NDIVideoSourceDebugInfo(
                videoCaptured, videoDropped, videoRejected, repeatedFrames, fallbackFrames,
                maxVideoReadMs, jitterBufferFrames, queueDepth,
                incomingPixelFormat, outputPixelFormat, conversionPath),
            VideoOutput: new NDIVideoOutputDebugInfo(vPushOk, vPushFail, aPushOk, aPushFail, maxOutputPushMs),
            // Issue 5.11: renamed from ClockDriftMs; this is a static config-derived budget, not live drift.
            DiagnosticsIntervalBudgetMs: _diagnosticsOptions.DiagnosticsTickInterval.TotalMilliseconds /
                                         _limitsOptions.MaxPendingVideoFrames,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    private INDICaptureCoordinator GetOrCreateCaptureCoordinatorLocked(NDIReceiver receiver)
    {
        if (_captureCoordinators.TryGetValue(receiver, out var existing))
            return existing;

        // Prefer NDIFrameSyncCoordinator (SDK-managed TBC + dynamic audio resampling).
        // Fall back to NDICaptureCoordinator when the framesync cannot be initialised.
        INDICaptureCoordinator created;
        if (NDIFrameSyncCoordinator.Create(out var fsCoordinator, receiver) == 0 && fsCoordinator is not null)
        {
            created = fsCoordinator;
        }
        else
        {
            created = new NDICaptureCoordinator(
                receiver,
                _limitsOptions.MaxPendingVideoFrames,
                _limitsOptions.MaxPendingAudioFrames);
        }

        _captureCoordinators[receiver] = created;
        return created;
    }
}

