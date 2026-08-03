using System.Diagnostics;
using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Present.SDL3;
using S.Media.Decode.FFmpeg;
using S.Media.Routing;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>One cue that is holding a voice, and how far into it the show is.</summary>
/// <param name="Elapsed">Wall time since it fired — the clock the Active panel counts up.</param>
/// <param name="IsFading">Whether something has asked it to come down.</param>
public sealed record ActiveCueState(Guid CueId, Guid ListId, TimeSpan Elapsed, bool IsFading);

/// <summary>What the show is doing right now, as one snapshot.</summary>
/// <param name="Sounding">Cue ids currently playing.</param>
/// <param name="Standby">Per cue-list, the cue GO would fire next.</param>
/// <param name="Active">The same cues as <paramref name="Sounding"/>, with their clocks.</param>
public sealed record ShowState(
    IReadOnlySet<Guid> Sounding,
    IReadOnlyDictionary<Guid, Guid> Standby,
    IReadOnlyList<ActiveCueState> Active,
    bool IsPaused,
    IReadOnlyList<string> Problems)
{
    public static ShowState Idle { get; } = new(
        new HashSet<Guid>(), new Dictionary<Guid, Guid>(), [], false, []);
}

/// <summary>
/// The running show: a session, the project patch bay, and the document that joins them.
/// </summary>
/// <remarks>
/// <para>
/// One object the app starts and stops. Everything thread-affine and device-holding lives behind it,
/// and the UI never touches <c>ShowSession</c> directly — it asks for a <see cref="ShowState"/> and
/// calls transport verbs.
/// </para>
/// <para>
/// <b>Reloads preserve.</b> Every edit recompiles and reloads the whole document, so
/// <c>preserveActiveGroups</c> and <c>preserveMatchingCompositions</c> are ON: a group whose voices
/// are all still described unchanged keeps playing, and GO cursors survive regardless.
/// </para>
/// <para>
/// <b>What a cue MEANS is decided here.</b> The compiled document carries a <c>CueDefinition</c> for
/// every cue so the session's cursor can stand on any of them, but only media and visualizer cues have
/// anything to play. Groups, jumps, fades, patches, actions and comments are resolved by this class
/// when they fire — the session has no vocabulary for them and should not grow one.
/// </para>
/// </remarks>
public sealed class ShowHost : IAsyncDisposable
{
    /// <summary>
    /// How deep a chain of auto-continues and jumps may run from one GO.
    /// </summary>
    /// <remarks>
    /// A jump back to its own list plus auto-continue is a legal way to author a loop and an equally
    /// legal way to author an infinite one. The bound turns "the app hangs on GO" into one reported
    /// line, which is the difference between a bug somebody can see and one they cannot.
    /// </remarks>
    private const int MaxChainDepth = 64;

    private readonly MediaRegistry _registry;
    private readonly ProjectPatchBay _bay;
    private readonly ProjectVideoOutputs _screens;
    private readonly ShowSession _session;
    private readonly HashSet<string> _attached = [];
    private readonly ActionSender _actions = new();
    private readonly Dictionary<Guid, Sounding> _sounding = [];
    private readonly List<string> _runtimeProblems = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _life = new();
    private HaCueProject _project;
    private bool _paused;

    /// <summary>A cue that is holding a voice: when it started, and where it came from.</summary>
    private readonly record struct Sounding(long StartedTicks, Guid ListId, bool IsFading);

    private ShowHost(
        MediaRegistry registry,
        ProjectPatchBay bay,
        ProjectVideoOutputs screens,
        ShowSession session,
        HaCueProject project)
    {
        _registry = registry;
        _bay = bay;
        _screens = screens;
        _session = session;
        _project = project;
    }

