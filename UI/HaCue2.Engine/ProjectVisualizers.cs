using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using S.Media.Session;
using S.Media.Visualizer.ProjectM;
using S.Media.Core.Video;

namespace HaCue2.Engine;

internal readonly record struct VisualizerCueInstance(Guid CueId, Guid InstanceId);

/// <summary>
/// The visualizer cues that are running, and the projectM renderers behind them.
/// </summary>
/// <remarks>
/// <para>
/// A visualizer cue is not a clip. It has nothing to decode and nothing to seek — firing it means
/// attaching a renderer to every composition its placements name, and stopping it means taking that
/// renderer away. So it does not go through the session's clip path at all; it goes through
/// <c>SetCompositionVisualizerAsync</c>, which is the framework's own seam for exactly this.
/// </para>
/// <para>
/// <b>One source per composition, not per placement.</b> A cue that puts the same visualizer into
/// three sections of one canvas gets ONE renderer with three placement specs — the framework's
/// <c>ILayerSurfaceVideoSource</c> contract says its surface is created at most once per source, and
/// building one renderer per section crashed projectM in HaPlay before that was understood. Two
/// different compositions do get two renderers: they have separate GL threads and cannot share one.
/// </para>
/// <para>
/// <b>A missing or damaged projectM bundle is reported, never silently blank.</b> Packaged desktop
/// apps carry the native, but a developer checkout or damaged install can still lack it. A cue that
/// appears to fire onto a canvas that then stays black is the worst version of that — the operator
/// has no way to tell it from a mis-authored placement.
/// </para>
/// </remarks>
public sealed class ProjectVisualizers : IAsyncDisposable
{
    // The deployed pack is immutable for the process lifetime. Resolve its recursive tree once rather
    // than scanning 552 presets every time an unqualified visualizer cue fires.
    private static readonly Lazy<string?> DefaultPresetPack = new(ProjectMAssetPaths.DefaultPresetDirectory);

    private sealed record RunningAttachment(
        string CompositionId,
        string VisualizerId,
        ProjectMVisualSource Source,
        IReadOnlyDictionary<Guid, int> PlacementIndexes);

    private sealed record PreparedAttachment(
        string CompositionId,
        string VisualizerId,
        ProjectMVisualSource Source,
        IReadOnlyList<VideoPlacementSpec> VisiblePlacements,
        IReadOnlyDictionary<Guid, int> PlacementIndexes);

    private sealed record PreparedCue(
        VisualizerCueNode Cue, List<PreparedAttachment> Attachments, string? Problem);

    /// <summary>One cue's renderers: a source per composition it is placed on.</summary>
    private readonly Dictionary<Guid, List<RunningAttachment>> _running = [];
    private readonly Dictionary<Guid, Guid> _instances = [];
    private readonly Dictionary<(Guid InstanceId, string Target), Guid> _controllerOwners = [];

    private readonly ShowSession _session;
    private readonly Lock _gate = new();

