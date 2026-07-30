using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// Playlist / armed-list group runs (Ideas/CuePlayer-Enhancements.md §3): the per-group run state, how the
/// next item is picked (in order, shuffled without replacement, random with an anti-repeat rule), and what a
/// natural end does about it - advance, wrap, or hand control back to the sequence.
/// <para>Split out of the root file (2026-07-30 review §3). It is the largest single piece of state the cue
/// player owns that is deliberately SESSION-only rather than project data (loading, Stop and Panic all start
/// every playlist afresh), so keeping it in one file is also what makes that rule checkable.</para>
/// </summary>
public partial class CuePlayerViewModel
{
    // ----- Playlist / armed-list group runs (Ideas/CuePlayer-Enhancements.md §3) -----------------
    // Session-only state beside _lastRandomJumpTargetIds (the same "session state, not project
    // data" philosophy): loading a project, Stop and Panic all start every playlist afresh.

    private sealed class PlaylistRunState
    {
        /// <summary>This pass's item order (ids of direct non-comment children). For shuffle this IS
        /// the bag: a Fisher–Yates order drawn without replacement guarantees every child once per pass.</summary>
        public List<Guid> PassOrder = [];

        /// <summary>Index of the next item to fire within <see cref="PassOrder"/>.</summary>
        public int NextIndex;

        /// <summary>Items actually played per pass: the resolved PlayCount subset, else the child count.</summary>
        public int ItemsPerPass;

        /// <summary>1-based pass counter.</summary>
        public int Pass = 1;

        /// <summary>The item currently playing (the last consumed pick); guards the pass boundary
        /// no-repeat and routes natural-end events to this run.</summary>
        public Guid? CurrentItemId;

        // Display fields captured at consume time, BEFORE any pass rollover mutates the counters.
        public int CurrentItemOrdinal;
        public int CurrentItemPass;
        public int CurrentPassItemCount;

        /// <summary>The final pick of the final pass has fired; its natural end triggers the group's
        /// end behavior exactly once, after which the run is removed.</summary>
        public bool Finished;
    }

    private readonly Dictionary<Guid, PlaylistRunState> _playlistRuns = [];

    /// <summary>Shuffle RNG - injectable so tests can drive deterministic bags.</summary>
    internal Random PlaylistRandom { get; set; } = Random.Shared;

    /// <summary>Test/diagnostic accessor: whether a playlist run (armed or playing) exists for the group.</summary>
    internal bool HasActivePlaylistRun(Guid groupId) => _playlistRuns.ContainsKey(groupId);

    private bool HasFinishedPlaylistRun(Guid groupId) =>
        _playlistRuns.TryGetValue(groupId, out var run) && run.Finished;

    internal void ClearPlaylistRuns() => _playlistRuns.Clear();

    private bool IsPlaylistGroup(CueNodeViewModel? node) =>
        node is { Kind: CueNodeKind.Group }
        && ParseGroupFireMode(node) is CueGroupFireMode.Playlist or CueGroupFireMode.ArmedList;

    /// <summary>The group's playlist items: direct non-comment children, skipping nested groups with
    /// nothing fireable. Each item fires as a unit (a nested group runs through its own fire mode).</summary>
    private static List<CueNodeViewModel> PlaylistItems(CueNodeViewModel group) =>
        group.Children
            .Where(c => c.Kind != CueNodeKind.Comment
                        && (c.Kind != CueNodeKind.Group || EnumerateFireableCueOrder(c.Children).Any()))
            .ToList();

    /// <summary>The next pick of a playlist/armed-list group, creating (or repairing after tree
    /// edits) its session run on demand so standby pre-roll and GO always agree on the same item.
    /// Peeking commits the shuffle draw but consumes nothing - counters advance only when the pick
    /// fires. Null when the group has no items or its run just finished (transient window until the
    /// final item's natural end applies the end behavior).</summary>
    private CueNodeViewModel? PeekPlaylistPick(CueNodeViewModel group)
    {
        var items = PlaylistItems(group);
        if (items.Count == 0)
            return null;

        var run = GetPlaylistRun(group, items);
        if (run.Finished)
            return null;
        var pickId = run.PassOrder[run.NextIndex];
        return items.First(i => i.Id == pickId);
    }

