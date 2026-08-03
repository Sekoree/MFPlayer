using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using S.Media.Core.Audio;
using S.Media.Session;

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

            Remember(cue.Id, list?.Id ?? Guid.Empty);
            _outbound.Start(cue, TimeSpan.FromMilliseconds(Math.Max(1, visualizer.HoldMs)));
            return true;
        }

        var status = await _session.FireCueAsync(
            cue.Id.ToString(), crossfade, crossfadeCurve).ConfigureAwait(false);

        if (status != CueExecutionStatus.Fired)
        {
            if (status == CueExecutionStatus.Failed)
                Report($"“{cue.Label}” did not fire");

            return false;
        }

        Remember(cue.Id, list?.Id ?? Guid.Empty);
        if (PlayedLength(cue) is { } duration)
            _outbound.Start(cue, duration);
        return true;
    }

    private TimeSpan? PlayedLength(CueNode cue)
    {
        if (cue is not MediaCueNode media
            || _durations is null
            || !_durations.TryGetValue(cue.Id, out var fileLength))
            return null;

        return media.TrimmedLength(fileLength) is { } duration && duration > TimeSpan.Zero
            ? duration : null;
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

    /// <summary>Moves a sounding cue to a position inside its own media.</summary>
    /// <remarks>
    /// Addressed by the cue's OWN transport group. A timeline's children each have one — which is what
    /// makes them layer, and also what makes it possible to move one of them without moving the rest.
    /// </remarks>
    Task ICueExecutionHost.SeekCueAsync(Guid cueId, TimeSpan position)
    {
        if (_project.ListOf(cueId) is not { } list)
            return Task.CompletedTask;

        var group = _project.AllCues().OfType<GroupCueNode>()
            .FirstOrDefault(candidate => candidate.Children.Any(child => child.Id == cueId));

        return _session.SeekAsync(
            position,
            group is null
                ? ShowCompiler.GroupId(list)
                : ShowCompiler.GroupId(list, group, _project.FindCue(cueId)!));
    }

    /// <summary>Runs a cue later, on the show's own clock. Cancelled with the show.</summary>
    void ICueExecutionHost.Schedule(Guid cueId, TimeSpan when, int depth)
    {
        if (when <= TimeSpan.Zero)
        {
            _ = Executor.FireAsync(cueId, depth + 1);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(when, _life.Token).ConfigureAwait(false);
                await Executor.FireAsync(cueId, depth + 1).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
            {
                // The show stopped before this cue's moment arrived. Nothing to report.
            }
        });
    }
}