    public ProjectVisualizers(ShowSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Whether this machine has the native library at all.</summary>
    public static bool IsAvailable => ProjectMModule.IsAvailable;

    /// <summary>Why not, when it is not. Null when it is.</summary>
    public static string? UnavailableReason => ProjectMModule.UnavailableReason;

    /// <summary>Cue ids with a renderer attached — what the host counts as sounding.</summary>
    public IReadOnlyList<Guid> Running
    {
        get
        {
            lock (_gate)
                return [.. _running.Keys];
        }
    }

    /// <summary>
    /// Starts a visualizer cue, or explains why it could not.
    /// </summary>
    /// <returns>Null on success; the reason otherwise.</returns>
    /// <remarks>
    /// Re-firing a cue that is already running replaces it, which is what firing a media cue already
    /// does and what an operator pressing GO twice expects. It also means a preset pack edited mid-show
    /// takes effect on the next fire rather than needing the show restarted.
    /// </remarks>
    public async Task<string?> FireAsync(HaCueProject project, VisualizerCueNode cue)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(cue);

        if (!IsAvailable)
            return $"“{cue.Label}” needs projectM — {UnavailableReason ?? "its native bundle is unavailable"}";

        if (cue.Placements.Count == 0)
            return $"“{cue.Label}” is not placed on any composition";

        await StopAsync(cue.Id).ConfigureAwait(false);

        var attached = new List<RunningAttachment>();
        string? firstFailure = null;
        Func<string, bool>? feedFilter = null;
        if (!cue.FeedAll)
        {
            var allowed = cue.FeedCueIds
                .Concat(project.AllCues().OfType<MediaCueNode>()
                    .Where(media => media.SendToVisualizer)
                    .Select(media => media.Id))
                .Select(id => id.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            feedFilter = allowed.Contains;
        }

        // Grouped by composition: one renderer per canvas, however many sections of it the cue fills.
        foreach (var group in cue.Placements.GroupBy(placement => placement.CompositionId))
        {
            if (project.Compositions.FirstOrDefault(item => item.Id == group.Key) is not { } composition)
            {
                firstFailure ??= $"“{cue.Label}” is placed on a composition that is no longer in this show";
                continue;
            }

            var compositionId = composition.Id.ToString();
            var source = Renderer(cue, composition);

            var orderedPlacements = group
                .OrderBy(placement => placement.LayerIndex)
                .ToList();
            var placements = orderedPlacements
                .Select(placement => Spec(project, cue, compositionId, placement, 0))
                .ToList();

            bool ok;

            try
            {
                ok = await _session.SetCompositionVisualizerAsync(
                    compositionId,
                    source,
                    placements: placements,
                    audioFeedFilter: feedFilter,
                    // The composition is rebuilt whenever its size or rate changes, and every edit
                    // reloads the document. Without this a visualizer would go black on an unrelated
                    // keystroke and stay black.
                    preserveAcrossDocumentReload: true,
                    visualizerId: cue.Id.ToString()).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                source.Dispose();
                firstFailure ??= $"“{cue.Label}” could not start — {failure.Message}";
                continue;
            }

            if (!ok)
            {
                // The usual cause is a composition with no GL surface host: a CPU-fallback compositor
                // cannot render a layer surface at all, and saying "refused" beats a black canvas.
                source.Dispose();
                firstFailure ??= $"“{cue.Label}” was refused by “{composition.Name}” — it has no GL surface";
                continue;
            }

            attached.Add(new RunningAttachment(
                compositionId,
                cue.Id.ToString(),
                source,
                orderedPlacements
                    .Select((placement, index) => (placement.Id, index))
                    .ToDictionary(item => item.Id, item => item.index)));
        }

        if (attached.Count == 0)
            return firstFailure ?? $"“{cue.Label}” started nothing";

        lock (_gate)
        {
            _running[cue.Id] = attached;
            _instances[cue.Id] = Guid.NewGuid();
        }

        // Partial success is still a start — the canvases that came up are showing something — but the
        // ones that did not are worth saying out loud.
        return firstFailure;
    }

    /// <summary>
    /// Attaches renderers behind opacity-zero placements, then reveals them after one caller-owned edge.
    /// Renderer/GL startup is therefore paid during timeline pre-roll rather than after the authored frame.
    /// </summary>
    public async Task<(IReadOnlyList<Guid> Started, IReadOnlyList<string> Problems)> FireScheduledAsync(
        HaCueProject project,
        IReadOnlyList<TimelineVisualizerStart> cues,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(waitForStartEdge);

        var prepared = new List<PreparedCue>();
        var adopted = new HashSet<Guid>();
        try
        {
            foreach (var start in cues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prepared.Add(await PrepareHiddenAsync(
                        project, start.Cue, start.StartPosition, cancellationToken)
                    .ConfigureAwait(false));
            }

            await waitForStartEdge(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var started = new List<Guid>();
            var problems = new List<string>();
            foreach (var item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Problem is { } preparationProblem)
                    problems.Add(preparationProblem);
                if (item.Attachments.Count == 0)
                    continue;

                // A re-fire keeps the old visible renderer alive during preparation. Reveal the warm slot
                // first, then retire the old one, so there is no black composition frame between them.
                var revealed = false;
                foreach (var attachment in item.Attachments)
                {
                    for (var index = 0; index < attachment.VisiblePlacements.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (await _session.UpdateCompositionVisualizerPlacementAsync(
                                attachment.CompositionId,
                                attachment.VisiblePlacements[index],
                                index,
                                attachment.VisualizerId).ConfigureAwait(false))
                        {
                            revealed = true;
                        }
                        else
                        {
                            problems.Add(
                                $"“{item.Cue.Label}” could not reveal a prepared placement on " +
                                $"“{attachment.CompositionId}”");
                        }
                    }
                }

                if (!revealed)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                await StopAsync(item.Cue.Id).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _running[item.Cue.Id] = [.. item.Attachments.Select(attachment =>
                        new RunningAttachment(
                            attachment.CompositionId,
                            attachment.VisualizerId,
                            attachment.Source,
                            attachment.PlacementIndexes))];
                    _instances[item.Cue.Id] = Guid.NewGuid();
                }
                adopted.Add(item.Cue.Id);
                started.Add(item.Cue.Id);
            }

            return (started, problems);
        }
        finally
        {
            foreach (var item in prepared.Where(item => !adopted.Contains(item.Cue.Id)))
                await ReleasePreparedAsync(item.Attachments).ConfigureAwait(false);
        }
    }

