using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>
/// What firing a cue MEANS, for every kind.
/// </summary>
/// <remarks>
/// <para>
/// The compiled document carries a <c>CueDefinition</c> for every cue so the session's cursor can
/// stand on any of them, but only media and visualizer cues have anything to play. Groups, jumps,
/// fades, patches, actions and comments are resolved here — the session has no vocabulary for them and
/// should not grow one.
/// </para>
/// <para>
/// Every effect goes through <see cref="ICueExecutionHost"/>, so this class holds decisions and no
/// devices. That is what makes it testable, and it is the code with the most at stake in the app: it
/// decides what happens when somebody presses GO.
/// </para>
/// </remarks>
public sealed class CueExecutor(ICueExecutionHost host)
{
    /// <param name="Pass">
    /// Which pass through the list this is, one-based. Counted so <c>LoopCount</c> can end the run —
    /// "play this twice and then hold" needs a pass number, and there was nowhere to keep one.
    /// </param>
    private sealed record PlaylistRun(Guid GroupId, IReadOnlyList<Guid> Order, int Index, int Pass = 1);

    private readonly Lock _stateGate = new();
    private readonly Dictionary<Guid, PlaylistRun> _playlistRuns = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _stableShuffleOrders = [];
    private readonly Dictionary<Guid, int> _jumpVisits = [];

    /// <summary>
    /// How deep a chain of auto-continues and jumps may run from one GO.
    /// </summary>
    /// <remarks>
    /// A jump back to its own list plus auto-continue is a legal way to author a loop and an equally
    /// legal way to author an infinite one. The bound turns "the app hangs on GO" into one reported
    /// line, which is the difference between a bug somebody can see and one they cannot.
    /// </remarks>
    public const int MaxChainDepth = 64;

    private HaCueProject Project => host.Project;

    /// <summary>Fires one cue, resolved by what kind of cue it is.</summary>
    public Task<bool> FireAsync(Guid cueId) => FireAsync(cueId, depth: 0);

