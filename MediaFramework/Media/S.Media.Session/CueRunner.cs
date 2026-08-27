using System.Collections.Concurrent;
using System.Diagnostics;
using S.Media.Core.Diagnostics;

namespace S.Media.Session;

/// <summary>
/// The cue layer: fire sequencing and GO. Owns the PER-RUNTIME-GROUP fire locks (F-08: fires/GOs
/// touching the same group never interleave, while unrelated lists proceed independently - a cold
/// or slow open on list A no longer delays GO on list B) and each in-flight fire's cancellation
/// source, and runs every cue fire OFF the serial dispatcher (NXT-03) so a pre-wait or media open
/// never parks the loop - STOP/LOAD/DISPOSE preempt it through <see cref="CancelActiveFire"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reaches the engine only through <see cref="ICueRunnerHost"/>. That is the point: cue semantics - what
/// "next" means, when a cue is skipped, how the cursor moves - are this type's business, and the engine
/// underneath has no opinion about any of it. A host with no cue list never constructs this at all.
/// </para>
/// <para>The session's public fire/GO API delegates here.</para>
/// </remarks>
internal sealed class CueRunner
{
    private readonly ICueRunnerHost _host;

    // The queued and in-flight cue fires (each one's lock wait + pre/post-wait + open + auto-continue
    // chain), WITH their
    // identity: which cue ids each can start (the fired cues plus their static auto-continue closure)
    // and which runtime groups those land on. Entries live from request admission through execution;
    // read off-dispatcher so
    // STOP/LOAD/DISPOSE can abort them (NXT-03). The identity is what makes a per-cue or per-group
    // stop surgical: this was a bare single CancellationTokenSource once, and CancelFiresForCue/Group
    // cancelled it UNCONDITIONALLY - so stopping unrelated cue B while cue A sat in its pre-wait or
    // media open silently killed A's fire. An indexed SET (not a slot) because per-group fire locks
    // (F-08) allow fires on unrelated groups to be in flight simultaneously.
    private readonly ConcurrentDictionary<CancellationTokenSource, ActiveFire> _activeFires = new();

    private sealed record ActiveFire(
        IReadOnlyList<string> CueIds,
        IReadOnlyList<string> RuntimeGroupIds,
        CancellationTokenSource Cancellation)
    {
        public bool Involves(string cueId) => CueIds.Contains(cueId, StringComparer.Ordinal);

        public bool InvolvesGroup(string runtimeGroupId) =>
            RuntimeGroupIds.Contains(runtimeGroupId, StringComparer.Ordinal);
    }

    // Absolute timeline fires release the ordinary fire lock once every voice is prepared, otherwise a cue
    // waiting several seconds for its authored edge would prevent later timeline events from preparing. They
    // still have to remain cancellable after that release - but only by an operation that actually MEANS
    // them: the batch remembers which cues and runtime groups it carries, so a per-cue or per-group stop can
    // target it while a stop of some unrelated cue leaves it waiting for its edge. Blanket cancellation here
    // once let any single-cue stop silently kill every prepared timeline event on every list (a window of
    // seconds per event, mid-show) - the timeline run's own token already covers run-level teardown, so the
    // pending set only needs panic/load/dispose plus these targeted forms.
    private readonly ConcurrentDictionary<CancellationTokenSource, PendingScheduledFire> _pendingScheduledFires = new();

    private sealed record PendingScheduledFire(IReadOnlyList<string> CueIds, IReadOnlyList<string> RuntimeGroupIds);

    // Off-dispatcher fire model (NXT-03): a cue fire runs OFF the serial dispatcher (its pre-wait + media open
    // no longer park the loop, so STOP/seek/load/queries stay responsive), re-entering only for short state
    // commits. The session's show generation lets a fire whose open straddled a reload discard its
    // (now-stale) clip at commit instead of corrupting the newer show.
    //
    // F-08: fires are serialized PER RUNTIME GROUP rather than by one global lock. The old single
    // _fireLock held select → pre-wait → open → fire → cursor advance for EVERY fire, so an
    // unrelated list's GO waited behind a cold or slow open - up to the 30 s batch bound in the
    // worst case. A fire now acquires the lock of every runtime group its cue chain can land a
    // voice on (plus any caller-synthesized batch groups), ALWAYS in ordinal order so overlapping
    // multi-group batches cannot deadlock; same-group operations (double GO, replacing commits)
    // stay exactly-once/serialized, and unrelated groups proceed independently.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupFireLocks = new(StringComparer.Ordinal);

    private SemaphoreSlim GroupFireLock(string groupId) =>
        _groupFireLocks.GetOrAdd(groupId, static _ => new SemaphoreSlim(1, 1));

