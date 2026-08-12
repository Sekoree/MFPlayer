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
        public List<TimelineVisualizerStart> Visualizers { get; } = [];
        public List<TimelineControlStart> Controls { get; } = [];
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

    private readonly Lock _stateGate = new();
    private readonly Dictionary<Guid, PlaylistRun> _playlistRuns = [];
    private readonly Dictionary<Guid, ArmedRun> _armedRuns = [];
    private readonly Dictionary<Guid, Guid> _armedOwners = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _stableShuffleOrders = [];
    private readonly Dictionary<Guid, int> _jumpVisits = [];
    private readonly Dictionary<Guid, TimelineRun> _timelineRuns = [];

    /// <summary>
    /// Follow chains whose successor is already opening, keyed by the OUTGOING cue. Populated at
    /// "approaching end" and consumed at the out-point; see <see cref="TryBeginPreparedFollowAsync"/>.
    /// </summary>
    private readonly Dictionary<Guid, PreparedFollow> _preparedFollows = [];

    /// <summary>
    /// A successor that has been fully opened, committed, pre-rolled and sync-presented, and is now
    /// holding its clock at <see cref="Edge"/>. Completing the edge starts it; cancelling
    /// <see cref="Cancellation"/> rolls it back.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the lead: the media open happens BEFORE the out-point instead of
    /// after it, so the successor's start no longer carries its own decoder-open time. The instant it
    /// starts is unchanged - the edge is still the outgoing clip's natural end - which is why this
    /// needs no new time source and can never fire early.
    /// </remarks>
    private sealed class PreparedFollow(
        Guid successorId,
        CueList successorList,
        TaskCompletionSource edge,
        CancellationTokenSource cancellation)
    {
        public Guid SuccessorId { get; } = successorId;
        public CueList SuccessorList { get; } = successorList;
        public TaskCompletionSource Edge { get; } = edge;

        /// <summary>Cancelled by stops/resets; DISPOSED only by <see cref="ObservePreparedFollowAsync"/>
        /// (or the reservation rollback), so a racing Cancel never touches a disposed source.</summary>
        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>
        /// Null while the entry is only a reservation. The entry is inserted BEFORE the open is
        /// launched so a stop landing in that gap can find and cancel it; the run is attached the
        /// moment it exists.
        /// </summary>
        public Task<IReadOnlyList<Guid>>? Run { get; set; }
    }

    // Preparing five seconds ahead absorbs decoder/open jitter without holding every clip in a long show open
    // for the life of the timeline. A cue that explicitly disables pre-roll is prepared at its due point.
    private static readonly TimeSpan TimelinePreparationLead = TimeSpan.FromSeconds(5);

    // Lateness accounting for timeline dispatches. The scheduler can only ever fire LATE (the poll loop
    // exits at-or-after the target, never before), and normally by single milliseconds - but a dispatch
    // can also slip visibly when its preparation was not done by the due point or its release queued
    // behind a long dispatcher turn. Those excursions used to be invisible: an event that fired 300 ms
    // past its authored time looked identical, in every log and panel, to one that fired on time. Counted
    // above one 60 Hz frame (anything smaller is scheduler weather), reported to the operator above a
    // threshold no show should ever hit in normal operation.
    private static readonly TimeSpan LateDispatchCountThreshold = TimeSpan.FromMilliseconds(17);
    private static readonly TimeSpan LateDispatchReportThreshold = TimeSpan.FromMilliseconds(45);
    private readonly Lock _timingGate = new();
    private long _timelineDispatches;
    private long _timelineLateDispatches;
    private TimeSpan _timelineMaxLateness;
    private TimeSpan _timelineLastLateness;

    /// <summary>Timeline dispatch timing since load — dispatch count, how many slipped past one frame,
    /// and the worst/most recent slip. For the rig report: this is the number that says whether authored
    /// times are actually being honoured, which no per-voice clock readout can.</summary>
    public (long Dispatched, long Late, TimeSpan MaxLateness, TimeSpan LastLateness) TimelineDispatchTiming
    {
        get
        {
            lock (_timingGate)
                return (_timelineDispatches, _timelineLateDispatches, _timelineMaxLateness, _timelineLastLateness);
        }
    }

    private void RecordTimelineDispatch(TimeSpan due, TimeSpan lateness)
    {
        lock (_timingGate)
        {
            _timelineDispatches++;
            if (lateness > LateDispatchCountThreshold)
                _timelineLateDispatches++;
            if (lateness > _timelineMaxLateness)
                _timelineMaxLateness = lateness;
            _timelineLastLateness = lateness;
        }

        if (lateness > LateDispatchReportThreshold)
            host.Report(
                $"timeline event at {due:mm\\:ss\\.fff} fired {lateness.TotalMilliseconds:0} ms late — " +
                "its media was still preparing at the due time, or the release queued behind other work");
    }

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
        bool skipPreWait = false,
        TimeSpan? automationStartPosition = null,
        CancellationToken cancellationToken = default)
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
            AutomationCueNode automation => await host.RunAutomationAsync(
                automation, list, automationStartPosition ?? TimeSpan.Zero, cancellationToken)
                .ConfigureAwait(false),
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
                 && cue is not AutomationCueNode
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
                        [.. visualizers.Select(cue => new TimelineVisualizerStart(cue))],
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

        // A successor prepared during the lead window is ALREADY holding its clock at this exact edge -
        // releasing it here is the whole hop. Checked first: the ordinary paths below would fire it a
        // second time, from cold.
        if (await TryReleasePreparedFollowAsync(cueId).ConfigureAwait(false))
            return;

        if (await AdvancePlaylistAsync(cueId, approaching: false).ConfigureAwait(false))
            return;

        if (await FinishArmedItemAsync(cueId).ConfigureAwait(false))
            return;

        if (Project.FindCue(cueId) is MediaCueNode { EndTargetCueId: not null } media)
        {
            if (ResolveEndTarget(media) is not ({ } target, { } targetList))
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

    /// <summary>
    /// The clip is inside its pre-end window. Two things can want that notification: a playlist
    /// crossfade (start the next item early, overlapping) and a follow lead (OPEN the successor early
    /// but start it on the out-point). The playlist gets first refusal, because when a group owns the
    /// cue its crossfade is the authored intent and a follow lead would fire a second successor.
    /// </summary>
    public async Task OnApproachingEndAsync(Guid cueId)
    {
        if (await AdvancePlaylistAsync(cueId, approaching: true).ConfigureAwait(false))
            return;
        await TryBeginPreparedFollowAsync(cueId).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens this cue's follow successor now, and parks it on an edge that the out-point will release.
    /// Returns false - leaving the historical open-after-the-edge path in charge - whenever the chain is
    /// not one this can schedule.
    /// </summary>
    /// <remarks>
    /// The opt-outs are all "there is no fixed edge to schedule against, or nothing to pre-open":
    /// <list type="bullet">
    /// <item>the outgoing cue has a <c>PostWaitMs</c> - an authored wait sits between the out-point and
    /// the successor, so the out-point is not the successor's start;</item>
    /// <item>the successor has a <c>PreWaitMs</c> - same thing on the other side;</item>
    /// <item>the successor is not a playable cue (a group, jump, fade, patch or action) - those resolve
    /// to decisions rather than to media, and there is nothing to open ahead of time;</item>
    /// <item>a prepared follow for this cue already exists - the notification is one-shot per committed
    /// clip, but a re-fire can re-arm it.</item>
    /// </list>
    /// </remarks>
    private async Task<bool> TryBeginPreparedFollowAsync(Guid cueId)
    {
        // Gated HERE as well as in the compiler: a playlist crossfade sets PreEndNotify on its own
        // account, so "approaching end" reaches cues in shows that configured no follow lead at all.
        if (Project.Settings.FollowLeadMs <= 0)
            return false;
        if (Project.FindCue(cueId) is not { } cue || Project.ListOf(cueId) is not { } list)
            return false;
        if (cue.PostWaitMs > 0)
            return false;

        // A sequence-owned cue's Follow/end target is swallowed by its group: the playlist or armed
        // run advances the chain, not the cue. A successor prepared here would release at the
        // out-point AHEAD of AdvancePlaylistAsync/FinishArmedItemAsync and hijack the run.
        if (IsSequenceOwned(cueId))
            return false;

        var successor = ResolveFollowSuccessor(cue, list);
        if (successor is null || successor.PreWaitMs > 0)
            return false;
        if (successor is not (MediaCueNode or TextCueNode))
            return false;

        var successorList = Project.ListOf(successor.Id) ?? list;
        var edge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        var prepared = new PreparedFollow(successor.Id, successorList, edge, cancellation);

        // Reserve the entry BEFORE the open is launched. A stop landing between "approaching end"
        // and the open used to find no entry to cancel and then watch the open commit a successor
        // voice nothing would ever release; with the reservation in place first, that stop's
        // CancelPreparedFollow always has something to cancel.
        lock (_stateGate)
        {
            if (_preparedFollows.ContainsKey(cueId))
            {
                cancellation.Dispose();
                return false;
            }

            _preparedFollows[cueId] = prepared;
        }

        // The stop path removes the cue from Sounding BEFORE it calls OnStopped, so after the
        // reservation this read is decisive: a stop that beat the reservation is visible here (roll
        // back, never open), and one that lands after it finds the reservation and cancels it.
        if (!host.Sounding.Contains(cueId))
        {
            lock (_stateGate)
            {
                if (_preparedFollows.TryGetValue(cueId, out var current)
                    && ReferenceEquals(current, prepared))
                    _preparedFollows.Remove(cueId);
            }

            cancellation.Dispose();
            return false;
        }

        // PlayTimelineMediaAsync does the opening, committing, pre-rolling and sync-presenting, then
        // enters waitForStartEdge. Everything expensive is therefore behind us when the edge completes.
        // A cancel that raced in since the reservation has already flagged the token, so the open
        // unwinds instead of committing.
        var run = host.PlayTimelineMediaAsync(
            [new TimelineMediaStart(successor)],
            successorList,
            async ct => await edge.Task.WaitAsync(ct).ConfigureAwait(false),
            cancellation.Token);
        lock (_stateGate)
            prepared.Run = run;

        // NOT AdvanceAsync here: the cursor marks what the show has moved on to, and during the lead
        // window it has not - the outgoing cue is still playing. Advancing at prepare time would make
        // the standby readout jump forward seconds early, and a stop inside the window would leave it
        // pointing at a cue that never started. It happens at the edge instead, as it always did.
        _ = ObservePreparedFollowAsync(cueId, prepared);
        return true;
    }

    /// <summary>Completes a prepared successor's edge, if there is one. True when this hop is handled.</summary>
    private async Task<bool> TryReleasePreparedFollowAsync(Guid cueId)
    {
        PreparedFollow? prepared;
        lock (_stateGate)
        {
            if (!_preparedFollows.TryGetValue(cueId, out prepared))
                return false;
            _preparedFollows.Remove(cueId);
        }

        // Still only a reservation: the open never launched. Cancel it so a late launch unwinds, and
        // let the ordinary path advance from cold.
        if (prepared.Run is not { } run)
        {
            try { prepared.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            return false;
        }

        // If preparation already failed or was cancelled, ObservePreparedFollowAsync has cancelled the
        // edge and setting it is a no-op - fall through to the ordinary path so the chain still
        // advances, from cold.
        if (!prepared.Edge.TrySetResult())
            return false;

        // Await the run itself, not just the edge: TrySetResult merely RELEASES the successor, and its
        // start then runs on whatever thread picks up the continuation. Callers of the natural-end
        // handler are entitled to treat its return as "the hop happened".
        IReadOnlyList<Guid> started;
        try
        {
            started = await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Raced with a stop between the release and the start - the chain simply ends here.
            return true;
        }
        catch (Exception failure)
        {
            host.Report($"the prepared next cue failed to start — {failure.Message}; opening it from cold");
            return false;
        }

        // The host swallows most open failures (a moved file, a decoder that refused) and completes
        // the run with an empty voice list instead of faulting. A "successful" run that started
        // nothing is still a failed preparation - fall back to the cold open.
        if (started.Count == 0)
        {
            host.Report("the prepared next cue had nothing to start — opening it from cold instead");
            return false;
        }

        await AdvanceAsync(prepared.SuccessorList, prepared.SuccessorId).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Drops a prepared follow that never reached its edge - the outgoing cue was stopped, the document
    /// reloaded, or its own preparation faulted - and rolls the successor back.
    /// </summary>
    private void CancelPreparedFollow(Guid cueId)
    {
        PreparedFollow? prepared;
        lock (_stateGate)
        {
            if (!_preparedFollows.TryGetValue(cueId, out prepared))
                return;
            _preparedFollows.Remove(cueId);
        }

        try { prepared.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Cancels every prepared follow and returns their rollbacks to await.</summary>
    private Task[] CancelAllPreparedFollows()
    {
        PreparedFollow[] pending;
        lock (_stateGate)
        {
            pending = [.. _preparedFollows.Values];
            _preparedFollows.Clear();
        }

        foreach (var prepared in pending)
        {
            try { prepared.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // Swallow here rather than at the join: a cancelled preparation faulting its own task is the
        // EXPECTED outcome of this method, and ObservePreparedFollowAsync already reports real failures.
        // A null Run is a reservation whose open never launched; its token is already cancelled, so
        // the launch (if it still happens) unwinds on arrival and there is nothing to await.
        return [.. pending.Select(async prepared =>
        {
            try
            {
                if (prepared.Run is { } run)
                    await run.ConfigureAwait(false);
            }
            catch { /* rolled back */ }
        })];
    }

    /// <summary>
    /// Reports a preparation that failed on its own (a missing file, a decoder that would not open) and
    /// clears its entry, so the out-point falls back to the ordinary cold path instead of releasing an
    /// edge nothing is waiting on. Also owns the token source's disposal - it is the one continuation
    /// guaranteed to run exactly once per launched preparation.
    /// </summary>
    private async Task ObservePreparedFollowAsync(Guid cueId, PreparedFollow prepared)
    {
        try
        {
            var started = await prepared.Run!.ConfigureAwait(false);

            // The host swallows most open failures (a moved file, a decoder that refused) and completes
            // the run with an empty voice list instead of faulting - without ever entering the edge
            // wait. Cancelling the edge FIRST decides the race with a concurrent release: whichever
            // side wins, the loser sees the completed edge and stands down, so the out-point either
            // handles the fallback itself or finds no entry and advances from cold.
            if (started.Count == 0 && prepared.Edge.TrySetCanceled())
                DropDeadPreparation(cueId, prepared,
                    "follow pre-open started nothing — the next cue will open at its edge instead");
        }
        catch (OperationCanceledException)
        {
            // Stopped, reloaded, or superseded - not a fault.
        }
        catch (Exception failure)
        {
            if (prepared.Edge.TrySetCanceled())
                DropDeadPreparation(cueId, prepared,
                    $"follow pre-open failed — the next cue will open at its edge instead ({failure.Message})");
        }
        finally
        {
            prepared.Cancellation.Dispose();
        }
    }

    /// <summary>Removes a dead preparation's entry - only if it is still THIS preparation, not a
    /// re-armed successor from a later fire - and tells the operator.</summary>
    private void DropDeadPreparation(Guid cueId, PreparedFollow prepared, string report)
    {
        lock (_stateGate)
        {
            if (_preparedFollows.TryGetValue(cueId, out var current) && ReferenceEquals(current, prepared))
                _preparedFollows.Remove(cueId);
        }

        host.Report(report);
    }

    /// <summary>
    /// Where this cue hands on to: its explicit end target if it has one, otherwise the next enabled cue
    /// in the list. GUARANTEED to be the same resolution <see cref="OnNaturalEndAsync"/> and
    /// <see cref="ContinueFromAsync"/> use - all three consume <see cref="ResolveEndTarget"/> and
    /// <see cref="ResolveFollowNext"/> rather than restating the policy - because a lead that prepared
    /// a different successor from the one the chain would pick is worse than no lead.
    /// </summary>
    private CueNode? ResolveFollowSuccessor(CueNode cue, CueList list)
    {
        if (cue is MediaCueNode { EndTargetCueId: not null } media)
            return ResolveEndTarget(media) is ({ } target, _) ? target : null;

        if (cue.Trigger != CueTrigger.Follow)
            return null;

        return ResolveFollowNext(list, cue.Id, out _);
    }

    /// <summary>
    /// The live cue a media cue's end target points at, with the list its cursor moves in - or null
    /// when the target is the cue itself, missing, disabled, a comment, or in no list. Null means the
    /// chain STOPS: a dead end target never falls back to the Follow rule.
    /// </summary>
    private (CueNode Target, CueList List)? ResolveEndTarget(MediaCueNode media)
    {
        if (media.EndTargetCueId is not { } targetId || targetId == media.Id)
            return null;
        if (Project.FindCue(targetId) is not { Enabled: true } target || target is CommentCueNode)
            return null;
        return Project.ListOf(targetId) is { } targetList ? (target, targetList) : null;
    }

    /// <summary>
    /// The next cue a Follow hands to within its list, honouring the disabled-cue policy.
    /// <paramref name="stopChain"/> distinguishes "the policy stops the chain at a disabled successor"
    /// from "walked off the end of the list", where the list-end policy applies instead.
    /// </summary>
    private CueNode? ResolveFollowNext(CueList list, Guid cueId, out bool stopChain)
    {
        if (Project.Settings.DisabledCueFollow == DisabledCueFollow.StopTheChain)
        {
            var next = NextAfterSubtree(list, cueId);
            stopChain = next is { Enabled: false };
            return stopChain ? null : next;
        }

        stopChain = false;
        return CueOrder.NextEnabled(list, cueId);
    }

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
        // A stop is the operator vetoing this cue's chain: the successor it was quietly opening must go
        // back rather than start when something else ends.
        CancelPreparedFollow(cueId);
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
        _ = CancelAllPreparedFollows(); // the awaited form is CancelTimelineRunsAsync; this path is fire-and-forget
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

        // A prepared follow is the same kind of thing as a timeline run - a committed voice holding at
        // an edge - so it has to roll back here too, and be AWAITED. Without that, a successor opened
        // during a lead window could cross its edge while the already-active set was fading out.
        var follows = CancelAllPreparedFollows();

        await Task.WhenAll(runs.Select(run => run.Completion).Concat(follows)).ConfigureAwait(false);
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

        if (follow)
        {
            next = ResolveFollowNext(list, cue.Id, out var stopChain);
            if (stopChain)
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
                var canStraddle = child is MediaCueNode or TextCueNode or VisualizerCueNode
                    or AutomationCueNode;
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
                    // The model's own cue→media mapping, never hand-rolled trim arithmetic: the two
                    // halves of this mapping being written independently is exactly what made trimmed
                    // cues seek wrong (see MediaCueNode.MediaTimeAt).
                    initialPosition = child is MediaCueNode media ? media.MediaTimeAt(into) : into;
                else if (child is AutomationCueNode or VisualizerCueNode)
                    initialPosition = into;
            }

            var timelineEvent = EventAt(due);
            if (child is MediaCueNode or TextCueNode)
                timelineEvent.Media.Add(new TimelineMediaStart(child, initialPosition));
            else if (child is VisualizerCueNode visualizer)
                timelineEvent.Visualizers.Add(new TimelineVisualizerStart(
                    visualizer, initialPosition ?? TimeSpan.Zero));
            else
                timelineEvent.Controls.Add(new TimelineControlStart(
                    child, initialPosition ?? TimeSpan.Zero));
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
            AutomationRunClock clock;

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
                clock = new AutomationRunClock(host, playhead);
                await DispatchTimelineEventAsync(initial, depth, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                clock = new AutomationRunClock(host, playhead);
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
                        item.Event, depth, cancellationToken, clock));
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
                    Project.ListOf(timelineEvent.Visualizers[0].Cue.Id),
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
        CancellationToken cancellationToken,
        AutomationRunClock? clock = null)
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

            // Sampled at the actual release moment (scheduler wait AND ready-gates both behind us), against
            // the same pause-aware coordinate that scheduled the event - so the number is the show-time slip
            // the audience got, not a wall-clock artifact. Null clock = the initial straddled event, whose
            // release IS the timeline epoch; it cannot be late by definition.
            if (clock is not null)
                RecordTimelineDispatch(timelineEvent.Due, clock.Position - timelineEvent.Due);

            // Release prepared media and dispatch non-media participants in the same continuation. Media
            // starters then serialize through one dispatcher turn; controls no longer wait for decoder opens.
            timelineEvent.MediaGate.Release();
            timelineEvent.VisualizerGate.Release();
            var controls = timelineEvent.Controls
                .Select(start => FireAsync(
                    start.Cue.Id, depth + 1, skipPreWait: true,
                    automationStartPosition: start.StartPosition,
                    cancellationToken: cancellationToken))
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

        if (cue is AutomationCueNode { DurationMs: > 0 } automation)
            return TimeSpan.FromMilliseconds(automation.DurationMs);

        var probed = host.MediaLength(cue.Id);

        return cue is MediaCueNode media ? media.TrimmedLength(probed) : probed;
    }
}
