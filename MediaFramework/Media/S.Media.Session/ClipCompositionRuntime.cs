using S.Media.Players;
using S.Media.Routing;
using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Time;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Core.Video.Effects;

namespace S.Media.Session;

public sealed record ClipCompositionDefinition(
    string Id,
    string Name,
    int Width,
    int Height,
    int FrameRateNum,
    int FrameRateDen);

/// <param name="PresentWhenIdle">
/// Start the composition's pump as soon as this output attaches, rather than waiting for a layer.
/// <para>Opt-in because it decides what an output DOES between cues, and the two answers are both
/// right for somebody. A media player attaches an output to show a clip, and a pump running over an
/// empty canvas would light a screen the user never asked for. A cue player's projector is switched
/// on for the evening: it has to show the idle image — or black — from the moment it is patched, and
/// with no pump it shows nothing at all, which on most sinks means the window is never even created
/// (an <c>IVideoOutput</c> is configured by its first submit).</para>
/// </param>
public sealed record ClipCompositionOutputLease(
    string OutputId,
    string DisplayName,
    IVideoOutput Output,
    Action? Release = null,
    bool DisposeOutputOnRuntimeDispose = false,
    ClipOutputMappingSpec? Mapping = null,
    bool PresentWhenIdle = false);

/// <summary>A host-provided audio output for a clip route's device - the audio analogue of
/// <see cref="ClipCompositionOutputLease"/>. Lets the host route a clip's audio to a sink the session's
/// <c>IAudioBackend</c> can't create, e.g. an NDI sender's audio side that must share the SAME carrier as the
/// composition's video. A BORROWED output declares <see cref="DisposeOutputOnRuntimeDispose"/> = false so the
/// session never disposes it (the host owns the carrier's lifetime); <see cref="Release"/> runs on teardown.</summary>
public sealed record ClipAudioOutputLease(
    IAudioOutput Output,
    bool DisposeOutputOnRuntimeDispose = false,
    Action? Release = null);

public sealed record ClipCompositionCompositor(
    IVideoCompositor Compositor,
    bool RequiresBgraLayerConversion,
    string BackendName,
    Action? DisposeOnDriverThread = null);