    private async Task<PreparedCue> PrepareHiddenAsync(
        HaCueProject project,
        VisualizerCueNode cue,
        TimeSpan startPosition,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return new PreparedCue(cue, [],
                $"“{cue.Label}” needs projectM — {UnavailableReason ?? "its native bundle is unavailable"}");
        if (cue.Placements.Count == 0)
            return new PreparedCue(cue, [], $"“{cue.Label}” is not placed on any composition");

        var attached = new List<PreparedAttachment>();
        string? firstFailure = null;
        Func<string, bool>? feedFilter = null;
        if (!cue.FeedAll)
        {
            var allowed = cue.FeedCueIds
                .Concat(project.AllCues().OfType<MediaCueNode>()
                    .Where(media => media.SendToVisualizer)
                    .Select(media => media.Id))
                .Select(id => id.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            feedFilter = allowed.Contains;
        }
        var preparedId = $"{cue.Id}:scheduled:{Guid.NewGuid():N}";

        try
        {
            foreach (var group in cue.Placements.GroupBy(placement => placement.CompositionId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (project.Compositions.FirstOrDefault(item => item.Id == group.Key) is not { } composition)
                {
                    firstFailure ??= $"“{cue.Label}” is placed on a composition that is no longer in this show";
                    continue;
                }

                var compositionId = composition.Id.ToString();
                var source = Renderer(cue, composition);
                var orderedPlacements = group
                    .OrderBy(placement => placement.LayerIndex)
                    .ToList();
                var visible = orderedPlacements
                    .Select(placement => Spec(
                        project,
                        cue,
                        compositionId,
                        placement,
                        Math.Clamp((long)startPosition.TotalMilliseconds, 0, Math.Max(1, cue.HoldMs))))
                    .ToList();
                var hidden = visible.Select(placement => placement with { Opacity = 0f }).ToList();

                bool ok;
                try
                {
                    ok = await _session.SetCompositionVisualizerAsync(
                        compositionId,
                        source,
                        placements: hidden,
                        audioFeedFilter: feedFilter,
                        preserveAcrossDocumentReload: true,
                        visualizerId: preparedId).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    source.Dispose();
                    firstFailure ??= $"“{cue.Label}” could not prepare — {failure.Message}";
                    continue;
                }

                if (!ok)
                {
                    source.Dispose();
                    firstFailure ??= $"“{cue.Label}” was refused by “{composition.Name}” — it has no GL surface";
                    continue;
                }

                attached.Add(new PreparedAttachment(
                    compositionId,
                    preparedId,
                    source,
                    visible,
                    orderedPlacements
                        .Select((placement, index) => (placement.Id, index))
                        .ToDictionary(item => item.Id, item => item.index)));
            }
        }
        catch
        {
            await ReleasePreparedAsync(attached).ConfigureAwait(false);
            throw;
        }

        return new PreparedCue(
            cue,
            attached,
            attached.Count == 0 ? firstFailure ?? $"“{cue.Label}” prepared nothing" : firstFailure);
    }

    private async Task ReleasePreparedAsync(IReadOnlyList<PreparedAttachment> attachments)
    {
        foreach (var attachment in attachments)
        {
            var removed = false;
            try
            {
                removed = await _session.SetCompositionVisualizerAsync(
                    attachment.CompositionId, null, visualizerId: attachment.VisualizerId).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Cancellation/teardown may already own the dead session slot.
            }

            if (!removed)
                attachment.Source.Dispose();
        }
    }

    /// <summary>Takes one visualizer cue down. Silent when it was not running.</summary>
    public async Task StopAsync(Guid cueId)
    {
        List<RunningAttachment>? attached;

        lock (_gate)
        {
            if (!_running.Remove(cueId, out attached))
                return;
            if (_instances.Remove(cueId, out var instanceId))
                foreach (var key in _controllerOwners.Keys
                             .Where(key => key.InstanceId == instanceId)
                             .ToArray())
                    _controllerOwners.Remove(key);
        }

        foreach (var attachment in attached)
        {
            try
            {
                await _session.SetCompositionVisualizerAsync(
                    attachment.CompositionId, null, visualizerId: attachment.VisualizerId).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A session already torn down, or a composition already gone. The renderer still has to
                // be disposed either way, which is what the loop below is for.
            }

            attachment.Source.Dispose();
        }
    }

    /// <summary>Takes every visualizer down — what PANIC and stop-all mean here.</summary>
    public async Task StopAllAsync()
    {
        List<Guid> running;

        lock (_gate)
            running = [.. _running.Keys];

        foreach (var cueId in running)
            await StopAsync(cueId).ConfigureAwait(false);
    }

    /// <summary>Samples one cue-owned visualizer lane against the already-running surface layer.</summary>
    public async Task<bool> ApplyAutomationAsync(
        VisualizerCueNode cue, AutomationTrack track, double value)
    {
        if (!TryResolvePlacement(cue, track, out var placement))
            return false;

        RunningAttachment? attachment;
        int placementIndex;
        lock (_gate)
        {
            attachment = _running.GetValueOrDefault(cue.Id)?.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.CompositionId,
                    placement.CompositionId.ToString(),
                    StringComparison.Ordinal));
            if (attachment is null
                || !attachment.PlacementIndexes.TryGetValue(placement.Id, out placementIndex))
                return false;
        }

        if (track.Target.PropertyId == AutomationPropertyIds.PlacementOpacity)
            return await _session.ApplyCompositionVisualizerOpacityAutomationAsync(
                    attachment.CompositionId,
                    placementIndex,
                    (float)Math.Clamp(value, 0, 1),
                    attachment.VisualizerId)
                .ConfigureAwait(false);

        if (TryPlacementProperty(track.Target.PropertyId, out var placementProperty))
            return await _session.ApplyCompositionVisualizerPlacementAutomationAsync(
                    attachment.CompositionId,
                    placementIndex,
                    placementProperty,
                    value,
                    attachment.VisualizerId)
                .ConfigureAwait(false);

        if (track.Target.ObjectId is { } effectId
            && TryEffectProperty(track.Target.PropertyId, out var effectProperty))
            return await _session.ApplyCompositionVisualizerEffectAutomationAsync(
                    attachment.CompositionId,
                    placementIndex,
                    effectId.ToString(),
                    effectProperty,
                    value,
                    attachment.VisualizerId)
                .ConfigureAwait(false);

        return false;
    }