    private PlaylistRunState GetPlaylistRun(CueNodeViewModel group, List<CueNodeViewModel> items)
    {
        if (_playlistRuns.TryGetValue(group.Id, out var run))
        {
            if (run.Finished)
                return run;
            // Repair after tree edits: the armed pick must reference a live child, else rebuild.
            if (run.NextIndex < run.PassOrder.Count
                && items.Any(i => i.Id == run.PassOrder[run.NextIndex]))
                return run;
            _playlistRuns.Remove(group.Id);
        }

        run = new PlaylistRunState();
        StartPlaylistPass(run, group, items);
        _playlistRuns[group.Id] = run;
        return run;
    }

    /// <summary>(Re)builds the pass order and per-pass counters. Reuses the previous shuffled order
    /// when ReshuffleEachPass is off; applies the AvoidImmediateRepeat pass-boundary guard.</summary>
    private void StartPlaylistPass(PlaylistRunState run, CueNodeViewModel group, List<CueNodeViewModel> items)
    {
        var ids = items.Select(i => i.Id).ToList();
        var keepOrder = group.PlaylistShuffle
                        && !group.PlaylistReshuffleEachPass
                        && run.PassOrder.Count == ids.Count
                        && run.PassOrder.All(ids.Contains);
        if (!keepOrder)
        {
            run.PassOrder = [.. ids];
            if (group.PlaylistShuffle)
            {
                // Fisher–Yates: the whole pass is one bag drawn without replacement.
                for (var i = run.PassOrder.Count - 1; i > 0; i--)
                {
                    var j = PlaylistRandom.Next(i + 1);
                    (run.PassOrder[i], run.PassOrder[j]) = (run.PassOrder[j], run.PassOrder[i]);
                }
            }
        }

        // Pass-boundary guard: never open a pass with the item that just played when an
        // alternative exists (what "avoid immediate repeat" means across a reshuffle). Shuffle
        // only - a sequential playlist's order is authored, and the guard would scramble it
        // (with PlayCount=1 every pass replays child #1, which IS an immediate repeat by design).
        if (group.PlaylistShuffle
            && group.PlaylistAvoidImmediateRepeat
            && run.PassOrder.Count > 1
            && run.CurrentItemId is { } lastPlayed
            && run.PassOrder[0] == lastPlayed)
        {
            var swapWith = 1 + PlaylistRandom.Next(run.PassOrder.Count - 1);
            (run.PassOrder[0], run.PassOrder[swapWith]) = (run.PassOrder[swapWith], run.PassOrder[0]);
        }

        run.ItemsPerPass = Math.Clamp(
            group.PlaylistPlayCount ?? run.PassOrder.Count, 1, run.PassOrder.Count);
        run.NextIndex = 0;
    }

    /// <summary>Advances the run after its armed pick fired: bumps the counters, rolls the pass
    /// boundary (honoring LoopCount/PlayCount) and marks the run finished after the final pick.
    /// A pick that is itself a playlist/armed-list group is consumed recursively: its plan
    /// (<c>BuildTriggerPlan</c>'s nested branch) peeked the INNER run's armed pick, and without the
    /// inner consume that run's <see cref="PlaylistRunState.CurrentItemId"/> stays null - natural-end
    /// routing then swallows the item's end and neither run ever advances.</summary>
    private void ConsumePlaylistPick(CueNodeViewModel group)
    {
        var items = PlaylistItems(group);
        if (items.Count == 0 || GetPlaylistRun(group, items) is not { Finished: false } run)
            return;

        var pickId = run.PassOrder[run.NextIndex];
        run.CurrentItemId = pickId;
        run.CurrentItemOrdinal = run.NextIndex + 1;
        run.CurrentItemPass = run.Pass;
        run.CurrentPassItemCount = run.ItemsPerPass;
        run.NextIndex++;
        if (run.NextIndex >= run.ItemsPerPass)
        {
            var loops = Math.Max(0, group.PlaylistLoopCount);
            if (loops != 0 && run.Pass >= loops)
            {
                run.Finished = true;
            }
            else
            {
                run.Pass++;
                StartPlaylistPass(run, group, items);
            }
        }

        RefreshPlaylistNowPlayingStatus(group.Id);

        if (items.FirstOrDefault(i => i.Id == pickId) is { } pick && IsPlaylistGroup(pick))
            ConsumePlaylistPick(pick);
    }