    /// <param name="depth">
    /// How many cues deep into one operator GO this is. Auto-continue and jump-on-arrival both recurse
    /// through here, and <see cref="MaxChainDepth"/> is what stops an authored loop becoming a hang.
    /// </param>
    public async Task<bool> FireAsync(
        Guid cueId,
        int depth,
        TimeSpan? crossfade = null,
        FadeShape crossfadeCurve = default)
    {
        if (depth > MaxChainDepth)
        {
            host.Report(
                $"the chain from this GO ran past {MaxChainDepth} cues and was stopped — check for a jump loop");
            return false;
        }

        if (Project.FindCue(cueId) is not { } cue)
            return false;

        // A disabled cue is stepped over wherever it is reached from, not only by GO: an auto-follow
        // chain and a jump have to agree with the cue list about what is in the show tonight.
        if (!cue.Enabled)
            return false;

        var list = Project.ListOf(cueId);

        // The pre-wait belongs to every kind, not just the ones that play something: "wait two seconds,
        // then tell the lighting desk" is an ordinary thing to author.
        if (cue.PreWaitMs > 0
            && !await host.DelayAsync(TimeSpan.FromMilliseconds(cue.PreWaitMs)).ConfigureAwait(false))
            return false;

        var fired = cue switch
        {
            MediaCueNode or VisualizerCueNode =>
                await host.PlayAsync(cue, list, crossfade, crossfadeCurve).ConfigureAwait(false),
            GroupCueNode group => await FireGroupAsync(group, depth).ConfigureAwait(false),
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
        if (cue.Trigger == CueTrigger.Continue && list is not null)
        {
            await ContinueFromAsync(cue, list, depth, follow: false).ConfigureAwait(false);
        }
        else if (cue.Trigger == CueTrigger.Follow
                 && cue is not MediaCueNode
                 && cue is not VisualizerCueNode
                 && list is not null)
        {
            // Instant cues have no natural-end event. Their successful completion is their end.
            await ContinueFromAsync(cue, list, depth, follow: true).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Fires a group according to its fire mode.
    /// </summary>
    /// <remarks>
    /// <b>All together</b> fires every enabled child at once. <b>Playlist</b> fires the first and lets
    /// each child's natural end chain to the next. <b>Timeline</b> fires each child at its authored
    /// offset, on the show's own clock rather than by chaining, because a timeline's whole point is
    /// that its cues do not depend on each other's lengths.
    /// <para>
    /// The group itself holds no voice — its CHILDREN do — so it is never remembered as sounding. The
    /// Active panel shows the children, which is what is actually making noise.
    /// </para>
    /// </remarks>
    private async Task<bool> FireGroupAsync(GroupCueNode group, int depth)
    {
        var children = group.Children.Where(child => child.Enabled).ToList();

        if (children.Count == 0)
            return true;

        switch (group.FireMode)
        {
            case GroupFireMode.Playlist:
                var order = PlaylistOrder(group, children);
                lock (_stateGate)
                    _playlistRuns[group.Id] = new PlaylistRun(group.Id, order, 0);
                return await FireAsync(order[0], depth + 1).ConfigureAwait(false);

            case GroupFireMode.Timeline:
                await FireTimelineAsync(group, TimeSpan.Zero, depth).ConfigureAwait(false);
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

    /// <summary>Advances Follow and playlist runs from the framework's authoritative natural-end edge.</summary>
    public async Task OnNaturalEndAsync(Guid cueId)
    {
        host.Forget(cueId);

        if (await AdvancePlaylistAsync(cueId, approaching: false).ConfigureAwait(false))
            return;

        if (Project.FindCue(cueId) is { Trigger: CueTrigger.Follow } cue
            && Project.ListOf(cueId) is { } list)
            await ContinueFromAsync(cue, list, depth: 0, follow: true).ConfigureAwait(false);
    }

    /// <summary>Starts the next playlist item early when the current clip enters its crossfade window.</summary>
    public Task OnApproachingEndAsync(Guid cueId) => AdvancePlaylistAsync(cueId, approaching: true);

    private async Task<bool> AdvancePlaylistAsync(Guid ended, bool approaching)
    {
        PlaylistRun? run = null;
        var position = -1;
        GroupCueNode? group;

        lock (_stateGate)
        {
            foreach (var candidate in _playlistRuns.Values)
            {
                for (var index = 0; index <= candidate.Index && index < candidate.Order.Count; index++)
                {
                    if (candidate.Order[index] != ended)
                        continue;

                    run = candidate;
                    position = index;
                    break;
                }

                if (run is not null)
                    break;
            }
        }

        if (run is null || Project.FindCue(run.GroupId) is not GroupCueNode found)
            return false;

        group = found;

        // A positive crossfade advances the run while the outgoing item is still alive. Its later
        // natural-end edge belongs to this playlist, but must not advance the new item or fall through
        // to the outgoing cue's ordinary Follow rule and fire the successor a second time.
        if (position < run.Index)
            return true;

        // A zero-window playlist has no useful pre-end edge. For a positive window, natural end is
        // still a fallback if a backend could not issue pre-end notification.
        if (approaching && group.CrossfadeMs <= 0)
            return false;

        var nextIndex = run.Index + 1;
        if (nextIndex >= run.Order.Count)
        {
            if (approaching)
                return true; // the final item must reach natural end before its end policy runs

            return await FinishPlaylistAsync(group, run).ConfigureAwait(false);
        }

        var advanced = run with { Index = nextIndex };
        lock (_stateGate)
            _playlistRuns[group.Id] = advanced;

        var fired = await FireAsync(
            advanced.Order[nextIndex],
            depth: 1,
            approaching ? TimeSpan.FromMilliseconds(group.CrossfadeMs) : null,
            group.CrossfadeCurve.Resolve(Project)).ConfigureAwait(false);

        if (!fired)
        {
            lock (_stateGate)
                _playlistRuns[group.Id] = run;
        }

        return true;
    }

    private async Task<bool> FinishPlaylistAsync(GroupCueNode group, PlaylistRun completed)
    {
        switch (group.AtEnd)
        {
            case AtListEnd.Loop:
            {
                var children = group.Children.Where(cue => cue.Enabled).ToList();
                if (children.Count == 0)
                    return true;

                // LoopCount is passes, and zero is forever. It is separate from AtEnd, which says what
                // happens AFTER the last pass — "play this twice and then hold" needs both.
                if (group.LoopCount > 0 && completed.Pass >= group.LoopCount)
                {
                    lock (_stateGate)
                        _playlistRuns.Remove(group.Id);
                    return true;
                }

                var order = PlaylistOrder(group, children, completed.Order.LastOrDefault());
                var next = new PlaylistRun(group.Id, order, 0, completed.Pass + 1);
                lock (_stateGate)
                    _playlistRuns[group.Id] = next;
                await FireAsync(order[0], depth: 1).ConfigureAwait(false);
                return true;
            }

            case AtListEnd.NextList:
                lock (_stateGate)
                    _playlistRuns.Remove(group.Id);
                if (Project.ListOf(group.Id) is { } owner)
                    await ContinueAtListEndAsync(owner, depth: 0, AtListEnd.NextList).ConfigureAwait(false);
                return true;

            default:
                lock (_stateGate)
                    _playlistRuns.Remove(group.Id);
                return true;
        }
    }

    /// <param name="closedPreviousPass">
    /// The item that ended the pass before this one, or null on the first. A shuffled bed that opens a
    /// pass with the track that just closed the last one plays it twice in a row, which in front of an
    /// audience reads as a fault rather than as chance.
    /// </param>
    private IReadOnlyList<Guid> PlaylistOrder(
        GroupCueNode group, IReadOnlyList<CueNode> children, Guid? closedPreviousPass = null)
    {
        if (!group.Shuffle)
            return [.. children.Select(cue => cue.Id)];

        lock (_stateGate)
        {
            if (!group.ReshuffleEachPass
                && _stableShuffleOrders.TryGetValue(group.Id, out var existing)
                && existing.Count == children.Count
                && existing.All(id => children.Any(cue => cue.Id == id)))
                return existing;

            var shuffled = children.Select(cue => cue.Id).ToArray();
            Random.Shared.Shuffle(shuffled);

            // Swap the head with a neighbour rather than reshuffling until it differs: one swap always
            // works, and a reshuffle loop on a two-item list can run a long time before it does.
            if (group.AvoidImmediateRepeat
                && shuffled.Length > 1
                && closedPreviousPass is { } last
                && shuffled[0] == last)
                (shuffled[0], shuffled[^1]) = (shuffled[^1], shuffled[0]);

            if (!group.ReshuffleEachPass)
                _stableShuffleOrders[group.Id] = shuffled;

            return shuffled;
        }
    }

    private async Task ContinueFromAsync(CueNode cue, CueList list, int depth, bool follow)
    {
        CueNode? next;

        if (follow && Project.Settings.DisabledCueFollow == DisabledCueFollow.StopTheChain)
        {
            next = NextAfterSubtree(list, cue.Id);
            if (next is { Enabled: false })
                return;
        }
        else
        {
            next = CueOrder.NextEnabled(list, cue.Id);
        }

        if (cue.PostWaitMs > 0
            && !await host.DelayAsync(TimeSpan.FromMilliseconds(cue.PostWaitMs)).ConfigureAwait(false))
            return;

        if (next is null)
        {
            await ContinueAtListEndAsync(list, depth, Project.Settings.AtListEnd).ConfigureAwait(false);
            return;
        }

        await AdvanceAsync(list, next.Id).ConfigureAwait(false);
        await FireAsync(next.Id, depth + 1).ConfigureAwait(false);
    }

    private async Task ContinueAtListEndAsync(CueList list, int depth, AtListEnd policy)
    {
        CueList? targetList = policy switch
        {
            AtListEnd.Loop => list,
            AtListEnd.NextList => NextList(list),
            _ => null,
        };

        if (targetList is null || CueOrder.NextEnabled(targetList, null) is not { } first)
        {
            await host.SetStandbyAsync(list, null).ConfigureAwait(false);
            return;
        }

        if (!ReferenceEquals(targetList, list))
            await host.SetStandbyAsync(list, null).ConfigureAwait(false);

        await AdvanceAsync(targetList, first.Id).ConfigureAwait(false);
        await FireAsync(first.Id, depth + 1).ConfigureAwait(false);
    }

    private CueList? NextList(CueList list)
    {
        var at = Project.CueLists.FindIndex(candidate => candidate.Id == list.Id);
        return at >= 0 && at + 1 < Project.CueLists.Count ? Project.CueLists[at + 1] : null;
    }

    private static CueNode? NextAfterSubtree(CueList list, Guid after)
    {
        var order = list.Flatten().ToList();
        var at = order.FindIndex(cue => cue.Id == after);
        if (at < 0)
            return null;

        var index = at + CueOrder.Subtree(order[at]);
        return index < order.Count && order[index].Enabled ? order[index] : null;
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
        if (jump.Condition == JumpCondition.WhileTriggerHeld && !host.IsExternalTriggerActive)
            return true;

        if (jump.Condition == JumpCondition.CountThenContinue)
        {
            lock (_stateGate)
            {
                var visits = _jumpVisits.GetValueOrDefault(jump.Id) + 1;
                if (visits > Math.Max(1, jump.JumpCount))
                {
                    _jumpVisits.Remove(jump.Id);
                    return true;
                }
                _jumpVisits[jump.Id] = visits;
            }
        }

        var targets = jump.TargetCueIds
            .Select(Project.FindCue)
            .OfType<CueNode>()
            .Where(cue => cue.Enabled)
            .ToList();

        if (targets.Count == 0)
        {
            host.Report($"“{jump.Label}” has no live target — the jump did nothing");
            return false;
        }

        var target = jump.PickAtRandom ? targets[Random.Shared.Next(targets.Count)] : targets[0];

        if (Project.ListOf(target.Id) is not { } list)
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
        var origin = Project.AudioPatch.Cells.Select(cell => cell with { }).ToList();

        var applied = 0;
        var broken = new List<BrokenBinding>();

        if (patch.SnapshotId is { } snapshotId)
        {
            var recall = PatchOperations.Recall(Project, snapshotId);
            applied += recall.CellsApplied;
            broken.AddRange(recall.Broken);
        }

        if (patch.Levels.Count > 0)
        {
            var levels = PatchOperations.ApplyLevels(Project, patch.Levels);
            applied += levels.CellsApplied;
            broken.AddRange(levels.Broken);
        }

        foreach (var failure in broken)
            host.Report($"“{patch.Label}”: {failure.Reason}");

        if (applied == 0)
            return broken.Count == 0;

        var destination = Project.AudioPatch.Cells.Select(cell => cell with { }).ToList();

        await host.ApplyPatchAsync(
                origin,
                destination,
                TimeSpan.FromMilliseconds(patch.FadeMs),
                patch.FadeCurve.Resolve(Project))
            .ConfigureAwait(false);

        return true;
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

        // SNAPSHOT, not the live list. Stopping a target below calls Forget, which removes it from
        // what the host reports as sounding — iterating that list while emptying it throws. It never
        // bit in production only because ShowHost happens to hand back a copy; the contract does not
        // promise one, and a fade-everything cue crashing mid-show is not a bug to leave to luck.
        var cues = fade.FadeEverythingSounding
            ? host.Sounding.ToList()
            : [.. fade.TargetCueIds.Where(id => Project.FindCue(id) is not null)];

        if (fade.TargetChannelIds.Count > 0)
        {
            var origin = Project.AudioPatch.Cells.Select(cell => cell with { }).ToList();
            var destination = origin
                .Select(cell => fade.TargetChannelIds.Contains(cell.LogicalChannelId)
                    ? cell with { GainDb = fade.ToLevelDb, Muted = toSilence }
                    : cell)
                .ToList();

            // The document keeps what the fade landed on: a fade cue that left the patch at a level
            // the file disagrees with would be undone by the next unrelated reload.
            foreach (var cell in Project.AudioPatch.Cells.Where(
                cell => fade.TargetChannelIds.Contains(cell.LogicalChannelId)))
            {
                cell.GainDb = fade.ToLevelDb;
                cell.Muted = toSilence;
            }

            await host.ApplyPatchAsync(origin, destination, duration, fade.Curve.Resolve(Project))
                .ConfigureAwait(false);
        }

        foreach (var cueId in cues)
        {
            host.MarkFading(cueId);

            await host.FadeCueAsync(
                cueId,
                fade.ToLevelDb,
                duration,
                fade.Curve.Resolve(Project),
                toSilence && fade.StopTargetsWhenComplete).ConfigureAwait(false);

            if (toSilence && fade.StopTargetsWhenComplete)
                host.Forget(cueId);
        }

        return cues.Count > 0 || fade.TargetChannelIds.Count > 0;
    }

    /// <summary>Sends an action cue, reporting a refusal rather than swallowing it.</summary>
    private async Task<bool> ActAsync(ActionCueNode action)
    {
        var endpoint = action.EndpointId is { } id
            ? Project.ActionEndpoints.FirstOrDefault(item => item.Id == id)
            : null;

        if (await host.SendActionAsync(action, endpoint).ConfigureAwait(false) is { } failure)
        {
            host.Report(failure);
            return false;
        }

        return true;
    }

    /// <summary>Moves a list's cursor onward from the cue that just fired.</summary>
    public Task AdvanceAsync(CueList list, Guid fired) =>
        host.SetStandbyAsync(list, CueOrder.NextEnabled(list, fired)?.Id);

    /// <summary>
    /// Runs a timeline group from a position inside it — the whole of it when <paramref name="from"/>
    /// is zero, which is what firing the group means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three cases, and the third is the one that makes this worth having. A child entirely BEFORE the
    /// playhead is skipped; one entirely after is scheduled at its own offset less the playhead; and
    /// one that STRADDLES it is fired now and moved to the right place inside its own media. Skipping
    /// the third would mean rehearsing the second half of a scene with no bed under it — which is the
    /// half of the scene an operator is least able to judge without one.
    /// </para>
    /// <para>
    /// The seek is in FILE time, so the clip's in-point is added back: a cue trimmed to start ten
    /// seconds in, rehearsed from five seconds into the timeline, plays from fifteen seconds into the
    /// file. Seeking to five would play material the show never contains.
    /// </para>
    /// <para>
    /// A cue nobody has probed has no length, so whether it straddles cannot be known. It is treated as
    /// starting at its offset — the answer that plays something rather than the one that silently
    /// leaves a hole.
    /// </para>
    /// </remarks>
    public async Task FireTimelineAsync(GroupCueNode group, TimeSpan from, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(group);

        var playhead = from < TimeSpan.Zero ? TimeSpan.Zero : from;

        foreach (var child in group.Children.Where(child => child.Enabled))
        {
            var start = TimeSpan.FromMilliseconds(child.TimelineOffsetMs);

            if (start >= playhead)
            {
                host.Schedule(child.Id, start - playhead, depth);
                continue;
            }

            var into = playhead - start;

            // No probe, no length, no way to tell whether it is still running. Firing it is the
            // answer that plays something.
            if (Length(child) is { } length && into >= length)
                continue;

            if (!await FireAsync(child.Id, depth + 1).ConfigureAwait(false))
                continue;

            await host.SeekCueAsync(
                child.Id,
                into + TimeSpan.FromMilliseconds(child is MediaCueNode media ? media.TrimInMs : 0))
                .ConfigureAwait(false);
        }
    }

    /// <summary>How long a child occupies the timeline: its TRIMMED length, not the file's.</summary>
    private TimeSpan? Length(CueNode cue)
    {
        var probed = host.MediaLength(cue.Id);

        return cue is MediaCueNode media ? media.TrimmedLength(probed) : probed;
    }
}