    internal VisualizerCueInstance? CaptureInstance(Guid cueId)
    {
        lock (_gate)
            return _running.ContainsKey(cueId) && _instances.TryGetValue(cueId, out var instanceId)
                ? new VisualizerCueInstance(cueId, instanceId)
                : null;
    }

    internal async Task<bool> ApplyControllerAutomationAsync(
        VisualizerCueNode cue,
        AutomationTrack track,
        double value,
        VisualizerCueInstance instance,
        Guid ownerId,
        bool claim)
    {
        if (!TryResolveControllerTarget(cue, track, instance, ownerId, claim,
                out var attachment, out var placementIndex))
            return false;

        if (track.Target.PropertyId == AutomationPropertyIds.PlacementOpacity)
            return await _session.ApplyCompositionVisualizerControllerOpacityAutomationAsync(
                    attachment.CompositionId,
                    placementIndex,
                    (float)Math.Clamp(value, 0, 1),
                    attachment.VisualizerId)
                .ConfigureAwait(false);

        if (TryPlacementProperty(track.Target.PropertyId, out var placementProperty))
            return await _session.ApplyCompositionVisualizerControllerPlacementAutomationAsync(
                    attachment.CompositionId,
                    placementIndex,
                    placementProperty,
                    value,
                    attachment.VisualizerId)
                .ConfigureAwait(false);

        if (track.Target.ObjectId is { } effectId
            && TryEffectProperty(track.Target.PropertyId, out var effectProperty))
            return await _session.ApplyCompositionVisualizerControllerEffectAutomationAsync(
                    attachment.CompositionId,
                    placementIndex,
                    effectId.ToString(),
                    effectProperty,
                    value,
                    attachment.VisualizerId)
                .ConfigureAwait(false);

        return false;
    }