/// <summary>
/// Shared cue composition runtime: owns the compositor source, layer slots, output fan-out pump,
/// and optional clock-mastered presentation cadence for one composition canvas.
/// </summary>
public sealed class ClipCompositionRuntime : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Playback.ClipCompositionRuntime");

    private readonly ClipCompositionDefinition _definition;
    private readonly VideoFormat _canvasFormat;
    private readonly IVideoCompositor _compositor;
    private readonly Action? _disposeCompositorOnDriverThread;
    private readonly VideoCompositorSource _mixer;
    private readonly object _gate = new();
    private readonly List<AcquiredOutput> _acquired = [];
    // Lock-free, allocation-free read of the acquired outputs for the per-frame pump (NXT-11): republished under
    // _gate whenever _acquired changes, so PumpOneFrame reads a stable immutable view without a per-frame ToList.
    private volatile IReadOnlyList<AcquiredOutput> _acquiredSnapshot = [];

    /// <summary>Composition-level idle frame, shown on every output when nothing is playing. Takes
    /// precedence over an output's own idle (owner decision, rev-3 item 23).</summary>
    private volatile VideoFrame? _idleFrame;
    private readonly List<LayerSlot> _slots = [];
    private readonly List<SurfaceLayerSlot> _surfaceLayers = [];
    private readonly TimeSpan _canvasPeriod;
    private long _nextLayerSequence;
    private long _framesComposited;
    private readonly TimingAccumulator _pumpTiming = new();
    private readonly TimingAccumulator _compositeTiming = new();
    private long _framesSubmitted;
    private long _pumpOverruns;
    private long _lastPumpFrameTicks;
    private long _maxPumpFrameTicks;
    private long _framesBehindMaster;
    private long _lastBehindMasterReport;
    private long _pumpStartCount;
    private long _lastDriftCheckTicks;
    // Nullable so "not yet primed" is distinct from a master parked at exactly 0 - with a
    // TimeSpan.Zero sentinel a 0-position master re-primed every tick and drift was never measured.
    private TimeSpan? _lastMasterPosition;
    private IPlaybackClock? _master;
    private IPlayhead? _timeline;
    private ITransportTimeline? _transportTimeline;
    // Active transport-bound clips claim the composition clock for the lifetime of their placed layers.
    // A composition is still one clock domain: claims for another timeline wait behind the current owner,
    // then take over when its final claim is released. This lets sequential transport groups reuse one
    // composition without leaving it permanently slaved to the first group's stopped timeline.
    private readonly List<TransportTimelineClaim> _transportTimelineClaims = [];
    private bool _masterOwnedByTransportClaim;
    private MediaClock? _slaveClock;
    private int _driverDisposeState;
    private bool _disposed;

    private readonly Func<VideoFormat, ClipCompositionCompositor> _compositorFactory;

    /// <summary>Mapping stages whose compositor must be torn down on the pump (driver) thread -
    /// retired by live mapping updates or runtime dispose; drained at the next tick.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<OutputMappingStage> _retiredMappingStages = new();
    /// <summary>Stored idle/calibration frames retired by a control thread. They are disposed only at
    /// the next pump boundary, after the previous iteration can no longer be reading their pixels.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<VideoFrame> _retiredStaticFrames = new();

    /// <summary>True when the single mapped output's warp runs inside the canvas compositor
    /// (<see cref="IWarpPassVideoCompositor"/>) - the mixer frame is already warped and the
    /// chained per-lease stage is skipped. Saves a full readback + re-upload per frame.</summary>
    private volatile bool _integratedWarpActive;

    /// <summary>Optional composition-level video FX: a canvas-sized mapping applied after all layers
    /// are composited and before per-output mappings/fan-out.</summary>
    private volatile OutputMappingStage? _compositionMappingStage;

    /// <summary>Clip-owned subtitle layers. Each feed has its own clip-position provider, allowing several
    /// subtitle tracks and clips on one composition without sharing a global subtitle timeline.</summary>
    private readonly List<SubtitleLayerFeed> _subtitleFeeds = [];
    // Same lock-free snapshot pattern as _acquiredSnapshot - the per-frame DriveSubtitleLayers reads this
    // instead of snapshotting _subtitleFeeds.ToArray() every tick (NXT-11).
    private volatile IReadOnlyList<SubtitleLayerFeed> _subtitleFeedsSnapshot = [];

    public ClipCompositionRuntime(
        ClipCompositionDefinition definition,
        IReadOnlyList<ClipCompositionOutputLease> outputs,
        Func<VideoFormat, ClipCompositionCompositor>? compositorFactory = null,
        ClipOutputMappingSpec? compositionMapping = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ArgumentNullException.ThrowIfNull(outputs);

        var den = Math.Max(1, definition.FrameRateDen);
        var num = Math.Max(1, definition.FrameRateNum);
        var rate = new Rational(num, den);
        _canvasFormat = new VideoFormat(
            Math.Max(16, definition.Width),
            Math.Max(16, definition.Height),
            PixelFormat.Bgra32,
            rate);
        _canvasPeriod = TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond * (long)den / Math.Max(1L, (long)num)));

        _compositorFactory = compositorFactory ?? CreateDefaultCompositor;
        var compositor = _compositorFactory(_canvasFormat);
        _compositor = compositor.Compositor ?? throw new InvalidOperationException("Compositor factory returned null compositor.");
        RequiresBgraLayerConversion = compositor.RequiresBgraLayerConversion;
        CompositorBackendName = string.IsNullOrWhiteSpace(compositor.BackendName) ? "Unknown" : compositor.BackendName;
        _disposeCompositorOnDriverThread = compositor.DisposeOnDriverThread;
        _mixer = new VideoCompositorSource(_canvasFormat, _compositor, disposeCompositorOnDispose: false);

        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} initialized ({Width}x{Height} {Rate}, compositor={Backend})",
            CompositionName, _canvasFormat.Width, _canvasFormat.Height, _canvasFormat.FrameRate, CompositorBackendName);

        foreach (var output in outputs)
        {
            if (output.Output is null)
                continue;
            var acquired = new AcquiredOutput(output);
            if (output.Mapping is not null)
                acquired.SetMapping(output.Mapping, _canvasFormat, out _);
            _acquired.Add(acquired);
            acquired.SubscribePumpPressure(this);
        }
        RepublishAcquiredSnapshot();

        SetCompositionMappingCore(compositionMapping, out _);
        ReevaluateIntegratedWarp();
    }

    // Rebuilds the lock-free pump snapshots after a mutation (NXT-11). Callers either hold _gate or run during
    // single-threaded construction; the volatile publish makes the new view visible to the pump thread.
    private void RepublishAcquiredSnapshot() => _acquiredSnapshot = _acquired.ToArray();
    private void RepublishSubtitleFeedsSnapshot() => _subtitleFeedsSnapshot = _subtitleFeeds.ToArray();

    /// <summary>
    /// Routes the warp through the canvas compositor itself when possible: with exactly one mapped
    /// output and a warp-capable compositor, the canvas pass renders straight into the warped output
    /// (one GPU pass, one readback). Multi-output mappings use <see cref="TryPumpIntegratedMultiWarp"/>.
    /// </summary>
    private void ReevaluateIntegratedWarp()
    {
        if (_compositor is not IWarpPassVideoCompositor warp)
            return;

        if (_compositionMappingStage is not null)
        {
            if (_integratedWarpActive)
                warp.SetWarpPass(_canvasFormat, null);
            _integratedWarpActive = false;
            return;
        }

        OutputMappingStage? single;
        lock (_gate)
        {
            // The integrated path renders the canvas straight into the warped output inside the canvas
            // pass, which leaves no seam at which one output's canvas can be substituted. While any
            // calibration frame is set the chained per-output path runs instead - marginally more work,
            // during calibration only, and the only way a pattern can be warped per output.
            single = _acquired.Count == 1 && !_acquired.Any(a => a.TestPattern is not null)
                ? _acquired[0].MappingStage
                : null;
        }

        if (single is not null)
        {
            warp.SetWarpPass(single.OutputFormat, single.BuildWarpSections());
            _integratedWarpActive = true;
        }
        else if (_integratedWarpActive)
        {
            warp.SetWarpPass(_canvasFormat, null);
            _integratedWarpActive = false;
        }
    }

    /// <summary>
    /// Shows a calibration frame on ONE output, or clears it with null. The frame passes through that
    /// output's mapping stage, so it is cut and mesh-warped exactly as programme content is - which is
    /// what makes it usable for aligning a projector rather than merely proving a cable works.
    /// </summary>
    /// <remarks>
    /// Per output, so calibrating a projector no longer lights up every other line bound to the same
    /// composition - the composition-wide pattern could only be all-or-nothing. Ownership transfers: the
    /// runtime disposes the frame when it is replaced, cleared, or the output retires.
    /// </remarks>
    /// <returns>False when no output is attached under <paramref name="outputId"/>.</returns>
    public bool SetOutputTestPattern(string outputId, VideoFrame? pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);

        VideoFrame? retired = null;
        var found = false;
        lock (_gate)
        {
            foreach (var output in _acquired)
            {
                if (!string.Equals(output.OutputId, outputId, StringComparison.Ordinal))
                    continue;
                found = output.SetTestPattern(pattern, out retired);
                break;
            }

            if (found)
                ReevaluateIntegratedWarp();
        }

        if (retired is not null)
            RetireStaticFrame(retired);
        if (!found)
            pattern?.Dispose(); // never leak a frame the caller handed over
        return found;
    }

    /// <summary>
    /// Sets (or clears, with null) the composition's idle frame - what every output shows while nothing
    /// is playing on this canvas. Takes precedence over any per-output idle.
    /// </summary>
    /// <remarks>Ownership transfers: the runtime disposes it when replaced, cleared, or disposed. This
    /// is the level that was missing entirely - a per-output idle existed only on local video lines, and
    /// only while the line was NOT held by playback, so once a cue list held it the image never showed
    /// again and the canvas simply went black.</remarks>
    public void SetIdleFrame(VideoFrame? idle)
    {
        VideoFrame? retired;
        lock (_gate)
        {
            retired = _idleFrame;
            _idleFrame = _disposed ? null : idle;
        }

        if (retired is not null)
            RetireStaticFrame(retired);
        if (_disposed)
            idle?.Dispose();
    }

    /// <summary>
    /// Sets (or clears) ONE output's fallback idle frame, used only when the composition has no idle of
    /// its own. Returns false when no output is attached under <paramref name="outputId"/>.
    /// </summary>
    public bool SetOutputIdleFrame(string outputId, VideoFrame? idle)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);

        VideoFrame? retired = null;
        var found = false;
        lock (_gate)
        {
            foreach (var output in _acquired)
            {
                if (!string.Equals(output.OutputId, outputId, StringComparison.Ordinal))
                    continue;
                found = output.SetIdleFrame(idle, out retired);
                break;
            }
        }

        if (retired is not null)
            RetireStaticFrame(retired);
        if (!found)
            idle?.Dispose();
        return found;
    }

    /// <summary>The outputs currently showing a calibration frame.</summary>
    public IReadOnlyList<string> OutputsShowingTestPattern
    {
        get
        {
            lock (_gate)
                return [.. _acquired.Where(a => a.TestPattern is not null).Select(a => a.OutputId)];
        }
    }

    public string CompositionId => _definition.Id;

    public string CompositionName => _definition.Name;

    public VideoFormat CanvasFormat => _canvasFormat;

    public bool RequiresBgraLayerConversion { get; }

    public string CompositorBackendName { get; }

    public int LayerCount
    {
        get { lock (_gate) return _slots.Count; }
    }

    /// <summary>
    /// True when nothing at all is rendering on this canvas - no frame layers and no surface layers.
    /// </summary>
    /// <remarks>Surface layers (the visualizer) are held in their own collection and never appear in
    /// <see cref="LayerCount"/>, so anything asking "is this canvas idle" has to consult both or it will
    /// declare a composition that is busily rendering a visualizer to be empty.</remarks>
    private bool IsCanvasEmpty
    {
        get { lock (_gate) return _slots.Count == 0 && _surfaceLayers.Count == 0; }
    }

    public int OutputCount
    {
        get { lock (_gate) return _acquired.Count; }
    }

    public long PumpStartCount => Volatile.Read(ref _pumpStartCount);

    public event EventHandler<ClipCompositionDriftWarning>? DriftWarning;

    public event EventHandler<ClipCompositionPumpPressureWarning>? PumpPressureWarning;

    public ClipCompositionRuntimeStats GetStats()
    {
        long slotOverflow = 0;
        int layerCount;
        lock (_gate)
        {
            layerCount = _slots.Count;
            foreach (var slot in _slots)
                slotOverflow += slot.RawSlot.OverflowFrames;
        }

        return new ClipCompositionRuntimeStats(
            CompositionId,
            Volatile.Read(ref _framesComposited),
            Volatile.Read(ref _framesSubmitted),
            Volatile.Read(ref _pumpOverruns),
            slotOverflow,
            TimeSpan.FromTicks(Volatile.Read(ref _lastPumpFrameTicks)),
            TimeSpan.FromTicks(Volatile.Read(ref _maxPumpFrameTicks)),
            Volatile.Read(ref _framesBehindMaster),
            _master is not null,
            layerCount,
            _pumpTiming.Snapshot(),
            _compositeTiming.Snapshot(),
            _canvasPeriod,
            SnapshotOutputStats(),
            CompositorBackendName);
    }

    /// <summary>One throughput row per attached output. Reads the same lock-free output snapshot the pump
    /// uses, so it never blocks a frame.</summary>
    private IReadOnlyList<ClipCompositionOutputStats> SnapshotOutputStats()
    {
        var outputs = _acquiredSnapshot;
        if (outputs.Count == 0)
            return [];
        var rows = new ClipCompositionOutputStats[outputs.Count];
        for (var i = 0; i < outputs.Count; i++)
            rows[i] = outputs[i].SnapshotStats();
        return rows;
    }

    public void EnsurePumpStarted()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_slaveClock is not null) return;
            StartPumpLocked();
        }
    }

    /// <summary>
    /// Live-swaps the output mapping of <paramref name="outputId"/> (null clears it - the output
    /// goes back to receiving the raw canvas). Safe while the pump runs; the editor calls this on
    /// every change. Returns false when the output isn't part of this runtime.
    /// </summary>
    public bool UpdateOutputMapping(string outputId, ClipOutputMappingSpec? mapping)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        AcquiredOutput? target;
        lock (_gate)
        {
            if (_disposed) return false;
            target = _acquired.FirstOrDefault(a => string.Equals(a.OutputId, outputId, StringComparison.Ordinal));
        }

        if (target is null)
            return false;

        if (!target.SetMapping(mapping, _canvasFormat, out var retired))
            return false;
        if (retired is not null)
            _retiredMappingStages.Enqueue(retired);
        ReevaluateIntegratedWarp();
        return true;
    }

    /// <summary>
    /// Removes one fan-out output from a running composition. Any in-flight submit for the output is allowed
    /// to finish before the lease is released; future pump snapshots that still hold the retired output drop
    /// their frame instead of touching a runtime the host may be about to dispose.
    /// </summary>
    /// <summary>
    /// Attach an additional live output (e.g. a UI preview surface) to this running composition; the pump picks it up
    /// on its next tick. Symmetric to <see cref="RemoveOutput"/>. The caller owns the output's lifetime unless the
    /// lease sets <see cref="ClipCompositionOutputLease.DisposeOutputOnRuntimeDispose"/>.
    /// </summary>
    public bool AddOutput(ClipCompositionOutputLease output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Output is null)
            return false;

        var acquired = new AcquiredOutput(output);
        if (output.Mapping is not null)
            acquired.SetMapping(output.Mapping, _canvasFormat, out _);

        lock (_gate)
        {
            // Disposed mid-attach: drop it. (A mapping stage, if any, is GC-reclaimed - preview attaches carry none.)
            if (_disposed)
                return false;
            _acquired.Add(acquired);
            RepublishAcquiredSnapshot();
        }

        acquired.SubscribePumpPressure(this);
        ReevaluateIntegratedWarp();

        // A cue player's output is on for the evening, so it has to be receiving frames before the
        // first cue rather than from it: the pump otherwise starts with the first layer, and until
        // then nothing is submitted at all - which on a windowed sink means the window is never
        // created, because an IVideoOutput is configured by its first submit.
        if (output.PresentWhenIdle)
            EnsurePumpStarted();

        return true;
    }

    public bool RemoveOutput(string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);

        AcquiredOutput? removed = null;
        lock (_gate)
        {
            if (_disposed) return false;

            for (var i = 0; i < _acquired.Count; i++)
            {
                if (!string.Equals(_acquired[i].OutputId, outputId, StringComparison.Ordinal))
                    continue;

                removed = _acquired[i];
                _acquired.RemoveAt(i);
                RepublishAcquiredSnapshot();
                break;
            }
        }

        if (removed is null)
            return false;

        if (removed.Retire("ClipCompositionRuntime.RemoveOutput", RetireStaticFrame) is { } retired)
            _retiredMappingStages.Enqueue(retired);
        ReevaluateIntegratedWarp();
        return true;
    }

    /// <summary>
    /// Live-swaps the composition-level video FX mapping. Null clears it. The stage output is always
    /// the composition canvas size, regardless of any editor-side output-size fields.
    /// </summary>
    public bool UpdateCompositionMapping(ClipOutputMappingSpec? mapping)
    {
        OutputMappingStage? retired;
        lock (_gate)
        {
            if (_disposed) return false;
            SetCompositionMappingCore(mapping, out retired);
        }

        if (retired is not null)
            _retiredMappingStages.Enqueue(retired);
        ReevaluateIntegratedWarp();
        return true;
    }

    private void SetCompositionMappingCore(ClipOutputMappingSpec? mapping, out OutputMappingStage? retired)
    {
        retired = null;
        var current = _compositionMappingStage;
        if (mapping is null)
        {
            _compositionMappingStage = null;
            retired = current;
            return;
        }

        var canvasMapping = ForceCanvasSizedMapping(mapping);
        var sections = OutputMappingResolver.Resolve(canvasMapping, _canvasFormat.Width, _canvasFormat.Height);
        if (current is not null && current.OutputFormat == _canvasFormat)
        {
            _compositionMappingStage = current.WithSections(sections);
            return;
        }

        _compositionMappingStage = new OutputMappingStage(_canvasFormat, sections);
        retired = current;
    }

    private ClipOutputMappingSpec ForceCanvasSizedMapping(ClipOutputMappingSpec mapping) =>
        mapping with { OutputWidth = _canvasFormat.Width, OutputHeight = _canvasFormat.Height };

    private void DrainRetiredMappingStages()
    {
        while (_retiredMappingStages.TryDequeue(out var stage))
            stage.DisposeCompositor();
    }

    private void RetireStaticFrame(VideoFrame frame)
    {
        lock (_gate)
        {
            if (_slaveClock is not null)
            {
                _retiredStaticFrames.Enqueue(frame);
                return;
            }
        }
        MediaDiagnostics.SwallowDisposeErrors(frame.Dispose, "ClipCompositionRuntime: retired static frame");
    }

    private void DrainRetiredStaticFrames()
    {
        while (_retiredStaticFrames.TryDequeue(out var frame))
            MediaDiagnostics.SwallowDisposeErrors(frame.Dispose, "ClipCompositionRuntime: retired static frame");
    }

    public void SetClockMaster(IPlaybackClock master, IPlayhead? timeline = null)
    {
        ArgumentNullException.ThrowIfNull(master);
        MediaClock? clockToRetarget = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_master is not null)
            {
                // Preserve the first-master contract. In particular, a legacy caller must not detach an
                // already-installed TransportTimeline and leave source selection reading master coordinates.
                if (_transportTimeline is null && timeline is not null)
                    _timeline = timeline;
                return;
            }
            _master = master;
            _timeline = timeline;
            _transportTimeline = null;
            _masterOwnedByTransportClaim = false;
            foreach (var layer in _slots)
                layer.RawSlot.KeepPolicy = layer.RequestedKeepPolicy;
            clockToRetarget = _slaveClock;
        }

        clockToRetarget?.SetMaster(master);
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} pump now slaved to master clock",
            CompositionName);
    }

    /// <summary>
    /// Masters this composition to the transport group's authoritative timeline. The master coordinate drives
    /// pump cadence/output scheduling while <see cref="TransportTimelineSnapshot.SourceTime"/> selects decoded
    /// frames. The same contract is also passed to subtitle feeds, keeping seek/trim/live correlation on one
    /// generation instead of combining a raw player playhead with an unrelated session clock (NXT-04).
    /// </summary>
    public void SetTransportTimeline(ITransportTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        MediaClock? clockToRetarget = null;
        lock (_gate)
        {
            if (_disposed) return;
            // A composition is one clock domain. The first transport group to drive it owns that domain until
            // the composition is rebuilt; a later concurrent group must not retarget every existing layer.
            // Repeated calls from successive clips in the SAME group carry the same stable timeline object.
            if (_master is not null)
                return;
            _transportTimeline = timeline;
            _timeline = null;
            _master = timeline;
            _masterOwnedByTransportClaim = false;
            clockToRetarget = _slaveClock;
            foreach (var layer in _slots)
                layer.RawSlot.KeepPolicy = layer.RequestedKeepPolicy;
        }

        clockToRetarget?.SetMaster(timeline);
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} now follows the transport timeline",
            CompositionName);
    }

    /// <summary>
    /// Claims this composition's single transport clock domain for an active clip. The first timeline owns the
    /// domain while any of its claims remain; claims from other groups wait in acquisition order. Disposing the
    /// returned lease releases this clip's ownership and atomically hands the clock to the next active timeline,
    /// or returns the composition to free-running mode when no transport-bound layers remain.
    /// </summary>
    public IDisposable AcquireTransportTimeline(ITransportTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        MediaClock? clockToRetarget = null;
        var becameMaster = false;
        TransportTimelineClaim claim;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            claim = new TransportTimelineClaim(this, timeline);
            _transportTimelineClaims.Add(claim);

            if (_master is null)
            {
                _transportTimeline = timeline;
                _timeline = null;
                _master = timeline;
                _masterOwnedByTransportClaim = true;
                clockToRetarget = _slaveClock;
                becameMaster = true;
                foreach (var layer in _slots)
                    layer.RawSlot.KeepPolicy = layer.RequestedKeepPolicy;
            }
        }

        clockToRetarget?.SetMaster(timeline);
        if (becameMaster)
        {
            Trace.LogInformation(
                "ClipCompositionRuntime: composition {Composition} now follows an active transport timeline",
                CompositionName);
        }
        return claim;
    }

    private void ReleaseTransportTimeline(TransportTimelineClaim claim)
    {
        MediaClock? clockToRetarget = null;
        ITransportTimeline? nextTimeline = null;
        var released = false;
        var handedOff = false;
        lock (_gate)
        {
            if (!_transportTimelineClaims.Remove(claim) || _disposed)
                return;
            if (!_masterOwnedByTransportClaim
                || !ReferenceEquals(_transportTimeline, claim.Timeline)
                || _transportTimelineClaims.Any(c => ReferenceEquals(c.Timeline, claim.Timeline)))
            {
                return;
            }

            nextTimeline = _transportTimelineClaims.Count > 0
                ? _transportTimelineClaims[0].Timeline
                : null;
            _transportTimeline = nextTimeline;
            _timeline = null;
            _master = nextTimeline;
            _masterOwnedByTransportClaim = nextTimeline is not null;
            clockToRetarget = _slaveClock;
            released = nextTimeline is null;
            handedOff = nextTimeline is not null;
        }

        clockToRetarget?.SetMaster(nextTimeline);
        if (handedOff)
        {
            Trace.LogInformation(
                "ClipCompositionRuntime: composition {Composition} handed its clock to the next active transport timeline",
                CompositionName);
        }
        else if (released)
        {
            Trace.LogInformation(
                "ClipCompositionRuntime: composition {Composition} clock master released (no active transport layers)",
                CompositionName);
        }
    }

    /// <summary>
    /// Releases the current clock master and any outstanding transport claims so the NEXT clip can re-master
    /// this composition. The pump keeps running, free-running on its own clock in the meantime, so persistent
    /// surface layers such as visualizers keep rendering. This is the escape hatch for the
    /// "one clock domain until rebuilt"
    /// contract: a PRESERVED composition (kept alive across a document reload) calls this once the outgoing
    /// clip's group is torn down, so the incoming clip's <see cref="AcquireTransportTimeline"/> takes effect
    /// instead of being ignored. Active clips normally release their individual claim leases instead.
    /// </summary>
    public void ResetClockMaster()
    {
        MediaClock? clockToClear;
        lock (_gate)
        {
            if (_disposed) return;
            _master = null;
            _transportTimeline = null;
            _timeline = null;
            _masterOwnedByTransportClaim = false;
            _transportTimelineClaims.Clear();
            clockToClear = _slaveClock;
        }

        clockToClear?.SetMaster(null); // free-run until the next clip masters it
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} clock master released (preserved across reload)",
            CompositionName);
    }

    private sealed class TransportTimelineClaim(
        ClipCompositionRuntime owner,
        ITransportTimeline timeline) : IDisposable
    {
        private ClipCompositionRuntime? _owner = owner;
        public ITransportTimeline Timeline { get; } = timeline;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseTransportTimeline(this);
    }

    /// <param name="alignmentTimeline">The transport timeline THIS layer's frames are stamped
    /// against, when the caller has one. A canvas hosts layers from independent transports whose
    /// PTS live in different domains (two clips with different trims), and judging them all against
    /// the single canvas master aligned only the clock-owning layer — the other churned its pending
    /// frames on the too-old/too-future fallbacks (a steady slot-overflow trickle and a visible
    /// single-frame judder that grew as the domains drifted). With its own timeline the layer is
    /// PTS-aligned regardless of which transport owns the canvas clock.</param>
    public LayerSlot AddLayer(
        VideoFormat sourceFormat, VideoPlacementSpec placement,
        SlotKeepPolicy keepPolicy = SlotKeepPolicy.MasterAligned,
        ITransportTimeline? alignmentTimeline = null)
    {
        LayerSlot layer;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rawSlot = _mixer.AddSlot();
            // Master-clock compositions align decoded frames to the clock by PTS; without a master they
            // are latest-wins. Callers whose frames carry no meaningful PTS (subtitle overlays are
            // pump-driven and re-rendered in place at PresentationTime 0) must opt into Latest explicitly -
            // MasterAligned would freeze on the first frame, since every frame is equidistant from the clock.
            if (alignmentTimeline is not null && keepPolicy == SlotKeepPolicy.MasterAligned)
            {
                rawSlot.AlignmentClock = () =>
                {
                    try
                    {
                        return alignmentTimeline.GetSnapshot().SourceTime;
                    }
                    catch
                    {
                        return null; // torn down mid-composite - the canvas master covers this tick
                    }
                };
                // Own-clock alignment needs no canvas master; it engages immediately.
                rawSlot.KeepPolicy = keepPolicy;
            }
            else if (_master is not null)
                rawSlot.KeepPolicy = keepPolicy;
            layer = new LayerSlot(this, rawSlot, sourceFormat, placement, Interlocked.Increment(ref _nextLayerSequence))
            {
                RequestedKeepPolicy = keepPolicy,
            };
            try
            {
                layer.ApplyPlacement();
            }
            catch
            {
                _mixer.RemoveSlot(rawSlot.Id);
                throw;
            }
            _slots.Add(layer);
            SortLayersLocked();
        }
        EnsurePumpStarted();
        return layer;
    }

    private void RemoveLayer(LayerSlot layer)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _slots.Remove(layer);
            _mixer.RemoveSlot(layer.RawSlot.Id);
            // Unconditional: losing the LAST frame layer still changes every surface's z (they all drop to
            // "nothing underneath"), so an early-out here would strand stale counts.
            RecomputeLayerOrderLocked();
        }
    }

    /// <summary>Whether this composition's compositor can host GPU layer surfaces (NXT-10). When false,
    /// surface-capable sources must be consumed through their normal CPU frame path.</summary>
    public bool SupportsSurfaceLayers => _mixer.SupportsSurfaceLayers;

    /// <summary>
    /// Adds a GPU layer surface (NXT-10): <paramref name="surface"/> renders directly into the canvas on
    /// the compositor's GL thread, ON TOP of every frame layer (surfaces don't z-interleave with frame
    /// layers - v1 contract; they order among themselves by <see cref="VideoPlacementSpec.LayerIndex"/>).
    /// The placement's destination rect/fit/opacity resolve exactly like a frame layer's (the surface's
    /// nominal source size is the canvas). Integrated multi-output warp is bypassed while any surface is
    /// present (the chained per-lease mapping path still applies). Disposing the returned slot removes
    /// the layer AND disposes the surface (the runtime owns it - mirrors <see cref="LayerSlot"/> handing
    /// its slot back). Throws when <see cref="SupportsSurfaceLayers"/> is false.
    /// </summary>
    /// <param name="ownsSurface">When true (default) the returned slot disposes <paramref name="surface"/>
    /// on removal. Pass false to add the SAME surface into an ADDITIONAL placement (one visualizer render
    /// shown in several sections of the canvas): the compositor keys ConfigureGl by surface instance and
    /// renders it once per layer, so a single surface must be owned by exactly one slot to avoid a
    /// double dispose. See <c>ShowSessionVisualizerService</c> (#26 multi-placement).</param>
    public SurfaceLayerSlot AddSurfaceLayer(
        IVideoCompositorLayerSurface surface, VideoPlacementSpec placement, bool ownsSurface = true)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(placement);
        SurfaceLayerSlot layer;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rawSlot = _mixer.AddSurfaceSlot(surface);
            layer = new SurfaceLayerSlot(this, rawSlot, placement, ownsSurface);
            try
            {
                layer.ApplyPlacement();
            }
            catch
            {
                _mixer.RemoveSurfaceSlot(rawSlot);
                throw;
            }
            _surfaceLayers.Add(layer);
            SortSurfaceLayersLocked();
        }
        EnsurePumpStarted();
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} gained a GPU surface layer (z={LayerIndex})",
            CompositionName, placement.LayerIndex);
        return layer;
    }

    private void RemoveSurfaceLayer(SurfaceLayerSlot layer)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _surfaceLayers.Remove(layer);
            _mixer.RemoveSurfaceSlot(layer.RawSlot);
            RecomputeLayerOrderLocked();
        }
    }

    private void SortSurfaceLayersLocked() => RecomputeLayerOrderLocked();

    /// <summary>
    /// Attaches a subtitle/overlay source as a full-canvas, top-z-order layer. Each frame the runtime renders the
    /// source at the owning clip's position, copies its (borrowed) overlay into a pooled, slot-owned frame, and pushes it
    /// like any other layer - so the mixer composites it uniformly (z-order, opacity, blend). The source should
    /// render at the canvas size. The returned lease removes the layer and disposes the source; dispose it when
    /// the owning clip stops.
    /// </summary>
    public IDisposable AttachSubtitleOverlay(
        IVideoOverlaySource source,
        Func<TimeSpan> positionProvider,
        int layerIndex = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(positionProvider);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var placement = new VideoPlacementSpec(CompositionName, layerIndex, Placement: "stretch");
        // Latest-wins: the subtitle feed is pump-driven - it renders the source at the current position each
        // tick and submits that frame. Its frames carry no per-frame PTS (re-rendered in place at PTS 0), so a
        // MasterAligned slot would freeze on the first frame (every frame equidistant from the clock). Latest
        // takes the newest submitted frame, which is exactly the one rendered for this position.
        var layer = AddLayer(_canvasFormat, placement, SlotKeepPolicy.Latest);
        var feed = new SubtitleLayerFeed(this, source, layer, positionProvider);
        lock (_gate)
        {
            _subtitleFeeds.Add(feed);
            RepublishSubtitleFeedsSnapshot();
        }
        return feed;
    }

    /// <summary>
    /// Attaches a subtitle source to the same authoritative transport timeline as video selection. Subtitle
    /// events use source time (so a trimmed file still selects events at its original media timestamps), while
    /// cue-local effects remain available from the contract's <see cref="TransportTimelineSnapshot.CueTime"/>.
    /// </summary>
    public IDisposable AttachSubtitleOverlay(
        IVideoOverlaySource source,
        ITransportTimeline timeline,
        int layerIndex = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return AttachSubtitleOverlay(source, () => timeline.GetSnapshot().SourceTime, layerIndex);
    }

    private void RemoveSubtitleFeed(SubtitleLayerFeed feed)
    {
        lock (_gate)
        {
            _subtitleFeeds.Remove(feed);
            RepublishSubtitleFeedsSnapshot();
        }
        RemoveLayer(feed.Layer);
    }

    /// <summary>Renders every subtitle source at its owning clip position. Pump-thread only.</summary>
    private void DriveSubtitleLayers()
    {
        var feeds = _subtitleFeedsSnapshot; // lock-free, allocation-free per-frame read (NXT-11)

        foreach (var feed in feeds)
            DriveSubtitleLayer(feed);
    }

    private void DriveSubtitleLayer(SubtitleLayerFeed feed)
    {
        VideoFrame? overlay;
        try
        {
            overlay = feed.RenderAtCurrentPosition();
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "ClipCompositionRuntime: subtitle render failed for {Composition}", CompositionName);
            feed.Layer.FadeLevel = 0f;
            return;
        }

        if (overlay is null)
        {
            feed.Layer.FadeLevel = 0f;
            return;
        }

        try
        {
            // The overlay frame is borrowed (the source reuses it) and slots take ownership of pushed frames, so
            // copy into a pooled, slot-owned frame. Pooled => no GC; the slot returns it to the pool on its swap.
            var owned = CopyToPooledBgra(overlay);
            try
            {
                feed.Layer.Output.Submit(owned);
            }
            catch
            {
                owned.Dispose();
                throw;
            }
            feed.Layer.FadeLevel = 1f;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "ClipCompositionRuntime: subtitle push failed for {Composition}", CompositionName);
            feed.Layer.FadeLevel = 0f;
        }
    }

    private static VideoFrame CopyToPooledBgra(VideoFrame source)
    {
        if (source.Format.PixelFormat != PixelFormat.Bgra32 || source.Planes.Length != 1 || source.Strides.Length != 1)
            throw new NotSupportedException(
                $"Subtitle overlays must be single-plane BGRA32, not {source.Format.PixelFormat} with {source.Planes.Length} planes.");
        var plane = source.Planes[0];
        var length = plane.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        plane.Span.CopyTo(buffer);
        var owned = buffer;
        return new VideoFrame(
            source.PresentationTime,
            source.Format,
            new ReadOnlyMemory<byte>(buffer, 0, length),
            source.Strides[0],
            metadata: new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied),
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)));
    }

    private sealed class SubtitleLayerFeed : IDisposable
    {
        private readonly ClipCompositionRuntime _owner;
        private readonly IVideoOverlaySource _source;
        private readonly Func<TimeSpan> _positionProvider;
        private readonly object _gate = new();
        private bool _disposed;

        public SubtitleLayerFeed(
            ClipCompositionRuntime owner,
            IVideoOverlaySource source,
            LayerSlot layer,
            Func<TimeSpan> positionProvider)
        {
            _owner = owner;
            _source = source;
            Layer = layer;
            _positionProvider = positionProvider;
        }

        public LayerSlot Layer { get; }

        public VideoFrame? RenderAtCurrentPosition()
        {
            lock (_gate)
                return _disposed ? null : _source.RenderAt(_positionProvider());
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            _owner.RemoveSubtitleFeed(this);
            _source.Dispose();
        }
    }

    private static ClipCompositionCompositor CreateDefaultCompositor(VideoFormat canvasFormat) =>
        new(new CpuVideoCompositor(canvasFormat), RequiresBgraLayerConversion: true, BackendName: "CPU");

    private void StartPumpLocked()
    {
        if (_slaveClock is not null) return;

        var audioInterval = TimeSpan.FromMilliseconds(50);
        _slaveClock = new MediaClock(audioInterval, _canvasPeriod);
        if (_master is not null)
            _slaveClock.SetMaster(_master);
        _slaveClock.VideoTick += OnSlaveVideoTick;
        _slaveClock.Start();
        Interlocked.Increment(ref _pumpStartCount);
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} pump started (videoTick={PeriodMs:0.00}ms, mastered={Mastered})",
            CompositionName,
            _canvasPeriod.TotalMilliseconds,
            _master is not null);
    }

    private void OnSlaveVideoTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (Interlocked.CompareExchange(ref _driverDisposeState, 2, 1) == 1)
        {
            DrainRetiredMappingStages();
            try { _disposeCompositorOnDriverThread?.Invoke(); }
            catch (Exception ex) { Trace.LogWarning(ex, "ClipCompositionRuntime.OnSlaveVideoTick: driver compositor dispose"); }
            return;
        }
        if (_disposed) return;
        DrainRetiredMappingStages();
        DrainRetiredStaticFrames();
        PumpOneFrame();
        CheckMasterDrift();
    }

    private void CheckMasterDrift()
    {
        var master = _master;
        if (master is null) return;

        TimeSpan masterPos;
        try { masterPos = master.ElapsedSinceStart; }
        catch { return; }

        if (_lastMasterPosition is not { } lastMasterPosition)
        {
            _lastMasterPosition = masterPos;
            _lastDriftCheckTicks = Stopwatch.GetTimestamp();
            return;
        }

        var wallElapsed = Stopwatch.GetElapsedTime(_lastDriftCheckTicks);
        var masterElapsed = masterPos - lastMasterPosition;
        if (masterElapsed < TimeSpan.FromMilliseconds(50)) return;

        var diff = wallElapsed - masterElapsed;
        if (Math.Abs(diff.Ticks) > _canvasPeriod.Ticks * 2)
            Interlocked.Increment(ref _framesBehindMaster);

        _lastMasterPosition = masterPos;
        _lastDriftCheckTicks = Stopwatch.GetTimestamp();

        var behind = Volatile.Read(ref _framesBehindMaster);
        var since = behind - Volatile.Read(ref _lastBehindMasterReport);
        if (since < 30)
            return;

        Volatile.Write(ref _lastBehindMasterReport, behind);
        try
        {
            DriftWarning?.Invoke(this, new ClipCompositionDriftWarning(
                CompositionId,
                CompositionName,
                behind,
                wallElapsed - masterElapsed));
        }
        catch (Exception ex)
        {
            Trace.LogTrace(ex, "ClipCompositionRuntime.CheckMasterDrift: DriftWarning handler threw");
        }
    }

    private void PumpOneFrame()
    {
        var sw = Stopwatch.StartNew();
        TimeSpan? masterPts = null;
        if (_transportTimeline is { } transportTimeline)
        {
            try { masterPts = transportTimeline.GetSnapshot().SourceTime; }
            catch (Exception ex) { Trace.LogTrace(ex, "ClipCompositionRuntime.PumpOneFrame: transport timeline read"); }
        }
        else if (_timeline is not null)
        {
            try { masterPts = _timeline.CurrentPosition; }
            catch (Exception ex) { Trace.LogTrace(ex, "ClipCompositionRuntime.PumpOneFrame: timeline read"); }
        }
        else if (_master is not null)
        {
            try { masterPts = _master.ElapsedSinceStart; }
            catch (Exception ex) { Trace.LogTrace(ex, "ClipCompositionRuntime.PumpOneFrame: master read"); }
        }

        // Subtitle layers render at their owning clips' positions before either pump path
        // reads the mixer, so it composites uniformly with the video layers (z-order/opacity/blend).
        DriveSubtitleLayers();

        var snapshot = _acquiredSnapshot; // lock-free, allocation-free per-frame read (NXT-11)

        if (snapshot.Count == 0)
            return;

        // Nothing is playing on this canvas: show the idle frame rather than leaving each sink holding
        // whatever it last received. Two things this condition must get right. It is "no content", NOT
        // "the mixer produced no frame this tick" - a playing clip legitimately has ticks with nothing
        // new, and idling on those would strobe between content and the idle image. And content means
        // frame layers AND SURFACE layers: a visualizer lives only in _surfaceLayers, so counting _slots
        // alone reports a composition rendering a visualizer as idle and blacks it out.
        if (IsCanvasEmpty)
        {
            PumpIdleFrames(snapshot);
            sw.Stop();
            RecordPumpTiming(sw.Elapsed, _canvasPeriod);
            return;
        }

        if (TryPumpIntegratedMultiWarp(masterPts, snapshot, sw))
            return;

        var compositeStarted = Stopwatch.GetTimestamp();
        if (!_mixer.TryReadNextFrame(masterPts, out var frame))
            return;
        _compositeTiming.RecordSince(compositeStarted);
        Interlocked.Increment(ref _framesComposited);

        var compositionStage = _compositionMappingStage;
        if (compositionStage is not null)
        {
            try
            {
                var fxFrame = compositionStage.Composite(frame, _compositorFactory);
                frame.Dispose();
                frame = fxFrame;
            }
            catch (Exception ex)
            {
                Trace.LogWarning(
                    ex,
                    "ClipCompositionRuntime.Pump: composition mapping stage failed for {Composition}",
                    CompositionName);
            }
        }

        // Output-mapping stages run first, while the canvas frame is alive: each mapped output
        // composites its warp sections from the canvas (compositors never take frame ownership)
        // and gets its own output-sized frame. Unmapped outputs share the canvas via the fan-out
        // below. With the integrated GPU warp active, the mixer frame IS the warped output already
        // - the chained stage is skipped. See Doc/HaPlay-Output-Mapping-Plan.md.
        var integratedWarp = _integratedWarpActive;
        List<AcquiredOutput>? unmapped = null;
        foreach (var output in snapshot)
        {
            var stage = integratedWarp ? null : output.MappingStage;
            if (stage is null)
            {
                (unmapped ??= new List<AcquiredOutput>(snapshot.Count)).Add(output);
                continue;
            }

            VideoFrame mappedFrame;
            try
            {
                // The calibration frame replaces the canvas for THIS output only, and does so before the
                // mapping stage - so the pattern is cut and mesh-warped exactly like programme content.
                // That is the whole point: a grid that bypassed the warp would show a rectangle on a
                // surface the warp exists to make non-rectangular, and align nothing.
                // Composite() does not take ownership of its source, so the stored frame survives.
                mappedFrame = stage.Composite(output.TestPattern ?? frame, _compositorFactory);
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "ClipCompositionRuntime.Pump: mapping stage failed for {Line}", output.DisplayName);
                continue;
            }

            SubmitToOutput(output, mappedFrame);
        }

        if (unmapped is null)
        {
            frame.Dispose();
            sw.Stop();
            RecordPumpTiming(sw.Elapsed, _canvasPeriod);
            return;
        }

        // Multi-output fan-out is zero-copy when the canvas is CPU-backed (the CpuVideoCompositor's
        // pool-rented buffer - the common case): every output gets a refcounted view over the same
        // pixels and the canvas returns to the pool once the last view is disposed. The per-output
        // deep copies this replaces were ~8 MB × (outputs−1) of memcpy per 1080p frame. Fallback to
        // cloning covers non-CPU backings (GL compositor) - TryCreateCpuFanOutViews leaves the frame
        // untouched when it declines.
        VideoFrame[]? views = null;
        if (unmapped.Count > 1)
            VideoFrame.TryCreateCpuFanOutViews(frame, unmapped.Count, frame.ColorTransferHint, out views);

        for (var i = 0; i < unmapped.Count; i++)
        {
            var output = unmapped[i];
            var isLast = i == unmapped.Count - 1;
            VideoFrame toSubmit;

            // An unmapped output takes the pattern straight, but submitting is a transfer of ownership,
            // so it gets a copy - the stored frame has to outlive every frame it is shown on.
            if (output.TestPattern is { } pattern)
            {
                // This output still owns a share of the canvas even though it will not show it. The
                // fan-out views share one refcount, so an unreleased view keeps the canvas buffer out of
                // the pool forever; the no-views case owns `frame` itself on the last iteration.
                if (views is not null)
                    views[i].Dispose();
                else if (isLast)
                    frame.Dispose();

                try
                {
                    SubmitToOutput(
                        output, VideoFrameCpuClone.DuplicateCpuBacking(pattern, pattern.ColorTransferHint));
                }
                catch (Exception ex)
                {
                    Trace.LogTrace(ex, "ClipCompositionRuntime.Pump: test-pattern clone failed for {Line}",
                        output.DisplayName);
                }
                continue;
            }

            if (views is not null)
            {
                // The canvas frame's release moved into the views' shared countdown; `frame` itself
                // is now an inert husk (disposing it is a no-op), so no isLast special-casing.
                toSubmit = views[i];
            }
            else
            {
                try
                {
                    toSubmit = isLast ? frame : VideoFrameCpuClone.DuplicateCpuBacking(frame, frame.ColorTransferHint);
                }
                catch (Exception ex)
                {
                    Trace.LogTrace(ex, "ClipCompositionRuntime.Pump: clone failed for {Line}", output.DisplayName);
                    if (isLast) frame.Dispose();
                    continue;
                }
            }

            SubmitToOutput(output, toSubmit);
        }

        sw.Stop();
        RecordPumpTiming(sw.Elapsed, _canvasPeriod);
    }

    private bool TryPumpIntegratedMultiWarp(TimeSpan? masterPts, IReadOnlyList<AcquiredOutput> snapshot, Stopwatch sw)
    {
        if (_compositionMappingStage is not null
            || _integratedWarpActive
            || _compositor is not IWarpPassVideoCompositor
            || snapshot.Count < 2
            || _mixer.HasSurfaceSlots) // surfaces composite on the plain path (CompositeWithSurfaces)
            return false;

        var requests = new WarpOutputRequest[snapshot.Count];
        var hasMappedOutput = false;
        for (var i = 0; i < snapshot.Count; i++)
        {
            var stage = snapshot[i].MappingStage;
            if (stage is null)
            {
                requests[i] = new WarpOutputRequest(_canvasFormat, null);
                continue;
            }

            hasMappedOutput = true;
            requests[i] = new WarpOutputRequest(stage.OutputFormat, stage.WarpSections);
        }

        if (!hasMappedOutput)
            return false;

        IReadOnlyList<VideoFrame> frames;
        var compositeStarted = Stopwatch.GetTimestamp();
        try
        {
            if (!_mixer.TryReadNextFrames(masterPts, requests, out frames))
                return true;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(
                ex,
                "ClipCompositionRuntime.Pump: integrated multi-output warp failed for {Composition}; falling back to chained mapping",
                CompositionName);
            return false;
        }

        if (frames.Count != snapshot.Count)
        {
            DisposeFrames(frames);
            Trace.LogWarning(
                "ClipCompositionRuntime.Pump: integrated multi-output warp returned {FrameCount} frames for {OutputCount} outputs in {Composition}; falling back to chained mapping",
                frames.Count,
                snapshot.Count,
                CompositionName);
            return false;
        }

        _compositeTiming.RecordSince(compositeStarted);
        Interlocked.Increment(ref _framesComposited);
        for (var i = 0; i < snapshot.Count; i++)
            SubmitToOutput(snapshot[i], frames[i]);

        sw.Stop();
        RecordPumpTiming(sw.Elapsed, _canvasPeriod);
        return true;
    }

    private static void DisposeFrames(IReadOnlyList<VideoFrame> frames)
    {
        for (var i = 0; i < frames.Count; i++)
            frames[i].Dispose();
    }

    /// <summary>Configures the output on format change (idempotent per format) and submits;
    /// disposes the frame on submit failure.</summary>
    /// <summary>
    /// Submits each output's static frame while the canvas is empty: a calibration pattern if one is set,
    /// otherwise the composition's idle image, otherwise that output's own idle fallback.
    /// </summary>
    /// <remarks>
    /// The precedence is the owner's: a composition idle beats a per-output idle, because the composition
    /// is the thing an operator thinks of as "what this canvas shows", and the per-output image exists to
    /// cover outputs the show does not otherwise dress. A calibration pattern beats both - it is a
    /// deliberate override and would be useless if an idle image hid it.
    /// <para>Every frame here is stored and reused across ticks, so mapped outputs composite from it
    /// (which does not consume it) and unmapped outputs get a clone (submitting transfers ownership).</para>
    /// </remarks>
    private void PumpIdleFrames(IReadOnlyList<AcquiredOutput> snapshot)
    {
        var compositionIdle = _idleFrame;

        foreach (var output in snapshot)
        {
            var source = output.TestPattern ?? compositionIdle ?? output.IdleFrame;
            if (source is null)
                continue; // nothing to show - the historical behaviour, i.e. leave the sink alone

            var stage = output.MappingStage;
            VideoFrame? compositionMapped = null;
            try
            {
                // Idle/calibration content follows the same composition-level FX as programme frames.
                // The composition mapping is canvas-sized, then the per-output stage fans it out.
                if (_compositionMappingStage is { } compositionStage)
                {
                    try
                    {
                        compositionMapped = compositionStage.Composite(source, _compositorFactory);
                        source = compositionMapped;
                    }
                    catch (Exception ex)
                    {
                        Trace.LogWarning(
                            ex,
                            "ClipCompositionRuntime.Pump: idle composition mapping failed for {Composition}",
                            CompositionName);
                    }
                }
                if (stage is not null)
                {
                    SubmitToOutput(output, stage.Composite(source, _compositorFactory));
                }
                else
                {
                    SubmitToOutput(
                        output, VideoFrameCpuClone.DuplicateCpuBacking(source, source.ColorTransferHint));
                }
            }
            catch (Exception ex)
            {
                Trace.LogTrace(ex, "ClipCompositionRuntime.Pump: idle frame failed for {Line}", output.DisplayName);
            }
            finally
            {
                compositionMapped?.Dispose();
            }
        }
    }

    private void SubmitToOutput(AcquiredOutput output, VideoFrame toSubmit)
    {
        try
        {
            if (output.TrySubmit(toSubmit))
                Interlocked.Increment(ref _framesSubmitted);
            else
                toSubmit.Dispose();
        }
        catch (Exception ex)
        {
            output.RecordSubmitFailure();
            Trace.LogTrace(ex, "ClipCompositionRuntime.Pump: Submit failed for {Line}", output.DisplayName);
            toSubmit.Dispose();
        }
    }

    private void RecordPumpTiming(TimeSpan elapsed, TimeSpan budget)
    {
        _pumpTiming.Record(elapsed);
        Volatile.Write(ref _lastPumpFrameTicks, elapsed.Ticks);
        UpdateMaxTicks(ref _maxPumpFrameTicks, elapsed.Ticks);

        if (elapsed <= budget)
            return;

        var overruns = Interlocked.Increment(ref _pumpOverruns);
        if (overruns == 1 || overruns % 120 == 0)
        {
            Trace.LogWarning(
                "ClipCompositionRuntime: composition {Composition} pump over budget ({ElapsedMs:0.00}ms > {BudgetMs:0.00}ms, layers={Layers}, slotOverflow={Overflow})",
                CompositionName,
                elapsed.TotalMilliseconds,
                budget.TotalMilliseconds,
                LayerCount,
                GetStats().SlotOverflowFrames);
        }
    }

    private static void UpdateMaxTicks(ref long target, long candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
                return;
        }
    }

    private void SortLayersLocked() => RecomputeLayerOrderLocked();

    /// <summary>
    /// Re-establishes ONE z-order across frame layers and surface layers together, by
    /// <c>(LayerIndex, Sequence)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two kinds used to be ordered independently, which is why a visualizer always sat on top however
    /// it was authored: the compositor was handed two lists and told "frames, then surfaces". Ordering them
    /// against each other and handing each surface the number of frame layers below it lets a clip authored
    /// above a visualizer actually cover it.
    /// </para>
    /// <para>
    /// Sequence breaks ties, matching what frame layers have always done among themselves: two layers
    /// authored at the same index stack in attach order, so firing a second cue onto an occupied index puts
    /// it on top - which is what an operator sees happen and therefore expects.
    /// </para>
    /// <para>Must run whenever EITHER list changes, including when the last frame layer leaves: every
    /// surface's count depends on the frame layers around it, not just on its own placement.</para>
    /// </remarks>
    private void RecomputeLayerOrderLocked()
    {
        _slots.Sort(static (a, b) =>
        {
            var cmp = a.LayerIndex.CompareTo(b.LayerIndex);
            return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence);
        });
        _surfaceLayers.Sort(static (a, b) =>
        {
            var cmp = a.LayerIndex.CompareTo(b.LayerIndex);
            return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence);
        });

        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _slots.Count; i++)
            order[_slots[i].RawSlot.Id] = i;

        _mixer.SortSlots((a, b) =>
        {
            var ai = order.TryGetValue(a.Id, out var av) ? av : int.MaxValue;
            var bi = order.TryGetValue(b.Id, out var bv) ? bv : int.MaxValue;
            return ai.CompareTo(bi);
        });

        if (_surfaceLayers.Count > 0)
        {
            var surfaceOrder = new Dictionary<VideoCompositorSource.SurfaceSlot, int>();
            for (var i = 0; i < _surfaceLayers.Count; i++)
                surfaceOrder[_surfaceLayers[i].RawSlot] = i;
            _mixer.SortSurfaceSlots((a, b) =>
            {
                var ai = surfaceOrder.TryGetValue(a, out var av) ? av : int.MaxValue;
                var bi = surfaceOrder.TryGetValue(b, out var bv) ? bv : int.MaxValue;
                return ai.CompareTo(bi);
            });

            // Both lists are sorted on the same key, so one forward walk assigns every surface the number
            // of frame layers that sort below it.
            var frameIndex = 0;
            foreach (var surface in _surfaceLayers)
            {
                while (frameIndex < _slots.Count && Below(_slots[frameIndex], surface))
                    frameIndex++;
                surface.RawSlot.DrawAfterFrameLayers = frameIndex;
            }
        }

        static bool Below(LayerSlot frame, SurfaceLayerSlot surface)
        {
            var cmp = frame.LayerIndex.CompareTo(surface.LayerIndex);
            return cmp != 0 ? cmp < 0 : frame.Sequence < surface.Sequence;
        }
    }

    internal void RaiseOutputPumpPressure(string outputId, string outputName, long droppedTotal, long droppedSinceLastReport)
    {
        try
        {
            PumpPressureWarning?.Invoke(this, new ClipCompositionPumpPressureWarning(
                CompositionId,
                CompositionName,
                outputId,
                outputName,
                droppedSinceLastReport,
                droppedTotal));
        }
        catch (Exception ex)
        {
            Trace.LogTrace(ex, "ClipCompositionRuntime: PumpPressureWarning handler threw");
        }
    }

    public void Dispose()
    {
        MediaClock? slaveClock;
        VideoFrame? idleToRelease;
        List<AcquiredOutput> acquiredToRetire;
        List<SubtitleLayerFeed> subtitleFeeds;
        List<SurfaceLayerSlot> surfaceLayers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // The composition idle frame is owned here (per-output idles go with their outputs' retire).
            idleToRelease = _idleFrame;
            _idleFrame = null;
            slaveClock = _slaveClock;
            acquiredToRetire = _acquired.ToList();
            subtitleFeeds = _subtitleFeeds.ToList();
            _subtitleFeeds.Clear();
            surfaceLayers = _surfaceLayers.ToList();
            _surfaceLayers.Clear();
            RepublishSubtitleFeedsSnapshot();

            // Retire mapping stages up front so the driver-thread dispose window below (and the
            // direct drain fallback at the end) can tear their compositors down.
            if (_compositionMappingStage is { } compositionStage)
            {
                _compositionMappingStage = null;
                _retiredMappingStages.Enqueue(compositionStage);
            }
        }

        if (idleToRelease is not null)
            RetireStaticFrame(idleToRelease);

        foreach (var feed in subtitleFeeds)
            feed.Dispose();
        foreach (var surfaceLayer in surfaceLayers)
            surfaceLayer.Dispose();

        foreach (var acquired in acquiredToRetire)
        {
            if (acquired.Retire("ClipCompositionRuntime.Dispose", RetireStaticFrame) is { } stage)
                _retiredMappingStages.Enqueue(stage);
        }

        if (slaveClock is not null && _disposeCompositorOnDriverThread is not null)
        {
            Interlocked.Exchange(ref _driverDisposeState, 1);
            var deadline = Environment.TickCount64 + 250;
            while (Volatile.Read(ref _driverDisposeState) != 2 && Environment.TickCount64 < deadline)
                Thread.Sleep(1);
        }

        if (slaveClock is { } sc)
        {
            try { sc.VideoTick -= OnSlaveVideoTick; } catch { /* best effort */ }
            try { sc.Stop(); } catch { /* best effort */ }
            try { sc.Dispose(); } catch { /* best effort */ }
            lock (_gate)
                _slaveClock = null;
        }

        lock (_gate)
        {
            _slots.Clear();
            _acquired.Clear();
            RepublishAcquiredSnapshot();
        }

        // Best-effort fallback for stages the driver window didn't reach (pump never started, or
        // the dispose deadline lapsed) - mirrors the direct canvas-compositor dispose below.
        DrainRetiredMappingStages();
        DrainRetiredStaticFrames();

        MediaDiagnostics.SwallowDisposeErrors(_mixer.Dispose, "ClipCompositionRuntime.Dispose: mixer");
        MediaDiagnostics.SwallowDisposeErrors(_compositor.Dispose, "ClipCompositionRuntime.Dispose: compositor");
    }

    /// <summary>
    /// Per-output warp stage: composites the canvas frame's mapping sections into an output-sized
    /// frame on the pump thread. The mapping compositor is created lazily on the pump thread (GL
    /// context affinity) and shared across section-only updates via a boxed holder so live editor
    /// drags never churn GL contexts; it is disposed on the pump thread via the retired-stage queue.
    /// </summary>
    private sealed class OutputMappingStage
    {
        private sealed class CompositorBox
        {
            public IVideoCompositor? Compositor;
            public Action? DisposeOnDriverThread;
        }

        private readonly List<ResolvedMappingSection> _sections;
        private readonly WarpSection[] _warpSections;
        private readonly CompositorBox _box;
        private readonly List<CompositorLayer> _layerScratch = new();

        public OutputMappingStage(VideoFormat outputFormat, List<ResolvedMappingSection> sections)
            : this(outputFormat, sections, new CompositorBox())
        {
        }

        private OutputMappingStage(VideoFormat outputFormat, List<ResolvedMappingSection> sections, CompositorBox box)
        {
            OutputFormat = outputFormat;
            _sections = sections;
            _warpSections = CreateWarpSections(sections);
            _box = box;
        }

        public VideoFormat OutputFormat { get; }

        public WarpSection[] WarpSections => _warpSections;

        /// <summary>Section-only update at the same output size: the new stage shares the live
        /// compositor (boxed), so nothing needs disposal.</summary>
        public OutputMappingStage WithSections(List<ResolvedMappingSection> sections) =>
            new(OutputFormat, sections, _box);

        /// <summary>Sections in the shape the integrated GPU warp pass consumes.</summary>
        public WarpSection[] BuildWarpSections() => _warpSections;

        private static WarpSection[] CreateWarpSections(IReadOnlyList<ResolvedMappingSection> resolvedSections)
        {
            var warpSections = new WarpSection[resolvedSections.Count];
            for (var i = 0; i < resolvedSections.Count; i++)
                warpSections[i] = new WarpSection(
                    resolvedSections[i].SourceCrop,
                    resolvedSections[i].Transform,
                    resolvedSections[i].Opacity,
                    resolvedSections[i].Mesh);
            return warpSections;
        }

        /// <summary>True when any section carries a mesh warp - needs the GL warp pass; the CPU
        /// fallback renders such sections with their affine placement instead.</summary>
        private bool HasMeshSection()
        {
            foreach (var section in _sections)
            {
                if (section.Mesh is not null)
                    return true;
            }

            return false;
        }

        private bool _warpModeApplied;
        private bool _meshFallbackWarned;

        /// <summary>Pump thread only. The canvas frame is borrowed - the compositor never takes
        /// ownership, so the caller keeps disposing it.</summary>
        public VideoFrame Composite(VideoFrame canvas, Func<VideoFormat, ClipCompositionCompositor> compositorFactory)
        {
            var compositor = _box.Compositor;
            if (compositor is null)
            {
                var created = compositorFactory(OutputFormat);
                compositor = created.Compositor
                             ?? throw new InvalidOperationException("Compositor factory returned null compositor.");
                compositor.Configure(OutputFormat);
                _box.Compositor = compositor;
                _box.DisposeOnDriverThread = created.DisposeOnDriverThread;
            }

            if (compositor is IWarpPassVideoCompositor warpCapable)
            {
                // Warp mode (GL): composite the canvas as one identity layer, then the compositor's
                // integrated warp pass cuts/warps it into the output - the same pixel path as the
                // single-output integrated warp, and the only path that renders mesh sections.
                // Applied once per stage instance; section edits arrive as a new instance sharing
                // the boxed compositor (WithSections), re-applying here on its first frame.
                if (!_warpModeApplied)
                {
                    warpCapable.Configure(canvas.Format);
                    warpCapable.SetWarpPass(OutputFormat, BuildWarpSections());
                    _warpModeApplied = true;
                }

                _layerScratch.Clear();
                _layerScratch.Add(new CompositorLayer(canvas, LayerTransform2D.Identity, 1f, BlendMode.Source));
                return compositor.Composite(_layerScratch, canvas.PresentationTime);
            }

            if (!_meshFallbackWarned && HasMeshSection())
            {
                _meshFallbackWarned = true;
                Trace.LogWarning(
                    "Output mapping has mesh-warp sections but the compositor backend is CPU-only; rendering them with their affine placement (mesh warp requires the GL compositor).");
            }

            _layerScratch.Clear();
            foreach (var section in _sections)
            {
                _layerScratch.Add(new CompositorLayer(canvas, section.Transform, section.Opacity, BlendMode.SourceOver)
                {
                    SourceCrop = section.SourceCrop,
                });
            }

            return compositor.Composite(_layerScratch, canvas.PresentationTime);
        }

        /// <summary>Idempotent (the box is shared across section updates and emptied on first call).</summary>
        public void DisposeCompositor()
        {
            var driver = _box.DisposeOnDriverThread;
            _box.DisposeOnDriverThread = null;
            var compositor = _box.Compositor;
            _box.Compositor = null;

            if (driver is not null)
                MediaDiagnostics.SwallowDisposeErrors(driver, "ClipCompositionRuntime: mapping compositor driver dispose");
            if (compositor is not null)
                MediaDiagnostics.SwallowDisposeErrors(compositor.Dispose, "ClipCompositionRuntime: mapping compositor dispose");
        }
    }

    private sealed class AcquiredOutput
    {
        private readonly ClipCompositionOutputLease _lease;
        private readonly object _lifecycleGate = new();
        private EventHandler<VideoOutputPumpPressureEventArgs>? _pressureHandler;
        private long _lastReportedDrops;
        private long _nextReportTicks;
        private volatile OutputMappingStage? _mappingStage;
        private VideoFormat? _configuredFormat;
        private bool _retired;

        /// <summary>Calibration frame that REPLACES the canvas for this output only. Volatile: the pump
        /// reads it once per frame, a UI thread swaps it.</summary>
        private volatile VideoFrame? _testPattern;

        /// <summary>This output's fallback idle frame, shown when the composition is empty and has no
        /// idle of its own.</summary>
        private volatile VideoFrame? _idleFrame;

        // Per-output throughput. The composition-wide FramesSubmitted sums across outputs, so it cannot
        // answer "which line is dropping" - the question a diagnostics row exists to answer.
        private long _submitted;
        private long _refused;
        private long _failed;

        public AcquiredOutput(ClipCompositionOutputLease lease)
        {
            _lease = lease;
            // Every sink is decoupled from the composite tick through a pump. A raw window output's
            // Submit ends in a vsynced GL present, and presenting INSIDE the tick made the canvas
            // hostage to display timing: the tick's 60.000 Hz beats against the display's actual
            // refresh, and at every beat crossing a present blocked an extra vblank - one dropped
            // frame every few seconds on an idle machine, per window. Two frames of queue keep
            // worst-case added display latency at ~33 ms while the pump's own thread absorbs the
            // blocking; a stalled line drops ITS OWN frames (visible as pump pressure) without
            // touching the canvas cadence or its sibling outputs.
            _output = lease.Output is null or VideoOutputPump
                ? lease.Output
                : new VideoOutputPump(
                    lease.Output, maxQueuedFrames: 2, name: $"CompositionOut:{lease.DisplayName}");
            _ownsPump = !ReferenceEquals(_output, lease.Output);
        }

        private readonly IVideoOutput? _output;
        private readonly bool _ownsPump;

        /// <summary>This output's calibration frame, or null. Owned by the output: the pump only reads it,
        /// so it survives across frames and is disposed when replaced, cleared, or retired.</summary>
        public VideoFrame? TestPattern => _testPattern;

        /// <summary>This output's idle frame, or null.</summary>
        public VideoFrame? IdleFrame => _idleFrame;

        /// <summary>Swaps the idle frame, handing the previous one back for disposal.</summary>
        public bool SetIdleFrame(VideoFrame? idle, out VideoFrame? retired)
        {
            lock (_lifecycleGate)
            {
                retired = null;
                if (_retired)
                    return false;
                retired = _idleFrame;
                _idleFrame = idle;
                return true;
            }
        }

        /// <summary>Swaps the calibration frame, handing the previous one back for disposal.</summary>
        public bool SetTestPattern(VideoFrame? pattern, out VideoFrame? retired)
        {
            lock (_lifecycleGate)
            {
                retired = null;
                if (_retired)
                    return false;
                retired = _testPattern;
                _testPattern = pattern;
                return true;
            }
        }

        public string OutputId => _lease.OutputId;

        public string DisplayName => _lease.DisplayName;

        public IVideoOutput Output => _output!;

        public Action? Release => _lease.Release;

        public bool DisposeOutputOnRuntimeDispose => _lease.DisposeOutputOnRuntimeDispose;

        /// <summary>Current mapping stage, or null when this output receives the raw canvas.
        /// Volatile snapshot - the pump reads it once per frame, the UI thread swaps it.</summary>
        public OutputMappingStage? MappingStage => _mappingStage;

        /// <summary>Swaps the mapping. When the output canvas size is unchanged the existing
        /// compositor carries over (no GL churn while dragging in the editor); otherwise the old
        /// stage is handed back via <paramref name="retired"/> for driver-thread disposal.</summary>
        public bool SetMapping(ClipOutputMappingSpec? mapping, VideoFormat canvasFormat, out OutputMappingStage? retired)
        {
            lock (_lifecycleGate)
            {
                retired = null;
                if (_retired)
                    return false;

                var current = _mappingStage;

                if (mapping is null)
                {
                    _mappingStage = null;
                    retired = current;
                    return true;
                }

                var outputFormat = OutputMappingResolver.ResolveOutputFormat(mapping, canvasFormat);
                var sections = OutputMappingResolver.Resolve(mapping, canvasFormat.Width, canvasFormat.Height);
                if (current is not null && current.OutputFormat == outputFormat)
                {
                    _mappingStage = current.WithSections(sections);
                    return true;
                }

                _mappingStage = new OutputMappingStage(outputFormat, sections);
                retired = current;
                return true;
            }
        }

        /// <summary>Configure-on-change: mapped and unmapped outputs see different formats, and a
        /// live mapping resize changes the format mid-run. Configure is idempotent downstream.</summary>
        private void EnsureConfigured(VideoFormat format)
        {
            if (_configuredFormat == format)
                return;
            Output.Configure(format);
            _configuredFormat = format;
        }

        public bool TrySubmit(VideoFrame frame)
        {
            lock (_lifecycleGate)
            {
                if (_retired)
                {
                    Interlocked.Increment(ref _refused);
                    return false;
                }

                EnsureConfigured(frame.Format);
                Output.Submit(frame);
                Interlocked.Increment(ref _submitted);
                return true;
            }
        }

        /// <summary>Counts a submit that threw, so a failing line is visible as more than a log line.</summary>
        public void RecordSubmitFailure() => Interlocked.Increment(ref _failed);

        /// <summary>This output's own throughput row, including the pump queue depth when the sink is a
        /// <see cref="VideoOutputPump"/> (the cue-line health probe used to hardcode those to zero).</summary>
        public ClipCompositionOutputStats SnapshotStats()
        {
            var queued = 0;
            var capacity = 0;
            if (Output is VideoOutputPump pump)
            {
                queued = pump.CurrentQueuedDepth;
                capacity = pump.MaxQueueDepth;
            }

            // Read through the pump to the real sink. A device that presents on its own cadence (a
            // vsynced window) decides there - not here - whether a frame is shown, and the pump's own
            // counters cannot see it: its Submit hands off without blocking, so its queue stays empty
            // and its drop count stays at zero while the device discards frames. Without this the
            // composition reports perfect health through a visibly stuttering output.
            long presentDropped = 0;
            long presentRepeated = 0;
            if (_lease.Output is IVideoOutputPresentDiagnostics present)
            {
                presentDropped = present.DroppedFrames;
                presentRepeated = present.RepeatedFrames;
            }

            var format = _configuredFormat;
            return new ClipCompositionOutputStats(
                OutputId,
                DisplayName,
                Interlocked.Read(ref _submitted),
                Interlocked.Read(ref _refused),
                Interlocked.Read(ref _failed),
                MappingStage is not null,
                format?.Width ?? 0,
                format?.Height ?? 0,
                queued,
                capacity,
                presentDropped,
                presentRepeated);
        }

        public void SubscribePumpPressure(ClipCompositionRuntime owner)
        {
            if (Output is not VideoOutputPump pump)
                return;

            _pressureHandler = (_, args) =>
            {
                var nowTicks = Environment.TickCount64;
                if (nowTicks < _nextReportTicks) return;
                var newDrops = args.DroppedFramesTotal - _lastReportedDrops;
                if (newDrops <= 0) return;
                _lastReportedDrops = args.DroppedFramesTotal;
                _nextReportTicks = nowTicks + 5000;
                owner.RaiseOutputPumpPressure(OutputId, DisplayName, args.DroppedFramesTotal, newDrops);
            };
            pump.PumpPressure += _pressureHandler;
        }

        public OutputMappingStage? Retire(string operation, Action<VideoFrame> retireStaticFrame)
        {
            lock (_lifecycleGate)
            {
                if (_retired)
                    return null;

                _retired = true;
                var retired = _mappingStage;
                _mappingStage = null;
                // The calibration frame is plain pixel data (never a GL resource), so it is safe to
                // release here rather than deferring to the driver thread.
                var pattern = _testPattern;
                _testPattern = null;
                if (pattern is not null)
                    retireStaticFrame(pattern);
                var idle = _idleFrame;
                _idleFrame = null;
                if (idle is not null)
                    retireStaticFrame(idle);
                UnsubscribePumpPressureCore();

                // Our pump goes down first (joins its thread, flushes its queue) so nothing can be
                // mid-Submit into the lease's output when that output is released below. The pump
                // never owns the inner sink - the lease's dispose policy stays the authority.
                if (_ownsPump && _output is IDisposable pumpDisposable)
                    MediaDiagnostics.SwallowDisposeErrors(pumpDisposable.Dispose, $"{operation}: output pump dispose");
                if (DisposeOutputOnRuntimeDispose && _lease.Output is IDisposable disposable)
                    MediaDiagnostics.SwallowDisposeErrors(disposable.Dispose, $"{operation}: output dispose");
                if (Release is not null)
                    MediaDiagnostics.SwallowDisposeErrors(Release, $"{operation}: output release");

                return retired;
            }
        }

        private void UnsubscribePumpPressureCore()
        {
            if (_pressureHandler is null || Output is not VideoOutputPump pump)
                return;
            pump.PumpPressure -= _pressureHandler;
            _pressureHandler = null;
        }
    }

    /// <summary>
    /// The placement surface a clip's composition layer exposes regardless of HOW it renders - a decoded
    /// frame slot (<see cref="LayerSlot"/>) or a GPU layer surface (<see cref="SurfaceLayerSlot"/>, NXT-10).
    /// The session's fade rides, live placement edits, and teardown all go through this contract, so a
    /// surface-backed clip behaves exactly like a frame-backed one for transport purposes.
    /// </summary>
    /// <summary>
    /// The ONE opacity composition for a placed layer: <c>authored x fade x automation</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Video's counterpart to <c>SoundingLevel</c>, and it exists for the same reason: three mechanisms
    /// write a layer's opacity and, without a composition, whichever wrote last simply erased the others.
    /// The concrete defect was that a live placement edit re-applied the AUTHORED opacity straight onto
    /// the slot, so editing a placement mid-fade snapped the layer to full and the fade then ramped from
    /// a value nothing had asked for.
    /// </para>
    /// <para>
    /// Each component has exactly one writer - placement applies own <see cref="Base"/>, fade paths own
    /// <see cref="Fade"/>, the automation lane owns <see cref="Automation"/> - so they compose by
    /// construction. Writes race only in the sense that two owners can recompute the product in either
    /// order; both land on the same value once both have written, and the worst case is one frame at a
    /// stale product.
    /// </para>
    /// </remarks>
    internal sealed class VisualLevel
    {
        /// <summary>The placement's authored opacity. Re-applied on every placement update.</summary>
        public float Base { get; set; } = 1f;

        /// <summary>What fade-ins, fade cues, stop ramps and crossfade tails ramp. The only component a
        /// ramp may capture as its start - capturing the effective product instead would re-apply the
        /// authored opacity on every step and darken the layer geometrically.</summary>
        public float Fade { get; set; } = 1f;

        /// <summary>The opacity-lane factor (1 = no automation).</summary>
        public float Automation { get; set; } = 1f;

        /// <summary>What actually renders.</summary>
        public float Effective => Math.Clamp(Base * Fade * Automation, 0f, 1f);
    }

    public interface IPlacedClipLayer : IDisposable
    {
        int LayerIndex { get; }

        /// <summary>The placement's own authored opacity, underneath every ramp. Read-only here: it
        /// changes through <see cref="UpdatePlacement"/>, which is where a placement is authored.</summary>
        float BaseOpacity { get; }

        /// <summary>The fade component - what every ramp in the session reads and writes. NOT the
        /// opacity that renders; see <see cref="EffectiveOpacity"/>.</summary>
        float FadeLevel { get; set; }

        /// <summary>The opacity-automation component, driven by an opacity lane. 1 = no automation.</summary>
        float AutomationLevel { get; set; }

        /// <summary>The composed opacity actually handed to the compositor.</summary>
        float EffectiveOpacity { get; }

        void UpdatePlacement(VideoPlacementSpec placement);

        /// <summary>
        /// Stops rendering this layer against the composition's master (transport) time. Irreversible and
        /// per LAYER, so the composition's other layers (and every normal clip) keep their master alignment
        /// untouched.
        /// <para>
        /// The one caller is the dual-voice crossfade handoff: the outgoing clip keeps its layers and its
        /// transport-timeline claim, but that timeline is re-bound to the INCOMING clip's playhead, so the
        /// pump would feed the tail a master time from a different clip. Nothing targets the tail's
        /// transport for its remaining fade window, so it is free to run on its own clip's time.
        /// </para>
        /// <para>
        /// A <see cref="LayerSlot"/> needs no clock: it picks a picture out of its slot's submitted frames,
        /// so it just switches to latest-wins and advances on the outgoing player's own paced submissions.
        /// (Master-aligned selection would reject every candidate more than one canvas period in the future
        /// and keep re-presenting the frame it already holds, freezing the tail on a still - crossfading out
        /// of a clip at 3:12 into one starting at 0:00 puts every outgoing frame ~192 s "in the future".)
        /// A <see cref="SurfaceLayerSlot"/> has no such queue - it RENDERS at whatever instant it is handed -
        /// so it needs <paramref name="ownClipTime"/> to render the right clip.
        /// </para>
        /// </summary>
        /// <param name="ownClipTime">The outgoing clip's own source clock, sampled once per composite.
        /// Required for a GPU surface layer (which otherwise keeps rendering the composition's - i.e. the
        /// INCOMING clip's - time); ignored by frame layers, whose frames carry their own timestamps. Null
        /// leaves a surface layer on the composition's master time.</param>
        void DetachFromMasterAlignment(Func<TimeSpan>? ownClipTime = null);
    }

    /// <summary>
    /// One GPU layer surface placed on this composition (NXT-10). Mirrors <see cref="LayerSlot"/>'s
    /// placement semantics (dest rect/fit/rotation/opacity resolve identically; the surface's nominal
    /// source size is the canvas). Disposing removes the layer and disposes the SURFACE - the runtime
    /// owns surface lifetime once placed.
    /// </summary>
    public sealed class SurfaceLayerSlot : IPlacedClipLayer
    {
        private readonly ClipCompositionRuntime _owner;
        private readonly bool _ownsSurface;
        internal VideoCompositorSource.SurfaceSlot RawSlot { get; }
        private VideoPlacementSpec _placement;
        private int _disposed;

        internal SurfaceLayerSlot(
            ClipCompositionRuntime owner,
            VideoCompositorSource.SurfaceSlot slot,
            VideoPlacementSpec placement,
            bool ownsSurface = true)
        {
            _owner = owner;
            RawSlot = slot;
            _placement = placement;
            _ownsSurface = ownsSurface;
            Sequence = Interlocked.Increment(ref owner._nextLayerSequence);
        }

        public IVideoCompositorLayerSurface Surface => RawSlot.Surface;

        public int LayerIndex => _placement.LayerIndex;

        public long Sequence { get; }

        private readonly VisualLevel _level = new();

        public float BaseOpacity => _level.Base;

        public float FadeLevel
        {
            get => _level.Fade;
            set
            {
                _level.Fade = Math.Clamp(value, 0f, 1f);
                RawSlot.Opacity = _level.Effective;
            }
        }

        public float AutomationLevel
        {
            get => _level.Automation;
            set
            {
                _level.Automation = Math.Clamp(value, 0f, 1f);
                RawSlot.Opacity = _level.Effective;
            }
        }

        public float EffectiveOpacity => _level.Effective;

        public void UpdatePlacement(VideoPlacementSpec placement)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ArgumentNullException.ThrowIfNull(placement);
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                var resort = placement.LayerIndex != _placement.LayerIndex;
                _placement = placement;
                ApplyPlacement();
                if (resort)
                    _owner.SortSurfaceLayersLocked();
            }
        }

        /// <inheritdoc />
        /// <remarks>Installs <paramref name="ownClipTime"/> as this surface's per-placement render clock
        /// (<see cref="VideoCompositorSource.SurfaceSlot.RenderTimeSource"/> →
        /// <see cref="CompositorSurfaceLayer.RenderTime"/> → the surface's <c>Render</c> <c>masterTime</c>).
        /// A surface holds no submitted-frame queue to select from, so this - not a keep policy - is how it
        /// leaves the composition's clock. A null clock leaves the surface on the composition's master time,
        /// which is also every non-detached surface's unchanged path.</remarks>
        public void DetachFromMasterAlignment(Func<TimeSpan>? ownClipTime = null) =>
            RawSlot.RenderTimeSource = ownClipTime;

        /// <summary>Resolves the placement to the surface's canvas transform - the same
        /// <see cref="PlacementResolver"/> math a frame layer uses, with the canvas as the source size
        /// (surfaces render canvas-resolution content; a full-canvas stretch is the identity). A
        /// placement with VideoFx takes the mapping/warp section path, media-layer parity.</summary>
        internal void ApplyPlacement()
        {
            var destRect = new RectNormalized(
                (float)_placement.DestX,
                (float)_placement.DestY,
                (float)(_placement.DestX + _placement.DestWidth),
                (float)(_placement.DestY + _placement.DestHeight));

            if (_placement.VideoFx is { } videoFx)
            {
                ApplyMappedPlacement(destRect, new OutputMappingGeometryEffect(videoFx));
                return;
            }

            var (transform, _) = PlacementResolver.Resolve(
                destRect,
                LayerSlot.MapFit(_placement.Placement),
                0f, 0f, 0f, 0f,
                _owner._canvasFormat,
                _owner._canvasFormat);
            transform = ApplyPlacementRotation(transform);

            RawSlot.MappingSections = null;
            RawSlot.Transform = transform;
            _level.Base = Math.Clamp((float)_placement.Opacity, 0f, 1f);
            RawSlot.Opacity = _level.Effective;
            // Same color-stage chain as frame layers (chroma key first, then brightness/contrast) -
            // a visualizer placement's Effects-tab settings apply to the surface like any clip layer.
            RawSlot.Effects = LayerSlot.BuildLayerEffects(_placement);
        }

        /// <summary>Mirror of <see cref="LayerSlot.ApplyGeometryPlacement"/> with the canvas as the
        /// source: the mapping's sections sample the surface's full-canvas render, so section crops,
        /// transforms and meshes mean the same thing they do on a media layer.</summary>
        private void ApplyMappedPlacement(RectNormalized destRect, IVideoLayerGeometryEffect geometry)
        {
            var canvas = _owner._canvasFormat;
            var effectFormat = geometry.ResolveOutputFormat(canvas);
            var (effectTransform, _) = PlacementResolver.Resolve(
                destRect,
                LayerSlot.MapFit(_placement.Placement),
                0f, 0f, 0f, 0f,
                effectFormat,
                canvas);
            effectTransform = ApplyPlacementRotation(effectTransform);

            var sourceBounds = new RectNormalized(
                Math.Clamp((float)_placement.CropLeft, 0f, 0.99f),
                Math.Clamp((float)_placement.CropTop, 0f, 0.99f),
                1f - Math.Clamp((float)_placement.CropRight, 0f, 0.99f),
                1f - Math.Clamp((float)_placement.CropBottom, 0f, 0.99f)).Clamped();

            var resolved = geometry.ResolveSections(canvas.Width, canvas.Height, sourceBounds);
            var sections = new WarpSection[resolved.Count];
            for (var i = 0; i < resolved.Count; i++)
            {
                var section = resolved[i];
                sections[i] = new WarpSection(
                    section.SourceCrop,
                    LayerTransform2D.Compose(effectTransform, section.Transform),
                    section.Opacity,
                    section.Mesh is null ? null : LayerSlot.TransformMesh(section.Mesh, effectTransform));
            }

            RawSlot.MappingSections = sections;
            RawSlot.Transform = LayerTransform2D.Identity;
            _level.Base = Math.Clamp((float)_placement.Opacity, 0f, 1f);
            RawSlot.Opacity = _level.Effective;
            RawSlot.Effects = LayerSlot.BuildLayerEffects(_placement);
        }

        private LayerTransform2D ApplyPlacementRotation(LayerTransform2D transform)
        {
            if (_placement.RotationDegrees == 0)
                return transform;

            var rad = (float)(_placement.RotationDegrees * Math.PI / 180.0);
            var cx = (float)((_placement.DestX + _placement.DestWidth * 0.5) * _owner._canvasFormat.Width);
            var cy = (float)((_placement.DestY + _placement.DestHeight * 0.5) * _owner._canvasFormat.Height);
            return LayerTransform2D.Compose(
                LayerTransform2D.Translate(cx, cy),
                LayerTransform2D.Compose(
                    LayerTransform2D.Rotate(rad),
                    LayerTransform2D.Compose(LayerTransform2D.Translate(-cx, -cy), transform)));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.RemoveSurfaceLayer(this);
            // A non-owning slot shares its surface with the owning slot (one surface, several placements);
            // only the owner disposes it, so the shared surface is not torn down while still rendered.
            if (_ownsSurface)
                Surface.Dispose();
        }
    }

    public sealed class LayerSlot : IDisposable, IPlacedClipLayer
    {
        private readonly ClipCompositionRuntime _owner;
        internal VideoCompositorSource.Slot RawSlot { get; }

        /// <summary>The policy this layer was ADDED with. The master-attach upgrade re-applies this
        /// rather than forcing MasterAligned onto every slot: an explicitly-Latest layer (the static
        /// calibration grid, a subtitle overlay re-rendered in place at PTS 0) must stay Latest, or
        /// the clock judges its one frame permanently stale.</summary>
        internal SlotKeepPolicy RequestedKeepPolicy { get; set; } = SlotKeepPolicy.MasterAligned;
        private readonly VideoFormat _source;
        private VideoPlacementSpec _placement;
        private int _disposed;

        internal LayerSlot(
            ClipCompositionRuntime owner,
            VideoCompositorSource.Slot slot,
            VideoFormat source,
            VideoPlacementSpec placement,
            long sequence)
        {
            _owner = owner;
            RawSlot = slot;
            _source = source;
            _placement = placement;
            Sequence = sequence;
        }

        public IVideoOutput Output => RawSlot.Output;

        public int LayerIndex => _placement.LayerIndex;

        private readonly VisualLevel _level = new();

        public float BaseOpacity => _level.Base;

        public float FadeLevel
        {
            get => _level.Fade;
            set
            {
                _level.Fade = Math.Clamp(value, 0f, 1f);
                RawSlot.Opacity = _level.Effective;
            }
        }

        public float AutomationLevel
        {
            get => _level.Automation;
            set
            {
                _level.Automation = Math.Clamp(value, 0f, 1f);
                RawSlot.Opacity = _level.Effective;
            }
        }

        public float EffectiveOpacity => _level.Effective;

        public long Sequence { get; }

        public void UpdatePlacement(VideoPlacementSpec placement)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ArgumentNullException.ThrowIfNull(placement);

            var resort = false;
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                resort = placement.LayerIndex != _placement.LayerIndex;
                _placement = placement;
                ApplyPlacement();
                if (resort)
                    _owner.SortLayersLocked();
            }
        }

        /// <inheritdoc />
        /// <remarks><paramref name="ownClipTime"/> is deliberately unused: a frame layer's pictures carry
        /// their own presentation timestamps, so latest-wins already tracks the outgoing player's paced
        /// submissions exactly.</remarks>
        public void DetachFromMasterAlignment(Func<TimeSpan>? ownClipTime = null) =>
            RawSlot.KeepPolicy = SlotKeepPolicy.Latest;

        public void ApplyPlacement()
        {
            var destRect = new RectNormalized(
                (float)_placement.DestX,
                (float)_placement.DestY,
                (float)(_placement.DestX + _placement.DestWidth),
                (float)(_placement.DestY + _placement.DestHeight));
            if (_placement.VideoFx is { } videoFx)
            {
                ApplyMappedPlacement(destRect, videoFx);
                return;
            }

            var (transform, crop) = PlacementResolver.Resolve(
                destRect,
                MapFit(_placement.Placement),
                (float)_placement.CropLeft,
                (float)_placement.CropTop,
                (float)_placement.CropRight,
                (float)_placement.CropBottom,
                _source,
                _owner._canvasFormat);

            // Per-layer rotation: spin the already-placed image about its destination-rect centre (in canvas
            // pixels). The compositor applies the full affine, so this works on both the GL and CPU backends.
            if (_placement.RotationDegrees != 0)
            {
                var rad = (float)(_placement.RotationDegrees * Math.PI / 180.0);
                var cx = (float)((_placement.DestX + _placement.DestWidth * 0.5) * _owner._canvasFormat.Width);
                var cy = (float)((_placement.DestY + _placement.DestHeight * 0.5) * _owner._canvasFormat.Height);
                transform = LayerTransform2D.Compose(
                    LayerTransform2D.Translate(cx, cy),
                    LayerTransform2D.Compose(
                        LayerTransform2D.Rotate(rad),
                        LayerTransform2D.Compose(LayerTransform2D.Translate(-cx, -cy), transform)));
            }

            RawSlot.MappingSections = null;
            RawSlot.Transform = transform;
            RawSlot.SourceCrop = crop;
            _level.Base = Math.Clamp((float)_placement.Opacity, 0f, 1f);
            RawSlot.Opacity = _level.Effective;
            RawSlot.BlendMode = BlendMode.SourceOver;
            RawSlot.Effects = BuildLayerEffects(_placement);
        }

        /// <summary>Color-stage effect chain for this placement. Chroma key runs FIRST (keying must
        /// see the original colors - a brightness shift would move pixels off the key), then
        /// brightness/contrast on the survivors. Hosts driving <c>Slot.Effects</c> directly own
        /// the whole list.</summary>
        internal static IReadOnlyList<VideoLayerEffect>? BuildLayerEffects(VideoPlacementSpec placement)
        {
            if (placement is { ChromaKey: null, ColorAdjust: null })
                return null;
            var effects = new List<VideoLayerEffect>(2);
            if (placement.ChromaKey is { } key)
                effects.Add(S.Media.Compositor.Effects.ChromaKeyVideoEffect.Create(key));
            if (placement.ColorAdjust is { } adjust)
                effects.Add(S.Media.Compositor.Effects.BrightnessContrastVideoEffect.Create(adjust));
            return effects;
        }

        private void ApplyMappedPlacement(RectNormalized destRect, ClipOutputMappingSpec videoFx) =>
            ApplyGeometryPlacement(destRect, new OutputMappingGeometryEffect(videoFx));

        /// <summary>
        /// Places a layer through a geometry-stage effect (<see cref="IVideoLayerGeometryEffect"/>):
        /// the effect resolves its sections in its own output space; the placement's dest-rect/fit
        /// transform is then composed onto every section (and its mesh) so the existing layout
        /// controls keep their meaning. The mapping/warp "VideoFx" is the built-in implementation.
        /// </summary>
        private void ApplyGeometryPlacement(RectNormalized destRect, IVideoLayerGeometryEffect geometry)
        {
            var effectFormat = geometry.ResolveOutputFormat(_source);
            var (effectTransform, _) = PlacementResolver.Resolve(
                destRect,
                MapFit(_placement.Placement),
                0f,
                0f,
                0f,
                0f,
                effectFormat,
                _owner._canvasFormat);

            effectTransform = ApplyPlacementRotation(effectTransform);

            var sourceBounds = new RectNormalized(
                Math.Clamp((float)_placement.CropLeft, 0f, 0.99f),
                Math.Clamp((float)_placement.CropTop, 0f, 0.99f),
                1f - Math.Clamp((float)_placement.CropRight, 0f, 0.99f),
                1f - Math.Clamp((float)_placement.CropBottom, 0f, 0.99f)).Clamped();

            var resolved = geometry.ResolveSections(_source.Width, _source.Height, sourceBounds);

            var sections = new WarpSection[resolved.Count];
            for (var i = 0; i < resolved.Count; i++)
            {
                var section = resolved[i];
                var transform = LayerTransform2D.Compose(effectTransform, section.Transform);
                sections[i] = new WarpSection(
                    section.SourceCrop,
                    transform,
                    section.Opacity,
                    section.Mesh is null ? null : TransformMesh(section.Mesh, effectTransform));
            }

            RawSlot.MappingSections = sections;
            RawSlot.Transform = LayerTransform2D.Identity;
            RawSlot.SourceCrop = RectNormalized.Full;
            _level.Base = Math.Clamp((float)_placement.Opacity, 0f, 1f);
            RawSlot.Opacity = _level.Effective;
            RawSlot.BlendMode = BlendMode.SourceOver;
            RawSlot.Effects = BuildLayerEffects(_placement);
        }

        private LayerTransform2D ApplyPlacementRotation(LayerTransform2D transform)
        {
            if (_placement.RotationDegrees == 0)
                return transform;

            var rad = (float)(_placement.RotationDegrees * Math.PI / 180.0);
            var cx = (float)((_placement.DestX + _placement.DestWidth * 0.5) * _owner._canvasFormat.Width);
            var cy = (float)((_placement.DestY + _placement.DestHeight * 0.5) * _owner._canvasFormat.Height);
            return LayerTransform2D.Compose(
                LayerTransform2D.Translate(cx, cy),
                LayerTransform2D.Compose(
                    LayerTransform2D.Rotate(rad),
                    LayerTransform2D.Compose(LayerTransform2D.Translate(-cx, -cy), transform)));
        }

        internal static WarpMesh TransformMesh(WarpMesh mesh, LayerTransform2D transform)
        {
            var points = new System.Numerics.Vector2[mesh.Points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var p = mesh.Points[i];
                var (x, y) = transform.Apply(p.X, p.Y);
                points[i] = new System.Numerics.Vector2(x, y);
            }

            return new WarpMesh(mesh.Columns, mesh.Rows, points, mesh.ParameterBounds);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.RemoveLayer(this);
        }

        internal static PlacementFit MapFit(string? placement) => placement?.ToLowerInvariant() switch
        {
            "letterbox" or "contain" or "center" => PlacementFit.Contain,
            "stretch" => PlacementFit.Stretch,
            "fillwidth" => PlacementFit.FillWidth,
            "fillheight" => PlacementFit.FillHeight,
            _ => PlacementFit.Cover,
        };
    }
}

