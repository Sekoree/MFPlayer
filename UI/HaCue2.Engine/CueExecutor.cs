using HaCue2.Core.Model;
using HaCue2.Core.Patch;
using S.Media.Session;
using S.Media.Time;

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
    private sealed record ArmedRun(
        Guid GroupId,
        IReadOnlyList<Guid> Order,
        int NextIndex,
        int Pass,
        Guid LastFiredId,
        bool FinalPending);

    private sealed class TimelineRun(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => Finished.Task;
        public void Complete() => Finished.TrySetResult();
    }

    private sealed class TimelineEvent(TimeSpan due)
    {
        public TimeSpan Due { get; } = due;
        public List<TimelineMediaStart> Media { get; } = [];
        public List<VisualizerCueNode> Visualizers { get; } = [];
        public List<CueNode> Controls { get; } = [];
        public TimelineStartGate MediaGate { get; } = new();
        public TimelineStartGate VisualizerGate { get; } = new();
        public Task<IReadOnlyList<Guid>>? MediaTask { get; set; }
        public Task<IReadOnlyList<Guid>>? VisualizerTask { get; set; }
    }

    /// <summary>One callback shared by all media voices at an authored timeline position.</summary>
    private sealed class TimelineStartGate
    {
        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ready => _ready.Task;

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            _ready.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void EnsureReady() => _ready.TrySetResult();
        public void Release() => _release.TrySetResult();
        public void Cancel(CancellationToken cancellationToken) =>
            _release.TrySetCanceled(cancellationToken);
    }

    /// <summary>
    /// A pause-aware, continuity-preserving coordinate over the bay's late-bound audio clock. Device-clock
    /// epochs may change underneath it; <see cref="SessionClock"/> splices those changes without a jump.
    /// </summary>
    private sealed class TimelineRunClock
    {
        // Five milliseconds keeps pause/resume quantisation comfortably below one 60 Hz frame while still
        // using one lightweight scheduler loop per active timeline (not one timer per cue/output).
        private static readonly TimeSpan MaxPoll = TimeSpan.FromMilliseconds(5);
        private static readonly TimeSpan MinimumPoll = TimeSpan.FromMilliseconds(1);

        private readonly ICueExecutionHost _host;
        private readonly SessionClock _master;
        private TimeSpan _lastMaster;
        private TimeSpan _position;

        public TimelineRunClock(ICueExecutionHost host, TimeSpan initialPosition)
        {
            _host = host;
            _master = new SessionClock(host.TimelineClock);
            _lastMaster = _master.Now;
            _position = initialPosition;
        }

        public TimeSpan Position
        {
            get
            {
                var now = _master.Now;
                var delta = now - _lastMaster;
                _lastMaster = now;
                if (!_host.TimelinePaused && _master.IsAdvancing && delta > TimeSpan.Zero)
                    _position += delta;
                return _position;
            }
        }

        public async Task WaitUntilAsync(TimeSpan target, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = target - Position;
                if (remaining <= TimeSpan.Zero)
                    return;

                var wait = remaining > MaxPoll ? MaxPoll : remaining;
                if (wait < MinimumPoll)
                    wait = MinimumPoll;
                await _host.DelayTimelineAsync(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private readonly Lock _stateGate = new();
    private readonly Dictionary<Guid, PlaylistRun> _playlistRuns = [];
    private readonly Dictionary<Guid, ArmedRun> _armedRuns = [];
    private readonly Dictionary<Guid, Guid> _armedOwners = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _stableShuffleOrders = [];
    private readonly Dictionary<Guid, int> _jumpVisits = [];
    private readonly Dictionary<Guid, TimelineRun> _timelineRuns = [];

    // Preparing five seconds ahead absorbs decoder/open jitter without holding every clip in a long show open
    // for the life of the timeline. A cue that explicitly disables pre-roll is prepared at its due point.
    private static readonly TimeSpan TimelinePreparationLead = TimeSpan.FromSeconds(5);

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
        FadeShape crossfadeCurve = default,
        bool skipPreWait = false)
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
        if (!skipPreWait
            && cue.PreWaitMs > 0
            && !await host.DelayAsync(TimeSpan.FromMilliseconds(cue.PreWaitMs)).ConfigureAwait(false))
            return false;

        var fired = cue switch
        {
            MediaCueNode or TextCueNode or VisualizerCueNode =>
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
        var sequenceOwned = IsSequenceOwned(cue.Id);
        if (!sequenceOwned && cue.Trigger == CueTrigger.Continue && list is not null)
        {
            await ContinueFromAsync(cue, list, depth, follow: false).ConfigureAwait(false);
        }
        else if (!sequenceOwned && cue.Trigger == CueTrigger.Follow
                 && cue is not MediaCueNode
                 && cue is not TextCueNode
                 && cue is not VisualizerCueNode
                 && list is not null)
        {
            // Instant cues have no natural-end event. Their successful completion is their end.
            await ContinueFromAsync(cue, list, depth, follow: true).ConfigureAwait(false);
        }

        return true;
    }

    private bool IsSequenceOwned(Guid cueId)
    {
        lock (_stateGate)
            return _armedOwners.ContainsKey(cueId)
                   || _playlistRuns.Values.Any(run => run.Order.Contains(cueId));
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
            case GroupFireMode.FirstCueOnly:
                return await FireAsync(children[0].Id, depth + 1).ConfigureAwait(false);

            case GroupFireMode.ArmedList:
                return await FireArmedAsync(group, children, depth).ConfigureAwait(false);

            case GroupFireMode.Playlist:
                var order = PassOrder(group, children);
                lock (_stateGate)
                    _playlistRuns[group.Id] = new PlaylistRun(group.Id, order, 0);
                return await FireAsync(order[0], depth + 1).ConfigureAwait(false);

            case GroupFireMode.Timeline:
                await FireTimelineAsync(group, TimeSpan.Zero, depth).ConfigureAwait(false);
                return true;

            default:
                return await FireTogetherAsync(children, depth).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ALL TOGETHER: every child opens at once and starts on the same edge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Firing them one after another meant each child's media was opened before the next was even
    /// asked for, so the group started as a staircase — each cue late by the sum of every open before
    /// it. On eleven stems plus two 1080p60 ProRes clips that is flam between the stems, two video
    /// layers arriving at visibly different moments, and a GO that costs the sum of thirteen opens
    /// rather than the longest one. The mode's entire meaning is simultaneity.
    /// </para>
    /// <para>
    /// Only PLAIN clip children go in the batch. A child with a pre-wait is asking to start later by
    /// definition; one with a post-wait would hold the session's fire lock after starting, which is the
    /// one thing a batch must not do; one that auto-continues starts a chain; and a nested group, a
    /// jump or an action is not a clip at all. Those keep the ordinary one-at-a-time path, which is
    /// also the fallback if the batch itself cannot run — a staircase start beats a silent group.
    /// </para>
    /// </remarks>
    private async Task<bool> FireTogetherAsync(IReadOnlyList<CueNode> children, int depth)
    {
        var batched = children
            .Where(child => child is MediaCueNode or TextCueNode
                            && child.PreWaitMs == 0
                            && child.PostWaitMs == 0
                            && child.Trigger != CueTrigger.Continue
                            && !IsSequenceOwned(child.Id))
            .ToList();
        var visualizers = children
            .OfType<VisualizerCueNode>()
            .Where(child => child.PreWaitMs == 0
                            && child.PostWaitMs == 0
                            && child.Trigger != CueTrigger.Continue
                            && !IsSequenceOwned(child.Id))
            .ToList();

        if (batched.Count == 0 && visualizers.Count == 0)
        {
            // Every authored wait begins from the group GO, rather than one child's delay being added to
            // every child after it merely because it appeared first in the list.
            await Task.WhenAll(children.Select(child => FireAsync(child.Id, depth + 1)))
                .ConfigureAwait(false);
            return true;
        }

        var mediaGate = new TimelineStartGate();
        var visualizerGate = new TimelineStartGate();
        var mediaTask = batched.Count > 0
            ? PrepareTogetherMediaAsync()
            : Task.FromResult<IReadOnlyList<Guid>>([]);
        var visualizerTask = visualizers.Count > 0
            ? PrepareTogetherVisualizersAsync()
            : Task.FromResult<IReadOnlyList<Guid>>([]);

        async Task<IReadOnlyList<Guid>> PrepareTogetherMediaAsync()
        {
            try
            {
                return await host.PlayTimelineMediaAsync(
                        [.. batched.Select(cue => new TimelineMediaStart(cue))],
                        Project.ListOf(batched[0].Id),
                        mediaGate.WaitAsync,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                mediaGate.EnsureReady();
            }
        }

        async Task<IReadOnlyList<Guid>> PrepareTogetherVisualizersAsync()
        {
            try
            {
                return await host.PlayTimelineVisualizersAsync(
                        visualizers,
                        Project.ListOf(visualizers[0].Id),
                        visualizerGate.WaitAsync,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                visualizerGate.EnsureReady();
            }
        }

        // The callback means every media voice is fully prepared. Release it and launch visualizers/actions/
        // nested groups in this same continuation; none of them waits for post-start session bookkeeping.
        var ready = new List<Task>(2);
        if (batched.Count > 0)
            ready.Add(mediaGate.Ready);
        if (visualizers.Count > 0)
            ready.Add(visualizerGate.Ready);
        await Task.WhenAll(ready).ConfigureAwait(false);
        mediaGate.Release();
        visualizerGate.Release();
        var otherTasks = children
            .Where(child => batched.All(media => media.Id != child.Id)
                            && visualizers.All(visualizer => visualizer.Id != child.Id))
            .Select(child => FireAsync(child.Id, depth + 1))
            .ToArray();

        var started = await mediaTask.ConfigureAwait(false);
        var startedVisualizers = await visualizerTask.ConfigureAwait(false);
        var fired = started.Concat(startedVisualizers).ToHashSet();

        // A failed batch still degrades to ordinary fires. Launch them together as well: the mode's contract
        // is simultaneity, even on the slower recovery path.
        var retries = batched.Cast<CueNode>().Concat(visualizers)
            .Where(child => !fired.Contains(child.Id))
            .Select(child => FireAsync(child.Id, depth + 1))
            .ToArray();
        await Task.WhenAll(otherTasks.Concat(retries)).ConfigureAwait(false);

        return true;
    }

    /// <summary>Advances Follow and playlist runs from the framework's authoritative natural-end edge.</summary>
    public async Task OnNaturalEndAsync(Guid cueId)
    {
        host.Forget(cueId);

        if (await AdvancePlaylistAsync(cueId, approaching: false).ConfigureAwait(false))
            return;

        if (await FinishArmedItemAsync(cueId).ConfigureAwait(false))
            return;

        if (Project.FindCue(cueId) is MediaCueNode { EndTargetCueId: { } targetId } media)
        {
            if (targetId == cueId || Project.FindCue(targetId) is not { Enabled: true } target
                               || target is CommentCueNode || Project.ListOf(targetId) is not { } targetList)
            {
                host.Report($"“{media.Label}” has no live end target — the chain stopped");
                return;
            }

            await AdvanceAsync(targetList, target.Id).ConfigureAwait(false);
            await FireAsync(target.Id, depth: 1).ConfigureAwait(false);
            return;
        }

        if (Project.FindCue(cueId) is { Trigger: CueTrigger.Follow } cue
            && Project.ListOf(cueId) is { } list)
            await ContinueFromAsync(cue, list, depth: 0, follow: true).ConfigureAwait(false);
    }

    /// <summary>Starts the next playlist item early when the current clip enters its crossfade window.</summary>
    public Task OnApproachingEndAsync(Guid cueId) => AdvancePlaylistAsync(cueId, approaching: true);

    /// <summary>
    /// Releases sequence state when a cue is stopped instead of reaching its natural end.
    /// </summary>
    /// <remarks>
    /// An armed list deliberately waits for natural end before it closes its final pass. A manual
    /// stop has no natural-end callback, so leaving that state behind would make every later GO look
    /// like a repeated GO on a still-running final item. Playlist ownership is released for the same
    /// reason: a stopped run must not keep swallowing that child's ordinary Follow or end target.
    /// </remarks>
    public void OnStopped(Guid cueId)
    {
        lock (_stateGate)
        {
            if (_armedOwners.Remove(cueId, out var armedGroupId))
            {
                _armedRuns.Remove(armedGroupId);
                foreach (var owned in _armedOwners
                             .Where(item => item.Value == armedGroupId)
                             .Select(item => item.Key)
                             .ToArray())
                    _armedOwners.Remove(owned);
            }

            foreach (var groupId in _playlistRuns
                         .Where(item => item.Value.Index < item.Value.Order.Count
                                        && item.Value.Order[item.Value.Index] == cueId)
                         .Select(item => item.Key)
                         .ToArray())
                _playlistRuns.Remove(groupId);
        }
    }

    /// <summary>Starts the next transport run with no state left by the stopped show.</summary>
    public void ResetTransientState()
    {
        CancelTimelineRuns();
        lock (_stateGate)
        {
            _playlistRuns.Clear();
            _armedRuns.Clear();
            _armedOwners.Clear();
            _stableShuffleOrders.Clear();
            _jumpVisits.Clear();
        }
    }

    /// <summary>Cancels every pending timeline edge and every voice prepared behind one.</summary>
    public void CancelTimelineRuns()
    {
        var runs = DetachTimelineRuns();
        foreach (var run in runs)
        {
            try { run.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* completed between the snapshot and cancellation */ }
        }
    }

    /// <summary>Cancels pending edges and waits until prepared media/visualizer resources have unwound.</summary>
    public async Task CancelTimelineRunsAsync()
    {
        var runs = DetachTimelineRuns();
        foreach (var run in runs)
        {
            try { run.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        await Task.WhenAll(runs.Select(run => run.Completion)).ConfigureAwait(false);
    }

    private TimelineRun[] DetachTimelineRuns()
    {
        lock (_stateGate)
        {
            var runs = _timelineRuns.Values.ToArray();
            _timelineRuns.Clear();
            return runs;
        }
    }

    /// <summary>Test/diagnostic seam: completes after every edge in this run has dispatched.</summary>
    public Task WaitForTimelineCompletionAsync(Guid groupId)
    {
        lock (_stateGate)
            return _timelineRuns.TryGetValue(groupId, out var run)
                ? run.Completion
                : Task.CompletedTask;
    }

    /// <summary>
    /// ARMED LIST fires exactly one enabled child per operator GO. Natural end never advances it;
    /// it merely releases ownership so the child's own Follow/end-target cannot escape the group.
    /// </summary>
    private async Task<bool> FireArmedAsync(GroupCueNode group, IReadOnlyList<CueNode> children, int depth)
    {
        ArmedRun? before;
        ArmedRun next;
        Guid selected;

        lock (_stateGate)
        {
            _armedRuns.TryGetValue(group.Id, out before);
            var current = before;
            if (current is null
                || current.Order.Count == 0
                || current.Order.Any(id => children.All(cue => cue.Id != id)))
            {
                var firstOrder = PassOrder(group, children);
                current = new ArmedRun(group.Id, firstOrder, 0, 1, Guid.Empty, false);
            }

            // The final item is still running. A repeated GO must not silently begin a new pass over
            // it; the natural-end edge closes this run and the next GO starts cleanly.
            if (current.FinalPending)
                return true;

            selected = current.Order[current.NextIndex];
            var after = current.NextIndex + 1;
            if (after < current.Order.Count)
            {
                next = current with { NextIndex = after, LastFiredId = selected };
            }
            else if (group.LoopCount == 0 || current.Pass < group.LoopCount)
            {
                var order = PassOrder(group, children, selected);
                next = new ArmedRun(group.Id, order, 0, current.Pass + 1, selected, false);
            }
            else
            {
                next = current with { NextIndex = current.Order.Count, LastFiredId = selected, FinalPending = true };
            }

            _armedRuns[group.Id] = next;
            _armedOwners[selected] = group.Id;
        }

        if (await FireAsync(selected, depth + 1).ConfigureAwait(false))
            return true;

        lock (_stateGate)
        {
            _armedOwners.Remove(selected);
            if (before is null)
                _armedRuns.Remove(group.Id);
            else
                _armedRuns[group.Id] = before;
        }
        return false;
    }

    private async Task<bool> FinishArmedItemAsync(Guid ended)
    {
        GroupCueNode? group = null;
        var final = false;

        lock (_stateGate)
        {
            if (!_armedOwners.Remove(ended, out var groupId))
                return false;

            if (_armedRuns.TryGetValue(groupId, out var run)
                && run.FinalPending && run.LastFiredId == ended)
            {
                final = true;
                _armedRuns.Remove(groupId);
                group = Project.FindCue(groupId) as GroupCueNode;
            }
        }

        if (final && group is { AtEnd: AtListEnd.NextList }
                  && Project.ListOf(group.Id) is { } owner)
            await ContinueAtListEndAsync(owner, depth: 0, AtListEnd.NextList).ConfigureAwait(false);

        return true;
    }

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
        var children = group.Children.Where(cue => cue.Enabled).ToList();
        var hasAnotherPass = children.Count > 0
                             && (group.LoopCount == 0 || completed.Pass < group.LoopCount);

        // Pass count and final behavior are independent: "twice, then hold" and "three times, then
        // next list" both run their remaining passes before the terminal policy is considered.
        if (hasAnotherPass)
        {
            var order = PassOrder(group, children, completed.Order.LastOrDefault());
            var next = new PlaylistRun(group.Id, order, 0, completed.Pass + 1);
            lock (_stateGate)
                _playlistRuns[group.Id] = next;
            await FireAsync(order[0], depth: 1).ConfigureAwait(false);
            return true;
        }

        switch (group.AtEnd)
        {
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

    private IReadOnlyList<Guid> PassOrder(
        GroupCueNode group, IReadOnlyList<CueNode> children, Guid? closedPreviousPass = null)
    {
        var order = PlaylistOrder(group, children, closedPreviousPass);
        var count = group.PlayCount is { } requested
            ? Math.Clamp(requested, 1, order.Count)
            : order.Count;
        return count == order.Count ? order : [.. order.Take(count)];
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
        // The caller needs to SEE a disabled successor when the show is configured to stop a Follow
        // chain at one. Returning null here made a disabled cue look like the end of the list, which
        // could loop the list or continue into the next one instead of stopping.
        return index < order.Count ? order[index] : null;
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
            {
                host.Forget(cueId);
                OnStopped(cueId);
            }
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
        var events = BuildTimelineEvents(group, playhead);
        var run = new TimelineRun(new CancellationTokenSource());
        TimelineRun? displaced;

        lock (_stateGate)
        {
            _timelineRuns.TryGetValue(group.Id, out displaced);
            _timelineRuns[group.Id] = run;
        }

        if (displaced is not null)
        {
            try { displaced.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // Do not invoke an async run while holding _stateGate: a virtual clock (and an empty timeline) can
        // complete synchronously, and its finally block takes the same gate to retire itself.
        _ = RunTimelineAsync(group, events, playhead, depth, run);

        // Starting/rehearsing a timeline is complete once any cue that belongs at the playhead has actually
        // crossed its edge. Future authored positions remain owned by the background run.
        await run.Started.Task.ConfigureAwait(false);
    }

    private IReadOnlyList<TimelineEvent> BuildTimelineEvents(GroupCueNode group, TimeSpan playhead)
    {
        var byDue = new SortedDictionary<long, TimelineEvent>();

        TimelineEvent EventAt(TimeSpan due)
        {
            if (!byDue.TryGetValue(due.Ticks, out var scheduled))
            {
                scheduled = new TimelineEvent(due);
                byDue.Add(due.Ticks, scheduled);
            }
            return scheduled;
        }

        foreach (var child in group.Children.Where(child => child.Enabled))
        {
            // Pre-wait is part of the authored start coordinate, not a second wall-clock timer launched
            // after the timeline says the cue is due.
            var authoredStart = TimeSpan.FromMilliseconds(
                Math.Max(0L, (long)child.TimelineOffsetMs + child.PreWaitMs));
            var due = authoredStart;
            TimeSpan? initialPosition = null;

            if (authoredStart < playhead)
            {
                var into = playhead - authoredStart;

                // Media/text and a still-held visualizer can reconstruct the state at a rehearsal playhead.
                // Instant controls before the playhead are history: replaying an old patch, OSC action or jump
                // while rehearsing later in the scene would be both surprising and potentially destructive.
                var canStraddle = child is MediaCueNode or TextCueNode or VisualizerCueNode;
                if (!canStraddle)
                    continue;

                if (Length(child) is { } length && into >= length)
                {
                    if (child is MediaCueNode { Loop: true } or MediaCueNode { EndBehavior: CueEndBehavior.Loop })
                    {
                        if (length <= TimeSpan.Zero)
                            continue;
                        into = TimeSpan.FromTicks(into.Ticks % length.Ticks);
                    }
                    else if (child is MediaCueNode { EndBehavior: CueEndBehavior.FreezeLastFrame })
                    {
                        // Arm on the final decodable instant and let the session's ordinary end monitor enter
                        // its freeze state. Seeking exactly to duration is EOF on several demuxers.
                        into = length > TimeSpan.FromMilliseconds(1)
                            ? length - TimeSpan.FromMilliseconds(1)
                            : TimeSpan.Zero;
                    }
                    else
                    {
                        continue;
                    }
                }

                due = playhead;
                if (child is MediaCueNode or TextCueNode)
                {
                    var trimIn = child is MediaCueNode media ? media.TrimInMs : 0;
                    initialPosition = into + TimeSpan.FromMilliseconds(trimIn);
                }
            }

            var timelineEvent = EventAt(due);
            if (child is MediaCueNode or TextCueNode)
                timelineEvent.Media.Add(new TimelineMediaStart(child, initialPosition));
            else if (child is VisualizerCueNode visualizer)
                timelineEvent.Visualizers.Add(visualizer);
            else
                timelineEvent.Controls.Add(child);
        }

        return [.. byDue.Values];
    }

    private async Task RunTimelineAsync(
        GroupCueNode group,
        IReadOnlyList<TimelineEvent> events,
        TimeSpan playhead,
        int depth,
        TimelineRun run)
    {
        var cancellationToken = run.Cancellation.Token;
        var dispatched = new List<Task>();
        try
        {
            var initial = events.FirstOrDefault(item => item.Due <= playhead);
            TimelineRunClock clock;

            if (initial is not null)
            {
                StartTimelinePreparation(initial, cancellationToken);
                var initialReady = new List<Task>(2);
                if (initial.Media.Count > 0)
                    initialReady.Add(initial.MediaGate.Ready);
                if (initial.Visualizers.Count > 0)
                    initialReady.Add(initial.VisualizerGate.Ready);
                await Task.WhenAll(initialReady).WaitAsync(cancellationToken).ConfigureAwait(false);

                // Decoder/open latency is paid before the timeline epoch exists. Position zero (or the
                // rehearsal playhead) is therefore the release edge, not the time opening happened to begin.
                clock = new TimelineRunClock(host, playhead);
                await DispatchTimelineEventAsync(initial, depth, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                clock = new TimelineRunClock(host, playhead);
            }

            run.Started.TrySetResult();

            var future = events.Where(item => item.Due > playhead).ToList();
            var transitions = future
                .SelectMany(item => new[]
                {
                    (At: TimelinePreparationAt(item, playhead), Prepare: true, Event: item),
                    (At: item.Due, Prepare: false, Event: item),
                })
                .OrderBy(item => item.At)
                .ThenByDescending(item => item.Prepare)
                .GroupBy(item => item.At);

            foreach (var transition in transitions)
            {
                await clock.WaitUntilAsync(transition.Key, cancellationToken).ConfigureAwait(false);

                foreach (var item in transition.Where(item => item.Prepare))
                    StartTimelinePreparation(item.Event, cancellationToken);

                foreach (var item in transition.Where(item => !item.Prepare))
                {
                    StartTimelinePreparation(item.Event, cancellationToken);
                    dispatched.Add(DispatchTimelineEventAsync(
                        item.Event, depth, cancellationToken));
                }

                // Let the just-released dispatcher continuations run before sampling/advancing toward the
                // next transition. This is normally only a few microseconds, but makes the edge ownership
                // explicit and prevents a fast virtual clock from overtaking its own released voices.
                if (transition.Any(item => !item.Prepare))
                    await Task.Yield();
            }

            await Task.WhenAll(dispatched).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // STOP, reload, re-fire or disposal owns this cancellation. Prepared voices unwind through
            // ShowSession's exact-voice rollback and future events simply never dispatch.
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            host.Report($"“{group.Label}” timeline stopped — {failure.Message}");
        }
        finally
        {
            foreach (var timelineEvent in events)
            {
                timelineEvent.MediaGate.Cancel(cancellationToken);
                timelineEvent.VisualizerGate.Cancel(cancellationToken);
            }

            // Cancellation is not complete until committed session voices and hidden visualizer slots have
            // observed it and released their resources. Reload/STOP await the run specifically for this edge.
            var preparations = events
                .SelectMany(timelineEvent => new Task?[]
                    { timelineEvent.MediaTask, timelineEvent.VisualizerTask })
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            try { await Task.WhenAll(preparations).ConfigureAwait(false); }
            catch { /* the run's catch already reported non-cancellation failures */ }
            run.Started.TrySetResult();

            run.Cancellation.Dispose();
            run.Complete();

            lock (_stateGate)
                if (_timelineRuns.TryGetValue(group.Id, out var current) && ReferenceEquals(current, run))
                    _timelineRuns.Remove(group.Id);
        }
    }

    private static TimeSpan TimelinePreparationAt(TimelineEvent timelineEvent, TimeSpan playhead)
    {
        // A live/device source may explicitly forbid opening early. If it shares an edge with prepared files,
        // those files wait for it rather than sacrificing simultaneity.
        var preRollAllowed = timelineEvent.Media.All(item =>
            item.Cue is not MediaCueNode { DisablePreRoll: true });
        if (!preRollAllowed)
            return timelineEvent.Due;

        var prepareAt = timelineEvent.Due - TimelinePreparationLead;
        return prepareAt < playhead ? playhead : prepareAt;
    }

    private void StartTimelinePreparation(TimelineEvent timelineEvent, CancellationToken cancellationToken)
    {
        if (timelineEvent.MediaTask is null && timelineEvent.Media.Count > 0)
            timelineEvent.MediaTask = PrepareTimelineMediaAsync(timelineEvent, cancellationToken);

        if (timelineEvent.VisualizerTask is null && timelineEvent.Visualizers.Count > 0)
            timelineEvent.VisualizerTask = PrepareTimelineVisualizersAsync(timelineEvent, cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> PrepareTimelineMediaAsync(
        TimelineEvent timelineEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await host.PlayTimelineMediaAsync(
                    timelineEvent.Media,
                    Project.ListOf(timelineEvent.Media[0].Cue.Id),
                    timelineEvent.MediaGate.WaitAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // A host failure before entering the callback must not strand the event's control cues forever.
            timelineEvent.MediaGate.EnsureReady();
        }
    }

    private async Task<IReadOnlyList<Guid>> PrepareTimelineVisualizersAsync(
        TimelineEvent timelineEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await host.PlayTimelineVisualizersAsync(
                    timelineEvent.Visualizers,
                    Project.ListOf(timelineEvent.Visualizers[0].Id),
                    timelineEvent.VisualizerGate.WaitAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            timelineEvent.VisualizerGate.EnsureReady();
        }
    }

    private async Task DispatchTimelineEventAsync(
        TimelineEvent timelineEvent,
        int depth,
        CancellationToken cancellationToken)
    {
        try
        {
            var ready = new List<Task>(2);
            if (timelineEvent.Media.Count > 0)
                ready.Add(timelineEvent.MediaGate.Ready);
            if (timelineEvent.Visualizers.Count > 0)
                ready.Add(timelineEvent.VisualizerGate.Ready);
            await Task.WhenAll(ready).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Release prepared media and dispatch non-media participants in the same continuation. Media
            // starters then serialize through one dispatcher turn; controls no longer wait for decoder opens.
            timelineEvent.MediaGate.Release();
            timelineEvent.VisualizerGate.Release();
            var controls = timelineEvent.Controls
                .Select(cue => FireAsync(
                    cue.Id, depth + 1, skipPreWait: true))
                .ToArray();

            IReadOnlyList<Guid> started = [];
            if (timelineEvent.MediaTask is not null)
                started = await timelineEvent.MediaTask.ConfigureAwait(false);
            if (timelineEvent.VisualizerTask is not null)
                await timelineEvent.VisualizerTask.ConfigureAwait(false);

            foreach (var mediaStart in timelineEvent.Media.Where(item => started.Contains(item.Cue.Id)))
                await FinishScheduledMediaCueAsync(mediaStart.Cue, depth, cancellationToken)
                    .ConfigureAwait(false);

            await Task.WhenAll(controls).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            host.Report($"a timeline event at {timelineEvent.Due:c} did not fire — {failure.Message}");
        }
    }

    private async Task FinishScheduledMediaCueAsync(
        CueNode cue,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSequenceOwned(cue.Id)
            || cue.Trigger != CueTrigger.Continue
            || Project.ListOf(cue.Id) is not { } list)
            return;

        if (cue.PostWaitMs > 0)
            await host.DelayTimelineAsync(
                    TimeSpan.FromMilliseconds(cue.PostWaitMs), cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await ContinueFromAsync(cue with { PostWaitMs = 0 }, list, depth, follow: false)
            .ConfigureAwait(false);
    }

    /// <summary>How long a child occupies the timeline: its TRIMMED length, not the file's.</summary>
    private TimeSpan? Length(CueNode cue)
    {
        if (cue is TextCueNode { DurationMs: > 0 } text)
            return TimeSpan.FromMilliseconds(text.DurationMs);

        if (cue is VisualizerCueNode { HoldMs: > 0 } visualizer)
            return TimeSpan.FromMilliseconds(visualizer.HoldMs);

        var probed = host.MediaLength(cue.Id);

        return cue is MediaCueNode media ? media.TrimmedLength(probed) : probed;
    }
}