    internal async Task<bool> ClearControllerAutomationAsync(
        VisualizerCueNode cue,
        AutomationTrack track,
        VisualizerCueInstance instance,
        Guid ownerId)
    {
        if (!TryReleaseControllerTarget(cue, track, instance, ownerId,
                out var attachment, out var placementIndex))
            return false;

        if (track.Target.PropertyId == AutomationPropertyIds.PlacementOpacity)
            return await _session.ClearCompositionVisualizerControllerOpacityAutomationAsync(
                    attachment.CompositionId, placementIndex, attachment.VisualizerId)
                .ConfigureAwait(false);

        if (TryPlacementProperty(track.Target.PropertyId, out var placementProperty))
            return await _session.ClearCompositionVisualizerControllerPlacementAutomationAsync(
                    attachment.CompositionId, placementIndex, placementProperty, attachment.VisualizerId)
                .ConfigureAwait(false);

        if (track.Target.ObjectId is { } effectId
            && TryEffectProperty(track.Target.PropertyId, out var effectProperty))
            return await _session.ClearCompositionVisualizerControllerEffectAutomationAsync(
                    attachment.CompositionId,
                    placementIndex,
                    effectId.ToString(),
                    effectProperty,
                    attachment.VisualizerId)
                .ConfigureAwait(false);

        return false;
    }

    internal async Task<bool> ApplyControllerVideoModifierAsync(
        VisualizerCueInstance instance, Guid ownerId, float level, bool claim)
    {
        List<RunningAttachment> attachments;
        lock (_gate)
        {
            if (!_instances.TryGetValue(instance.CueId, out var liveInstance)
                || liveInstance != instance.InstanceId)
                return false;
            var key = (instance.InstanceId, "group:video");
            if (claim)
                _controllerOwners[key] = ownerId;
            if (_controllerOwners.GetValueOrDefault(key) != ownerId)
                return false;
            attachments = [.. _running.GetValueOrDefault(instance.CueId) ?? []];
        }

        var applied = false;
        foreach (var attachment in attachments)
            applied |= await _session.ApplyCompositionVisualizerControllerVideoModifierAsync(
                    attachment.CompositionId, level, attachment.VisualizerId)
                .ConfigureAwait(false);
        return applied;
    }