public readonly record struct ClipCompositionRuntimeStats(
    string CompositionId,
    long FramesComposited,
    long FramesSubmitted,
    long PumpOverruns,
    long SlotOverflowFrames,
    TimeSpan LastPumpFrameTime,
    TimeSpan MaxPumpFrameTime,
    long FramesBehindMaster,
    bool ClockMastered,
    int LayerCount = 0,
    TimingSnapshot PumpTiming = default,
    TimingSnapshot CompositeTiming = default,
    TimeSpan CanvasPeriod = default,
    IReadOnlyList<ClipCompositionOutputStats>? Outputs = null,
    string CompositorBackend = "Unknown")
{
    /// <summary>
    /// The composition's target frame rate, derived from <see cref="CanvasPeriod"/>.
    /// </summary>
    /// <remarks>
    /// This is the TARGET, not the achieved rate: achieved fps is a delta of
    /// <see cref="FramesComposited"/> over wall time, which only the caller - who knows how long it has
    /// been since it last looked - can compute. Exposing the target here means a view can show
    /// "29.4 / 29.97" without hardcoding the denominator, and stops each caller re-deriving it.
    /// </remarks>
    public double TargetFramesPerSecond =>
        CanvasPeriod > TimeSpan.Zero ? 1d / CanvasPeriod.TotalSeconds : 0d;

    /// <summary>Per-output rows, empty when the runtime was not asked for them.</summary>
    public IReadOnlyList<ClipCompositionOutputStats> OutputStats => Outputs ?? [];
}

