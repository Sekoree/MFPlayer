using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// Turning a fire decision into actual playback: running a trigger plan (pre-waits, overlap lanes,
/// independent lanes), dispatching each cue to its executor, applying what came back to the row, and the
/// jump/auto-follow control flow that decides what happens next.
/// <para>Split out of the root file (2026-07-30 review §3), which had reached ~4 500 lines DESPITE already
/// having nine partials - the root had become the place anything without an obvious home landed. This is
/// the "GO actually happens" half; deciding WHAT to fire stays with the authoring/selection code.</para>
/// </summary>
public partial class CuePlayerViewModel
{
    /// <param name="trackCurrentCue">False for a headless cross-list run: the plan executes normally but
    /// leaves the VISIBLE transport's playing pointer alone (that pointer describes the selected list).</param>
    /// <param name="isPaused">The run's own pause state, which its pre-waits follow. Null = the VISIBLE
    /// transport's <see cref="IsTransportPaused"/> (the operator GO path).</param>
    private async Task RunTriggerPlanAsync(
        IReadOnlyList<(CueNodeViewModel Cue, int DelayMs, bool Independent)> plan, CancellationToken ct,
        (TimeSpan Duration, S.Media.Session.FadeCurve Curve)? advanceCrossfade = null,
        bool trackCurrentCue = true,
        Func<bool>? isPaused = null)
    {
        var startedAt = DateTime.UtcNow;

        // Group steps that share the same delay for coordinated start.
        var groups = plan.GroupBy(s => s.DelayMs).OrderBy(g => g.Key).ToList();
        foreach (var group in groups)
        {
            await WaitUntilDelayAsync(
                startedAt, group.Key, ct, countdownCues: group.Select(s => s.Cue).ToList(),
                isPaused: isPaused);
            ct.ThrowIfCancellationRequested();

            var steps = group.ToList();
            // Only the playing pointer follows fired steps. The editor selection is operator-owned and
            // transport never changes it; current/standby row dots show the live playhead instead.
            if (trackCurrentCue)
                foreach (var step in steps)
                    CurrentCueNode = step.Cue;

            if (steps.Count > 1 && MediaCueGroupExecutor is not null)
            {
                // Coordinated group: open all decoders in parallel, start in sync.
                DispatchCueGroupExecution(steps.Select(s => s.Cue).ToList(), ct);
            }
            else
            {
                foreach (var step in steps)
                {
                    // Overlap-mode media steps (Timeline lanes, staggered sim-mode pre-waits) get
                    // their own runtime transport group: the shared authored group holds ONE active
                    // clip, so this fire would otherwise cut the earlier lane's clip mid-play.
                    if (step.Independent && step.Cue.Kind == CueNodeKind.Media)
                    {
                        DispatchIndependentCueExecution(step.Cue, ct);
                    }
                    else
                    {
                        DispatchCueExecution(step.Cue, ct, advanceCrossfade);
                        if (step.Cue.Kind == CueNodeKind.Media)
                            advanceCrossfade = null; // the window belongs to the FIRST media fire only
                    }
                }
            }
        }
    }