    internal async Task<bool> ClearControllerVideoModifierAsync(
        VisualizerCueInstance instance, Guid ownerId)
    {
        List<RunningAttachment> attachments;
        lock (_gate)
        {
            if (!_instances.TryGetValue(instance.CueId, out var liveInstance)
                || liveInstance != instance.InstanceId
                || _controllerOwners.GetValueOrDefault((instance.InstanceId, "group:video")) != ownerId)
                return false;
            _controllerOwners.Remove((instance.InstanceId, "group:video"));
            attachments = [.. _running.GetValueOrDefault(instance.CueId) ?? []];
        }

        var cleared = false;
        foreach (var attachment in attachments)
            cleared |= await _session.ApplyCompositionVisualizerControllerVideoModifierAsync(
                    attachment.CompositionId, 1f, attachment.VisualizerId)
                .ConfigureAwait(false);
        return cleared;
    }

    private bool TryResolveControllerTarget(
        VisualizerCueNode cue,
        AutomationTrack track,
        VisualizerCueInstance instance,
        Guid ownerId,
        bool claim,
        out RunningAttachment attachment,
        out int placementIndex)
    {
        attachment = null!;
        placementIndex = -1;
        if (!TryResolvePlacement(cue, track, out var placement))
            return false;
        lock (_gate)
        {
            if (!_instances.TryGetValue(cue.Id, out var liveInstance)
                || liveInstance != instance.InstanceId)
                return false;
            attachment = _running.GetValueOrDefault(cue.Id)?.FirstOrDefault(candidate =>
                string.Equals(candidate.CompositionId, placement.CompositionId.ToString(), StringComparison.Ordinal))!;
            if (attachment is null || !attachment.PlacementIndexes.TryGetValue(placement.Id, out placementIndex))
                return false;
            var key = (instance.InstanceId, ControllerTarget(track));
            if (claim)
                _controllerOwners[key] = ownerId;
            return _controllerOwners.GetValueOrDefault(key) == ownerId;
        }
    }

    private bool TryReleaseControllerTarget(
        VisualizerCueNode cue,
        AutomationTrack track,
        VisualizerCueInstance instance,
        Guid ownerId,
        out RunningAttachment attachment,
        out int placementIndex)
    {
        attachment = null!;
        placementIndex = -1;
        if (!TryResolvePlacement(cue, track, out var placement))
            return false;
        lock (_gate)
        {
            if (!_instances.TryGetValue(cue.Id, out var liveInstance)
                || liveInstance != instance.InstanceId)
                return false;
            attachment = _running.GetValueOrDefault(cue.Id)?.FirstOrDefault(candidate =>
                string.Equals(candidate.CompositionId, placement.CompositionId.ToString(), StringComparison.Ordinal))!;
            if (attachment is null || !attachment.PlacementIndexes.TryGetValue(placement.Id, out placementIndex))
                return false;
            var key = (instance.InstanceId, ControllerTarget(track));
            if (_controllerOwners.GetValueOrDefault(key) != ownerId)
                return false;
            return _controllerOwners.Remove(key);
        }
    }

    private static string ControllerTarget(AutomationTrack track) =>
        $"{track.Target.PropertyId}:{track.Target.ObjectId}";

    private static bool TryResolvePlacement(
        VisualizerCueNode cue, AutomationTrack track, out LayerPlacement placement)
    {
        placement = null!;
        if (track.Target.ObjectId is not { } objectId)
            return false;

        placement = CuePlacements.Of(cue).FirstOrDefault(candidate =>
            candidate.Id == objectId
            || candidate.ChromaKey?.Id == objectId
            || candidate.ColorAdjust?.Id == objectId)!;
        return placement is not null;
    }

