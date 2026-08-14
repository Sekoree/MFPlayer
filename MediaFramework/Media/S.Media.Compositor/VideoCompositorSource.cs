using S.Media.Core.Video;

namespace S.Media.Compositor;

/// <summary>
/// Combines N video inputs into one output stream via an <see cref="IVideoCompositor"/>.
/// Each input is an <see cref="IVideoOutput"/> slot the upstream router targets; the single output
/// is an <see cref="IVideoSource"/> a downstream player or router pulls from.
/// </summary>
/// <remarks>
/// <para>
/// Same source/output-duality pattern as <see cref="Audio.AudioBus"/>, but **per slot**: each slot
/// holds the most-recent frame that has been promoted into the composition and keeps reusing it
/// until a newer submitted frame replaces it. When the downstream consumer calls
/// <see cref="TryReadNextFrame"/>, the output snapshots every slot's current frame, builds a
/// <see cref="CompositorLayer"/> list (slot order = back-to-front), calls
/// <see cref="IVideoCompositor.Composite"/>, and returns the composed frame.
/// </para>
/// <para>
/// <strong>Timestamp sampling</strong>: master-aligned slots retain a small PTS-ordered lookahead and
/// expose the newest frame not after the canvas target. Latest-wins slots promote the newest pending
/// frame. This lets a fixed-rate canvas sample irregular/VFR input without first collapsing it to one
/// nominal-rate pending slot.
/// </para>
/// <para>
/// <strong>Per-slot mutable state</strong>: <see cref="Slot.Opacity"/>, <see cref="Slot.Transform"/>,
/// and <see cref="Slot.BlendMode"/> are read each Composite call so a higher-level animator can
/// drive transitions by setting them from a timeline (see
/// <see cref="LayerOpacityTween"/>).
/// </para>
/// <para>
/// <strong>Threading</strong>: <see cref="IVideoOutput.Submit"/> on a slot may run on any thread
/// (typically the upstream player's clock thread). <see cref="TryReadNextFrame"/> runs on the
/// downstream consumer's thread. For a <c>GlVideoCompositor</c>, the downstream consumer's thread
/// must be the GL context owner.
/// </para>
/// </remarks>
public sealed class VideoCompositorSource : IVideoSource, IDisposable
{
    private readonly IVideoCompositor _compositor;
    private readonly bool _disposeCompositorOnDispose;
    private readonly VideoFormat _output;
    private readonly PixelFormat[] _native;
    private readonly TimeSpan _ptsStep;
    private readonly Lock _slotsGate = new();
    private readonly List<Slot> _slots = [];
    // Reusable scratch for the single-consumer read path (serialized by _readGate) so a steady-state
    // composite allocates nothing: the slot snapshot, the per-slot composite layers, and the slots to
    // release once Composite has read them.
    private readonly Lock _readGate = new();
    private readonly List<Slot> _snapshotScratch = [];
    private readonly List<CompositorLayer> _layerScratch = [];
    private readonly List<Slot> _acquiredScratch = [];
    // Surface layers (NXT-10): rendered by the compositor on top of the frame layers when it is an
    // IVideoCompositorSurfaceHost. Guarded by _slotsGate like _slots; snapshotted per composite.
    private readonly List<SurfaceSlot> _surfaceSlots = [];
    private readonly List<SurfaceSlot> _surfaceSnapshotScratch = [];
    private readonly List<CompositorSurfaceLayer> _surfaceScratch = [];
    private TimeSpan _nextPts = TimeSpan.Zero;
    private long _compositesEmitted;
    private bool _disposed;

    /// <param name="output">Output format. Pixel format must be one the compositor accepts on its **output** - both shipping compositors output BGRA32.</param>
    /// <param name="compositor">The compositor that does the actual blending. Lifecycle: owned by the output when <paramref name="disposeCompositorOnDispose"/> is <c>true</c>.</param>
    /// <param name="disposeCompositorOnDispose">When <c>true</c> (default), <see cref="Dispose"/> also disposes the compositor.</param>
    public VideoCompositorSource(
        VideoFormat output,
        IVideoCompositor compositor,
        bool disposeCompositorOnDispose = true,
        bool pipelinedSingleOutputReadback = true)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        if (compositor.OutputFormat != output)
            compositor.Configure(output);