    // F-08 acceptance: the fire path's latency phases are observable. Lock-wait is THE head-of-line
    // signal (an unrelated group's fire can no longer cause it; a same-group wait is real
    // serialization the operator should be able to see and attribute).
    private readonly TimingAccumulator _lockWaitTiming = new();
    private readonly TimingAccumulator _goSelectTiming = new();
    private readonly TimingAccumulator _fireExecuteTiming = new();

    /// <summary>A lock wait longer than this logs a warning naming the contended groups - the
    /// "why was my GO late" answer, in the log, at the moment it happened. Same-group serialization
    /// is CORRECT (a cue mid-pre-wait legitimately holds its group), so the default only speaks up
    /// when the wait is long enough for an operator to have felt it. Settable for tests.</summary>
    internal TimeSpan LockWaitWarnThreshold { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Point-in-time fire-path timings; see <see cref="CueFireTimings"/> for semantics.</summary>
    public CueFireTimings Timings => new(
        _lockWaitTiming.Snapshot(), _goSelectTiming.Snapshot(), _fireExecuteTiming.Snapshot());

    /// <summary>Every runtime group the fire of <paramref name="cueIds"/> can land a voice on: the
    /// authored groups of the cues plus their static auto-continue closure, plus any
    /// caller-synthesized <paramref name="runtimeGroupIds"/>. Never empty - a fire of an unknown cue
    /// still serializes on the default group rather than running unlocked.</summary>
    private IReadOnlyCollection<string> FireLockSet(
        CueGraph graph, IEnumerable<string> cueIds, IEnumerable<string>? runtimeGroupIds = null)
    {
        var groups = new HashSet<string>(runtimeGroupIds ?? [], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var walk = new Stack<string>(cueIds);
        while (walk.Count > 0)
        {
            var id = walk.Pop();
            if (!seen.Add(id) || !graph.TryGetCue(id, out var cue))
                continue;
            groups.Add(cue.GroupId ?? _host.DefaultGroupId);
            if (cue is { AutoContinue: true, FollowOnCueId: not null })
                walk.Push(cue.FollowOnCueId);
        }

        if (groups.Count == 0)
            groups.Add(_host.DefaultGroupId);
        return groups;
    }

    /// <summary>Acquires the fire locks of <paramref name="groupIds"/> in ordinal order (the one
    /// global order every acquirer uses - what keeps overlapping batches deadlock-free). On failure
    /// or cancellation, everything already taken is released before the throw.</summary>
    private async Task<IReadOnlyList<SemaphoreSlim>> AcquireGroupLocksAsync(
        IReadOnlyCollection<string> groupIds, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var ordered = groupIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(GroupFireLock)
            .ToArray();
        var acquired = 0;
        try
        {
            foreach (var groupLock in ordered)
            {
                await groupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired++;
            }
        }
        catch
        {
            for (var i = acquired - 1; i >= 0; i--)
                ordered[i].Release();
            throw;
        }

        var waitedTicks = Stopwatch.GetTimestamp() - started;
        _lockWaitTiming.Record(waitedTicks);
        var waited = TimeSpan.FromSeconds(waitedTicks / (double)Stopwatch.Frequency);
        if (waited >= LockWaitWarnThreshold)
        {
            // The head-of-line answer, at the moment it happened: this fire waited behind another
            // fire on the SAME group(s) - a pre-wait, a cold open, a preparing batch. Unrelated
            // groups cannot cause this (F-08); a frequent warning here means the show's cue layout
            // is serializing on one list, not that the runner regressed.
            MediaDiagnostics.LogWarning(
                "CueRunner: a fire waited {0:0} ms for the fire lock(s) of group(s) [{1}] - another "
                + "fire on the same group(s) held them (pre-wait, media open, or batch preparation)",
                waited.TotalMilliseconds,
                string.Join(", ", groupIds.Distinct(StringComparer.Ordinal)));
        }

        return ordered;
    }

    private static void ReleaseGroupLocks(IReadOnlyList<SemaphoreSlim> locks)
    {
        for (var i = locks.Count - 1; i >= 0; i--)
            locks[i].Release();
    }

    // A batch fire's preparation stage (open + commit + pre-roll + sync present) is bounded work by
    // construction - no authored waits live inside it - so a stage that has not finished in this long is
    // WEDGED, almost always a synchronous native open that cancellation cannot reach (the CancelActiveFire
    // doc's caveat). Without a bound, that one open held its group fire locks forever and every subsequent
    // GO on those groups blocked behind it for the rest of the show. The figure is deliberately far above any legitimate
    // open (cold 4K network files included): tripping it on a slow-but-alive open would tear down a batch
    // an operator still wanted, which is worse than waiting. Settable so a test can wedge a fake open
    // without waiting half a minute; production never writes it.
    internal TimeSpan BatchPreparationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // After a preparation timeout cancels the batch, responsive participants roll back promptly; the wedged
    // one, by definition, may never answer. Wait this long for the rollbacks, then abandon the batch task
    // rather than trading "GO blocks forever" for "STOP blocks forever". An abandoned voice's own finally
    // still runs if its open ever returns.
    internal TimeSpan AbandonedBatchDrainTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The live cue graph and its lifetime. A fire captures both: its lock set, identity, and
    /// execution all use the same graph. Committing a replacement atomically swaps the pair and cancels
    /// the old lifetime, closing the narrow race where a fire could register after Load's initial
    /// cancellation but before the graph swap.</summary>
    private sealed class CueGraphState(CueGraph graph)
    {
        public CueGraph Graph { get; } = graph;
        public CancellationTokenSource Lifetime { get; } = new();
    }

    private CueGraphState _graphState = new(new CueGraph());

    private CueGraphState CurrentGraphState => Volatile.Read(ref _graphState);

    // In-flight explicit crossfade fires' windows, KEYED BY CUE ID: published just before the graph
    // action runs and consumed by that cue's own action. A map rather than a parameter because the
    // action closure is built at load time, long before any particular fire knows whether it carries
    // a crossfade - and a map rather than the old single field because per-group fire locks (F-08)
    // let unrelated fires overlap, and a shared slot would have let a concurrent plain fire consume
    // (steal) another cue's crossfade window.
    private readonly ConcurrentDictionary<string, Tuple<TimeSpan, FadeShape>> _pendingFireCrossfades =
        new(StringComparer.Ordinal);

    public CueRunner(ICueRunnerHost host) => _host = host;

    /// <summary>Every cue in the live graph, in registration order.</summary>
    public IReadOnlyList<CueDefinition> Cues => CurrentGraphState.Graph.Cues;

    /// <summary>An immutable snapshot of the cue execution log.</summary>
    public IReadOnlyList<CueExecutionLogEntry> ExecutionLog => CurrentGraphState.Graph.ExecutionLog;

    /// <summary>Looks a cue up in the live graph.</summary>
    public bool TryGetCue(string cueId, out CueDefinition cue) => CurrentGraphState.Graph.TryGetCue(cueId, out cue);

    /// <summary>
    /// Builds a replacement cue graph without installing it - each cue wired to the engine's play primitive.
    /// </summary>
    /// <remarks>
    /// Staging and committing are separate so a load that fails part-way leaves the running show intact:
    /// nothing observable changes until <see cref="Commit"/> performs its single reference swap.
    /// </remarks>
    /// <param name="cues">The document's cues; registration order is by cue number.</param>
    /// <param name="resolveClip">Cue id → its clip, or null when the cue binds none.</param>
    /// <param name="defaultGroup">The group a cue with no explicit group plays on.</param>
    public CueGraph StageCues(
        IReadOnlyList<CueDefinition> cues,
        Func<string, ShowClipBinding?> resolveClip,
        string defaultGroup)
    {
        var graph = new CueGraph();
        foreach (var cue in cues.OrderBy(c => c.Number))
        {
            var groupId = cue.GroupId ?? defaultGroup;
            var cueId = cue.Id;
            var binding = resolveClip(cue.Id);
            // A cue without a clip currently has no executable session action. Do not report it as Fired: that
            // produced a successful no-op and made HaPlay briefly mark a stale/unbound media cue as playing.
            // Future control/stop cues need their own action binding rather than relying on an empty clip.
            graph.AddCue(
                cue,
                ct => _host.PlayClipAsync(
                    groupId, binding, ct, waitForStartBarrier: null, crossfade: TakePendingFireCrossfade(cueId)),
                binding is null ? static () => false : null);
        }

        return graph;
    }

    /// <summary>Installs a staged graph. One atomic reference assignment is the load's commit point;
    /// cancelling the previous graph lifetime prevents any request queued against it from running later.</summary>
    public void Commit(CueGraph graph)
    {
        var previous = Interlocked.Exchange(ref _graphState, new CueGraphState(graph));
        try { previous.Lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Consumes the cue's pending crossfade window, if any. Read once per graph action.</summary>
    private (TimeSpan Duration, FadeShape Curve)? TakePendingFireCrossfade(string cueId) =>
        _pendingFireCrossfades.TryRemove(cueId, out var pending)
            ? (pending.Item1, pending.Item2)
            : null;

    /// <summary>Runs a cue's fire against the live graph, publishing its crossfade window first.</summary>
    private async Task<CueExecutionStatus> FireOnGraphAsync(
        CueGraph graph,
        string cueId,
        CancellationToken token,
        (TimeSpan Duration, FadeShape Curve)? crossfade = null)
    {
        if (crossfade is { } window)
            _pendingFireCrossfades[cueId] = Tuple.Create(window.Duration, window.Curve);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await graph.FireAsync(cueId, token).ConfigureAwait(false);
        }
        finally
        {
            _fireExecuteTiming.RecordSince(started);
            // A skipped/failed/cancelled fire may never reach its clip action - the unconsumed window
            // must not leak into a later fire of the same cue. (A refire of this cue serializes on
            // its group lock, so this cleanup can never remove a newer fire's window.)
            if (crossfade is not null)
                _pendingFireCrossfades.TryRemove(cueId, out _);
        }
    }

    /// <summary>
    /// GO's cue selection: the next armed and enabled cue on <paramref name="groupId"/> after the cursor.
    /// </summary>
    /// <remarks>Reads the cursor from the engine, then decides here - "next" is a cue-list question, and a
    /// disabled or unarmed cue is skipped rather than fired (NXT-07).</remarks>
    private async Task<(CueDefinition? Next, int Generation)> SelectNextGoCueAsync(
        CueGraph graph, string groupId, string defaultGroup)
    {
        var (cursor, generation) = await _host.ReadGoCursorAsync(groupId).ConfigureAwait(false);
        return (SelectNext(graph, groupId, defaultGroup, cursor), generation);
    }

    /// <summary>The selection itself, shared by GO and both peeks so they can never disagree.</summary>
    private static CueDefinition? SelectNext(CueGraph graph, string groupId, string defaultGroup, int cursor) =>
        graph.Cues
            .Where(c => (c.GroupId ?? defaultGroup) == groupId && c.Number > cursor && c.Armed && c.Enabled)
            .OrderBy(c => c.Number)
            .FirstOrDefault();

    /// <summary>
    /// What GO would fire next on <paramref name="groupId"/>, without firing it.
    /// </summary>
    /// <remarks>The standby readout. Deliberately runs the SAME selection GO uses rather than a parallel
    /// "next cue" rule, so what a list says it will do and what it then does cannot disagree.</remarks>
    public async Task<CueDefinition?> PeekNextAsync(string groupId)
    {
        var graph = CurrentGraphState.Graph;
        var (next, _) = await SelectNextGoCueAsync(graph, groupId, _host.DefaultGroupId).ConfigureAwait(false);
        return next;
    }

    /// <summary>
    /// <see cref="PeekNextAsync"/> for every group at once, through ONE cursor read.
    /// </summary>
    /// <remarks>The per-group form costs a dispatcher round-trip each; a host polling standby for
    /// all its cue lists four times a second paid lists × 4 hops/s for cursors that live side by
    /// side. One answer per input, same order.</remarks>
    public async Task<IReadOnlyList<CueDefinition?>> PeekNextManyAsync(IReadOnlyList<string> groupIds)
    {
        if (groupIds.Count == 0)
            return [];

        var graph = CurrentGraphState.Graph;
        var cursors = await _host.ReadGoCursorsAsync(groupIds).ConfigureAwait(false);
        var next = new CueDefinition?[groupIds.Count];
        for (var i = 0; i < groupIds.Count; i++)
            next[i] = SelectNext(graph, groupIds[i], _host.DefaultGroupId, cursors[i]);
        return next;
    }

    /// <summary>The cue number that would put <paramref name="cueId"/> next in line, or null if unknown.</summary>
    /// <remarks>One before the cue's own number: GO selects the lowest number strictly greater than the
    /// cursor, so parking the cursor just below a cue makes that cue next.</remarks>
    public int? CursorForStandby(string cueId, string groupId, string defaultGroup) =>
        CurrentGraphState.Graph.TryGetCue(cueId, out var cue)
        && string.Equals(cue.GroupId ?? defaultGroup, groupId, StringComparison.Ordinal)
            ? cue.Number - 1
            : null;

    /// <summary>The next <paramref name="count"/> clip-bound cue ids on a group after <paramref name="cursor"/> -
    /// what the engine pre-rolls so the next GO opens warm.</summary>
    public IReadOnlyList<string> UpcomingCueIds(string groupId, string defaultGroup, int cursor, int count) =>
        [.. CurrentGraphState.Graph.Cues
            .Where(c => (c.GroupId ?? defaultGroup) == groupId && c.Number > cursor)
            .OrderBy(c => c.Number)
            .Take(count)
            .Select(c => c.Id)];

    /// <summary>See <see cref="ShowSession.FireCueAsync(string)"/> (the public doc lives there).
    /// <paramref name="crossfade"/> is the optional dual-voice window of the crossfade overload; null is the
    /// historical butt-splice fire, untouched.</summary>
    public async Task<CueExecutionStatus> FireCueAsync(
        string cueId, (TimeSpan Duration, FadeShape Curve)? crossfade = null)
    {
        var state = CurrentGraphState;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(state.Lifetime.Token);
        _activeFires.TryAdd(cts, DescribeFire(state.Graph, cts, [cueId]));
        IReadOnlyList<SemaphoreSlim>? locks = null;
        try
        {
            // Register BEFORE waiting: panic/load/group-stop must be able to cancel a request queued
            // behind an earlier same-group fire. The cancellation token also removes it from the
            // semaphore's waiter queue instead of letting it start after the stop returns.
            locks = await AcquireGroupLocksAsync(FireLockSet(state.Graph, [cueId]), cts.Token)
                .ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            return await FireOnGraphAsync(state.Graph, cueId, cts.Token, crossfade).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CueExecutionStatus.Failed; // cancelled by stop/load/dispose
        }
        finally
        {
            _activeFires.TryRemove(cts, out _);
            if (locks is not null)
                ReleaseGroupLocks(locks);
        }
    }

    /// <summary>
    /// The identity of a fire about to run: the requested cues plus their STATIC auto-continue closure
    /// (one fire's token covers the whole chain, so a stop of any chain member must reach it), and the
    /// runtime groups those cues land on. <paramref name="runtimeGroupIds"/> adds the caller-synthesized
    /// groups of an independent/scheduled batch, which are not in the graph.
    /// </summary>
    private ActiveFire DescribeFire(
        CueGraph graph,
        CancellationTokenSource cts,
        IEnumerable<string> cueIds,
        IEnumerable<string>? runtimeGroupIds = null)
    {
        var ids = new List<string>();
        var groups = new List<string>(runtimeGroupIds ?? []);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var walk = new Stack<string>(cueIds);

        while (walk.Count > 0)
        {
            var id = walk.Pop();
            if (!seen.Add(id))
                continue;

            ids.Add(id);
            if (!graph.TryGetCue(id, out var cue))
                continue;

            groups.Add(cue.GroupId ?? _host.DefaultGroupId);
            if (cue is { AutoContinue: true, FollowOnCueId: not null })
                walk.Push(cue.FollowOnCueId);
        }

        return new ActiveFire(ids, [.. groups.Distinct(StringComparer.Ordinal)], cts);
    }

    /// <summary>See <see cref="ShowSession.FireCuesAsync"/> (the public doc lives there).</summary>
    public async Task<IReadOnlyList<CueExecutionStatus>> FireCuesAsync(IReadOnlyList<string> cueIds)
    {
        if (cueIds.Count == 0)
            return [];
        if (cueIds.Count == 1)
            return [await FireCueAsync(cueIds[0]).ConfigureAwait(false)];

        var state = CurrentGraphState;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(state.Lifetime.Token);
        _activeFires.TryAdd(cts, DescribeFire(state.Graph, cts, cueIds));
        IReadOnlyList<SemaphoreSlim>? locks = null;
        try
        {
            locks = await AcquireGroupLocksAsync(FireLockSet(state.Graph, cueIds), cts.Token)
                .ConfigureAwait(false);
            var fires = new Task<CueExecutionStatus>[cueIds.Count];
            for (var i = 0; i < cueIds.Count; i++)
                fires[i] = FireForGroupAsync(state.Graph, cueIds[i], cts.Token);
            return await Task.WhenAll(fires).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [.. cueIds.Select(static _ => CueExecutionStatus.Failed)];
        }
        finally
        {
            _activeFires.TryRemove(cts, out _);
            if (locks is not null)
                ReleaseGroupLocks(locks);
        }
    }

    /// <summary>Fires several clip-bound cues concurrently, each on the caller-provided runtime transport group.
    /// This is used when authored siblings must remain active together: assigning a distinct runtime group prevents
    /// one sibling's commit from replacing another, while the batch's group-lock set (every authored AND
    /// synthesized group it touches) keeps a same-group GO/fire from interleaving with it - an unrelated
    /// group's GO proceeds independently (F-08).</summary>
    public async Task<IReadOnlyList<CueExecutionStatus>> FireCuesIndependentAsync(
        IReadOnlyList<(string CueId, string RuntimeGroupId)> targets,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return [];

        var state = CurrentGraphState;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, state.Lifetime.Token);
        _activeFires.TryAdd(cts, DescribeFire(
            state.Graph, cts, targets.Select(t => t.CueId), targets.Select(t => t.RuntimeGroupId)));
        IReadOnlyList<SemaphoreSlim>? locks = null;
        try
        {
            locks = await AcquireGroupLocksAsync(
                    FireLockSet(state.Graph, targets.Select(t => t.CueId), targets.Select(t => t.RuntimeGroupId)),
                    cts.Token)
                .ConfigureAwait(false);
            var startBarrier = new CoordinatedFireBarrier(targets.Count);
            // The second edge: voices commit and fully prepare (decode spin-up, buffer wait, sync
            // present) behind the first barrier, then start their clocks together behind this one.
            // Without it the serialized commits staggered sibling starts by each other's slow half -
            // a video sibling's present-sync alone is up to 250 ms, and an all-together group came up
            // hundreds of milliseconds apart in per-run-random order.
            var startEdge = new CoordinatedFireBarrier(targets.Count);
            var fires = new Task<CueExecutionStatus>[targets.Count];
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                fires[i] = FireIndependentForGroupAsync(
                    target.CueId, target.RuntimeGroupId, startBarrier, startEdge, cts.Token);
            }

            // This whole batch is bounded work (open + commit + start; no authored waits inside), so the
            // same wedged-open bound as the scheduled path applies - without it one uncancellable native
            // open held the batch's group locks for the rest of the show.
            try
            {
                return await Task.WhenAll(fires).WaitAsync(BatchPreparationTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException timedOut)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
                try { await Task.WhenAll(fires).WaitAsync(AbandonedBatchDrainTimeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    MediaDiagnostics.LogError(
                        timedOut,
                        "CueRunner: abandoning an all-together batch whose participant is wedged in an " +
                        "uncancellable stage - its resources release if the stage ever returns");
                }
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [.. targets.Select(static _ => CueExecutionStatus.Failed)];
        }
        finally
        {
            _activeFires.TryRemove(cts, out _);
            if (locks is not null)
                ReleaseGroupLocks(locks);
        }
    }

    /// <summary>
    /// Prepares a batch behind the normal arm/commit barriers, releases the ordinary fire lock, and then holds
    /// every committed voice behind one caller-owned absolute start edge.
    /// </summary>
    public async Task<IReadOnlyList<CueExecutionStatus>> FireCuesIndependentScheduledAsync(
        IReadOnlyList<ScheduledCueStart> targets,
        Func<CancellationToken, Task> waitForStartEdge,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            await waitForStartEdge(cancellationToken).ConfigureAwait(false);
            return [];
        }

        var state = CurrentGraphState;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, state.Lifetime.Token);
        IReadOnlyList<SemaphoreSlim>? locks = null;
        var lockHeld = true;
        _pendingScheduledFires.TryAdd(cts, new PendingScheduledFire(
            [.. targets.Select(t => t.CueId)],
            [.. targets.Select(t => t.RuntimeGroupId)]));
        _activeFires.TryAdd(cts, DescribeFire(
            state.Graph, cts, targets.Select(t => t.CueId), targets.Select(t => t.RuntimeGroupId)));

        var releasePrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CueExecutionStatus>[]? fires = null;
        try
        {
            locks = await AcquireGroupLocksAsync(
                    FireLockSet(state.Graph, targets.Select(t => t.CueId), targets.Select(t => t.RuntimeGroupId)),
                    cts.Token)
                .ConfigureAwait(false);
            var startBarrier = new CoordinatedFireBarrier(targets.Count);
            var preparedEdge = new CoordinatedFireBarrier(targets.Count);
            fires = new Task<CueExecutionStatus>[targets.Count];
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                fires[i] = FireIndependentForGroupAsync(
                    target.CueId,
                    target.RuntimeGroupId,
                    startBarrier,
                    preparedEdge,
                    cts.Token,
                    target.StartPosition,
                    releasePrepared.Task);
            }

            // All viable voices have opened, committed, pre-rolled and presented their synchronization frame.
            // Let same-group fires and later timeline events prepare while these voices wait for absolute time.
            // Bounded (see BatchPreparationTimeout): a wedged open must fail THIS batch with a message, not
            // hold its group locks until the show ends. The batch stays targetable through
            // _pendingScheduledFires after this release.
            await preparedEdge.Released
                .WaitAsync(BatchPreparationTimeout, cts.Token).ConfigureAwait(false);
            _activeFires.TryRemove(cts, out _);
            ReleaseGroupLocks(locks);
            lockHeld = false;

            await waitForStartEdge(cts.Token).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            releasePrepared.TrySetResult();
            return await Task.WhenAll(fires).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            releasePrepared.TrySetCanceled(cts.Token);

            // A committed voice owns real layers/routes while it waits. Do not return until every participant
            // has observed cancellation and run the PlayClipAsync rollback path - EXCEPT a wedged participant
            // that will never answer, which gets AbandonedBatchDrainTimeout and is then left behind so the
            // stop/timeout that initiated this teardown can complete (its own finally still runs if the
            // wedged call ever returns).
            if (fires is not null)
            {
                try { await Task.WhenAll(fires).WaitAsync(AbandonedBatchDrainTimeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    MediaDiagnostics.LogError(
                        failure,
                        "CueRunner: abandoning a scheduled batch whose participant is wedged in an " +
                        "uncancellable stage (rollback did not complete within " +
                        $"{AbandonedBatchDrainTimeout.TotalSeconds:0} s) - its resources release if the stage ever returns");
                }
                catch { /* preserve the scheduling/cancellation exception that initiated teardown */ }
            }
            throw;
        }
        finally
        {
            _pendingScheduledFires.TryRemove(cts, out _);
            _activeFires.TryRemove(cts, out _);
            if (lockHeld && locks is not null)
                ReleaseGroupLocks(locks);
        }
    }

    /// <summary>One cue's fire within a <see cref="FireCuesAsync"/> group: maps cancellation to a non-throwing
    /// <see cref="CueExecutionStatus.Failed"/> (so one cancelled cue doesn't fault the whole <c>WhenAll</c>); a
    /// <see cref="CueFaultPolicy.StopShow"/> fault still propagates, matching single-cue fire.</summary>
    private async Task<CueExecutionStatus> FireForGroupAsync(
        CueGraph graph, string cueId, CancellationToken token)
    {
        try { return await FireOnGraphAsync(graph, cueId, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CueExecutionStatus.Failed; }
    }

    private async Task<CueExecutionStatus> FireIndependentForGroupAsync(
        string cueId,
        string runtimeGroupId,
        CoordinatedFireBarrier startBarrier,
        CoordinatedFireBarrier startEdge,
        CancellationToken token,
        TimeSpan? initialPosition = null,
        Task? externalStartRelease = null)
    {
        var reachedBarrier = false;
        var reachedEdge = false;
        try
        {
            return await _host.FireCueIndependentAtBarrierAsync(
                    cueId,
                    runtimeGroupId,
                    async () =>
                    {
                        reachedBarrier = true;
                        await startBarrier.SignalAndWaitAsync(token).ConfigureAwait(false);
                    },
                    async () =>
                    {
                        reachedEdge = true;
                        await startEdge.SignalAndWaitAsync(token).ConfigureAwait(false);
                        if (externalStartRelease is not null)
                            await externalStartRelease.WaitAsync(token).ConfigureAwait(false);
                    },
                    token,
                    initialPosition)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CueExecutionStatus.Failed;
        }
        finally
        {
            // Invalid/unbound/failed-to-open cues never reach the arm barrier, and a commit that threw
            // never reaches the start edge. Count them as arrived at BOTH so one bad sibling cannot
            // strand every successfully prepared clip waiting forever.
            if (!reachedBarrier)
                startBarrier.Signal();
            if (!reachedEdge)
                startEdge.Signal();
        }
    }

    private sealed class CoordinatedFireBarrier(int participantCount)
    {
        private int _remaining = participantCount;
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Released => _released.Task;

        public void Signal()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
                _released.TrySetResult();
        }

        public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            Signal();
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels every queued or in-flight cue fire, WITHOUT marshaling onto the dispatcher - so a stop/load/
    /// dispose can unblock the serial loop that a long pre-wait or open is parking, then run promptly (NXT-03).
    /// A no-op when nothing is firing. Note: a synchronous, uninterruptible native open still runs to completion;
    /// this preempts the (common) pre/post-wait and any cancellable stage.
    /// Deliberately does NOT touch <see cref="_pendingScheduledFires"/>: a prepared timeline event waiting for
    /// its absolute edge is not "an in-flight fire", and a surgical stop must not kill it (see the field doc;
    /// use <see cref="CancelAllFires"/> or the targeted forms for that).</summary>
    public void CancelActiveFire() => CancelActiveFiresWhen(static _ => true);

    /// <summary>Cancels the in-flight fires whose IDENTITY matches - the surgical half of the
    /// targeted stops. A stop of unrelated cue B used to cancel cue A's in-flight pre-wait/open here,
    /// because the active fire was an anonymous token with nothing to match against.</summary>
    private void CancelActiveFiresWhen(Func<ActiveFire, bool> affects)
    {
        foreach (var (cts, fire) in _activeFires)
        {
            if (!affects(fire))
                continue;

            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* the fire already finished and disposed it */ }
        }
    }

    /// <summary>Panic/load/dispose semantics: the active fire AND every prepared batch still waiting for its
    /// absolute start edge - after this, nothing new starts.</summary>
    public void CancelAllFires()
    {
        CancelActiveFire();
        CancelPendingScheduledFires(static _ => true);
    }

    /// <summary>A per-cue stop's reach: the in-flight fire IF it involves the cue (the fired cues plus
    /// their auto-continue closure), plus any pending scheduled batch that CONTAINS the cue. The whole
    /// batch goes down, not just the one member - a scheduled batch shares one cancellation source and
    /// one coordinated edge, and starting the surviving siblings without the stopped member would mean
    /// firing an event the operator just visibly vetoed part of. Batches - and an in-flight fire - not
    /// involving the cue keep going untouched: stopping cue B while unrelated cue A sat in its pre-wait
    /// or open used to cancel A's fire.</summary>
    public void CancelFiresForCue(string cueId)
    {
        CancelActiveFiresWhen(fire => fire.Involves(cueId));
        CancelPendingScheduledFires(pending => pending.CueIds.Contains(cueId, StringComparer.Ordinal));
    }

    /// <summary>A group stop's reach: the in-flight fire IF it would land a voice on the group, plus any
    /// pending scheduled batch that would - "take this group down" includes what is about to come up on
    /// it, and nothing else. Note scheduled batches run on RUNTIME group ids (per-child synthesized for
    /// timeline events), so a session-group stop matches only batches actually targeting that id;
    /// non-matching batches are left alone, and a voice they later start is still stoppable normally.</summary>
    public void CancelFiresForGroup(string runtimeGroupId)
    {
        CancelActiveFiresWhen(fire => fire.InvolvesGroup(runtimeGroupId));
        CancelPendingScheduledFires(pending => pending.RuntimeGroupIds.Contains(runtimeGroupId, StringComparer.Ordinal));
    }

    private void CancelPendingScheduledFires(Func<PendingScheduledFire, bool> affects)
    {
        foreach (var (cts, pending) in _pendingScheduledFires)
        {
            if (!affects(pending))
                continue;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* completed between snapshot and cancellation */ }
        }
    }