    /// <summary>First fireable cue after the whole group (skipping all its descendants).</summary>
    private static CueNodeViewModel? NextCueAfterGroup(
        CueNodeViewModel group, IReadOnlyList<CueNodeViewModel> ordered)
    {
        var lastDescendant = EnumerateFireableCueOrder(group.Children).LastOrDefault();
        return lastDescendant is null ? null : NextCueAfter(lastDescendant, ordered);
    }

    /// <summary>Routes a natural-end event to the playlist run that owns it, if any. Innermost
    /// playlist group wins. Returns null (default sequential logic applies) when a nested-group
    /// item is still running its own internal Auto-Follow chain.</summary>
    private (CueNodeViewModel Group, PlaylistRunState Run, bool IsCurrentItem)? FindPlaylistRunForEndedCue(
        CueNodeViewModel ended)
    {
        if (_playlistRuns.Count == 0)
            return null;

        var path = FindContainingGroupPath(ended);
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var group = path[i];
            if (!IsPlaylistGroup(group) || !_playlistRuns.TryGetValue(group.Id, out var run))
                continue;

            var item = i + 1 < path.Count ? path[i + 1] : ended;
            if (item.Kind == CueNodeKind.Group)
            {
                // The item's own Auto-Follow chain continues inside it - not the item's end yet.
                var ordered = EnumerateFireableCueOrderFor(ended).ToList();
                var idx = ordered.FindIndex(c => ReferenceEquals(c, ended));
                if (idx >= 0 && idx + 1 < ordered.Count)
                {
                    var next = ordered[idx + 1];
                    if (FindContainingGroupPath(next).Any(g => ReferenceEquals(g, item))
                        && SequentialTransitionUsesMode(ended, next, CueTriggerMode.AutoFollow))
                        return null;
                }
            }

            return (group, run, run.CurrentItemId == item.Id);
        }