    private static bool TryPlacementProperty(string propertyId, out ShowPlacementProperty property)
    {
        property = propertyId switch
        {
            AutomationPropertyIds.PlacementX => ShowPlacementProperty.DestX,
            AutomationPropertyIds.PlacementY => ShowPlacementProperty.DestY,
            AutomationPropertyIds.PlacementWidth => ShowPlacementProperty.DestWidth,
            AutomationPropertyIds.PlacementHeight => ShowPlacementProperty.DestHeight,
            AutomationPropertyIds.PlacementRotation => ShowPlacementProperty.RotationDegrees,
            _ => (ShowPlacementProperty)(-1),
        };
        return (int)property >= 0;
    }

    private static bool TryEffectProperty(string propertyId, out ShowPlacementEffectProperty property)
    {
        property = propertyId switch
        {
            AutomationPropertyIds.ChromaSimilarity => ShowPlacementEffectProperty.ChromaSimilarity,
            AutomationPropertyIds.ChromaSmoothness => ShowPlacementEffectProperty.ChromaSmoothness,
            AutomationPropertyIds.ChromaSpillReduction => ShowPlacementEffectProperty.ChromaSpillReduction,
            AutomationPropertyIds.ColorBrightness => ShowPlacementEffectProperty.ColorBrightness,
            AutomationPropertyIds.ColorContrast => ShowPlacementEffectProperty.ColorContrast,
            _ => (ShowPlacementEffectProperty)(-1),
        };
        return (int)property >= 0;
    }

    /// <summary>
    /// The renderer for one cue on one composition.
    /// </summary>
    /// <remarks>
    /// Sized and paced to the COMPOSITION rather than to a fixed 1080p60: the canvas is what the
    /// surface is composited onto, so rendering larger only costs GPU time and rendering smaller is
    /// visible. The document deliberately carries no render size of its own for the same reason.
    /// </remarks>
    private static ProjectMVisualSource Renderer(VisualizerCueNode cue, CompositionDefinition composition)
    {
        var width = composition.Width > 0 ? composition.Width : 1920;
        var height = composition.Height > 0 ? composition.Height : 1080;
        var frameRate = composition.ExactFrameRate;
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            frameRate = new Rational(60, 1);

        return new ProjectMVisualSource(
            width,
            height,
            frameRate,
            new ProjectMOptions
            {
                // A blank pack means the repository's deployed, pinned Milkdrop bundle — not the
                // projectM idle preset. An authored path still wins so a show can carry its own pack.
                PresetDirectory = cue.PresetPack.Length > 0
                    ? cue.PresetPack
                    : DefaultPresetPack.Value,
                RenderWidth = width,
                RenderHeight = height,
                Fps = Math.Max(1, (int)Math.Round(frameRate.ToDouble())),
                // A locked preset is one that never advances. projectM has no lock of its own, so it is
                // expressed as a hold longer than any show — which is also honest about what it is.
                PresetDurationSeconds = cue.LockPreset
                    ? TimeSpan.FromDays(1).TotalSeconds
                    : Math.Max(5, cue.HoldMs / 1000d),
                // Shuffling a locked preset would pick a different one each time the cue fired, which
                // is the opposite of what locking it means.
                Shuffle = !cue.LockPreset,
                TransitionSeconds = Math.Clamp(cue.BlendMs / 1000d, 0, 30),
            });
    }