/// <summary>One video output's own throughput, which the composition-wide totals cannot express.</summary>
/// <param name="OutputId">Stable id of the line.</param>
/// <param name="DisplayName">Operator-facing name.</param>
/// <param name="FramesSubmitted">Frames handed to this sink.</param>
/// <param name="FramesRefused">Frames dropped because the output had already retired.</param>
/// <param name="SubmitFailures">Submits that threw - a failing line, visible as a number rather than
/// only as a log line.</param>
/// <param name="IsMapped">Whether this output runs its own mapping stage (warped) or takes the raw canvas.</param>
/// <param name="Width">Configured output width, 0 before the first frame.</param>
/// <param name="Height">Configured output height, 0 before the first frame.</param>
/// <param name="QueuedFrames">Current pump queue depth, when the sink is a pump.</param>
/// <param name="QueueCapacity">Pump queue capacity, when the sink is a pump.</param>
/// <param name="PresentDropped">Frames the DEVICE discarded because the canvas outran its refresh, when
/// the sink reports <see cref="IVideoOutputPresentDiagnostics"/>. This is the only counter that can see
/// the last stage; every other number here can read healthy while this one climbs.</param>
/// <param name="PresentRepeated">Device refreshes that re-showed the previous frame because the canvas
/// was slower than the device - the mirror image of <paramref name="PresentDropped"/>.</param>
public readonly record struct ClipCompositionOutputStats(
    string OutputId,
    string DisplayName,
    long FramesSubmitted,
    long FramesRefused,
    long SubmitFailures,
    bool IsMapped,
    int Width,
    int Height,
    int QueuedFrames,
    int QueueCapacity,
    long PresentDropped = 0,
    long PresentRepeated = 0);

public readonly record struct ClipCompositionDriftWarning(
    string CompositionId,
    string CompositionName,
    long FramesBehindMaster,
    TimeSpan LagFromMaster);

public readonly record struct ClipCompositionPumpPressureWarning(
    string CompositionId,
    string CompositionName,
    string OutputId,
    string OutputName,
    long DroppedSinceLastReport,
    long DroppedTotal);
