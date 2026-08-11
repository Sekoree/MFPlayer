using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using S.Media.Core.Audio;
using S.Media.Session;
using S.Media.Time;

namespace HaCue2.Engine;

/// <summary>
/// The <see cref="ICueExecutionHost"/> surface: everything firing a cue can ask the rig to do.
/// </summary>
/// <remarks>
/// This is the DEVICE half of firing a cue, and it is deliberately dumb — play this, send that, wait,
/// write these sends. What a cue MEANS lives in <see cref="CueExecutor"/>, which is the code with the
/// most at stake and can be tested against a fake host because this interface is the only thing it
/// touches.
/// </remarks>
public sealed partial class ShowHost
{
    /// <summary>
    /// What firing a cue means, for every kind — extracted so it can be tested without devices.
    /// </summary>
    /// <remarks>
    /// This class stays the DEVICE half: it owns the session, the bay, the sockets and the windows,
    /// and implements <see cref="ICueExecutionHost"/> over them. The decisions live in
    /// <see cref="CueExecutor"/>, which is the code with the most at stake and had no test behind it
    /// while it could only be reached through a running session.
    /// </remarks>
    private CueExecutor Executor => _executor ??= new CueExecutor(this);

    private CueExecutor? _executor;

    HaCueProject ICueExecutionHost.Project => _project;

    bool ICueExecutionHost.IsExternalTriggerActive => Volatile.Read(ref _externalTriggerDepth) > 0;

    IReadOnlyList<Guid> ICueExecutionHost.Sounding => SoundingIds();

    void ICueExecutionHost.Report(string problem) => Report(problem);

    void ICueExecutionHost.MarkFading(Guid cueId) => MarkFading(cueId);

    void ICueExecutionHost.Forget(Guid cueId) => Forget(cueId.ToString());

    async Task ICueExecutionHost.SetStandbyAsync(CueList list, Guid? cueId)
    {
        await _session.SetStandbyCueAsync(cueId?.ToString(), ShowCompiler.GroupId(list)).ConfigureAwait(false);
        if (_project.CueLists.FirstOrDefault(candidate => candidate.Id == list.Id) is { } runtimeList)
            runtimeList.StandbyCueId = cueId;
    }

    /// <summary>The executor's route to a stop. Same two halves as the operator's, for the same reason.</summary>
    async Task ICueExecutionHost.StopCueAsync(Guid cueId)
    {
        _outbound.Interrupt(cueId);
        await _visualizers.StopAsync(cueId).ConfigureAwait(false);
        await _session.StopCueAsync(cueId.ToString()).ConfigureAwait(false);
        Executor.OnStopped(cueId);
    }

    Task<string?> ICueExecutionHost.SendActionAsync(ActionCueNode action, ActionEndpoint? endpoint) =>
        _actions.SendAsync(action, endpoint);

