using S.Media.Core.Buses;
using S.Media.Core.Diagnostics;
using S.Media.Compositor;

namespace S.Media.Session;

/// <summary>
/// Owns the per-composition visualizer slots for a <see cref="ShowSession"/>: attach/replace,
/// per-placement hot updates, fade snapshots, reload retention and persistent reattachment
/// (extracted from ShowSession per review P2-6 - one runtime responsibility, one owner).
/// </summary>
/// <remarks>
/// <para><strong>Dispatcher-confined.</strong> Every method must run on the owning session's
/// dispatcher; the session's public API provides the serialization (InvokeAsync) and the fade
/// pacing policy - this type only holds the slot state and its lifecycle rules.</para>
/// <para>One slot owns a LIST of surface layers because a visualizer cue can place the same source
/// into several sections of one canvas (#26 multi-placement); they attach, fade and detach
/// together, sharing one audio tap and one metadata registration.</para>
/// <para><strong>ONE surface, N layers.</strong> <see cref="ILayerSurfaceVideoSource.CreateLayerSurface"/>
/// is called AT MOST ONCE per source (its contract; calling it per placement created several GL
/// surfaces bound to one native renderer and crashed projectM). The single surface is added to the
/// composition once per placement - the compositor keys ConfigureGl by surface instance and renders it
/// once per layer - so every placement shows the same render. Exactly ONE layer owns the surface's
/// disposal; the rest are non-owning (see <see cref="ClipCompositionRuntime.AddSurfaceLayer"/>).</para>
/// </remarks>
internal sealed class ShowSessionVisualizerService
{
    internal sealed record Layer(
        ClipCompositionRuntime.SurfaceLayerSlot Slot,
        VideoPlacementSpec Placement);

    internal sealed record Slot(
        IReadOnlyList<Layer> Layers,
        Guid TapId,
        IAudioVisualSource Source,
        bool DisposeSource,
        bool PreserveAcrossDocumentReload);

    /// <summary>Fade snapshot: slot identity makes the final detach safe when a new visualizer is
    /// fired onto the same composition while the old one is fading.</summary>
    internal sealed record FadeCapture(SlotKey Key, Slot Captured, IReadOnlyList<float> StartFadeLevels);

    internal sealed record Reattachment(SlotKey Key, Slot Captured, ClipCompositionRuntime Replacement);

    /// <summary>
    /// Identifies one visualizer: a composition plus the id of whatever attached it.
    /// </summary>
    /// <remarks>
    /// Keyed by composition ALONE until now, which meant attaching a second visualizer silently replaced
    /// the first - so "the visualizer is an ordinary layer like any other" was not true, and two
    /// visualizer cues could not coexist on one canvas. The visualizer id is the caller's (a cue id, in
    /// practice); <see cref="DefaultVisualizerId"/> preserves the historical single-slot behaviour for
    /// callers that do not care.
    /// </remarks>
    internal readonly record struct SlotKey(string CompositionId, string VisualizerId);

    /// <summary>Id used when a caller attaches without naming its visualizer - the single-slot case.</summary>
    public const string DefaultVisualizerId = "default";

    /// <summary>
    /// How many visualizers one composition may host before it is worth warning about.
    /// </summary>
    /// <remarks>
    /// A SOFT cap (owner decision): attaching more still works, because a legitimate heavy rig should not
    /// hit a wall the framework invented. But each visualizer is a projectM renderer sharing that
    /// composition's single GL thread, so the ceiling is real - and an operator should learn about it from
    /// a log line while building the show, not from dropped frames during a get-in.
    /// </remarks>
    public const int SoftVisualizerLimitPerComposition = 4;

    private readonly Dictionary<SlotKey, Slot> _slots = [];
    private readonly Func<IAudioVisualSource, Func<string, bool>?, Guid> _registerTap;
    private readonly Action<Guid> _detachTapFromActiveClips;
    private readonly Action<Guid> _releaseTapRegistration;
    private readonly BusMetadataHub _metadataHub;