    /// <summary>See <see cref="ShowSession.GoAsync"/> (the public doc lives there).</summary>
    public async Task<CueExecutionStatus> GoAsync(string groupId)
    {
        // Hold the group's fire lock across select → fire → advance, so a concurrent GO (e.g. two rapid
        // remote commands) can't read the same cursor and double-fire the same cue. The fire itself still
        // runs off the dispatcher, and a GO on an UNRELATED group proceeds independently (F-08) - it takes
        // different locks.
        //
        // The fired chain can land voices on OTHER groups (auto-continue across lists), and those groups'
        // locks must be held too or their commits would interleave with a concurrent same-group fire. The
        // chain is only knowable after selection, so this grows the lock set and RETRIES: release, acquire
        // the wider set (still in the one global ordinal order - never escalate while holding, that is the
        // deadlock), and re-select, because the cursor may have moved while unlocked. The set only grows
        // and groups are finite, so the loop terminates; the generation check keeps a stale advance a
        // no-op regardless.
        var state = CurrentGraphState;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(state.Lifetime.Token);
        _activeFires.TryAdd(cts, new ActiveFire([], [groupId], cts));
        var lockSet = new HashSet<string>(StringComparer.Ordinal) { groupId };
        try
        {
            while (true)
            {
                var locks = await AcquireGroupLocksAsync(lockSet, cts.Token).ConfigureAwait(false);
                try
                {
                    // Selection on the dispatcher (reads the captured cue graph + the group cursor).
                    var selectStarted = Stopwatch.GetTimestamp();
                    var (next, generation) = await SelectNextGoCueAsync(
                            state.Graph, groupId, _host.DefaultGroupId)
                        .ConfigureAwait(false);
                    _goSelectTiming.RecordSince(selectStarted);
                    cts.Token.ThrowIfCancellationRequested();
                    if (next is null)
                        return CueExecutionStatus.NotReady;

                    // Publish the selected cue before any wider-lock retry. A targeted cue stop can
                    // now cancel this queued GO as well as a GO already executing the cue.
                    _activeFires[cts] = DescribeFire(state.Graph, cts, [next.Id], [groupId]);
                    var needed = FireLockSet(state.Graph, [next.Id]);
                    if (!needed.All(lockSet.Contains))
                    {
                        lockSet.UnionWith(needed);
                        continue; // finally releases; the loop reacquires the wider set and re-selects
                    }

                    // Fire OFF the dispatcher (we already hold every group the chain can land on).
                    var status = await FireOnGraphAsync(state.Graph, next.Id, cts.Token).ConfigureAwait(false);

                    // Advance the cursor on the dispatcher - only when the cue actually ran (or faulted), never a skip/cancel.
                    if (status is CueExecutionStatus.Fired or CueExecutionStatus.Failed)
                        await _host.AdvanceGoCursorAsync(groupId, next.Number, generation).ConfigureAwait(false);
                    _ = _host.WarmUpcomingAsync(groupId); // pre-roll the next cue(s) in the background so the next GO is instant
                    return status;
                }
                finally
                {
                    ReleaseGroupLocks(locks);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return CueExecutionStatus.Failed; // cancelled - do NOT advance
        }
        finally
        {
            _activeFires.TryRemove(cts, out _);
        }
    }
}
