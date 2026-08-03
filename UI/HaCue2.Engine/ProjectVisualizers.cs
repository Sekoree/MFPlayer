using HaCue2.Core.Model;
using S.Media.Session;
using S.Media.Visualizer.ProjectM;
using S.Media.Core.Video;

namespace HaCue2.Engine;

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
/// <b>A machine without projectM is reported, never silently blank.</b> The visualizer is a native
/// library that a booth box may simply not have, and a cue that appears to fire onto a canvas that
/// then stays black is the worst version of that — the operator has no way to tell it from a
/// mis-authored placement.
/// </para>
/// </remarks>
public sealed class ProjectVisualizers : IAsyncDisposable
{
    /// <summary>One cue's renderers: a source per composition it is placed on.</summary>
    private readonly Dictionary<Guid, List<(string CompositionId, ProjectMVisualSource Source)>> _running = [];

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
            return $"“{cue.Label}” needs projectM — {UnavailableReason ?? "it is not installed"}";

        if (cue.Placements.Count == 0)
            return $"“{cue.Label}” is not placed on any composition";

        await StopAsync(cue.Id).ConfigureAwait(false);

        var attached = new List<(string, ProjectMVisualSource)>();
        string? firstFailure = null;

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

            var placements = group
                .OrderBy(placement => placement.LayerIndex)
                .Select(placement => Spec(compositionId, placement))
                .ToList();

            bool ok;

            try
            {
                ok = await _session.SetCompositionVisualizerAsync(
                    compositionId,
                    source,
                    placements: placements,
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

            attached.Add((compositionId, source));
        }

        if (attached.Count == 0)
            return firstFailure ?? $"“{cue.Label}” started nothing";

        lock (_gate)
            _running[cue.Id] = attached;

        // Partial success is still a start — the canvases that came up are showing something — but the
        // ones that did not are worth saying out loud.
        return firstFailure;
    }

    /// <summary>Takes one visualizer cue down. Silent when it was not running.</summary>
    public async Task StopAsync(Guid cueId)
    {
        List<(string CompositionId, ProjectMVisualSource Source)>? attached;

        lock (_gate)
        {
            if (!_running.Remove(cueId, out attached))
                return;
        }

        foreach (var (compositionId, source) in attached)
        {
            try
            {
                await _session.SetCompositionVisualizerAsync(
                    compositionId, null, visualizerId: cueId.ToString()).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A session already torn down, or a composition already gone. The renderer still has to
                // be disposed either way, which is what the loop below is for.
            }

            source.Dispose();
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
        var fps = composition.FramesPerSecond > 0 ? composition.FramesPerSecond : 60;

        return new ProjectMVisualSource(
            width,
            height,
            new Rational((int)Math.Round(fps * 1000), 1000),
            new ProjectMOptions
            {
                PresetDirectory = cue.PresetPack.Length > 0 ? cue.PresetPack : null,
                RenderWidth = width,
                RenderHeight = height,
                Fps = (int)Math.Round(fps),
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
    private static VideoPlacementSpec Spec(string compositionId, LayerPlacement placement) =>
        new(
            compositionId,
            placement.LayerIndex,
            Opacity: placement.Opacity,
            Placement: placement.Fit switch
            {
                LayerFit.Cover => "Cover",
                LayerFit.Stretch => "Stretch",
                _ => "Contain",
            },
            DestX: placement.X,
            DestY: placement.Y,
            DestWidth: placement.Width,
            DestHeight: placement.Height);

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}