    /// <summary>A wait that reports whether it completed, so a cancelled show stops its chains.</summary>
    async Task<bool> ICueExecutionHost.DelayAsync(TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration, _life.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hands a playable cue to the session and starts its clock.
    /// </summary>
    /// <remarks>
    /// A VISUALIZER is playable but is not a clip: it has nothing to decode and nothing to seek, so it
    /// takes the composition-visualizer seam instead. It still counts as sounding — it is holding a
    /// canvas, it appears in the Active panel, and STOP has to be able to take it down.
    /// </remarks>
    async Task<bool> ICueExecutionHost.PlayAsync(
        CueNode cue,
        CueList? list,
        TimeSpan? crossfade,
        FadeShape crossfadeCurve)
    {
        if (cue is VisualizerCueNode visualizer)
        {
            var problem = await _visualizers.FireAsync(_project, visualizer).ConfigureAwait(false);

            if (problem is not null)
                Report(problem);

            // A partial start is still a start: the canvases that came up are showing something, and
            // the ones that did not have just been reported by name.
            if (!_visualizers.Running.Contains(cue.Id))
                return false;

            // A visualizer holds a renderer rather than a transport, so it has no group to seek.
            Remember(cue.Id, list?.Id ?? Guid.Empty, groupId: "");
            _outbound.Start(cue, TimeSpan.FromMilliseconds(Math.Max(1, visualizer.HoldMs)));
            return true;
        }

        // Sounding from the moment of the GO, not the moment the decoder finished opening: a cold
        // file's open takes long enough that an Active panel waiting for it reads as a GO that did
        // not take. A fire that fails takes the entry straight back down.
        var group = GroupOf(cue.Id);
        Remember(cue.Id, list?.Id ?? Guid.Empty, group);

        var status = await _session.FireCueAsync(
            cue.Id.ToString(), crossfade, crossfadeCurve).ConfigureAwait(false);

        if (status != CueExecutionStatus.Fired)
        {
            Forget(cue.Id.ToString());
            if (status == CueExecutionStatus.Failed)
                Report($"“{cue.Label}” did not fire");

            return false;
        }

        // A re-fire's displaced voice tears down DURING the fire and its Forget can race the entry
        // away; this reasserts it without touching a surviving fire-start stamp.
        ConfirmSounding(cue.Id, list?.Id ?? Guid.Empty, group);
        if (PlayedLength(cue) is { } duration)
            _outbound.Start(cue, duration);
        return true;
    }

    /// <summary>Prepares timeline media without exposing it, then releases it on the scheduler's edge.</summary>
    async Task<IReadOnlyList<Guid>> ICueExecutionHost.PlayTimelineMediaAsync(
        IReadOnlyList<TimelineMediaStart> cues,
        CueList? list,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken)
    {
        var targets = cues
            .Select(start => (Start: start, Group: GroupOf(start.Cue.Id)))
            .Where(item => item.Group.Length > 0)
            .ToList();

        if (targets.Count == 0)
        {
            await waitForStartEdge(cancellationToken).ConfigureAwait(false);
            return [];
        }

        IReadOnlyList<CueExecutionStatus> statuses;
        try
        {
            statuses = await _session.FireCuesIndependentScheduledAsync(
                    [.. targets.Select(item => new ScheduledCueStart(
                        item.Start.Cue.Id.ToString(), item.Group, item.Start.StartPosition))],
                    waitForStartEdge,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Report($"“{list?.Name ?? "a timeline"}” could not prepare its scheduled media — {failure.Message}");
            return [];
        }

        var started = new List<Guid>();
        for (var index = 0; index < targets.Count; index++)
        {
            var (start, group) = targets[index];
            var cue = start.Cue;
            if (statuses[index] != CueExecutionStatus.Fired)
            {
                if (statuses[index] == CueExecutionStatus.Failed)
                    Report($"“{cue.Label}” did not fire at its timeline position");
                continue;
            }

            // Unlike an operator GO, timeline preparation is not a visible/sounding state. Stamp the Active
            // entry only after the master edge has released the clock and the hidden/silent hold is gone.
            ConfirmSounding(cue.Id, list?.Id ?? Guid.Empty, group);
            if (PlayedLength(cue) is { } fullDuration)
            {
                // The model's media→cue mapping (MediaCueNode.CueTimeAt), not hand-rolled trim
                // arithmetic - asymmetric copies of this mapping are the trimmed-cue bug class.
                var elapsed = start.StartPosition is { } position
                    ? cue is MediaCueNode media ? media.CueTimeAt(position) : position
                    : TimeSpan.Zero;
                var remaining = fullDuration - (elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero);
                if (remaining > TimeSpan.Zero)
                    _outbound.Start(cue, remaining);
            }
            started.Add(cue.Id);
        }

        return started;
    }

    async Task<IReadOnlyList<Guid>> ICueExecutionHost.PlayTimelineVisualizersAsync(
        IReadOnlyList<VisualizerCueNode> cues,
        CueList? list,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken)
    {
        if (cues.Count == 0)
        {
            await waitForStartEdge(cancellationToken).ConfigureAwait(false);
            return [];
        }

        IReadOnlyList<Guid> started;
        IReadOnlyList<string> problems;
        try
        {
            (started, problems) = await _visualizers.FireScheduledAsync(
                    _project, cues, waitForStartEdge, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Report($"scheduled visualizers could not prepare — {failure.Message}");
            return [];
        }
        foreach (var problem in problems)
            Report(problem);

        foreach (var cue in cues.Where(cue => started.Contains(cue.Id)))
        {
            Remember(cue.Id, list?.Id ?? Guid.Empty, groupId: "");
            _outbound.Start(cue, TimeSpan.FromMilliseconds(Math.Max(1, cue.HoldMs)));
        }

        return started;
    }

    private TimeSpan? PlayedLength(CueNode cue)
    {
        if (cue is TextCueNode { DurationMs: > 0 } text)
            return TimeSpan.FromMilliseconds(text.DurationMs);

        if (cue is MediaCueNode media
            && _durations is not null
            && _durations.TryGetValue(cue.Id, out var fileLength))
            return media.TrimmedLength(fileLength) is { } duration && duration > TimeSpan.Zero
                ? duration : null;

        return null;
    }

    /// <summary>
    /// Rewrites a sounding cue's send gains to a new level.
    /// </summary>
    /// <remarks>
    /// This is the live send path, so it changes what a voice is doing without reopening it. The cue's
    /// authored per-send gains are kept as the SHAPE — the fade moves the whole cue, so a send trimmed
    /// 6 dB below its neighbour stays 6 dB below it.
    /// </remarks>
    async Task ICueExecutionHost.SetCueLevelAsync(Guid cueId, double levelDb)
    {
        if (_project.FindCue(cueId) is not MediaCueNode media)
            return;

        var sends = media.Sends
            .Select(send => new ShowClipLogicalSend(
                send.SourceChannel,
                send.LogicalChannelId.ToString(),
                send.Muted || levelDb <= GainRange.SilenceFloorDb
                    ? 0f
                    : (float)Math.Pow(10, (send.GainDb + levelDb) / 20)))
            .ToList();

        await _session.ApplyActiveLogicalSendsAsync(cueId.ToString(), sends).ConfigureAwait(false);
    }

    async Task ICueExecutionHost.FadeCueAsync(
        Guid cueId,
        double levelDb,
        TimeSpan duration,
        FadeShape curve,
        bool stopWhenSilent)
    {
        var linear = levelDb <= GainRange.SilenceFloorDb
            ? 0f
            : (float)Math.Pow(10, levelDb / 20);
        await _session.FadeClipAsync(
                cueId.ToString(), linear, duration, curve, stopWhenSilent, alsoFadeVideo: true)
            .ConfigureAwait(false);
    }

    /// <summary>Feeds the bay a series of intermediate patches, landing exactly on the destination.</summary>
    async Task ICueExecutionHost.ApplyPatchAsync(
        IReadOnlyList<PatchCell> origin,
        IReadOnlyList<PatchCell> destination,
        TimeSpan duration,
        FadeShape curve)
    {
        var before = origin.ToDictionary(
            cell => (cell.LogicalChannelId, cell.LineId, cell.LineChannel));
        var changed = destination
            .Where(cell => !before.TryGetValue(
                               (cell.LogicalChannelId, cell.LineId, cell.LineChannel), out var old)
                           || old.GainDb != cell.GainDb
                           || old.Muted != cell.Muted)
            .Select(cell => new RuntimePatchChange(
                cell.LogicalChannelId,
                cell.LineId,
                cell.LineChannel,
                cell.GainDb,
                cell.Muted))
            .ToArray();

        if (changed.Length > 0)
            DocumentChangedByCue?.Invoke(new RuntimeDocumentChange(Patch: changed));

        var steps = PatchRamp.StepsFor(duration);

        for (var step = 1; step <= steps; step++)
        {
            // The LAST step pushes the destination itself rather than a blend at progress 1, so the
            // live patch is bit-for-bit what the document says however the arithmetic rounded.
            var cells = step == steps
                ? destination
                : PatchRamp.Blend(origin, destination, (double)step / steps, curve);

            foreach (var failure in _bay.Apply(_project, cells))
                Report(failure);

            if (step == steps)
                break;

            try
            {
                await Task.Delay(PatchRamp.Step, _life.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>What a probe said this cue's media runs for, from the last reload.</summary>
    TimeSpan? ICueExecutionHost.MediaLength(Guid cueId) =>
        _durations is { } durations && durations.TryGetValue(cueId, out var length) ? length : null;

    IPlaybackClock ICueExecutionHost.TimelineClock => _bay.Bay.MasterClock;

    bool ICueExecutionHost.TimelinePaused => IsPaused;

    TimeSpan ICueExecutionHost.TimelinePausedElapsed => TimelinePausedElapsed;

    async Task ICueExecutionHost.DelayTimelineAsync(
        TimeSpan duration, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _life.Token);
        await Task.Delay(duration, linked.Token).ConfigureAwait(false);
    }
}