    /// <param name="registerTap">Creates + attaches the visualizer's audio tap (session-owned tap
    /// list) and returns its id.</param>
    /// <param name="detachTapFromActiveClips">Removes the tap's routes from currently-playing clips.</param>
    /// <param name="releaseTapRegistration">Removes the tap from the session's registration list and
    /// disposes its cached rate adapters.</param>
    public ShowSessionVisualizerService(
        Func<IAudioVisualSource, Func<string, bool>?, Guid> registerTap,
        Action<Guid> detachTapFromActiveClips,
        Action<Guid> releaseTapRegistration,
        BusMetadataHub metadataHub)
    {
        _registerTap = registerTap;
        _detachTapFromActiveClips = detachTapFromActiveClips;
        _releaseTapRegistration = releaseTapRegistration;
        _metadataHub = metadataHub;
    }

    /// <summary>True when the composition has ANY visualizer attached.</summary>
    public bool Has(string compositionId) =>
        _slots.Keys.Any(k => string.Equals(k.CompositionId, compositionId, StringComparison.Ordinal));

    /// <summary>True when this specific visualizer is attached.</summary>
    public bool Has(string compositionId, string visualizerId) =>
        _slots.ContainsKey(new SlotKey(compositionId, visualizerId));

    /// <summary>Removes and fully tears down EVERY visualizer on the composition (no-op when none).</summary>
    public void Remove(string compositionId)
    {
        foreach (var key in _slots.Keys
                     .Where(k => string.Equals(k.CompositionId, compositionId, StringComparison.Ordinal))
                     .ToList())
        {
            if (_slots.Remove(key, out var removed))
                DisposeSlot(removed);
        }
    }

    /// <summary>Removes one visualizer, leaving any others on the same composition running.</summary>
    public void Remove(string compositionId, string visualizerId)
    {
        if (_slots.Remove(new SlotKey(compositionId, visualizerId), out var removed))
            DisposeSlot(removed);
    }

    /// <summary>
    /// Attaches <paramref name="source"/> as one surface layer per placement, replacing any existing
    /// visualizer on the composition. The replacement is STAGED first: a renderer/surface creation
    /// failure must not tear down the currently-live visualizer.
    /// </summary>
    public void Attach(
        string compositionId,
        string visualizerId,
        ClipCompositionRuntime composition,
        Compositor.ILayerSurfaceVideoSource surfaceSource,
        IAudioVisualSource source,
        IReadOnlyList<VideoPlacementSpec> placements,
        bool disposeSourceOnRemove,
        Func<string, bool>? audioFeedFilter,
        bool preserveAcrossDocumentReload)
    {
        // ONE surface for the whole slot (contract + projectM crash fix); the FIRST layer owns its
        // disposal, every additional placement reuses the same instance non-owningly.
        var surface = surfaceSource.CreateLayerSurface();
        var stagedLayers = new List<Layer>(placements.Count);
        try
        {
            for (var i = 0; i < placements.Count; i++)
                stagedLayers.Add(new Layer(
                    composition.AddSurfaceLayer(surface, placements[i], ownsSurface: i == 0), placements[i]));
        }
        catch
        {
            DisposeStagedLayers(stagedLayers, surface);
            throw;
        }
        composition.EnsurePumpStarted();

        // Replaces only the SAME visualizer id; other visualizers on this composition keep running.
        var key = new SlotKey(compositionId, visualizerId);
        if (_slots.Remove(key, out var existing))
            DisposeSlot(ReferenceEquals(existing.Source, source)
                ? existing with { DisposeSource = false }
                : existing);

        var liveOnComposition = _slots.Keys.Count(k =>
            string.Equals(k.CompositionId, compositionId, StringComparison.Ordinal)) + 1;
        if (liveOnComposition > SoftVisualizerLimitPerComposition)
        {
            MediaDiagnostics.LogWarning(
                "ShowSession: composition '{0}' now hosts {1} visualizers (soft limit {2}). Each one is a "
                + "renderer on this composition's single GL thread, so expect the canvas to fall behind "
                + "its frame rate.",
                compositionId, liveOnComposition, SoftVisualizerLimitPerComposition);
        }

        var tapId = _registerTap(source, audioFeedFilter);
        if (source is IBusMetadataSink sink)
            _metadataHub.Attach(sink);

        _slots[key] = new Slot(
            stagedLayers, tapId, source, disposeSourceOnRemove, preserveAcrossDocumentReload);
    }