    /// <summary>Dispatches one media cue into its OWN runtime transport group (the
    /// <see cref="MediaCueIndependentExecutor"/> host path, same machinery as same-delay batches and
    /// operator overlap-fires). Falls back to the shared-group dispatch when no independent executor
    /// is configured (headless tests / degraded host).</summary>
    private void DispatchIndependentCueExecution(CueNodeViewModel cue, CancellationToken ct)
    {
        if (MediaCueIndependentExecutor is not { } executor || cue.ToModel() is not MediaCueNode media)
        {
            DispatchCueExecution(cue, ct);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await executor(media, ct).ConfigureAwait(false);
                await ApplyCueExecutionResultOnUiAsync(cue, result, MediaExecutionConfigured).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Stop / Panic cancelled the dispatched cue. */ }
            catch (Exception ex)
            {
                await ApplyCueExecutionFailureOnUiAsync(cue, ex.Message).ConfigureAwait(false);
            }
        }, ct);
    }

    private void DispatchCueExecution(
        CueNodeViewModel cue, CancellationToken ct,
        (TimeSpan Duration, S.Media.Session.FadeCurve Curve)? advanceCrossfade = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var exec = await ExecuteCueAsync(cue, ct, advanceCrossfade).ConfigureAwait(false);
                await ApplyCueExecutionResultOnUiAsync(cue, exec, MediaExecutionConfigured).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Stop / Panic cancelled the dispatched cue. */ }
            catch (Exception ex)
            {
                await ApplyCueExecutionFailureOnUiAsync(cue, ex.Message).ConfigureAwait(false);
            }
        }, ct);
    }

    private void DispatchCueGroupExecution(IReadOnlyList<CueNodeViewModel> cues, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var mediaCues = cues
                    .Where(c => c.Kind == CueNodeKind.Media)
                    .Select(c => c.ToModel())
                    .OfType<MediaCueNode>()
                    .ToList();

                string? groupStatus = null;
                if (mediaCues.Count > 0 && MediaCueGroupExecutor is not null)
                {
                    var result = await MediaCueGroupExecutor(mediaCues, ct).ConfigureAwait(false);
                    // Deliberately NOT awaited here. This used to marshal the status message to the UI thread
                    // BEFORE dispatching the lanes below, so every non-media lane of a "fire all together"
                    // group - a visualizer, an action, a fade - waited on a UI round-trip that lands only
                    // when the UI thread is free. During a GO it frequently is not (decoders opening,
                    // surfaces building), so the lanes started late; and if that marshal never completed,
                    // the outer catch swallowed it and they never started at all. The message is a
                    // notification, not a prerequisite - it is published after the lanes are away.
                    groupStatus = string.IsNullOrWhiteSpace(result)
                        ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), $"{mediaCues.Count} cues")
                        : result;
                }

                // Non-media cues in the group still dispatch individually. They run AFTER the media batch on
                // purpose: a visualizer attaches to a composition's GL surface host, which may not exist
                // until a clip is playing on that composition.
                foreach (var cue in cues.Where(c => c.Kind != CueNodeKind.Media))
                {
                    try
                    {
                        var exec = await ExecuteCueAsync(cue, ct).ConfigureAwait(false);
                        // ALWAYS applied, success included. This used to run only when the executor returned
                        // a message, so a lane that succeeded silently - the normal case - skipped
                        // ApplyCueExecutionResult entirely. That is where a visualizer's Now Playing row is
                        // created and where an instant cue's Auto-Follow chain advances, so in a same-delay
                        // batch a visualizer played with no indicator that it was running, and a cue chained
                        // after an instant one never fired. Fired on its own the cue went through
                        // DispatchCueExecution, which always applies - which is why this only ever showed up
                        // inside a group.
                        await ApplyCueExecutionResultOnUiAsync(cue, exec, mediaExecutionConfigured: false)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await ApplyCueExecutionFailureOnUiAsync(cue, ex.Message).ConfigureAwait(false);
                    }
                }

                // Every lane is away; now the batch's own notification can go up. A lane that reported its
                // own message has already overwritten this one, which is the right precedence - a
                // per-lane failure matters more than "n cues fired".
                if (groupStatus is not null)
                    await SetStatusMessageOnUiAsync(groupStatus).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await SetStatusMessageOnUiAsync(ex.Message);
            }
        }, ct);
    }

    private Task ApplyCueExecutionResultOnUiAsync(CueNodeViewModel cue, string? detail, bool mediaExecutionConfigured)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyCueExecutionResult(cue, detail, mediaExecutionConfigured);
            return Task.CompletedTask;
        }

        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            ApplyCueExecutionResult(cue, detail, mediaExecutionConfigured)).GetTask();
    }

    private Task ApplyCueExecutionFailureOnUiAsync(CueNodeViewModel cue, string detail)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ApplyCueExecutionFailure(cue, detail);
            return Task.CompletedTask;
        }

        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            ApplyCueExecutionFailure(cue, detail)).GetTask();
    }

    private void ApplyCueExecutionResult(CueNodeViewModel cue, string? detail, bool mediaExecutionConfigured)
    {
        if (cue.Kind == CueNodeKind.Media
            && mediaExecutionConfigured
            && !_activeCueIds.Contains(cue.Id))
        {
            ApplyCueExecutionFailure(cue, detail);
            return;
        }

        // Qualified: this runs for cross-list fires too, and it lands AFTER GoForeignListAsync's own
        // qualified GO line, so an unprefixed label here overwrote the qualifier milliseconds later.
        StatusMessage = string.IsNullOrWhiteSpace(detail)
            ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), CueDisplayQualified(cue))
            : Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat), CueDisplayQualified(cue), detail);

        // Visualizer rows in Now Playing: a fired Start cue appears with a ticking position (count-up
        // when indefinite, progress toward the set duration otherwise); a Stop cue (or the row's X, or
        // the duration elapsing) ends the row. The projectM LAYER itself keeps rendering unless stopped.
        if (cue.Kind == CueNodeKind.Visualizer && string.IsNullOrWhiteSpace(detail))
        {
            if (string.Equals(cue.Extra, "Stop", StringComparison.OrdinalIgnoreCase))
                EndVisualizerRows(VisualizerTargetComposition(cue));
            else
                StartVisualizerRow(cue);
        }

        // Runtime chaining for INSTANT cues (visualizer/action/fade - jumps redirect themselves): the
        // media end-machinery never fires for them, so a following Auto-Follow cue would otherwise never
        // start. An infinite visualizer (or an action, or a fade - its ramp runs in the background) is
        // non-blocking → the chain advances NOW; a visualizer WITH a duration occupies the timeline like
        // an image slide → advance after it.
        if (string.IsNullOrWhiteSpace(detail)
            && cue.Kind is CueNodeKind.Visualizer or CueNodeKind.Action or CueNodeKind.Fade)
        {
            var delayMs = cue.Kind == CueNodeKind.Visualizer ? Math.Max(0, cue.VisualizerDurationMs) : 0;
            _ = AdvanceAutoFollowAfterInstantCueAsync(cue, delayMs);
        }
    }

    /// <summary>Row-X cancel: a running VISUALIZER row stops the projectM layer (the generic session
    /// stop is a no-op for it - visualizers are not session clips); everything else goes to the host's
    /// cancel callback as before.</summary>
    private async Task CancelActiveCueAsync(Guid cueId)
    {
        if (_runningVisualizers.ContainsKey(cueId))
        {
            await StopVisualizerAsync(cueId);
            return;
        }

        await (CancelCueCallback?.Invoke(cueId) ?? Task.CompletedTask);
    }

    /// <summary>Stops one running visualizer: detaches its layer (synthetic Stop through the executor)
    /// and retires its Now-Playing row.</summary>
    private async Task StopVisualizerAsync(Guid vizCueId)
    {
        if (!_runningVisualizers.TryGetValue(vizCueId, out var info))
            return;
        if (VisualizerCueExecutor is not null)
        {
            try
            {
                await VisualizerCueExecutor(
                    new VisualizerCueNode { CompositionId = info.Composition, StartVisualizer = false },
                    CancellationToken.None);
            }
            catch
            {
                // best effort - the row still retires below
            }
        }

        EndVisualizerRows(info.Composition);
    }

    /// <summary>Panic/Stop: every running visualizer layer detaches and its row retires.</summary>
    public void StopAllVisualizers()
    {
        foreach (var id in _runningVisualizers.Keys.ToList())
            _ = StopVisualizerAsync(id);
    }

    /// <summary>A host rebuild removed one composition visualizer without executing its Stop cue.</summary>
    internal void OnVisualizerLayerCleared(Guid compositionId) => EndVisualizerRows(compositionId);

    /// <summary>The host removed every composition visualizer without executing individual Stop cues.</summary>
    internal void OnVisualizerLayersCleared()
    {
        foreach (var compositionId in _runningVisualizers.Values.Select(info => info.Composition).Distinct().ToList())
            EndVisualizerRows(compositionId);
    }

    // --- Visualizer Now-Playing rows (#29 first slice) --------------------------------------------
    // _runningVisualizers tracks the actual persistent layer lifetime. A finite cue duration only retires
    // its Now-Playing timeline row; it does not stop projectM, so keep that distinction explicit or Panic and
    // later live placement edits would lose the still-running layer after the row timed out.
    private readonly Dictionary<Guid, (Guid Composition, long StartedTicks, int DurationMs)> _runningVisualizers = new();
    private readonly HashSet<Guid> _visibleVisualizerRows = new();
    private Avalonia.Threading.DispatcherTimer? _visualizerRowTimer;

    private static Guid VisualizerTargetComposition(CueNodeViewModel viz) =>
        viz.VideoPlacements.FirstOrDefault()?.CompositionId ?? viz.VisualizerCompositionId;

    internal void StartVisualizerRow(CueNodeViewModel viz)
    {
        // Re-firing replaces the layer on that composition - end any prior rows for it first.
        EndVisualizerRows(VisualizerTargetComposition(viz));
        _runningVisualizers[viz.Id] =
            (VisualizerTargetComposition(viz), System.Diagnostics.Stopwatch.GetTimestamp(), Math.Max(0, viz.VisualizerDurationMs));
        _visibleVisualizerRows.Add(viz.Id);
        OnCueStarted(viz.Id);
        _visualizerRowTimer ??= new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(500), Avalonia.Threading.DispatcherPriority.Background, OnVisualizerRowTick);
        _visualizerRowTimer.Start();
    }

    private void EndVisualizerRows(Guid compositionId)
    {
        foreach (var (id, info) in _runningVisualizers.Where(kv => kv.Value.Composition == compositionId).ToList())
        {
            _runningVisualizers.Remove(id);
            if (_visibleVisualizerRows.Remove(id))
                OnCueEnded(id);
        }

        if (_visibleVisualizerRows.Count == 0)
            _visualizerRowTimer?.Stop();
    }

    private void OnVisualizerRowTick(object? sender, EventArgs e)
    {
        foreach (var id in _visibleVisualizerRows.ToList())
        {
            if (!_runningVisualizers.TryGetValue(id, out var info))
            {
                _visibleVisualizerRows.Remove(id);
                continue;
            }
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(info.StartedTicks);
            if (info.DurationMs > 0 && elapsed.TotalMilliseconds >= info.DurationMs)
            {
                // Timeline slot done (the layer keeps rendering; only the row retires).
                _visibleVisualizerRows.Remove(id);
                OnCueEnded(id);
                continue;
            }

            OnCueProgress(new CuePlaybackProgress(
                id, elapsed, TimeSpan.FromMilliseconds(info.DurationMs)));
        }

        if (_visibleVisualizerRows.Count == 0)
            _visualizerRowTimer?.Stop();
    }

    /// <summary>Fires the next Auto-Follow cue after an instant cue (optionally after its timeline
    /// duration). Cancels itself when the operator moved on (another GO changed the standby).
    /// <para>Cross-list merged session: the chain is resolved and continued inside the list that OWNS
    /// <paramref name="fired"/>, mirroring the media natural-end path
    /// (<see cref="OnMediaCueNaturallyEndedAsync(Guid)"/>). Resolving against the SELECTED list found no
    /// index for a foreign Action/Visualizer/Fade cue, so its Auto-Follow chain silently stopped dead
    /// after the first cue; continuing through <c>GoCore</c> would have been worse still - it would have
    /// armed the VISIBLE standby with another list's cue.</para></summary>
    private async Task AdvanceAutoFollowAfterInstantCueAsync(CueNodeViewModel fired, int delayMs)
    {
        var foreign = IsForeignListNode(fired);
        var ordered = EnumerateFireableCueOrderFor(fired).ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, fired));
        if (idx < 0 || idx + 1 >= ordered.Count)
            return;
        var next = ordered[idx + 1];
        if (!SequentialTransitionUsesMode(fired, next, CueTriggerMode.AutoFollow))
            return;

        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
            // Stale? The operator fired something else while the visualizer slide ran. Only meaningful
            // for the visible transport - CurrentCueNode describes the SELECTED list, and a headless
            // cross-list run is superseded by its own list's next fire (which cancels its run scope).
            if (!foreign && !ReferenceEquals(CurrentCueNode, fired) && CurrentCueNode is not null)
                return;
        }

        StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowStatusFormat), CueDisplayQualified(next));
        if (foreign)
        {
            await GoForeignListAsync(next);
            return;
        }

        StandbyCueNode = next;
        _immediateJumpChain.Clear();
        await GoCore();
    }

    private void ApplyCueExecutionFailure(CueNodeViewModel cue, string? detail)
    {
        if (ReferenceEquals(CurrentCueNode, cue))
            CurrentCueNode = null;

        // Parking the failed cue in standby is the OPERATOR's affordance ("it didn't go - press GO to
        // retry"), and it only makes sense for a cue the operator can see. A failed cross-list fire
        // must not reach it: writing a foreign node into StandbyCueNode dropped the visible list's own
        // standby (RefreshRowStatuses walks the SELECTED list only, so its dot simply vanished) and
        // armed the next GO to fire that foreign cue through the VISIBLE transport - the one thing
        // cross-list firing is defined not to do. Un-pausing the visible transport on another list's
        // failure is the same category of mistake.
        if (!IsForeignListNode(cue))
        {
            StandbyCueNode = cue;
            IsTransportPaused = false;
        }

        // Qualified like every other cross-list status line: an unprefixed "1 Station ID" reads as the
        // visible list's cue 1.
        StatusMessage = string.IsNullOrWhiteSpace(detail)
            ? Strings.Format(nameof(Strings.CueExecutionFailedStatusFormat), CueDisplayQualified(cue))
            : Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat), CueDisplayQualified(cue), detail);
    }

    private async Task SetStatusMessageOnUiAsync(string? message)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            StatusMessage = message;
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusMessage = message);
    }

    private async Task<string?> ExecuteCueAsync(
        CueNodeViewModel cue, CancellationToken ct,
        (TimeSpan Duration, S.Media.Session.FadeCurve Curve)? advanceCrossfade = null)
    {
        switch (cue.Kind)
        {
            case CueNodeKind.Media:
                if (MediaCueExecutor is null)
                    return Strings.CueMediaExecutionNotConfigured;
                if (cue.ToModel() is not MediaCueNode media)
                    return Strings.CueInvalidMediaCue;
                // A playlist-crossfade advance fires through the dual-voice seam; without one (a host
                // that cleared it mid-run) the plain executor is the butt-splice safety net.
                return advanceCrossfade is { } crossfade && MediaCueCrossfadeExecutor is { } crossfadeExecutor
                    ? await crossfadeExecutor(media, crossfade.Duration, crossfade.Curve, ct)
                    : await MediaCueExecutor(media, ct);
            case CueNodeKind.Action:
                if (ActionCueExecutor is null)
                    return Strings.CueActionExecutionNotConfigured;
                return cue.ToModel() is ActionCueNode action
                    ? await ActionCueExecutor(action, ct)
                    : Strings.CueInvalidActionCue;
            case CueNodeKind.Comment:
                return Strings.CueCommentResult;
            case CueNodeKind.Visualizer:
                if (VisualizerCueExecutor is null)
                    return Strings.CueActionExecutionNotConfigured;
                return cue.ToModel() is VisualizerCueNode viz
                    ? await VisualizerCueExecutor(viz, ct)
                    : Strings.CueInvalidActionCue;
            case CueNodeKind.Jump:
                // Control flow is transport/UI state - marshal to the UI thread (this executor runs on a
                // worker). The jump moves the playhead to a target (loop back / section repeat / random
                // pick) and, by default, fires it through the normal GO machinery.
                return await Avalonia.Threading.Dispatcher.UIThread
                    .InvokeAsync(() => ExecuteJumpCueOnUi(cue));
            case CueNodeKind.Fade:
            {
                if (FadeCueExecutor is null)
                    return Strings.CueFadeExecutionNotConfigured;
                if (cue.ToModel() is not FadeCueNode fade)
                    return Strings.CueInvalidActionCue;
                // Target resolution reads transport state (_activeCueIds) and the cue tree - UI thread.
                var fadeTargets = await Avalonia.Threading.Dispatcher.UIThread
                    .InvokeAsync(() => ResolveFadeCueTargetsOnUi(cue));
                if (fadeTargets.Count == 0)
                    // "Fade all playing" with nothing playing is a benign no-op; an explicit target
                    // list that resolved to nothing is an authoring error worth surfacing.
                    return cue.FadeTargetAllPlaying ? null : Strings.CueFadeNoTargets;
                return await FadeCueExecutor(fade, fadeTargets, ct);
            }
            case CueNodeKind.Group:
            default:
                return null;
        }
    }

    /// <summary>Executes a Jump cue (UI thread): resolves a live target by stable cue ID (numbers are
    /// display-only, so renumber/reorder never retargets), picks randomly when configured, then either
    /// fires the target (default - the loop actually loops) or arms it as standby for the next GO.</summary>
    internal string? ExecuteJumpCueOnUi(CueNodeViewModel jump)
    {
        if (jump.JumpTargetIds.Count == 0)
            return Strings.CueJumpNoTargets;

        // Targets resolve inside the jump's OWN list (the selected one for every visible jump): a cue
        // fired from another list by a schedule/trigger runs its list's control flow, not the visible
        // list's. Jump target ids are authored links within one list.
        var byId = EnumerateAllCueNodesFor(jump).ToDictionary(c => c.Id, c => c);
        var live = jump.JumpTargetIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Where(c => c.Kind != CueNodeKind.Comment)
            .DistinctBy(c => c.Id)
            .ToList();
        if (live.Count == 0)
            return Strings.CueJumpTargetMissing;

        if (!_immediateJumpChain.Add(jump.Id))
        {
            _immediateJumpChain.Clear();
            throw new InvalidOperationException(Strings.CueJumpCycleDetected);
        }

        // A group target resolves through its fire mode to a concrete first cue. Exclude any candidate whose
        // next immediate jump was already visited: e.g. Jump #5 → containing Group → first child Jump #5.
        // Valid media/action targets in the same pool remain usable, so one bad group link cannot spin the app.
        var safe = live.Where(candidate =>
        {
            var resolved = ResolveFireableCue(candidate);
            return resolved is null
                   || resolved.Kind != CueNodeKind.Jump
                   || !_immediateJumpChain.Contains(resolved.Id);
        }).ToList();
        if (safe.Count == 0)
        {
            _immediateJumpChain.Clear();
            throw new InvalidOperationException(Strings.CueJumpCycleDetected);
        }

        var candidates = safe;
        if (jump.JumpRandom
            && jump.JumpAvoidImmediateRepeat
            && safe.Count > 1
            && _lastRandomJumpTargetIds.TryGetValue(jump.Id, out var previousTargetId))
        {
            var nonRepeating = safe.Where(candidate => candidate.Id != previousTargetId).ToList();
            if (nonRepeating.Count > 0)
                candidates = nonRepeating;
        }

        var target = jump.JumpRandom && candidates.Count > 1
            ? candidates[Random.Shared.Next(candidates.Count)]
            : candidates[0];
        if (jump.JumpRandom && jump.JumpAvoidImmediateRepeat)
            _lastRandomJumpTargetIds[jump.Id] = target.Id;
        else
            _lastRandomJumpTargetIds.Remove(jump.Id);
        var resolvedTarget = ResolveFireableCue(target);

        if (IsForeignListNode(jump))
        {
            // Cross-list merged session: this jump belongs to a list the operator is not looking at, so
            // it must not arm the VISIBLE standby. "Standby" mode therefore has nothing to point at and
            // ends the chain; a firing jump continues headlessly in its own list.
            _immediateJumpChain.Clear();
            if (!string.Equals(jump.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase))
                _ = GoForeignListAsync(target);
            return null;
        }

        StandbyCueNode = target;
        if (!string.Equals(jump.SourceOrAction, "standby", StringComparison.OrdinalIgnoreCase))
        {
            if (resolvedTarget?.Kind != CueNodeKind.Jump)
                _immediateJumpChain.Clear();
            _ = GoCore(); // preserve the visited set only across an immediate Jump→Jump continuation
        }
        else
        {
            _immediateJumpChain.Clear();
        }
        return null;
    }

    /// <summary>Pre-wait visibility threshold: shorter waits are perceptually immediate and a badge
    /// would only flicker.</summary>
    private static readonly TimeSpan PreWaitCountdownThreshold = TimeSpan.FromMilliseconds(1500);

    /// <param name="isPaused">Which run's pause state freezes this pre-wait. Null = the VISIBLE
    /// transport (the operator GO path). A headless cross-list run passes its OWN state: gating a
    /// foreign list's pre-waits on <see cref="IsTransportPaused"/> meant that pausing the list the
    /// operator happened to be LOOKING at froze every other list's scheduled pre-rolls too - a
    /// half-hour station-ID pre-wait in an automation list simply never came due. A pre-wait is a
    /// scheduling delay, not playback; the visible Pause has no claim on a cue that has not started.</param>
    private async Task WaitUntilDelayAsync(
        DateTime startedAtUtc, int delayMs, CancellationToken ct,
        IReadOnlyList<CueNodeViewModel>? countdownCues = null,
        Func<bool>? isPaused = null)
    {
        if (delayMs <= 0)
            return;

        isPaused ??= () => IsTransportPaused;
        var showingCountdown = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                while (isPaused())
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(40, ct);
                    startedAtUtc = startedAtUtc.AddMilliseconds(40);
                }

                var due = startedAtUtc.AddMilliseconds(delayMs);
                var remaining = due - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return;

                // Pre-wait visibility (review §6): a live "⏳ in m:ss" badge on the waiting cues' rows
                // so the operator sees WHY nothing is sounding yet. Runs on the UI thread already.
                if (countdownCues is not null && remaining >= PreWaitCountdownThreshold)
                {
                    showingCountdown = true;
                    var text = $"⏳ in {(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";
                    foreach (var cue in countdownCues)
                        cue.PreWaitCountdownText = text;
                }
                else if (showingCountdown)
                {
                    ClearPreWaitCountdown(countdownCues);
                    showingCountdown = false;
                }

                var slice = remaining > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : remaining;
                await Task.Delay(slice, ct);
            }
        }
        finally
        {
            if (showingCountdown)
                ClearPreWaitCountdown(countdownCues);
        }
    }

    private static void ClearPreWaitCountdown(IReadOnlyList<CueNodeViewModel>? cues)
    {
        if (cues is null)
            return;
        foreach (var cue in cues)
            cue.PreWaitCountdownText = null;
    }

    private void CancelTransportRun()
    {
        try { _transportRunCts?.Cancel(); } catch { /* best effort */ }
        try { _transportRunCts?.Dispose(); } catch { /* best effort */ }
        _transportRunCts = null;
    }

    /// <summary>Cancels every headless cross-list run (Stop / Panic, which stop the whole session).
    /// A list switch deliberately does NOT: those runs play in the same merged session and keep going,
    /// exactly like the clips they started.</summary>
    private void CancelForeignListRuns()
    {
        if (_foreignListRuns.Count == 0)
            return;
        foreach (var cts in _foreignListRuns.Values)
        {
            try { cts.Cancel(); } catch { /* best effort */ }
            try { cts.Dispose(); } catch { /* best effort */ }
        }

        _foreignListRuns.Clear();
    }

    private static IEnumerable<CueNodeViewModel> EnumerateMediaNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == CueNodeKind.Media)
                yield return node;
            foreach (var child in EnumerateMediaNodes(node.Children))
                yield return child;
        }
    }
}