    /// <summary>One placement as the session's spec. The same fractions the media placements use.</summary>
    private static VideoPlacementSpec Spec(
        HaCueProject project,
        VisualizerCueNode cue,
        string compositionId,
        LayerPlacement placement,
        long timeMs)
    {
        var appearance = ShowCompiler.VideoPlacement(placement);
        foreach (var track in cue.AutomationTracks.Where(track =>
                     track.Enabled
                     && AutomationPropertyCatalog.Get(track.Target.PropertyId)
                         is { Domain: not AutomationDomain.External }
                     && (track.Target.ObjectId == placement.Id
                         || track.Target.ObjectId == placement.ChromaKey?.Id
                         || track.Target.ObjectId == placement.ColorAdjust?.Id)))
        {
            var value = AutomationEvaluator.Sample(
                track, project, timeMs, AuthoredValue(placement, track.Target.PropertyId));
            appearance = track.Target.PropertyId switch
            {
                AutomationPropertyIds.PlacementOpacity => appearance with { Opacity = value },
                AutomationPropertyIds.PlacementX => appearance with { DestX = value },
                AutomationPropertyIds.PlacementY => appearance with { DestY = value },
                AutomationPropertyIds.PlacementWidth => appearance with { DestWidth = value },
                AutomationPropertyIds.PlacementHeight => appearance with { DestHeight = value },
                AutomationPropertyIds.PlacementRotation => appearance with { RotationDegrees = value },
                AutomationPropertyIds.ChromaSimilarity when appearance.ChromaKey is { } chroma =>
                    appearance with { ChromaKey = chroma with { Similarity = (float)value } },
                AutomationPropertyIds.ChromaSmoothness when appearance.ChromaKey is { } chroma =>
                    appearance with { ChromaKey = chroma with { Smoothness = (float)value } },
                AutomationPropertyIds.ChromaSpillReduction when appearance.ChromaKey is { } chroma =>
                    appearance with { ChromaKey = chroma with { SpillSuppression = (float)value } },
                AutomationPropertyIds.ColorBrightness when appearance.ColorAdjust is { } color =>
                    appearance with { ColorAdjust = color with { Brightness = (float)value } },
                AutomationPropertyIds.ColorContrast when appearance.ColorAdjust is { } color =>
                    appearance with { ColorAdjust = color with { Contrast = (float)value } },
                _ => appearance,
            };
        }

        return new VideoPlacementSpec(
            compositionId,
            placement.LayerIndex,
            Opacity: appearance.Opacity,
            Placement: appearance.Fit,
            DestX: appearance.DestX,
            DestY: appearance.DestY,
            DestWidth: appearance.DestWidth,
            DestHeight: appearance.DestHeight,
            CropLeft: appearance.CropLeft,
            CropTop: appearance.CropTop,
            CropRight: appearance.CropRight,
            CropBottom: appearance.CropBottom,
            RotationDegrees: appearance.RotationDegrees,
            VideoFx: appearance.VideoFx,
            ChromaKey: appearance.ChromaKey,
            ColorAdjust: appearance.ColorAdjust,
            ChromaKeyInstanceId: appearance.ChromaKeyInstanceId,
            ColorAdjustInstanceId: appearance.ColorAdjustInstanceId);
    }

    private static double AuthoredValue(LayerPlacement placement, string propertyId) => propertyId switch
    {
        AutomationPropertyIds.PlacementOpacity => placement.Opacity,
        AutomationPropertyIds.PlacementX => placement.X,
        AutomationPropertyIds.PlacementY => placement.Y,
        AutomationPropertyIds.PlacementWidth => placement.Width,
        AutomationPropertyIds.PlacementHeight => placement.Height,
        AutomationPropertyIds.PlacementRotation => placement.RotationDegrees,
        AutomationPropertyIds.ChromaSimilarity => placement.ChromaKey?.Similarity ?? .4,
        AutomationPropertyIds.ChromaSmoothness => placement.ChromaKey?.Smoothness ?? .1,
        AutomationPropertyIds.ChromaSpillReduction => placement.ChromaKey?.SpillReduction ?? .1,
        AutomationPropertyIds.ColorBrightness => placement.ColorAdjust?.Brightness ?? 0,
        AutomationPropertyIds.ColorContrast => placement.ColorAdjust?.Contrast ?? 1,
        _ => 0,
    };

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}