    /// <summary>Hot-updates one surface layer's placement; false when the composition has no
    /// visualizer or the index is out of range (see ShowSession.UpdateCompositionVisualizerPlacementAsync).</summary>
    public bool UpdatePlacement(
        string compositionId, string visualizerId, VideoPlacementSpec placement, int placementIndex)
    {
        var key = new SlotKey(compositionId, visualizerId);
        if (!_slots.TryGetValue(key, out var slot)
            || placementIndex < 0 || placementIndex >= slot.Layers.Count)
            return false;
        var layer = slot.Layers[placementIndex];
        layer.Slot.UpdatePlacement(placement);
        var layers = slot.Layers.ToArray();
        layers[placementIndex] = layer with { Placement = placement };
        _slots[key] = slot with { Layers = layers };
        return true;
    }

    /// <summary>Applies absolute opacity automation to one visualizer placement without changing its
    /// authored opacity or either of the independent fade/modifier components.</summary>
    public bool ApplyOpacityAutomation(
        string compositionId, string visualizerId, int placementIndex, float level)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.SetAutomationLevel(level, absolute: true);
        return true;
    }

    public bool ApplyControllerOpacityAutomation(
        string compositionId, string visualizerId, int placementIndex, float level)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.SetControllerAutomationLevel(level, absolute: true);
        return true;
    }

    public bool ClearControllerOpacityAutomation(
        string compositionId, string visualizerId, int placementIndex)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.ClearControllerAutomationLevel();
        return true;
    }

    public bool ApplyControllerVideoModifier(
        string compositionId, string visualizerId, float level)
    {
        if (!_slots.TryGetValue(new SlotKey(compositionId, visualizerId), out var slot))
            return false;
        foreach (var layer in slot.Layers)
            layer.Slot.ModifierLevel = Math.Clamp(level, 0f, 1f);
        return true;
    }

    public bool ApplyPlacementAutomation(
        string compositionId,
        string visualizerId,
        int placementIndex,
        ShowPlacementProperty property,
        double value)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.SetPlacementAutomation(property, value);
        return true;
    }

    public bool ClearPlacementAutomation(
        string compositionId,
        string visualizerId,
        int placementIndex,
        ShowPlacementProperty property)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.ClearPlacementAutomation(property);
        return true;
    }

    public bool ApplyControllerPlacementAutomation(
        string compositionId,
        string visualizerId,
        int placementIndex,
        ShowPlacementProperty property,
        double value)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.SetControllerPlacementAutomation(property, value);
        return true;
    }

    public bool ClearControllerPlacementAutomation(
        string compositionId,
        string visualizerId,
        int placementIndex,
        ShowPlacementProperty property)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.ClearControllerPlacementAutomation(property);
        return true;
    }

    public bool ApplyEffectAutomation(
        string compositionId,
        string visualizerId,
        int placementIndex,
        string effectInstanceId,
        ShowPlacementEffectProperty property,
        double value)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.SetEffectAutomation(effectInstanceId, property, value);
        return true;
    }

    public bool ClearEffectAutomation(
        string compositionId,
        string visualizerId,
        int placementIndex,
        string effectInstanceId,
        ShowPlacementEffectProperty property)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.ClearEffectAutomation(effectInstanceId, property);
        return true;
    }

    public bool ApplyControllerEffectAutomation(
        string compositionId,
        string visualizerId,
        int placementIndex,
        string effectInstanceId,
        ShowPlacementEffectProperty property,
        double value)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.SetControllerEffectAutomation(effectInstanceId, property, value);
        return true;
    }

    public bool ClearControllerEffectAutomation(
        string compositionId,
        string visualizerId,
        int placementIndex,
        string effectInstanceId,
        ShowPlacementEffectProperty property)
    {
        if (!TryLayer(compositionId, visualizerId, placementIndex, out var layer))
            return false;
        layer.ClearControllerEffectAutomation(effectInstanceId, property);
        return true;
    }

    private bool TryLayer(
        string compositionId,
        string visualizerId,
        int placementIndex,
        out ClipCompositionRuntime.IPlacedClipLayer layer)
    {
        if (_slots.TryGetValue(new SlotKey(compositionId, visualizerId), out var slot)
            && placementIndex >= 0
            && placementIndex < slot.Layers.Count)
        {
            layer = slot.Layers[placementIndex].Slot;
            return true;
        }

        layer = null!;
        return false;
    }

    /// <summary>Snapshots the slots (all, or one composition) for a fade: identities + start opacities.</summary>
    public IReadOnlyList<FadeCapture> CaptureForFade(string? compositionId) =>
        _slots
            .Where(pair => compositionId is null
                           || string.Equals(pair.Key.CompositionId, compositionId, StringComparison.Ordinal))
            .Select(pair => new FadeCapture(
                pair.Key, pair.Value,
                pair.Value.Layers.Select(l => l.Slot.FadeLevel).ToArray()))
            .ToArray();

    /// <summary>Applies one fade level to every captured slot that is still the live one. Returns
    /// false when nothing applied (every captured slot was replaced mid-fade).</summary>
    public bool ApplyFadeLevel(IReadOnlyList<FadeCapture> fades, float level)
    {
        var applied = false;
        foreach (var fade in fades)
        {
            if (!_slots.TryGetValue(fade.Key, out var current)
                || !ReferenceEquals(current, fade.Captured))
                continue;
            for (var i = 0; i < fade.Captured.Layers.Count; i++)
                fade.Captured.Layers[i].Slot.FadeLevel = fade.StartFadeLevels[i] * level;
            applied = true;
        }

        return applied;
    }

    /// <summary>Detaches the faded slots - only those whose identity still matches, so a replacement
    /// fired during the fade is never torn down.</summary>
    public void FinalizeFade(IReadOnlyList<FadeCapture> fades)
    {
        foreach (var fade in fades)
        {
            if (!_slots.TryGetValue(fade.Key, out var current)
                || !ReferenceEquals(current, fade.Captured))
                continue;
            _slots.Remove(fade.Key);
            DisposeSlot(fade.Captured);
        }
    }

    /// <summary>Session-teardown clear: taps unregister and sources dispose; the surface layers
    /// themselves die with their owning compositions (the caller disposes those next).</summary>
    public void Clear()
    {
        foreach (var slot in _slots.Values)
            DisposeAuxiliaries(slot);

        _slots.Clear();
    }

    /// <summary>Reload-time cleanup that SPARES preserved compositions. Slots on a preserved
    /// composition are left intact; a persistent slot on a rebuilt composition keeps its durable
    /// parts (source/tap/filter) and is returned for reattachment after the composition map commits;
    /// every other slot gets the historical full-reload teardown.</summary>
    public List<Reattachment> RetainForPreservedCompositionsOnly(
        HashSet<string> preservedIds,
        IReadOnlyDictionary<string, ClipCompositionRuntime> replacementCompositions)
    {
        var reattachments = new List<Reattachment>();
        foreach (var (key, slot) in _slots
                     .Where(kv => !preservedIds.Contains(kv.Key.CompositionId)).ToList())
        {
            if (slot.PreserveAcrossDocumentReload
                && replacementCompositions.TryGetValue(key.CompositionId, out var replacement)
                && replacement.SupportsSurfaceLayers
                && slot.Source is Compositor.ILayerSurfaceVideoSource)
            {
                reattachments.Add(new Reattachment(key, slot, replacement));
                continue;
            }

            DisposeAuxiliaries(slot);
            _slots.Remove(key);
        }

        return reattachments;
    }

    /// <summary>Recreates every persistent slot's surface layers on its replacement composition; a
    /// failed slot is fully torn down (auxiliaries included) rather than left half-attached.</summary>
    public void ReattachPersistent(IReadOnlyList<Reattachment> reattachments)
    {
        foreach (var pending in reattachments)
        {
            var recreated = new List<Layer>(pending.Captured.Layers.Count);
            IVideoCompositorLayerSurface? surface = null;
            try
            {
                // One surface for every placement, exactly like Attach - the first layer owns it.
                var surfaceSource = (Compositor.ILayerSurfaceVideoSource)pending.Captured.Source;
                surface = surfaceSource.CreateLayerSurface();
                for (var i = 0; i < pending.Captured.Layers.Count; i++)
                {
                    var placement = pending.Captured.Layers[i].Placement;
                    recreated.Add(new Layer(
                        pending.Replacement.AddSurfaceLayer(surface, placement, ownsSurface: i == 0), placement));
                }
                pending.Replacement.EnsurePumpStarted();
                _slots[pending.Key] = pending.Captured with { Layers = recreated };
            }
            catch (Exception ex)
            {
                DisposeStagedLayers(recreated, surface);
                _slots.Remove(pending.Key);
                DisposeAuxiliaries(pending.Captured);
                MediaDiagnostics.LogWarning(
                    "ShowSession: persistent visualizer could not reattach to rebuilt composition '{0}' ({1}).",
                    pending.Key.CompositionId, ex.Message);
            }
        }
    }

    /// <summary>Detaches one live visualizer: surface layers, active-clip tap routes, then the
    /// auxiliary registrations. The dictionary entry is removed by the caller FIRST so
    /// replacement/fade identity checks cannot tear down a newer source on the same composition.</summary>
    private void DisposeSlot(Slot slot)
    {
        DisposeLayers(slot.Layers);
        _detachTapFromActiveClips(slot.TapId);
        DisposeAuxiliaries(slot);
    }

    /// <summary>Disposes the shared-surface layers NON-OWNERS FIRST so the compositor never renders a
    /// still-referenced layer whose surface was already torn down by the owning layer.</summary>
    private static void DisposeLayers(IReadOnlyList<Layer> layers)
    {
        for (var i = layers.Count - 1; i >= 1; i--)
            MediaDiagnostics.SwallowDisposeErrors(layers[i].Slot.Dispose, "ShowSession: visualizer layer");
        if (layers.Count > 0)
            MediaDiagnostics.SwallowDisposeErrors(layers[0].Slot.Dispose, "ShowSession: visualizer layer");
    }

    /// <summary>Rollback for a partially-staged attach/reattach: dispose whatever layers were added
    /// (owner last), then the surface if no owning layer took responsibility for it yet.</summary>
    private static void DisposeStagedLayers(IReadOnlyList<Layer> staged, IVideoCompositorLayerSurface? surface)
    {
        DisposeLayers(staged);
        // The owning layer (index 0) disposes the surface; if it was never added, dispose it here.
        if (staged.Count == 0 && surface is not null)
            MediaDiagnostics.SwallowDisposeErrors(surface.Dispose, "ShowSession: staged visualizer surface");
    }

    private void DisposeAuxiliaries(Slot slot)
    {
        _releaseTapRegistration(slot.TapId);
        if (slot.Source is IBusMetadataSink sink)
            _metadataHub.Detach(sink);
        if (slot.DisposeSource && slot.Source is IDisposable disposable)
            MediaDiagnostics.SwallowDisposeErrors(disposable.Dispose, "ShowSession: visualizer source");
    }
}