        return null;
    }

    /// <param name="foreign">The run lives in a cue list OTHER than the selected one (cross-list merged
    /// session): its advance plays into the same session but must not move the visible transport, so every
    /// Standby/Current write below is skipped and the advance rides the headless fire path.</param>
    private async Task HandlePlaylistItemEndedAsync(
        CueNodeViewModel group, PlaylistRunState run, CueNodeViewModel ended, bool isCurrentItem,
        bool foreign = false)
    {
        // A skipped/overlapped item finishing late must not double-advance the run - swallow it
        // (falling through to default Auto-Follow would defeat the group semantics too).
        if (!isCurrentItem)
            return;

        // Armed list: GO advances, a natural end does not - and it must not fall through to the
        // default next-sibling Auto-Follow either.
        if (ParseGroupFireMode(group) == CueGroupFireMode.ArmedList)
            return;

        if (!run.Finished)
        {
            // Auto-advance: fire the next pick through the normal GO machinery (GoCore's playlist
            // branch consumes the pick and keeps standby on the group).
            if (foreign)
            {
                await GoForeignListAsync(group);
                return;
            }

            StandbyCueNode = group;
            _immediateJumpChain.Clear();
            await GoCore(group);
            return;
        }

        // Run complete - the run state is over either way.
        _playlistRuns.Remove(group.Id);
        RefreshPlaylistNowPlayingStatus(group.Id);

        // A nested playlist completing while an ENCLOSING run plays it as its current item is that
        // outer item's natural end: route completion to the outer run (its semantics take precedence
        // over the inner group's own end behavior, the same rule that trumps per-cue end-jumps).
        // Without this the outer run stalls forever on its nested-group item.
        foreach (var outer in FindContainingGroupPath(group).Reverse())
        {
            if (IsPlaylistGroup(outer)
                && _playlistRuns.TryGetValue(outer.Id, out var outerRun)
                && outerRun.CurrentItemId == group.Id)
            {
                await HandlePlaylistItemEndedAsync(outer, outerRun, ended, isCurrentItem: true, foreign);
                return;
            }
        }

        // No enclosing run owns this group - apply its own configured end behavior.
        switch (group.PlaylistEndBehavior)
        {
            case CuePlaylistEndBehavior.AdvancePastGroup:
            {
                var ordered = EnumerateFireableCueOrderFor(group).ToList();
                var next = NextCueAfterGroup(group, ordered);
                if (next is null)
                {
                    if (!foreign)
                        CurrentCueNode = null;
                    return;
                }

                if (!foreign)
                    StandbyCueNode = next;
                if (SequentialTransitionUsesMode(ended, next, CueTriggerMode.AutoFollow))
                {
                    StatusMessage = Strings.Format(
                        nameof(Strings.CueAutoFollowStatusFormat), CueDisplayQualified(next));
                    if (foreign)
                    {
                        await GoForeignListAsync(next);
                        return;
                    }

                    _immediateJumpChain.Clear();
                    await GoCore();
                }

                return;
            }
            case CuePlaylistEndBehavior.Hold:
                // Leave the transport exactly where it is (held/freeze-frame clips keep showing).
                return;
            case CuePlaylistEndBehavior.Stop:
            default:
                if (!foreign)
                {
                    CurrentCueNode = null;
                    StandbyCueNode = group; // a fresh GO restarts the playlist
                }

                StatusMessage = Strings.Format(
                    nameof(Strings.CuePlaylistFinishedStatusFormat), CueDisplayQualified(group));
                return;
        }
    }

    /// <summary>Now-Playing aggregate status for a playlist group row: "item i/N · pass p/M"
    /// (or "… · pass p" for an infinite run). Null when the group has no live run item.</summary>
    internal string? BuildPlaylistStatus(CueNodeViewModel group)
    {
        if (!IsPlaylistGroup(group)
            || !_playlistRuns.TryGetValue(group.Id, out var run)
            || run.CurrentItemId is null)
            return null;

        var loops = Math.Max(0, group.PlaylistLoopCount);
        return loops == 0
            ? Strings.Format(
                nameof(Strings.PlaylistStatusInfiniteFormat),
                run.CurrentItemOrdinal, run.CurrentPassItemCount, run.CurrentItemPass)
            : Strings.Format(
                nameof(Strings.PlaylistStatusFormat),
                run.CurrentItemOrdinal, run.CurrentPassItemCount, run.CurrentItemPass, loops);
    }

    private void RefreshPlaylistNowPlayingStatus(Guid groupId)
    {
        var row = NowPlayingRows.OfType<ActiveGroupViewModel>().FirstOrDefault(g => g.GroupId == groupId);
        if (row is not null)
            row.PlaylistStatus = BuildPlaylistStatus(row.GroupNode);
    }

    /// <summary>Called when the active player finishes a file naturally during cue-driven playback.</summary>
    public Task OnMediaCueNaturallyEndedAsync() =>
        CurrentCueNode is { Kind: CueNodeKind.Media } current
            ? OnMediaCueNaturallyEndedAsync(current.Id)
            : Task.CompletedTask;

    public async Task OnMediaCueNaturallyEndedAsync(Guid endedCueId)
    {
        // Cross-list merged session: the ended clip may belong to any loaded list. Its chain (playlist
        // advance / end target / Auto-Follow) is resolved inside that list; only the SELECTED list's
        // chain is allowed to move the visible Standby/Current pointers, everything else advances
        // headlessly into the same session.
        if (FindNodeById(endedCueId) is not { Kind: CueNodeKind.Media } ended)
            return;
        var foreign = IsForeignListNode(ended);

        // Playlist runs own their children's end events (before per-cue EndTarget: the group's run
        // semantics take precedence over a child's authored end-jump while the run is active).
        if (FindPlaylistRunForEndedCue(ended) is { } playlistHit)
        {
            await HandlePlaylistItemEndedAsync(
                playlistHit.Group, playlistHit.Run, ended, playlistHit.IsCurrentItem, foreign);
            return;
        }

        // End target ("then fire cue #"): an explicit on-end jump wins over the default
        // next-cue-Auto-Follow chain - "after this song, go anywhere".
        if (ended.EndTargetCueId is { } targetId)
        {
            var endTarget = EnumerateAllCueNodesFor(ended).FirstOrDefault(c => c.Id == targetId);
            if (endTarget is null || ReferenceEquals(endTarget, ended) || endTarget.Kind == CueNodeKind.Comment)
            {
                // An authored end target is an override, not a best-effort hint. If its stable link became
                // invalid after deletion/import, stop here and surface it instead of unexpectedly firing the
                // ordinary next Auto-Follow cue.
                StatusMessage = Strings.CueEndTargetUnavailable;
                return;
            }

            StatusMessage = Strings.Format(
                nameof(Strings.CueAutoFollowStatusFormat), CueDisplayQualified(endTarget));
            if (foreign)
            {
                await GoForeignListAsync(endTarget);
                return;
            }

            StandbyCueNode = endTarget;
            _immediateJumpChain.Clear();
            await GoCore();
            return;
        }

        var ordered = EnumerateFireableCueOrderFor(ended).ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, ended));
        if (idx < 0 || idx + 1 >= ordered.Count)
            return;

        var next = ordered[idx + 1];
        if (!SequentialTransitionUsesMode(ended, next, CueTriggerMode.AutoFollow))
            return;

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

    /// <summary>Called when a playing media cue enters its playlist group's crossfade window (the
    /// session's <c>ClipApproachingEnd</c>, routed by the coordinator): fires the run's NEXT pick early,
    /// with the group's <see cref="CueNodeViewModel.PlaylistCrossfadeMs"/> as the dual-voice overlap, so
    /// the outgoing item fades out under the incoming one. Advancing here moves the run's current item,
    /// and the outgoing clip then never raises a natural end as the ACTIVE clip (it releases as the
    /// crossfade tail) - the natural-end handler's not-current-item guard swallows any straggler.
    /// Everything else is a no-op and keeps the butt-splice natural-end path: armed lists (GO-only
    /// advance), finished runs (the final item's natural end applies the end behavior), non-playlist
    /// cues, a zero window, and hosts without the crossfade seam.</summary>
    public async Task OnMediaCueApproachingEndAsync(Guid endingCueId)
    {
        if (MediaCueCrossfadeExecutor is null)
            return; // no dual-voice seam - advancing early would CUT the current item, not crossfade it
        if (FindNodeById(endingCueId) is not { Kind: CueNodeKind.Media } ending)
            return;
        if (FindPlaylistRunForEndedCue(ending) is not { } hit
            || !hit.IsCurrentItem
            || hit.Run.Finished
            || ParseGroupFireMode(hit.Group) != CueGroupFireMode.Playlist)
            return;
        var crossfadeMs = hit.Group.PlaylistCrossfadeMs;
        if (crossfadeMs <= 0)
            return;

        // The same advance as HandlePlaylistItemEndedAsync's auto-advance (GoCore consumes the pick and
        // keeps standby on the group), except the fire carries the overlap window. EqualPower is the
        // crossfade's law by construction: complementary up/down legs sum to constant power, which is
        // what an overlapping music transition should do (a linear pair dips audibly at the midpoint).
        var window = (TimeSpan.FromMilliseconds(crossfadeMs), S.Media.Session.FadeCurve.EqualPower);
        if (IsForeignListNode(ending))
        {
            // Another list's playlist crossfades in the same session - without touching this list's standby.
            await GoForeignListAsync(hit.Group, window);
            return;
        }

        StandbyCueNode = hit.Group;
        _immediateJumpChain.Clear();
        await GoCore(hit.Group, window);
    }
}