        _compositor = compositor;
        _disposeCompositorOnDispose = disposeCompositorOnDispose;
        // Callers choose whether throughput or latency wins: async GPU readback avoids a stall but
        // returns frame N-1, while the synchronous path avoids that full-frame pipeline delay.
        if (compositor is IPipelinedReadbackVideoCompositor pipelined)
            pipelined.PipelinedSingleOutputReadback = pipelinedSingleOutputReadback;
        _output = output;
        _native = [output.PixelFormat];
        _ptsStep = DerivePeriod(output.FrameRate);
    }

    public VideoFormat Format => _output;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;
    public bool IsExhausted => _disposed;

    /// <summary>
    /// F-14 preflight: ids of currently-registered layer/surface effects the backing compositor does
    /// NOT render (see <see cref="IEffectCapabilityVideoCompositor"/>) - on the CPU fallback these
    /// degrade to pass-through. Empty when every active effect has an implementation on this backend.
    /// Cheap (walks the slot lists once); intended for output-health snapshots, not per-frame calls.
    /// </summary>
    public IReadOnlyList<string> CollectUnsupportedEffectIds()
    {
        // Fast path: compositors without the capability interface support everything.
        if (_compositor is not IEffectCapabilityVideoCompositor caps)
            return [];

        List<string>? ids = null;
        lock (_slotsGate)
        {
            foreach (var slot in _slots)
                Accumulate(slot.Effects, caps, ref ids);
            foreach (var surface in _surfaceSlots)
                Accumulate(surface.Effects, caps, ref ids);
        }

        return ids is null ? [] : ids;

        static void Accumulate(
            IReadOnlyList<VideoLayerEffect>? effects,
            IEffectCapabilityVideoCompositor caps,
            ref List<string>? ids)
        {
            if (effects is not { Count: > 0 })
                return;
            foreach (var effect in effects)
            {
                if (caps.SupportsEffect(effect.Descriptor))
                    continue;
                ids ??= [];
                if (!ids.Contains(effect.Descriptor.Id))
                    ids.Add(effect.Descriptor.Id);
            }
        }
    }

    /// <summary>Cumulative number of composite frames emitted from <see cref="TryReadNextFrame"/>.</summary>
    public long CompositesEmitted => Volatile.Read(ref _compositesEmitted);

    /// <summary>The time a slot's <see cref="SlotKeepPolicy.MasterAligned"/> selection is judged
    /// against: the slot's own clock when it has one (layers from independent transports live in
    /// different PTS domains - see <see cref="Slot.AlignmentClock"/>), the canvas master otherwise.
    /// A clock that throws mid-teardown falls back to the canvas master for this composite.</summary>
    private static TimeSpan? SlotAlignmentTime(
        Slot slot,
        TimeSpan? canvasAlignmentTime,
        TimeSpan? outputPresentationTime)
    {
        if (slot.AlignmentTimeMapper is { } mapper && outputPresentationTime is { } outputTime)
        {
            try
            {
                return mapper(outputTime) + slot.AlignmentTimeOffset;
            }
            catch
            {
                return canvasAlignmentTime + slot.AlignmentTimeOffset;
            }
        }
        if (slot.AlignmentClock is not { } own)
            return canvasAlignmentTime + slot.AlignmentTimeOffset;
        try
        {
            return own() is { } ownTime
                ? ownTime + slot.AlignmentTimeOffset
                : canvasAlignmentTime + slot.AlignmentTimeOffset;
        }
        catch
        {
            return canvasAlignmentTime + slot.AlignmentTimeOffset;
        }
    }

    /// <summary>Snapshot of all slots in insertion order (back-to-front for the compositor).</summary>
    public IReadOnlyList<Slot> Slots
    {
        get
        {
            lock (_slotsGate)
                return _slots.ToArray();
        }
    }

    /// <summary>
    /// Adds an input slot. The returned <see cref="Slot"/> exposes the <see cref="IVideoOutput"/>
    /// upstream code should target (<see cref="Slot.Output"/>) plus mutable per-slot
    /// <see cref="Slot.Opacity"/> / <see cref="Slot.Transform"/> / <see cref="Slot.BlendMode"/>.
    /// </summary>
    /// <param name="id">Optional stable id for diagnostics; defaults to <c>slot_1</c>, <c>slot_2</c>, …</param>
    /// <param name="acceptedFormats">Override the slot's <see cref="IVideoOutput.AcceptedPixelFormats"/>. Defaults to the compositor's <see cref="IVideoCompositor.AcceptedLayerPixelFormats"/>.</param>
    public Slot AddSlot(string? id = null, IReadOnlyList<PixelFormat>? acceptedFormats = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_slotsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A monotonic ordinal, NOT `count + 1`: slots are removed as layers end, so a count-based
            // default reissues an id a surviving slot still holds - remove slot_2 of four and the next
            // add is another "slot_4". On a live composition that collision aborted a whole batch fire
            // ("slot id 'slot_3' is already registered"), and the staircase fallback then skipped cues.
            var slotId = id ?? $"slot_{++_nextSlotOrdinal}";
            foreach (var s in _slots)
            {
                if (s.Id == slotId)
                    throw new ArgumentException($"slot id '{slotId}' is already registered", nameof(id));
            }
            var slot = new Slot(slotId, acceptedFormats ?? _compositor.AcceptedLayerPixelFormats);
            _slots.Add(slot);
            return slot;
        }
    }

    /// <summary>Ever-increasing default-id ordinals, so a released default id is never reissued.</summary>
    private long _nextSlotOrdinal;

    private long _nextSurfaceOrdinal;

    /// <summary>Whether the compositor behind this mixer can host surface layers (NXT-10).</summary>
    public bool SupportsSurfaceLayers => _compositor is IVideoCompositorSurfaceHost;

    /// <summary>Whether any surface slot is currently registered (integrated multi-warp callers must
    /// route through the plain composite path while surfaces are present).</summary>
    public bool HasSurfaceSlots
    {
        get
        {
            lock (_slotsGate)
                return _surfaceSlots.Count > 0;
        }
    }

    /// <summary>
    /// Adds a surface layer (NXT-10): the compositor renders <paramref name="surface"/> directly into the
    /// canvas ON TOP of every frame layer, in surface-slot list order. The returned slot carries the
    /// mutable placement (<see cref="SurfaceSlot.Transform"/>/<see cref="SurfaceSlot.Opacity"/>); the
    /// SURFACE's lifetime stays with the caller (removing the slot does not dispose it). Throws when the
    /// compositor is not an <see cref="IVideoCompositorSurfaceHost"/> - gate on
    /// <see cref="SupportsSurfaceLayers"/> and fall back to the source's frame path.
    /// </summary>
    public SurfaceSlot AddSurfaceSlot(IVideoCompositorLayerSurface surface, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_compositor is not IVideoCompositorSurfaceHost)
            throw new InvalidOperationException(
                $"compositor '{_compositor.GetType().Name}' cannot host surface layers - check SupportsSurfaceLayers and use the source's frame path instead.");
        lock (_slotsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Monotonic for the same reason as AddSlot: a count-based default collides after removals.
            var slotId = id ?? $"surface_{++_nextSurfaceOrdinal}";
            foreach (var s in _surfaceSlots)
            {
                if (s.Id == slotId)
                    throw new ArgumentException($"surface slot id '{slotId}' is already registered", nameof(id));
            }
            var slot = new SurfaceSlot(slotId, surface);
            _surfaceSlots.Add(slot);
            return slot;
        }
    }

    /// <summary>Removes a surface slot and waits for any in-flight composite that snapshotted it. The
    /// surface itself is NOT disposed (caller-owned), so it is safe for the caller to dispose it after
    /// this method returns.</summary>
    public bool RemoveSurfaceSlot(SurfaceSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_readGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_slotsGate)
                return _surfaceSlots.Remove(slot);
        }
    }

    /// <summary>Reorders surface slots in-place - the compositor's on-top draw order.</summary>
    public void SortSurfaceSlots(Comparison<SurfaceSlot> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_slotsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _surfaceSlots.Sort(comparison);
        }
    }

    /// <summary>
    /// Reorders slots in-place. The list order is the compositor's back-to-front draw order.
    /// </summary>
    public void SortSlots(Comparison<Slot> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_slotsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _slots.Sort(comparison);
        }
    }

    /// <summary>Removes a slot and disposes any frame it was holding.</summary>
    public bool RemoveSlot(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Slot? toDispose = null;
        lock (_slotsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Id != id) continue;
                toDispose = _slots[i];
                _slots.RemoveAt(i);
                break;
            }
        }

        if (toDispose is null) return false;
        toDispose.Close();
        return true;
    }

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != _output.PixelFormat)
            throw new InvalidOperationException(
                $"VideoCompositorSource only delivers {_output.PixelFormat}; consumer requested {format}.");
    }

    public bool TryReadNextFrame(out VideoFrame frame) =>
        TryReadNextFrame(masterAlignmentTime: null, out frame);

    public bool TryReadNextFrames(
        TimeSpan? masterAlignmentTime,
        IReadOnlyList<WarpOutputRequest> outputs,
        out IReadOnlyList<VideoFrame> frames) =>
        TryReadNextFrames(masterAlignmentTime, masterAlignmentTime, outputs, out frames);

    /// <summary>Multi-output form with independent selection and output timestamp coordinates.</summary>
    public bool TryReadNextFrames(
        TimeSpan? canvasAlignmentTime,
        TimeSpan? outputPresentationTime,
        IReadOnlyList<WarpOutputRequest> outputs,
        out IReadOnlyList<VideoFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (_compositor is not IWarpPassVideoCompositor warpCompositor)
            throw new InvalidOperationException("The configured compositor does not support multi-output warp composition.");

        // Surface layers don't participate in the integrated multi-warp pass (v1 scope) - report
        // "not handled" so the caller falls back to the plain composite path, where
        // CompositeWithSurfaces renders them (per-lease chained mapping still applies there).
        lock (_slotsGate)
        {
            if (_surfaceSlots.Count > 0)
            {
                frames = Array.Empty<VideoFrame>();
                return false;
            }
        }

        // Single-consumer read (class contract): serialize so the reused scratch below is exclusive.
        lock (_readGate)
        {
            if (_disposed)
            {
                frames = Array.Empty<VideoFrame>();
                return false;
            }

            // Snapshot slot refs under _slotsGate (brief - keeps AddSlot/RemoveSlot from contending
            // with the whole composite), then acquire each slot's held frame outside it. AddRange from
            // an ICollection copies without per-element/enumerator alloc once the scratch is warm.
            _snapshotScratch.Clear();
            lock (_slotsGate)
                _snapshotScratch.AddRange(_slots);

            _layerScratch.Clear();
            _acquiredScratch.Clear();
            try
            {
                foreach (var slot in _snapshotScratch)
                {
                    // Acquire holds a read-ref on the slot's frame (the slot won't dispose it until we
                    // ReleaseFrame below). Track the slot, not a per-call lease object, to release it.
                    var f = slot.KeepPolicy == SlotKeepPolicy.MasterAligned
                            && SlotAlignmentTime(slot, canvasAlignmentTime, outputPresentationTime) is { } masterPts
                        ? slot.AcquireMasterAlignedFrame(masterPts, _ptsStep)
                        : slot.AcquireLatestFrame();
                    if (f is null) continue;
                    _acquiredScratch.Add(slot);
                    AddSlotLayers(slot, f);
                }

                // When the caller drives composition from a master timeline (the declarative
                // VideoCompositor), stamp the composite with that master time so downstream players
                // align to a real clock rather than a synthetic free-running counter. For read-paced
                // scaler/preset paths (OutputPresetVideoSource), propagate the background layer's
                // source PTS so shared-demux seeks stay aligned with audio.
                TimeSpan pts;
                if (outputPresentationTime is { } outputPts)
                {
                    pts = outputPts;
                }
                else if (_layerScratch.Count > 0)
                {
                    pts = _layerScratch[0].Frame.PresentationTime;
                    _nextPts = pts + _ptsStep;
                }
                else
                {
                    pts = _nextPts;
                    _nextPts += _ptsStep;
                }

                frames = warpCompositor.CompositeMulti(_layerScratch, outputs, pts);
                Interlocked.Increment(ref _compositesEmitted);
                return true;
            }
            finally
            {
                foreach (var slot in _acquiredScratch)
                    slot.ReleaseFrame();
                _acquiredScratch.Clear();
                _layerScratch.Clear();
                _snapshotScratch.Clear();
            }
        }
    }

    /// <param name="masterAlignmentTime">When set, slots with
    /// <see cref="Slot.KeepPolicy"/> = <see cref="SlotKeepPolicy.MasterAligned"/> pick the
    /// newest frame whose PTS is not after this position.</param>
    public bool TryReadNextFrame(TimeSpan? masterAlignmentTime, out VideoFrame frame) =>
        TryReadNextFrame(
            masterAlignmentTime,
            outputPresentationTime: masterAlignmentTime,
            defaultSurfaceRenderTime: null,
            out frame);

    /// <summary>
    /// Composites one frame while keeping the canvas selection coordinate, canonical output timestamp,
    /// and GPU-surface render coordinate independent.
    /// </summary>
    public bool TryReadNextFrame(
        TimeSpan? canvasAlignmentTime,
        TimeSpan? outputPresentationTime,
        TimeSpan? defaultSurfaceRenderTime,
        out VideoFrame frame)
    {
        // Single-consumer read (class contract): serialize so the reused scratch below is exclusive.
        lock (_readGate)
        {
            if (_disposed)
            {
                frame = null!;
                return false;
            }

            // Snapshot slot refs under _slotsGate (brief - keeps AddSlot/RemoveSlot from contending
            // with the whole composite), then acquire each slot's held frame outside it. AddRange from
            // an ICollection copies without per-element/enumerator alloc once the scratch is warm.
            _snapshotScratch.Clear();
            lock (_slotsGate)
                _snapshotScratch.AddRange(_slots);

            _layerScratch.Clear();
            _acquiredScratch.Clear();
            try
            {
                foreach (var slot in _snapshotScratch)
                {
                    // Acquire holds a read-ref on the slot's frame (the slot won't dispose it until we
                    // ReleaseFrame below). Track the slot, not a per-call lease object, to release it.
                    var f = slot.KeepPolicy == SlotKeepPolicy.MasterAligned
                            && SlotAlignmentTime(slot, canvasAlignmentTime, outputPresentationTime) is { } masterPts
                        ? slot.AcquireMasterAlignedFrame(masterPts, _ptsStep)
                        : slot.AcquireLatestFrame();
                    if (f is null) continue;
                    _acquiredScratch.Add(slot);
                    AddSlotLayers(slot, f);
                }

                // When the caller drives composition from a master timeline (the declarative
                // VideoCompositor), stamp the composite with that master time so downstream players
                // align to a real clock rather than a synthetic free-running counter. For read-paced
                // scaler/preset paths (OutputPresetVideoSource), propagate the background layer's
                // source PTS so shared-demux seeks stay aligned with audio.
                TimeSpan pts;
                if (outputPresentationTime is { } outputPts)
                {
                    pts = outputPts;
                }
                else if (_layerScratch.Count > 0)
                {
                    pts = _layerScratch[0].Frame.PresentationTime;
                    _nextPts = pts + _ptsStep;
                }
                else
                {
                    pts = _nextPts;
                    _nextPts += _ptsStep;
                }

                // Surface layers (NXT-10) ride the same composite: snapshot under _slotsGate, then let
                // the surface-hosting compositor render them on top of the frame layers. The slot REFS
                // are copied under the gate (same brief-hold pattern the frame slots use above) and the
                // placements read outside it, because a slot may carry a caller-supplied
                // RenderTimeSource delegate - arbitrary code must never run under the mixer's slot lock.
                _surfaceScratch.Clear();
                if (_compositor is IVideoCompositorSurfaceHost surfaceHost)
                {
                    _surfaceSnapshotScratch.Clear();
                    lock (_slotsGate)
                        _surfaceSnapshotScratch.AddRange(_surfaceSlots);

                    foreach (var s in _surfaceSnapshotScratch)
                    {
                        var placed = s.Placement;
                        if (placed.Opacity <= 0f)
                            continue;
                        // Detached surface (a crossfade tail): render at its OWN clip time instead of
                        // this composite's master time. Unset on every ordinary placement, which then
                        // reaches the host exactly as before. A source that throws (its clip is being
                        // torn down under us) falls back to the master time rather than faulting the pump.
                        if (s.RenderTimeSource is { } renderTimeSource)
                        {
                            try { placed = placed with { RenderTime = renderTimeSource() + s.RenderTimeOffset }; }
                            catch { /* fall back to the composite's master time */ }
                        }
                        else if (defaultSurfaceRenderTime is { } surfaceTime)
                        {
                            placed = placed with { RenderTime = surfaceTime + s.RenderTimeOffset };
                        }
                        _surfaceScratch.Add(placed);
                    }

                    if (_surfaceScratch.Count > 0)
                    {
                        frame = surfaceHost.CompositeWithSurfaces(_layerScratch, _surfaceScratch, pts);
                        Interlocked.Increment(ref _compositesEmitted);
                        return true;
                    }
                }

                frame = _compositor.Composite(_layerScratch, pts);
                Interlocked.Increment(ref _compositesEmitted);
                return true;
            }
            finally
            {
                foreach (var slot in _acquiredScratch)
                    slot.ReleaseFrame();
                _acquiredScratch.Clear();
                _layerScratch.Clear();
                _snapshotScratch.Clear();
                _surfaceScratch.Clear();
                _surfaceSnapshotScratch.Clear();
            }
        }
    }

    public void Dispose()
    {
        lock (_readGate)
        {
            if (_disposed) return;
            _disposed = true;
            Slot[] toClose;
            lock (_slotsGate)
            {
                toClose = _slots.ToArray();
                _slots.Clear();
                _surfaceSlots.Clear();
            }
            foreach (var s in toClose)
                s.Close();
            if (_disposeCompositorOnDispose)
                _compositor.Dispose();
        }
    }

    private static TimeSpan DerivePeriod(Rational frameRate)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            return TimeSpan.FromMilliseconds(33);
        return TimeSpan.FromSeconds((double)frameRate.Denominator / frameRate.Numerator);
    }

    private void AddSlotLayers(Slot slot, VideoFrame frame)
    {
        var opacity = slot.Opacity;
        var blendMode = slot.BlendMode;
        var effects = slot.Effects;
        var mapping = slot.MappingSections;
        if (mapping is not null)
        {
            foreach (var section in mapping)
            {
                _layerScratch.Add(new CompositorLayer(
                    frame,
                    section.Transform,
                    opacity * section.Opacity,
                    blendMode)
                {
                    SourceCrop = section.SourceCrop,
                    Mesh = section.Mesh,
                    Effects = effects,
                });
            }

            return;
        }

        _layerScratch.Add(new CompositorLayer(frame, slot.Transform, opacity, blendMode)
        {
            SourceCrop = slot.SourceCrop,
            Effects = effects,
        });
    }

    /// <summary>One input slot - combines an <see cref="IVideoOutput"/> target with mutable composite parameters.</summary>
    /// <summary>
    /// One surface layer registered on the mixer (NXT-10): the GL-rendering surface plus its mutable
    /// placement. Thread-safe like <see cref="Slot"/> - placement writes come from control threads while
    /// the composite thread snapshots. Removing the slot never disposes the surface (caller-owned).
    /// </summary>
    public sealed class SurfaceSlot
    {
        private readonly Lock _gate = new();
        private LayerTransform2D _transform = LayerTransform2D.Identity;
        private float _opacity = 1f;
        private IReadOnlyList<VideoLayerEffect>? _effects;
        private IReadOnlyList<WarpSection>? _mappingSections;

        internal SurfaceSlot(string id, IVideoCompositorLayerSurface surface)
        {
            Id = id;
            Surface = surface;
        }

        public string Id { get; }

        public IVideoCompositorLayerSurface Surface { get; }

        /// <summary>Maps the surface's canvas-sized output into the destination canvas (pixels).</summary>
        public LayerTransform2D Transform
        {
            get { lock (_gate) return _transform; }
            set { lock (_gate) _transform = value; }
        }

        public float Opacity
        {
            get { lock (_gate) return _opacity; }
            set { lock (_gate) _opacity = Math.Clamp(value, 0f, 1f); }
        }

        /// <summary>Optional per-layer effect chain (e.g. chroma key) applied to the surface's
        /// rendered output. Null = none (the surface paints the canvas directly). Live-editable
        /// like <see cref="Opacity"/>; swap the whole list to change parameters.</summary>
        public IReadOnlyList<VideoLayerEffect>? Effects
        {
            get { lock (_gate) return _effects; }
            set { lock (_gate) _effects = value; }
        }

        /// <summary>
        /// Optional per-slot section mapping (media-layer parity): each section samples the
        /// surface's full-canvas render and places/warps it independently; <see cref="Transform"/>
        /// is ignored while set. Null keeps the direct single-transform path.
        /// </summary>
        public IReadOnlyList<WarpSection>? MappingSections
        {
            get { lock (_gate) return _mappingSections; }
            set { lock (_gate) _mappingSections = value; }
        }

        /// <summary>
        /// Optional clock this surface renders on INSTEAD of the composition's master time - the
        /// surface-layer counterpart of <see cref="Slot.KeepPolicy"/> = <see cref="SlotKeepPolicy.Latest"/>
        /// on a frame slot. Null (default) = the composite's master/presentation time, byte for byte the
        /// historical behavior; non-null is sampled once per composite and delivered as the placement's
        /// <see cref="CompositorSurfaceLayer.RenderTime"/>. A delegate that throws is treated as unset for
        /// that composite. Set it for a layer whose source no longer follows the canvas clock - a
        /// dual-voice crossfade's outgoing tail, which must keep rendering ITS clip while the composition's
        /// master time has already moved to the incoming clip.
        /// <para>Threading: a lock-free plain property (matching <see cref="Slot.KeepPolicy"/>) - written
        /// from a control thread, read once per composite by the read thread; reference assignment is
        /// atomic and the composite either sees the old delegate or the new one.</para>
        /// </summary>
        public Func<TimeSpan>? RenderTimeSource { get; set; }

        /// <summary>Manual content-time adjustment applied after the default or per-surface clock is read.</summary>
        public TimeSpan RenderTimeOffset { get; set; }

        /// <summary>
        /// How many frame layers render underneath this surface; null = on top of all of them.
        /// </summary>
        /// <remarks>
        /// Set by whoever owns the composition's z-order (it is the only party that knows how the frame
        /// slots and surface slots interleave). Lock-free for the same reason as
        /// <see cref="RenderTimeSource"/>: written from a control thread, read once per composite, and an
        /// int assignment is atomic - a composite either sees the old placement or the new one, never a
        /// torn z.
        /// </remarks>
        public int? DrawAfterFrameLayers { get; set; }

        /// <summary>Atomic placement snapshot for the composite thread. <see cref="RenderTimeSource"/> is
        /// deliberately NOT sampled here - the mixer resolves it outside every lock.</summary>
        internal CompositorSurfaceLayer Placement
        {
            get
            {
                lock (_gate)
                    return new CompositorSurfaceLayer(Surface, _transform, _opacity, _effects, _mappingSections)
                    {
                        DrawAfterFrameLayers = DrawAfterFrameLayers,
                    };
            }
        }
    }

    public sealed class Slot
    {
        private readonly Lock _gate = new();
        private readonly SlotOutput _sink;
        private VideoFrame? _current;
        // Timestamp-ordered lookahead. A fixed-rate canvas samples this buffer at its exact target time;
        // it must not collapse an irregular/VFR source to one pending frame before that sample occurs.
        private readonly List<VideoFrame> _pending = new(16);
        private const int PendingCapacity = 32;
        private VideoFrame? _abandonedCurrent;
        private long _overflowFrames;
        private long _samplingSkippedFrames;
        private long _samplingRepeatedFrames;
        private int _activeReaders;
        private float _opacity = 1f;
        private LayerTransform2D _transform = LayerTransform2D.Identity;
        private BlendMode _blendMode = BlendMode.SourceOver;
        private RectNormalized _sourceCrop = RectNormalized.Full;
        private IReadOnlyList<WarpSection>? _mappingSections;
        private IReadOnlyList<VideoLayerEffect>? _effects;
        private bool _closed;

        internal Slot(string id, IReadOnlyList<PixelFormat> accepted)
        {
            Id = id;
            _sink = new SlotOutput(this, accepted);
        }

        /// <summary>Stable id for diagnostics.</summary>
        public string Id { get; }

        /// <summary>The <see cref="IVideoOutput"/> upstream code targets.</summary>
        public IVideoOutput Output => _sink;

        /// <summary>Per-layer alpha multiplier in [0, 1]. Animator-friendly; read on every Composite.</summary>
        public float Opacity
        {
            get { lock (_gate) return _opacity; }
            set { lock (_gate) _opacity = value; }
        }

        /// <summary>Source-to-destination affine. Animator-friendly; read on every Composite.</summary>
        public LayerTransform2D Transform
        {
            get { lock (_gate) return _transform; }
            set { lock (_gate) _transform = value; }
        }

        /// <summary>Blend mode applied when this slot's frame is drawn.</summary>
        public BlendMode BlendMode
        {
            get { lock (_gate) return _blendMode; }
            set { lock (_gate) _blendMode = value; }
        }

        /// <summary>Normalized source crop applied before compositing. Animator-friendly; read on every Composite.</summary>
        public RectNormalized SourceCrop
        {
            get { lock (_gate) return _sourceCrop; }
            set { lock (_gate) _sourceCrop = value; }
        }

        /// <summary>Optional per-layer effect chain (e.g. chroma key) applied to this slot's
        /// layer(s). Null = none. Live-editable; read on every Composite like <see cref="Opacity"/>.
        /// Swap the whole list to change parameters (instances are immutable).</summary>
        public IReadOnlyList<VideoLayerEffect>? Effects
        {
            get { lock (_gate) return _effects; }
            set { lock (_gate) _effects = value; }
        }

        /// <summary>
        /// Optional per-slot section mapping. When present, each section becomes one compositor layer
        /// sampling this slot's current frame; null keeps the historical single-layer path.
        /// </summary>
        public IReadOnlyList<WarpSection>? MappingSections
        {
            get { lock (_gate) return _mappingSections; }
            set { lock (_gate) _mappingSections = value; }
        }

        /// <summary>Which submitted frame is exposed at composite time. Default
        /// <see cref="SlotKeepPolicy.Latest"/>.</summary>
        public SlotKeepPolicy KeepPolicy { get; set; } = SlotKeepPolicy.Latest;

        /// <summary>
        /// This slot's OWN alignment clock for <see cref="SlotKeepPolicy.MasterAligned"/>, or null to
        /// use the canvas-wide master time. Read once per composite, like <see cref="KeepPolicy"/>.
        /// </summary>
        /// <remarks>
        /// A canvas can host layers from INDEPENDENT transports whose frame timestamps live in
        /// different time domains (two clips with different trims composite together every day).
        /// Judging every slot against the one canvas master aligned only the clock-owning layer; the
        /// other rode the too-old/too-future fallbacks, churning its pending frames - a steady
        /// trickle of slot overflow and a visible single-frame judder that grew as the domains
        /// drifted apart. Each layer aligned against its own transport's source time is the fix.
        /// </remarks>
        public Func<TimeSpan?>? AlignmentClock { get; set; }

        /// <summary>
        /// Preferred per-slot mapping from canonical output/master time to this source's PTS domain.
        /// Unlike <see cref="AlignmentClock"/>, this samples the exact composition-grid target rather than
        /// a slightly later wall-clock snapshot.
        /// </summary>
        public Func<TimeSpan, TimeSpan>? AlignmentTimeMapper { get; set; }

        /// <summary>Manual content-time adjustment applied after the slot's alignment mapping.</summary>
        public TimeSpan AlignmentTimeOffset { get; set; }

        /// <summary>Frames replaced before the compositor could read them.</summary>
        public long OverflowFrames => Volatile.Read(ref _overflowFrames);

        /// <summary>Valid source samples intentionally passed over while resampling onto the canvas grid.</summary>
        public long SamplingSkippedFrames => Volatile.Read(ref _samplingSkippedFrames);

        /// <summary>Canvas samples that re-used the previous source frame because no newer frame was due
        /// yet. Expected and steady when the source rate is below the canvas rate; for a rate-matched
        /// source a nonzero rate here is a cadence slip (each one pairs with a later
        /// <see cref="SamplingSkippedFrames"/> increment and is seen as a stutter).</summary>
        public long SamplingRepeatedFrames => Volatile.Read(ref _samplingRepeatedFrames);

        internal void SubmitFromOutput(VideoFrame frame)
        {
            // A closed slot drops silently (frame disposed, ownership honored): a player tick is
            // routinely in flight while a cue's slots are torn down, and throwing here only
            // produced per-tick error spam upstream - no submitter can react to it anyway.
            VideoFrame? toDispose = null;
            var closed = false;
            lock (_gate)
            {
                if (_closed)
                {
                    closed = true;
                    toDispose = frame;
                }
                else
                {
                    var index = _pending.BinarySearch(frame, PresentationTimeComparer.Instance);
                    if (index < 0)
                        index = ~index;
                    else
                    {
                        // Equal PTS: the most recently submitted frame wins.
                        while (index < _pending.Count
                               && _pending[index].PresentationTime == frame.PresentationTime)
                            index++;
                    }
                    _pending.Insert(index, frame);

                    if (_pending.Count > PendingCapacity)
                    {
                        // Keep the freshest bounded window. During a delayed tick the player can hand off
                        // a burst of due VFR timestamps; retaining the oldest 32 would force visible
                        // catch-up instead of allowing the canvas to sample the newest eligible frame.
                        const int dropIndex = 0;
                        toDispose = _pending[dropIndex];
                        _pending.RemoveAt(dropIndex);
                    }
                }
            }
            if (toDispose is not null)
            {
                if (!closed)
                    Interlocked.Increment(ref _overflowFrames);
                toDispose.Dispose();
            }
        }

        /// <summary>Returns the slot's latest held frame and registers an active read-ref on it (the
        /// slot won't dispose it until <see cref="ReleaseFrame"/>). Returns null (no ref taken) when the
        /// slot is closed or empty.</summary>
        internal VideoFrame? AcquireLatestFrame()
        {
            VideoFrame? currentToDispose = null;
            List<VideoFrame>? supersededToDispose = null;
            VideoFrame? frame;
            lock (_gate)
            {
                if (_closed)
                    return null;

                if (_pending.Count > 0 && _activeReaders == 0)
                {
                    var newest = _pending[^1];
                    var unsampled = _pending.Count - 1;
                    currentToDispose = _current;
                    if (unsampled > 0)
                    {
                        supersededToDispose = new List<VideoFrame>(unsampled);
                        for (var i = 0; i < _pending.Count - 1; i++)
                            supersededToDispose.Add(_pending[i]);
                    }
                    _current = newest;
                    _pending.Clear();
                    if (unsampled > 0)
                        Interlocked.Add(ref _overflowFrames, unsampled);
                }

                frame = _current;
                if (frame is null)
                    return null;
                _activeReaders++;
            }

            currentToDispose?.Dispose();
            DisposeFrames(supersededToDispose);
            return frame;
        }

        /// <summary>Picks the newest held frame whose PTS is not after <paramref name="masterPts"/>. The
        /// first frame may be accepted up to one canvas period early to avoid a blank start. Registers an active
        /// read-ref (release via <see cref="ReleaseFrame"/>) when it returns non-null.</summary>
        internal VideoFrame? AcquireMasterAlignedFrame(TimeSpan masterPts, TimeSpan canvasPeriod)
        {
            VideoFrame? currentToDispose = null;
            List<VideoFrame>? skippedToDispose = null;
            VideoFrame? frame;
            lock (_gate)
            {
                if (_closed)
                    return null;

                frame = ChooseMasterAlignedFrame(
                    masterPts, canvasPeriod, ref currentToDispose, ref skippedToDispose);
                if (frame is null)
                    return null;
                _activeReaders++;
            }

            currentToDispose?.Dispose();
            DisposeFrames(skippedToDispose);
            return frame;
        }

        internal void AbandonQueuedFrames()
        {
            List<VideoFrame> pendingToDispose;
            VideoFrame? currentToDispose = null;
            lock (_gate)
            {
                pendingToDispose = [.. _pending];
                _pending.Clear();
                if (_activeReaders == 0)
                {
                    currentToDispose = _current;
                    _current = null;
                }
                else if (_current is not null)
                {
                    _abandonedCurrent = _current;
                    _current = null;
                }
            }

            DisposeFrames(pendingToDispose);
            currentToDispose?.Dispose();
        }

        internal bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_pending.Count == 0 && _activeReaders == 0)
                        return true;
                }

                if (Environment.TickCount64 >= deadline)
                    return false;

                Thread.Sleep(1);
            }
        }

        private VideoFrame? ChooseMasterAlignedFrame(
            TimeSpan masterPts,
            TimeSpan canvasPeriod,
            ref VideoFrame? currentToDispose,
            ref List<VideoFrame>? skippedToDispose)
        {
            // The canvas is a sampler: hold the newest source frame whose PTS is not after the exact
            // target. A small future allowance is used only to avoid a blank first frame at start-up.
            VideoFrame? best = _current is { PresentationTime: var currentPts } && currentPts <= masterPts
                ? _current
                : null;
            var selectedPending = -1;
            for (var i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].PresentationTime > masterPts)
                    break;
                best = _pending[i];
                selectedPending = i;
            }

            if (best is null && _current is not null)
                best = _current;
            if (best is null && _pending.Count > 0
                && _pending[0].PresentationTime <= masterPts + canvasPeriod)
            {
                best = _pending[0];
                selectedPending = 0;
            }

            if (selectedPending < 0)
            {
                // Nothing newer was due: the canvas re-samples the frame it already showed. Counted so
                // a rate-matched layer's cadence slips are visible (see SamplingRepeatedFrames).
                if (best is not null && ReferenceEquals(best, _current))
                    Interlocked.Increment(ref _samplingRepeatedFrames);
                return best;
            }
            if (_activeReaders != 0)
            {
                Interlocked.Increment(ref _samplingRepeatedFrames);
                return _current;
            }

            currentToDispose = _current;
            if (selectedPending > 0)
            {
                skippedToDispose = new List<VideoFrame>(selectedPending);
                for (var i = 0; i < selectedPending; i++)
                    skippedToDispose.Add(_pending[i]);
            }
            _current = _pending[selectedPending];
            _pending.RemoveRange(0, selectedPending + 1);
            if (selectedPending > 0)
                Interlocked.Add(ref _samplingSkippedFrames, selectedPending);
            best = _current;
            return best;
        }

        internal void Close()
        {
            VideoFrame? currentToDispose = null;
            VideoFrame? abandonedToDispose = null;
            List<VideoFrame> pendingToDispose;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                pendingToDispose = [.. _pending];
                _pending.Clear();
                if (_activeReaders == 0)
                {
                    currentToDispose = _current;
                    _current = null;
                    abandonedToDispose = _abandonedCurrent;
                    _abandonedCurrent = null;
                }
            }
            DisposeFrames(pendingToDispose);
            currentToDispose?.Dispose();
            abandonedToDispose?.Dispose();
        }

        private static void DisposeFrames(IEnumerable<VideoFrame>? frames)
        {
            if (frames is null)
                return;
            foreach (var frame in frames)
                frame.Dispose();
        }

        private sealed class PresentationTimeComparer : IComparer<VideoFrame>
        {
            public static PresentationTimeComparer Instance { get; } = new();
            public int Compare(VideoFrame? x, VideoFrame? y) =>
                Nullable.Compare(x?.PresentationTime, y?.PresentationTime);
        }

        /// <summary>Releases one read-ref taken by <see cref="AcquireLatestFrame"/> /
        /// <see cref="AcquireMasterAlignedFrame"/>. Must be called exactly once per non-null acquire.</summary>
        internal void ReleaseFrame()
        {
            VideoFrame? toDispose = null;
            VideoFrame? abandonedToDispose = null;
            lock (_gate)
            {
                if (_activeReaders > 0)
                    _activeReaders--;
                if (_activeReaders == 0)
                {
                    abandonedToDispose = _abandonedCurrent;
                    _abandonedCurrent = null;
                    if (_closed)
                    {
                        toDispose = _current;
                        _current = null;
                    }
                }
            }
            toDispose?.Dispose();
            abandonedToDispose?.Dispose();
        }

        private sealed class SlotOutput(Slot owner, IReadOnlyList<PixelFormat> accepted) :
            INonBlockingVideoOutput, IVideoOutputQueueControl
        {
            private VideoFormat _format;
            public VideoFormat Format => _format;
            public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = accepted;

            public void Configure(VideoFormat format)
            {
                var ap = AcceptedPixelFormats;
                if (ap.Count > 0 && !ContainsFormat(ap, format.PixelFormat))
                    throw new ArgumentException(
                        $"slot '{owner.Id}' does not accept pixel format {format.PixelFormat}; " +
                        $"accepted: {string.Join(", ", ap)}",
                        nameof(format));
                _format = format;
            }

            public void Submit(VideoFrame frame)
            {
                ArgumentNullException.ThrowIfNull(frame);
                owner.SubmitFromOutput(frame);
            }

            public void AbandonQueuedFrames() => owner.AbandonQueuedFrames();

            public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default) =>
                owner.WaitForIdle(timeout, cancellationToken);

            private static bool ContainsFormat(IReadOnlyList<PixelFormat> list, PixelFormat pf)
            {
                for (var i = 0; i < list.Count; i++)
                    if (list[i] == pf) return true;
                return false;
            }
        }
    }
}
