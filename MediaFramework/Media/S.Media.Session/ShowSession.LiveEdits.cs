using S.Media.Compositor;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.Routing;

namespace S.Media.Session;

/// <summary>
/// Editing a cue WHILE it plays: its composition placement, its composition's outputs, its audio matrix and
/// routes, and the held frame of a still. Each one reaches into a live voice's already-wired plumbing and
/// changes it without a re-fire, which is what makes them a family and what makes them the riskiest verbs in
/// the session - every one of them has to leave the voice releasable if it fails half-way.
/// <para>Split out of the root file (2026-07-30 review §3), where they sat between the clip-commit path that
/// builds this plumbing and the soundboard delegation that has nothing to do with it.</para>
/// </summary>
public sealed partial class ShowSession
{

    /// <summary>Live-edit the active cue's composition placement while it plays (the GUI's
    /// <c>UpdateActiveCueVideoPlacement</c>) - repositions / re-opacities its layer. Returns false when the
    /// cue isn't the active clip on any group (or has no composition layer).</summary>
    public Task<bool> UpdateActivePlacementAsync(string cueId, string compositionId, int layerIndex, ShowVideoPlacement placement) =>
        InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { } voice)
                return Task.FromResult(voice.UpdatePlacement(
                    compositionId, layerIndex, BuildVideoPlacementSpec(compositionId, layerIndex, placement)));
            return Task.FromResult(false);
        });

    /// <summary>Hot-attaches an output lease to a LIVE composition so a playing clip starts fanning its
    /// composited video to a newly-selected line WITHOUT a re-fire (the GUI's <c>TryAddOutput</c> under the
    /// ShowSession path). Returns false when the composition isn't currently loaded. The lease carries the same
    /// borrowed/owned ownership contract as the fire-path video leases (a borrowed host output declares
    /// <see cref="ClipCompositionOutputLease.DisposeOutputOnRuntimeDispose"/> = false).</summary>
    public Task<bool> AddCompositionOutputAsync(string compositionId, ClipCompositionOutputLease lease)
    {
        ArgumentException.ThrowIfNullOrEmpty(compositionId);
        ArgumentNullException.ThrowIfNull(lease);
        return InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition) && composition.AddOutput(lease)));
    }

    /// <summary>Hot-detaches an output (by its lease <c>OutputId</c>) from a LIVE composition - the GUI's
    /// <c>TryRemoveOutput</c> under the ShowSession path. Returns false when the composition isn't loaded or had
    /// no such output. The detached output is NOT disposed here (the host that leased it owns its lifetime).</summary>
    public Task<bool> RemoveCompositionOutputAsync(string compositionId, string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(compositionId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        return InvokeAsync(() => Task.FromResult(
            _compositions.TryGetValue(compositionId, out var composition) && composition.RemoveOutput(outputId)));
    }

    /// <summary>Live-edit the active cue's audio routing matrix (source channels → <paramref name="outputId"/>'s
    /// channels) while it plays (the GUI's <c>UpdateActiveCueAudioRoutes</c>). Returns false when the cue isn't
    /// the active clip on any group (or has no audio router). Applies on the clip's source→output route.
    /// <para>The edited matrix becomes that output's route TARGET and is installed by the voice's one level
    /// composition (<c>master × fade × envelope</c>), never written raw: writing the caller's cells straight
    /// onto the router un-trimmed and un-faded the output for the rest of the clip's life, and left
    /// <c>RouteTargets</c> describing the OLD route so no later trim/fade/envelope step could reconcile it -
    /// the same defect class as the live re-apply and the hot rebuild, which both route through here now.</para></summary>
    public Task<bool> ApplyActiveAudioMatrixAsync(string cueId, string outputId, float[,] gains)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        ArgumentNullException.ThrowIfNull(gains);
        return InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is not { Player: { AudioRouter: { } router, AudioSourceId: { } sourceId } } voice)
                return Task.FromResult(false);

            // Validate the dimensions BEFORE touching the router (the same rule ApplyMatrix enforces): the
            // switch below removes the output's current route, so a matrix the router would reject has to
            // fail while the live routing is still intact rather than leaving the line silent.
            var dstChannels = router.TryGetOutput(outputId, out var sink) && sink is { } live
                ? live.Format.Channels
                : 0;
            var srcChannels = voice.Player.AudioSource?.Format.Channels ?? 0;
            if (gains.GetLength(0) > srcChannels || gains.GetLength(1) > dstChannels)
                throw new ArgumentException(
                    $"matrix is {gains.GetLength(0)}x{gains.GetLength(1)} but cue '{cueId}' has {srcChannels} " +
                    $"source channels and output '{outputId}' has {dstChannels}",
                    nameof(gains));

            // The cells become this output's route TARGET so every later level write (fade, envelope, master
            // trim, a stop ramp) re-derives them; every cell is carried, zeros included, and the output width
            // is declared, so the installed matrix keeps the caller's exact dimensions.
            var old = voice.RouteTargets.FirstOrDefault(t => t.OutputId == outputId);
            var cells = new List<ShowAudioMatrixCell>(gains.Length);
            for (var i = 0; i < gains.GetLength(0); i++)
                for (var o = 0; o < gains.GetLength(1); o++)
                    cells.Add(new ShowAudioMatrixCell(i, o, gains[i, o]));
            var route = (old?.Route ?? new ShowClipAudioRoute()) with
            {
                MatrixCells = cells,
                MatrixOutputChannels = gains.GetLength(1),
                Gain = 1f, // the per-cell gains ARE the authored level here; the route envelope stays unity
            };

            // A matrix coexists with a legacy route for the same pair (by the router's design), so switching
            // kinds has to drop the old one - otherwise the line would play the sum of both, and only the
            // matrix half would follow the trim. Same rule as ApplyActiveAudioRoutesAsync's kind switch.
            if (old?.Route is not { HasGainMatrix: true })
                router.RemoveRoute(sourceId, outputId);

            var targets = voice.RouteTargets.Where(t => t.OutputId != outputId).ToList();
            targets.Add(new AudioRouteTarget(outputId, route.Gain, route));
            voice.SetRouteTargets(targets);
            // ONE pass through the single place a route gain is computed - it installs the edited matrix at
            // the clip's current composed level and leaves every other route value-identical.
            voice.ApplyAudioScale(voice.RouteTargets, voice.ClipLevel);
            return Task.FromResult(true);
        });
    }

    /// <summary>Live-edit the active cue's audio routing by re-applying its per-output-line routes (each line's
    /// channel map/full gain matrix + gain) while it plays - the GUI's <c>UpdateActiveCueAudioRoutes</c> under the
    /// ShowSession path. Each route <c>i</c> replaces every route for the clip's <c>clip{i}</c> output, then installs
    /// either its legacy channel map or its per-cell matrix. Returns false when
    /// the cue isn't the active clip on any group. If the live clip-output count no longer matches the edited route
    /// count (a line was added/removed/muted mid-playback, which reorders the positional <c>clip{i}</c> ids), the
    /// live apply is skipped so nothing is mis-patched - that change lands cleanly on the next fire instead.</summary>
    public Task<bool> ApplyActiveAudioRoutesAsync(string cueId, IReadOnlyList<ShowClipAudioRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { Player: { AudioRouter: { } router, AudioSourceId: { } sourceId } } voice)
            {
                // Count the clip's contiguous clip0..clipN outputs; only live-apply when that count matches the
                // edited routes (stable composition - the common level/channel tweak). A count change reorders
                // the positional ids, so defer it to the next fire rather than mis-patch a live output.
                var ids = router.GetRegisteredOutputIds().ToHashSet(StringComparer.Ordinal);
                var liveClipOutputs = 0;
                while (ids.Contains($"clip{liveClipOutputs}"))
                    liveClipOutputs++;
                if (liveClipOutputs != routes.Count)
                    return Task.FromResult(true); // composition changed → applies on the next fire

                // Install the edited routes at the clip's CURRENT composed level, not at the fade level
                // alone: EffectiveAudioLevel is the single source of truth (fade × envelope × master
                // trim) that ApplyAudioScale writes with, and a live edit must not resurrect the
                // untrimmed/un-enveloped gain. Reading the composed product here (rather than
                // re-deriving it) also means the reconciling pass below writes identical values, so a
                // slider drag never blips through an untrimmed gain.
                var level = voice.EffectiveAudioLevel;
                var updatedTargets = new List<AudioRouteTarget>(routes.Count);
                for (var i = 0; i < routes.Count; i++)
                {
                    var map = routes[i].ToChannelMap();
                    var outputId = $"clip{i}";
                    if (!routes[i].HasGainMatrix && map is null)
                    {
                        // A fully-unrouted line carries no map - nothing to re-apply. Its previously
                        // installed route keeps playing, so keep its OLD target too: dropping it from the
                        // rebuilt list would exempt that line from stop-fades/scale rides (hard cut).
                        if (voice.RouteTargets.FirstOrDefault(t => t.OutputId == outputId) is { } kept)
                            updatedTargets.Add(kept);
                        continue;
                    }
                    var old = voice.RouteTargets.FirstOrDefault(t => t.OutputId == outputId);
                    var switchedKinds = old is null || old.Route?.HasGainMatrix != routes[i].HasGainMatrix;
                    try
                    {
                        // Same-kind updates reconcile in place (matrix cells ramp atomically; legacy route id
                        // replaces in place). Only a matrix↔legacy mode switch needs all pair routes removed.
                        if (switchedKinds)
                            router.RemoveRoute(sourceId, outputId);
                        if (routes[i].HasGainMatrix)
                            router.ApplyMatrix(sourceId, outputId,
                                routes[i].ToGainMatrix(routes[i].Gain * level));
                        else
                            router.AddRoute(sourceId, outputId, map!.Value,
                                routes[i].Gain * level);
                        updatedTargets.Add(new AudioRouteTarget(outputId, routes[i].Gain, routes[i]));
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        // channel count mismatch vs the live output - lands on the next fire
                        if (old is not null)
                        {
                            if (switchedKinds && old.Route is { } oldRoute)
                            {
                                try
                                {
                                    if (oldRoute.HasGainMatrix)
                                        router.ApplyMatrix(sourceId, outputId,
                                            oldRoute.ToGainMatrix(old.TargetGain * level));
                                    else if (oldRoute.ToChannelMap() is { } oldMap)
                                        router.AddRoute(sourceId, outputId, oldMap,
                                            old.TargetGain * level);
                                }
                                catch (Exception rollbackEx) when (
                                    rollbackEx is ArgumentException or InvalidOperationException)
                                {
                                    // The output changed underneath both edits; the next rebuild/fire owns it.
                                }
                            }
                            updatedTargets.Add(old); // keep stop/fade ownership of the still-installed route
                        }
                    }
                }

                voice.SetRouteTargets(updatedTargets);
                // One composition pass over the NEW target set, through the single place route gains
                // are written (fade × envelope × master trim). Value-wise a no-op after the installs
                // above, but it is what makes the level composition - not this method - authoritative,
                // and it covers the rolled-back/kept targets uniformly.
                voice.ApplyAudioScale(voice.RouteTargets, voice.ClipLevel);

                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });
    }

    /// <summary>REBUILDS the active cue's audio outputs from a fresh route set while it plays - the count-change
    /// counterpart of <see cref="ApplyActiveAudioRoutesAsync"/> (which only re-applies in place for a stable
    /// count). Removes EVERY current <c>clip{i}</c> output from the router (its <c>_audio_discard</c>
    /// negotiation-lead sink stays, so the router keeps running - the clip plays on even with ZERO device
    /// outputs, on the wall clock), then re-adds one output per route. Used by the deck's hot output add/remove so
    /// unrouting an output keeps playback going and re-routing re-attaches at the live position. Returns false
    /// when the cue isn't the active clip on any group.</summary>
    public Task<bool> RebuildActiveClipAudioOutputsAsync(string cueId, IReadOnlyList<ShowClipAudioRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { Player: { AudioRouter: { } router, AudioSourceId: not null } } voice)
            {
                // 1) Drop every current clip{i} output from the router FIRST (before releasing the tracked sinks,
                //    so no route dangles to a released output). The discard sink is left, so the router keeps pacing.
                foreach (var id in router.GetRegisteredOutputIds()
                             .Where(id => id.StartsWith("clip", StringComparison.Ordinal)).ToList())
                    router.RemoveOutput(id);

                // 2) Re-add one output per route (mirrors CommitClipAsync's per-clip audio block). Per-route
                //    isolation is CRITICAL here: step 1 already removed every output, so without it one
                //    un-openable device (e.g. a fixed-rate JACK graph rejecting the clip's mix rate) faulted the
                //    whole rebuild and left the clip totally silent instead of playing its remaining routes.
                var rate = voice.Player.SampleRate > 0 ? voice.Player.SampleRate : 48_000;
                // Re-attach at the clip's CURRENT composed level (fade × envelope × master trim), never at
                // the raw authored gain: the rebuild can land while the clip sits under a master trim, mid
                // fade-in, or mid stop-fade, and attaching at unity would jump the cue to full level for
                // the gap before the reconciling pass below (an audible pop, and permanent for a clip with
                // no fade/envelope running to rewrite it). Read from the one place the product is defined.
                var level = voice.EffectiveAudioLevel;
                var newOutputs = new List<ClipAudioOutput>(routes.Count);
                var audioPumps = new List<(string OutputId, string DeviceId)>();
                var routeTargets = new List<AudioRouteTarget>();
                for (var i = 0; i < routes.Count; i++)
                {
                    var route = routes[i];
                    var outputId = $"clip{i}";
                    if (!TryAttachRouteOutput(
                            voice.Player, outputId, route.DeviceId, route.ToChannelMap(), rate,
                            gain: route.Gain * level, newOutputs, route))
                        continue;
                    routeTargets.Add(new AudioRouteTarget(outputId, route.Gain, route));
                    if (route.DeviceId is { } dev)
                        audioPumps.Add((outputId, dev));
                }

                // 3) Swap the voice's tracked set, release the OLD one per ownership, refresh route targets + pumps.
                foreach (var o in voice.SwapAudioOutputs(newOutputs))
                    ReleaseClipAudioOutput(o);
                voice.SetRouteTargets(routeTargets);
                // 4) One level-composition pass over the rebuilt targets - the same thing the fire path does
                //    after attaching (CommitClipAsync). The rebuilt routes are the ONLY ones the voice's fade
                //    ride now knows about, so this is what keeps a trimmed/faded clip at its real level.
                voice.ApplyAudioScale(voice.RouteTargets, voice.ClipLevel);
                voice.SetAudioPumps(audioPumps);
                PublishGroupViews();
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });
    }

    /// <summary>Live-swap the active cue's held video frame - a text / still cue whose content was edited while it
    /// plays - with no reload or re-fire. Finds the cue's active clip and, if its source supports it
    /// (<see cref="IReplaceableFrameSource"/>, e.g. a rendered text source), replaces the displayed frame in place.
    /// Returns false when the cue isn't the active clip on any group or its source can't be swapped; the session
    /// owns <paramref name="frame"/> after this call (disposed if not applied).</summary>
    public Task<bool> UpdateActiveClipFrameAsync(string cueId, VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return InvokeAsync(() =>
        {
            if (ActiveVoiceOf(cueId) is { Player.VideoSource: IReplaceableFrameSource replaceable })
            {
                replaceable.ReplaceFrame(frame);
                return Task.FromResult(true);
            }

            frame.Dispose(); // not applied → don't leak the caller's frame
            return Task.FromResult(false);
        });
    }

    /// <summary>Previews a loaded cue's clip on a separate (preview / headphones) device, independent of the
    /// transport groups (the GUI's <c>PreviewCue</c>). Opens a FRESH instance (not the standby-prepared clip),
    /// plays it on <paramref name="previewDeviceId"/> (or the default device), and fires
    /// <see cref="PreviewEnded"/> at its natural end. Replaces any current preview. Returns false when the cue
    /// has no clip binding, or when the preview was preempted (stopped/replaced) while its media was opening.
    /// The open runs OFF the serial dispatcher (NXT-19) so a slow audition open never parks transport, and
}