    /// <summary>
    /// Lines that would not open, and anything else the operator should be told.
    /// </summary>
    /// <remarks>
    /// Two sources, one list: what the bay could not open when the show started, and what a cue could
    /// not do since. A patch cue whose snapshot lost a channel and an action cue that could not reach
    /// its desk are both things nobody finds out about from a cue list that looks like it fired.
    /// </remarks>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_gate)
                return [.. _bay.Failures, .. _runtimeProblems];
        }
    }

    /// <summary>
    /// Raised when a cue changed the DOCUMENT — a patch cue's cells, or a fade cue's target levels.
    /// </summary>
    /// <remarks>
    /// These writes are deliberately not journaled: firing a cue during a show is not an edit, and an
    /// undo stack full of "the show changed the patch" would bury every real change the operator made.
    /// But they do travel in the file, so the shell has to learn that the project now differs from it —
    /// a document that changed and still reports itself clean is how a night's patch work is lost.
    /// </remarks>
    public event Action? DocumentChangedByCue;

    /// <summary>Forgets the runtime half of <see cref="Problems"/> — the Diagnostics reset.</summary>
    public void ClearProblems()
    {
        lock (_gate)
            _runtimeProblems.Clear();
    }

    /// <summary>
    /// The bay's own counters: terminals, leases, clock and per-logical-output levels.
    /// </summary>
    /// <remarks>
    /// A snapshot, taken on demand. The Diagnostics window and the Output info drawer both read it,
    /// and both get the same numbers because there is one source rather than two collectors that can
    /// disagree about what "dropped" means.
    /// </remarks>
    public AudioPatchBayDiagnostics Diagnostics() => _bay.Bay.SnapshotDiagnostics();

    /// <summary>The whole bay as plain text — what "Copy report" puts on the clipboard.</summary>
    public string Report() =>
        AudioPatchBayReport.Render(Diagnostics(), $"HaCue2 · {_project.Title}")
        + (Problems.Count == 0 ? "" : "\nProblems\n  " + string.Join("\n  ", Problems) + "\n");

    private void Report(string problem)
    {
        lock (_gate)
        {
            // Newest first and bounded: an endpoint that is down fails on every fire, and an unbounded
            // list would grow all night and bury the first, most diagnostic occurrence.
            _runtimeProblems.Remove(problem);
            _runtimeProblems.Insert(0, problem);

            if (_runtimeProblems.Count > 32)
                _runtimeProblems.RemoveRange(32, _runtimeProblems.Count - 32);
        }
    }

    /// <summary>
    /// Starts a session for a project.
    /// </summary>
    /// <remarks>
    /// The backend is injected for the same reason the device enumerator's is: which one to open is a
    /// composition-root decision, and passing null gives a session that can still be driven — useful
    /// for a test, and honest on a machine with no audio at all.
    /// </remarks>
    /// <param name="headless">
    /// No display: video outputs are reported as unopened rather than attempted. What a CI box, a
    /// preview and a booth machine with the projector unplugged all are.
    /// </param>
    public static async Task<ShowHost> StartAsync(
        HaCueProject project,
        IAudioBackend? backend,
        IReadOnlyDictionary<Guid, TimeSpan>? durations = null,
        bool headless = false)
    {
        ArgumentNullException.ThrowIfNull(project);

        var registry = MediaRegistry.Build(builder => builder.Use(new FFmpegModule()));
        var bay = ProjectPatchBay.Open(project, backend);
        var screens = ProjectVideoOutputs.OpenAll(project, headless);

        var target = new PatchBayShowProgramAudioTarget(
            bay.Bay,
            bay.LogicalChannelIds,
            defaultMonitorTerminalId: bay.MonitorTerminalId);

        var session = new ShowSession(registry, backend, programAudioTarget: target);
        var host = new ShowHost(registry, bay, screens, session, project);

        foreach (var failure in screens.Failures)
            host.Report(failure);

        // Sounding is tracked HERE rather than queried: the session's sounding bus is keyed by label,
        // and the events that matter carry the cue id. A fire adds, a natural end removes, and a stop
        // clears — which is the whole life of a cue as far as the cue list is concerned.
        session.ClipNaturallyEnded += id => host.Forget(id);
        session.VoiceEnded += id => host.Forget(id);

        // A preview that runs to its end releases itself, so the host has to stop claiming it is
        // auditioning — otherwise the button stays lit over a rig that is already silent.
        session.PreviewEnded += id =>
        {
            if (!Guid.TryParse(id, out var previewed))
                return;

            lock (host._gate)
            {
                if (host._previewing == previewed)
                    host._previewing = Guid.Empty;
            }
        };

        await host.ReloadAsync(project, durations).ConfigureAwait(false);
        return host;
    }

    /// <summary>
    /// Recompiles the project and hands it to the session, without stopping what is unaffected.
    /// </summary>
    /// <remarks>
    /// Called after every edit. The preservation flags are what make that safe: a group survives only
    /// when every voice it holds still maps to an identical clip binding, so a reload can never leave
    /// something playing content the document no longer describes.
    /// <para>
    /// The PATCH is pushed separately, because it is not in the document: the second matrix belongs to
    /// the rig. Pushing it here is what makes a patch edit audible without reopening anything —
    /// <see cref="ProjectPatchBay.Apply"/> reconciles cells under running voices.
    /// </para>
    /// </remarks>
    public async Task ReloadAsync(
        HaCueProject project, IReadOnlyDictionary<Guid, TimeSpan>? durations = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var previous = _project;
        _project = project;
        ForgetDetachedScreens(previous, project);

        await _session.LoadDocumentAsync(
            ShowCompiler.Compile(project, durations),
            preserveMatchingCompositions: true,
            preserveActiveGroups: true).ConfigureAwait(false);

        foreach (var failure in _bay.Apply(project))
            Report(failure);

        await AttachScreensAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches each open window to the composition it shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after every load, because <c>preserveMatchingCompositions</c> preserves the ones whose
    /// definition is unchanged and rebuilds the rest — an output attached to a rebuilt composition is
    /// no longer attached to anything, and its window would sit black for the rest of the show with
    /// nothing to say why.
    /// </para>
    /// <para>
    /// Idempotent: an output already attached to a surviving composition is skipped rather than
    /// re-added, so an edit that touches nothing visual costs nothing here.
    /// </para>
    /// </remarks>
    private async Task AttachScreensAsync()
    {
        foreach (var (compositionId, lease) in _screens.Leases(_project))
        {
            if (_attached.Contains(lease.OutputId))
                continue;

            if (await _session.AddCompositionOutputAsync(compositionId, lease).ConfigureAwait(false))
            {
                _attached.Add(lease.OutputId);
                continue;
            }

            Report($"“{lease.DisplayName}” could not be attached to its composition");
        }

    }

    /// <summary>
    /// Forgets attachments whose composition the session no longer has.
    /// </summary>
    /// <remarks>
    /// A reload rebuilds any composition whose definition changed, which silently detaches its
    /// outputs. Without this the host would believe they were still attached and never re-add them —
    /// so resizing a composition would blank its projector permanently.
    /// </remarks>
    private void ForgetDetachedScreens(HaCueProject previous, HaCueProject current)
    {
        foreach (var open in _screens.Open)
        {
            var before = previous.Compositions.FirstOrDefault(item => item.Id == open.CompositionId);
            var after = current.Compositions.FirstOrDefault(item => item.Id == open.CompositionId);

            // Size and rate are what the session keys "unchanged" on; a renamed composition is
            // preserved and keeps its outputs.
            if (before is null || after is null
                || before.Width != after.Width
                || before.Height != after.Height
                || Math.Abs(before.FramesPerSecond - after.FramesPerSecond) > 0.001)
                _attached.Remove(open.OutputId);
        }
    }


    // ── transport ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires the standby cue of a list and advances its cursor.
    /// </summary>
    /// <remarks>
    /// The cursor is read BEFORE anything fires, because firing is what moves it. The cue is then
    /// resolved by kind here rather than handed to <c>ShowSession.GoAsync</c>: the session would fire a
    /// jump cue as a clip with nothing to play and call that success.
    /// </remarks>
    public async Task<Guid?> GoAsync(CueList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        var standby = await _session.GetStandbyCueAsync(ShowCompiler.GroupId(list)).ConfigureAwait(false);

        if (standby is null || !Guid.TryParse(standby.Id, out var id))
            return null;

        await AdvanceAsync(list, id).ConfigureAwait(false);
        await FireAsync(id, depth: 0).ConfigureAwait(false);
        return id;
    }

    /// <summary>Puts a list's cursor on a cue without firing it.</summary>
    public Task<bool> StandbyAsync(CueList list, Guid? cueId)
    {
        ArgumentNullException.ThrowIfNull(list);
        return _session.SetStandbyCueAsync(cueId?.ToString(), ShowCompiler.GroupId(list));
    }

    /// <summary>Fires one cue by id, whatever the cursor is doing.</summary>
    public Task<bool> FireAsync(Guid cueId) => FireAsync(cueId, depth: 0);

    /// <summary>
    /// Fires one cue, resolved by what kind of cue it is.
    /// </summary>
    /// <param name="depth">
    /// How many cues deep into one operator GO this is. Auto-continue and jump-on-arrival both recurse
    /// through here, and <see cref="MaxChainDepth"/> is what stops an authored loop becoming a hang.
    /// </param>
    private async Task<bool> FireAsync(Guid cueId, int depth)
    {
        if (depth > MaxChainDepth)
        {
            Report($"the chain from this GO ran past {MaxChainDepth} cues and was stopped — check for a jump loop");
            return false;
        }

        if (_project.FindCue(cueId) is not { } cue)
            return false;

        // A disabled cue is stepped over wherever it is reached from, not only by GO: an auto-follow
        // chain and a jump have to agree with the cue list about what is in the show tonight.
        if (!cue.Enabled)
            return false;

        var list = _project.ListOf(cueId);

        // The pre-wait belongs to every kind, not just the ones that play something: "wait two seconds,
        // then tell the lighting desk" is an ordinary thing to author.
        if (cue.PreWaitMs > 0)
        {
            try
            {
                await Task.Delay(cue.PreWaitMs, _life.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        var fired = cue switch
        {
            MediaCueNode or VisualizerCueNode => await PlayAsync(cue, list).ConfigureAwait(false),
            GroupCueNode group => await FireGroupAsync(group, list, depth).ConfigureAwait(false),
            JumpCueNode jump => await JumpAsync(jump, depth).ConfigureAwait(false),
            PatchCueNode patch => await PatchAsync(patch).ConfigureAwait(false),
            FadeCueNode fade => await FadeAsync(fade).ConfigureAwait(false),
            ActionCueNode action => await ActAsync(action).ConfigureAwait(false),
            // A comment cue is its note. Firing one is a no-op that still SUCCEEDS, so an
            // auto-continue chain runs straight through it rather than stopping on a marker.
            _ => true,
        };

        if (!fired)
            return false;

        // Auto-continue is resolved here for every kind. The session chains on a clip's natural end,
        // which a jump or a comment never has — left to the session those chains would simply stall.
        if (cue.Trigger == CueTrigger.Continue
            && list is not null
            && CueOrder.NextEnabled(list, cueId) is { } next)
        {
            if (cue.PostWaitMs > 0)
            {
                try
                {
                    await Task.Delay(cue.PostWaitMs, _life.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return true;
                }
            }

            await AdvanceAsync(list, next.Id).ConfigureAwait(false);
            await FireAsync(next.Id, depth + 1).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Hands a playable cue to the session and starts its clock.</summary>
    private async Task<bool> PlayAsync(CueNode cue, CueList? list)
    {
        var status = await _session.FireCueAsync(cue.Id.ToString()).ConfigureAwait(false);

        if (status != CueExecutionStatus.Fired)
        {
            if (status == CueExecutionStatus.Failed)
                Report($"“{cue.Label}” did not fire");

            return false;
        }

        Remember(cue.Id, list?.Id ?? Guid.Empty);
        return true;
    }

    /// <summary>
    /// Fires a group according to its fire mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The group itself holds no voice — its CHILDREN do — so the group is not remembered as sounding.
    /// The Active panel shows the children, which is what is actually making noise.
    /// </para>
    /// <para>
    /// <b>All together</b> fires every enabled child at once. <b>Playlist</b> fires the first and lets
    /// each child's natural end chain to the next. <b>Timeline</b> fires each child at its authored
    /// offset, on the show's own clock rather than by chaining, because a timeline's whole point is
    /// that its cues do not depend on each other's lengths.
    /// </para>
    /// </remarks>
    private async Task<bool> FireGroupAsync(GroupCueNode group, CueList? list, int depth)
    {
        var children = group.Children.Where(child => child.Enabled).ToList();

        if (children.Count == 0)
            return true;

        switch (group.FireMode)
        {
            case GroupFireMode.Playlist:
                var first = group.Shuffle ? children[Random.Shared.Next(children.Count)] : children[0];
                return await FireAsync(first.Id, depth + 1).ConfigureAwait(false);

            case GroupFireMode.Timeline:
                foreach (var child in children)
                    Schedule(child, TimeSpan.FromMilliseconds(child.TimelineOffsetMs), depth);

                return true;

            default:
                // Sequentially awaited rather than fanned out with Task.WhenAll: the session runs
                // commands on one dispatcher, so concurrent fires queue behind each other anyway, and
                // in order means the group's layer order is the order the canvas receives them in.
                foreach (var child in children)
                    await FireAsync(child.Id, depth + 1).ConfigureAwait(false);

                return true;
        }
    }

    /// <summary>Fires a cue later, on the group's own clock. Cancelled with the show.</summary>
    private void Schedule(CueNode cue, TimeSpan when, int depth)
    {
        if (when <= TimeSpan.Zero)
        {
            _ = FireAsync(cue.Id, depth + 1);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(when, _life.Token).ConfigureAwait(false);
                await FireAsync(cue.Id, depth + 1).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The show stopped before this cue's moment arrived. Nothing to report.
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>
    /// Moves a list's cursor, and optionally fires what it lands on.
    /// </summary>
    /// <remarks>
    /// The target may be in ANOTHER list — jumping from a preshow list into act one is the ordinary
    /// use — so the list is resolved from the target cue rather than assumed to be the jump's own.
    /// </remarks>
    private async Task<bool> JumpAsync(JumpCueNode jump, int depth)
    {
        var targets = jump.TargetCueIds
            .Select(_project.FindCue)
            .OfType<CueNode>()
            .Where(cue => cue.Enabled)
            .ToList();

        if (targets.Count == 0)
        {
            Report($"“{jump.Label}” has no live target — the jump did nothing");
            return false;
        }

        var target = jump.PickAtRandom ? targets[Random.Shared.Next(targets.Count)] : targets[0];

        if (_project.ListOf(target.Id) is not { } list)
            return false;

        await AdvanceAsync(list, target.Id).ConfigureAwait(false);

        if (jump.FireOnArrival)
            await FireAsync(target.Id, depth + 1).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Applies a patch cue: a snapshot recall, inline level changes, or both.
    /// </summary>
    /// <remarks>
    /// The document is written ONCE, with the destination values, and the audible move is a ramp the
    /// bay is fed frame by frame. The write is deliberately not journaled — firing a cue during a show
    /// is not an edit, and an undo stack full of "the show changed the patch" would bury every real
    /// change the operator made. It is the same rule the standby cursor already follows.
    /// </remarks>
    private async Task<bool> PatchAsync(PatchCueNode patch)
    {
        // The state to ramp FROM has to be copied before the recall overwrites it — the cells are live
        // objects, and holding references would give us the destination twice.
        var origin = _project.AudioPatch.Cells.Select(cell => cell with { }).ToList();

        var applied = 0;
        var broken = new List<BrokenBinding>();

        if (patch.SnapshotId is { } snapshotId)
        {
            var recall = PatchOperations.Recall(_project, snapshotId);
            applied += recall.CellsApplied;
            broken.AddRange(recall.Broken);
        }

        if (patch.Levels.Count > 0)
        {
            var levels = PatchOperations.ApplyLevels(_project, patch.Levels);
            applied += levels.CellsApplied;
            broken.AddRange(levels.Broken);
        }

        foreach (var failure in broken)
            Report($"“{patch.Label}”: {failure.Reason}");

        if (applied == 0)
            return broken.Count == 0;

        var destination = _project.AudioPatch.Cells.Select(cell => cell with { }).ToList();
        DocumentChangedByCue?.Invoke();

        await RampPatchAsync(origin, destination, TimeSpan.FromMilliseconds(patch.FadeMs))
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>Feeds the bay a series of intermediate patches, landing exactly on the destination.</summary>
    private async Task RampPatchAsync(
        IReadOnlyList<PatchCell> origin, IReadOnlyList<PatchCell> destination, TimeSpan duration)
    {
        var steps = PatchRamp.StepsFor(duration);

        for (var step = 1; step <= steps; step++)
        {
            // The LAST step pushes the destination itself rather than a blend at progress 1, so the
            // live patch is bit-for-bit what the document says however the arithmetic rounded.
            var cells = step == steps ? destination : PatchRamp.Blend(origin, destination, (double)step / steps);

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

    /// <summary>
    /// Runs a fade cue over its targets.
    /// </summary>
    /// <remarks>
    /// Two kinds of target, two mechanisms. CUES ride the session's own stop, which fades the voice and
    /// releases it — and when the fade is to something audible rather than to silence, the level is
    /// what changes and the voice keeps playing. LOGICAL OUTPUTS are the patch, so they ramp through
    /// the same path a patch cue uses; the two cannot disagree because they are the same code.
    /// </remarks>
    private async Task<bool> FadeAsync(FadeCueNode fade)
    {
        var duration = TimeSpan.FromMilliseconds(fade.DurationMs);
        var toSilence = fade.ToLevelDb <= GainRange.SilenceFloorDb;

        var cues = fade.FadeEverythingSounding
            ? SoundingIds()
            : [.. fade.TargetCueIds.Where(id => _project.FindCue(id) is not null)];

        if (fade.TargetChannelIds.Count > 0)
        {
            var origin = _project.AudioPatch.Cells.Select(cell => cell with { }).ToList();
            var destination = origin
                .Select(cell => fade.TargetChannelIds.Contains(cell.LogicalChannelId)
                    ? cell with { GainDb = fade.ToLevelDb, Muted = toSilence }
                    : cell)
                .ToList();

            // The document keeps what the fade landed on: a fade cue that left the patch at a level
            // the file disagrees with would be undone by the next unrelated reload.
            foreach (var cell in _project.AudioPatch.Cells.Where(
                cell => fade.TargetChannelIds.Contains(cell.LogicalChannelId)))
            {
                cell.GainDb = fade.ToLevelDb;
                cell.Muted = toSilence;
            }

            DocumentChangedByCue?.Invoke();
            await RampPatchAsync(origin, destination, duration).ConfigureAwait(false);
        }

        foreach (var cueId in cues)
        {
            MarkFading(cueId);

            if (toSilence && fade.StopTargetsWhenComplete)
            {
                await _session.StopCueAsync(cueId.ToString()).ConfigureAwait(false);
                Forget(cueId.ToString());
            }
            else
            {
                await SetCueLevelAsync(cueId, fade.ToLevelDb).ConfigureAwait(false);
            }
        }

        return cues.Count > 0 || fade.TargetChannelIds.Count > 0;
    }

    /// <summary>
    /// Rewrites a sounding cue's send gains to a new level.
    /// </summary>
    /// <remarks>
    /// This is the live send path, so it changes what a voice is doing without reopening it. The cue's
    /// authored per-send gains are kept as the SHAPE — the fade moves the whole cue, so a send trimmed
    /// 6 dB below its neighbour stays 6 dB below it.
    /// </remarks>
    private async Task SetCueLevelAsync(Guid cueId, double levelDb)
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

    /// <summary>Sends an action cue, reporting a refusal rather than swallowing it.</summary>
    private async Task<bool> ActAsync(ActionCueNode action)
    {
        var endpoint = action.EndpointId is { } id
            ? _project.ActionEndpoints.FirstOrDefault(item => item.Id == id)
            : null;

        if (await _actions.SendAsync(action, endpoint).ConfigureAwait(false) is { } failure)
        {
            Report(failure);
            return false;
        }

        return true;
    }

    /// <summary>Moves a list's cursor onward from the cue that just fired.</summary>
    private async Task AdvanceAsync(CueList list, Guid fired)
    {
        var next = CueOrder.NextEnabled(list, fired);
        await _session.SetStandbyCueAsync(next?.Id.ToString(), ShowCompiler.GroupId(list))
            .ConfigureAwait(false);
    }

    // ── audition (register item 15) ────────────────────────────────────────────────────────────

    private Guid _previewing;
    private IVideoOutput? _auditionWindow;

    /// <summary>The cue currently being auditioned, or null.</summary>
    /// <remarks>
    /// A preview is deliberately NOT in <see cref="ShowState.Sounding"/> and never appears in the
    /// Active list: it is monitoring, not program. An operator glancing at Active during a show must
    /// see what the audience can hear, and nothing else.
    /// </remarks>
    public Guid? Previewing
    {
        get
        {
            lock (_gate)
                return _previewing == Guid.Empty ? null : _previewing;
        }
    }

    /// <summary>
    /// Auditions a cue through the rig, replacing whatever was previewing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The endpoint is the rig's LINE, so the preview takes that line's own channel count — never a
    /// hardcoded stereo pair (D8). Null names the bay's default monitor terminal, which is what makes
    /// audition work on a one-interface rig nobody has configured.
    /// </para>
    /// <para>
    /// One at a time by construction: the framework's preview player replaces the current preview, and
    /// an operator auditioning two cues at once is hearing neither.
    /// </para>
    /// </remarks>
    public async Task<bool> PreviewAsync(Guid cueId)
    {
        if (_project.FindCue(cueId) is not MediaCueNode media)
            return false;

        if (media.MediaPath.Length == 0)
        {
            Report($"“{media.Label}” has no media to audition");
            return false;
        }

        await EnsureAuditionSurfaceAsync().ConfigureAwait(false);

        var endpoint = _project.Audition.AudioLineId?.ToString();

        try
        {
            if (!await _session.PreviewCueAsync(cueId.ToString(), endpoint).ConfigureAwait(false))
            {
                Report($"“{media.Label}” could not be auditioned");
                return false;
            }
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            // A rig pointing at a line this machine did not open. Reported by name rather than thrown:
            // the operator can pick another line, and the show is unaffected either way.
            Report($"the audition rig could not be reached — {failure.Message}");
            return false;
        }

        lock (_gate)
            _previewing = cueId;

        return true;
    }

    /// <summary>Stops the audition. Never touches the program — that is the whole point of the rig.</summary>
    public async Task StopPreviewAsync()
    {
        await _session.StopPreviewAsync().ConfigureAwait(false);

        lock (_gate)
            _previewing = Guid.Empty;
    }

    /// <summary>
    /// Brings the audition canvas up, or takes it down, to match the rig.
    /// </summary>
    /// <remarks>
    /// Done lazily on the first audition rather than at start-up: a video surface costs a window, most
    /// cues are audio, and an operator who never previews a video cue should never see one appear.
    /// </remarks>
    private async Task EnsureAuditionSurfaceAsync()
    {
        var rig = _project.Audition;

        if (rig.Surface == AuditionSurface.None)
        {
            if (_auditionWindow is not null)
                await TearDownAuditionSurfaceAsync().ConfigureAwait(false);

            return;
        }

        if (_auditionWindow is not null)
            return;

        // Sized to the rig, or to the biggest composition in the show — the monitor should not be
        // smaller than the thing it is monitoring.
        var width = rig.SurfaceWidth > 0
            ? rig.SurfaceWidth
            : _project.Compositions.Select(item => item.Width).DefaultIfEmpty(1280).Max();

        var height = rig.SurfaceHeight > 0
            ? rig.SurfaceHeight
            : _project.Compositions.Select(item => item.Height).DefaultIfEmpty(720).Max();

        try
        {
            await _session.EnableAuditionCompositionAsync(
                new AuditionCompositionSpec(width, height)).ConfigureAwait(false);

            var window = new SDL3GLVideoOutput("HaCue2 · Audition", width, height);

            if (await _session.AttachAuditionOutputAsync(window).ConfigureAwait(false))
            {
                _auditionWindow = window;
            }
            else
            {
                window.Dispose();
                Report("the audition surface could not be attached");
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // No display, no GL, no window manager. Audio auditioning still works, which is the half
            // that matters most — so this is reported and stepped past, not thrown.
            Report($"the audition surface could not be opened — {failure.Message}");
        }
    }

    private async Task TearDownAuditionSurfaceAsync()
    {
        await _session.DetachAuditionOutputAsync().ConfigureAwait(false);
        await _session.DisableAuditionCompositionAsync().ConfigureAwait(false);

        (_auditionWindow as IDisposable)?.Dispose();
        _auditionWindow = null;
    }

    // ── stopping ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops one cue — the bare STOP.
    /// </summary>
    /// <remarks>
    /// A per-cue stop rather than a stop-all, because on a show with a music bed under a video the
    /// operator who wants the video gone almost never wants the bed gone with it. Stop-all is a
    /// separate, deliberate verb.
    /// </remarks>
    public async Task StopCueAsync(Guid cueId)
    {
        MarkFading(cueId);
        await _session.StopCueAsync(cueId.ToString()).ConfigureAwait(false);
        Forget(cueId.ToString());
    }

    /// <summary>Stops everything, fading over the project's stop fade.</summary>
    public Task StopAllAsync() =>
        StopEverythingAsync(TimeSpan.FromMilliseconds(_project.Settings.StopFadeMs));

    /// <summary>
    /// PANIC: stops everything over the project's panic fade.
    /// </summary>
    /// <remarks>
    /// A fade rather than a cut, and a SHORT one — the setting defaults to 250 ms. A true hard cut
    /// through a big PA is a thump that can damage drivers, so "as fast as is safe" is the honest
    /// reading of panic, and the number stays the operator's to set.
    /// </remarks>
    public Task PanicAsync() =>
        StopEverythingAsync(TimeSpan.FromMilliseconds(_project.Settings.PanicFadeMs));

    private async Task StopEverythingAsync(TimeSpan fade)
    {
        lock (_gate)
        {
            foreach (var id in _sounding.Keys.ToList())
                _sounding[id] = _sounding[id] with { IsFading = true };
        }

        await _session.StopAllAsync(fade).ConfigureAwait(false);

        lock (_gate)
            _sounding.Clear();
    }

    /// <summary>Pauses or resumes the show.</summary>
    public async Task SetPausedAsync(bool paused)
    {
        await _session.SetPausedAsync(paused).ConfigureAwait(false);

        lock (_gate)
            _paused = paused;
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _paused;
        }
    }

    // ── what is sounding ──────────────────────────────────────────────────────────────────────

    private void Remember(Guid cueId, Guid listId)
    {
        lock (_gate)
            _sounding[cueId] = new Sounding(Stopwatch.GetTimestamp(), listId, IsFading: false);
    }

    private void MarkFading(Guid cueId)
    {
        lock (_gate)
        {
            if (_sounding.TryGetValue(cueId, out var entry))
                _sounding[cueId] = entry with { IsFading = true };
        }
    }

    private void Forget(string cueId)
    {
        if (!Guid.TryParse(cueId, out var id))
            return;

        lock (_gate)
            _sounding.Remove(id);
    }

    private List<Guid> SoundingIds()
    {
        lock (_gate)
            return [.. _sounding.Keys];
    }

    /// <summary>
    /// What the show is doing, for the views.
    /// </summary>
    /// <remarks>
    /// Pulled rather than pushed: the session raises events on its own thread and the UI wants a
    /// consistent picture at a moment of its choosing, not a stream of edges to reassemble.
    /// </remarks>
    public async Task<ShowState> SnapshotAsync()
    {
        List<ActiveCueState> active;
        HashSet<Guid> sounding;
        bool paused;

        lock (_gate)
        {
            sounding = [.. _sounding.Keys];
            paused = _paused;
            active =
            [
                .. _sounding.Select(entry => new ActiveCueState(
                    entry.Key,
                    entry.Value.ListId,
                    Stopwatch.GetElapsedTime(entry.Value.StartedTicks),
                    entry.Value.IsFading)),
            ];
        }

        var standby = new Dictionary<Guid, Guid>();

        foreach (var list in _project.CueLists)
        {
            var cue = await _session.GetStandbyCueAsync(ShowCompiler.GroupId(list)).ConfigureAwait(false);

            if (cue is not null && Guid.TryParse(cue.Id, out var id))
                standby[list.Id] = id;
        }

        return new ShowState(sounding, standby, active, paused, Problems);
    }

    public async ValueTask DisposeAsync()
    {
        // Cancelled FIRST: scheduled timeline cues and in-flight pre-waits have to stop reaching for a
        // session that is about to go away, or disposal races a fire.
        await _life.CancelAsync().ConfigureAwait(false);

        // Before the session goes: the window is attached to it, and detaching afterwards would be
        // asking a disposed session to release something.
        if (_auditionWindow is not null)
        {
            try
            {
                await TearDownAuditionSurfaceAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (_auditionWindow as IDisposable)?.Dispose();
                _auditionWindow = null;
            }
        }

        await _session.DisposeAsync().ConfigureAwait(false);
        _actions.Dispose();
        _bay.Dispose();
        // After the session, because the leases declare DisposeOutputOnRuntimeDispose:false — the host
        // owns these windows and closes them itself, once the session has stopped submitting to them.
        _screens.Dispose();
        _registry.Dispose();
        _life.Dispose();
    }
}
