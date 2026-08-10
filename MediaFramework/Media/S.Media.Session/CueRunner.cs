using System.Collections.Concurrent;

namespace S.Media.Session;

/// <summary>
/// The cue layer: fire sequencing and GO. Owns the fire-lock (fires/GOs never interleave - the app drives GO
/// serially) and the in-flight fire's cancellation source, and runs every cue fire OFF the serial dispatcher
/// (NXT-03) so a pre-wait or media open never parks the loop - STOP/LOAD/DISPOSE preempt it through
/// <see cref="CancelActiveFire"/>.
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

    // The cancellation source of the in-flight cue fire (its pre/post-wait + open + auto-continue chain). Set
    // while a fire runs; read off-dispatcher by CancelActiveFire so STOP/LOAD/DISPOSE can abort it (NXT-03).
    private volatile CancellationTokenSource? _activeFireCts;

    // Absolute timeline fires release the ordinary fire lock once every voice is prepared, otherwise a cue
    // waiting several seconds for its authored edge would prevent later timeline events from preparing. They
    // still have to remain STOP-cancellable after that release, so keep their linked sources separately.
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _pendingScheduledFires = new();

    // Off-dispatcher fire model (NXT-03): a cue fire runs OFF the serial dispatcher (its pre-wait + media open
    // no longer park the loop, so STOP/seek/load/queries stay responsive), re-entering only for short state
    // commits. _fireLock serializes fires; the session's show generation lets a fire whose open straddled a
    // reload discard its (now-stale) clip at commit instead of corrupting the newer show.
    private readonly SemaphoreSlim _fireLock = new(1, 1);

    /// <summary>The live cue graph. Volatile: staged off the dispatcher during a load and swapped in by a
    /// single atomic reference assignment, so a fire that straddles the load sees one graph or the other -
    /// never a half-built one.</summary>
    private volatile CueGraph _graph = new();

    // The one in-flight explicit crossfade fire's window: published just before the graph action runs and
    // consumed by it. A field rather than a parameter because the action closure is built at load time,
    // long before any particular fire knows whether it carries a crossfade.
    private Tuple<TimeSpan, FadeShape>? _pendingFireCrossfade;

    public CueRunner(ICueRunnerHost host) => _host = host;

    /// <summary>Every cue in the live graph, in registration order.</summary>
    public IReadOnlyList<CueDefinition> Cues => _graph.Cues;

    /// <summary>An immutable snapshot of the cue execution log.</summary>
    public IReadOnlyList<CueExecutionLogEntry> ExecutionLog => _graph.ExecutionLog;

    /// <summary>Looks a cue up in the live graph.</summary>
    public bool TryGetCue(string cueId, out CueDefinition cue) => _graph.TryGetCue(cueId, out cue);

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
            var binding = resolveClip(cue.Id);
            // A cue without a clip currently has no executable session action. Do not report it as Fired: that
            // produced a successful no-op and made HaPlay briefly mark a stale/unbound media cue as playing.
            // Future control/stop cues need their own action binding rather than relying on an empty clip.
            graph.AddCue(
                cue,
                ct => _host.PlayClipAsync(
                    groupId, binding, ct, waitForStartBarrier: null, crossfade: TakePendingFireCrossfade()),
                binding is null ? static () => false : null);
        }

        return graph;
    }

    /// <summary>Installs a staged graph. One atomic reference assignment - the load's commit point.</summary>
    public void Commit(CueGraph graph) => _graph = graph;

    /// <summary>Consumes the pending crossfade window, if any. Read once per graph action.</summary>
    private (TimeSpan Duration, FadeShape Curve)? TakePendingFireCrossfade() =>
        Interlocked.Exchange(ref _pendingFireCrossfade, null) is { } pending
            ? (pending.Item1, pending.Item2)
            : null;

    /// <summary>Runs a cue's fire against the live graph, publishing its crossfade window first.</summary>
    private async Task<CueExecutionStatus> FireOnGraphAsync(
        string cueId, CancellationToken token, (TimeSpan Duration, FadeShape Curve)? crossfade = null)
    {
        if (crossfade is { } window)
            Interlocked.Exchange(ref _pendingFireCrossfade, Tuple.Create(window.Duration, window.Curve));
        try
        {
            return await _graph.FireAsync(cueId, token).ConfigureAwait(false);
        }
        finally
        {
            // A skipped/failed/cancelled fire may never reach its clip action - the unconsumed window
            // must not leak into a later plain fire.
            if (crossfade is not null)
                Interlocked.Exchange(ref _pendingFireCrossfade, null);
        }
    }

    /// <summary>
    /// GO's cue selection: the next armed and enabled cue on <paramref name="groupId"/> after the cursor.
    /// </summary>
    /// <remarks>Reads the cursor from the engine, then decides here - "next" is a cue-list question, and a
    /// disabled or unarmed cue is skipped rather than fired (NXT-07).</remarks>
    private async Task<(CueDefinition? Next, int Generation)> SelectNextGoCueAsync(string groupId, string defaultGroup)
    {
        var (cursor, generation) = await _host.ReadGoCursorAsync(groupId).ConfigureAwait(false);
        var next = _graph.Cues
            .Where(c => (c.GroupId ?? defaultGroup) == groupId && c.Number > cursor && c.Armed && c.Enabled)
            .OrderBy(c => c.Number)
            .FirstOrDefault();
        return (next, generation);
    }

    /// <summary>
    /// What GO would fire next on <paramref name="groupId"/>, without firing it.
    /// </summary>
    /// <remarks>The standby readout. Deliberately runs the SAME selection GO uses rather than a parallel
    /// "next cue" rule, so what a list says it will do and what it then does cannot disagree.</remarks>
    public async Task<CueDefinition?> PeekNextAsync(string groupId)
    {
        var (next, _) = await SelectNextGoCueAsync(groupId, _host.DefaultGroupId).ConfigureAwait(false);
        return next;
    }

    /// <summary>The cue number that would put <paramref name="cueId"/> next in line, or null if unknown.</summary>
    /// <remarks>One before the cue's own number: GO selects the lowest number strictly greater than the
    /// cursor, so parking the cursor just below a cue makes that cue next.</remarks>
    public int? CursorForStandby(string cueId, string groupId, string defaultGroup) =>
        _graph.TryGetCue(cueId, out var cue)
        && string.Equals(cue.GroupId ?? defaultGroup, groupId, StringComparison.Ordinal)
            ? cue.Number - 1
            : null;

    /// <summary>The next <paramref name="count"/> clip-bound cue ids on a group after <paramref name="cursor"/> -
    /// what the engine pre-rolls so the next GO opens warm.</summary>
    public IReadOnlyList<string> UpcomingCueIds(string groupId, string defaultGroup, int cursor, int count) =>
        [.. _graph.Cues
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
        await _fireLock.WaitAsync().ConfigureAwait(false);
        try { return await FireCoreAsync(cueId, crossfade).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CueExecutionStatus.Failed; } // cancelled by stop/load/dispose
        finally { _fireLock.Release(); }
    }

    /// <summary>The lock-free fire core (the caller MUST hold <see cref="_fireLock"/>): runs the cue graph fire OFF
    /// the serial dispatcher (NXT-03) - its pre/post-wait and media open no longer park the loop; only the short
    /// state commits re-enter it. The fire's cancellation source is published to <see cref="_activeFireCts"/> so
    /// <see cref="CancelActiveFire"/> aborts it; cancellation propagates as
    /// <see cref="OperationCanceledException"/> (callers map it to a non-advancing result).</summary>
    private async Task<CueExecutionStatus> FireCoreAsync(
        string cueId, (TimeSpan Duration, FadeShape Curve)? crossfade = null)
    {
        using var cts = new CancellationTokenSource();
        _activeFireCts = cts;
        try { return await FireOnGraphAsync(cueId, cts.Token, crossfade).ConfigureAwait(false); }
        finally { _activeFireCts = null; }
    }

    /// <summary>See <see cref="ShowSession.FireCuesAsync"/> (the public doc lives there).</summary>
    public async Task<IReadOnlyList<CueExecutionStatus>> FireCuesAsync(IReadOnlyList<string> cueIds)
    {
        if (cueIds.Count == 0)
            return [];
        if (cueIds.Count == 1)
            return [await FireCueAsync(cueIds[0]).ConfigureAwait(false)];

        await _fireLock.WaitAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource();
        _activeFireCts = cts;
        try
        {
            var fires = new Task<CueExecutionStatus>[cueIds.Count];
            for (var i = 0; i < cueIds.Count; i++)
                fires[i] = FireForGroupAsync(cueIds[i], cts.Token);
            return await Task.WhenAll(fires).ConfigureAwait(false);
        }
        finally
        {
            _activeFireCts = null;
            _fireLock.Release();
        }
    }

    /// <summary>Fires several clip-bound cues concurrently, each on the caller-provided runtime transport group.
    /// This is used when authored siblings must remain active together: assigning a distinct runtime group prevents
    /// one sibling's commit from replacing another while the shared fire lock still keeps an unrelated GO/fire from
    /// interleaving with the batch.</summary>
    public async Task<IReadOnlyList<CueExecutionStatus>> FireCuesIndependentAsync(
        IReadOnlyList<(string CueId, string RuntimeGroupId)> targets,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return [];

        await _fireLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeFireCts = cts;
        try
        {
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

            return await Task.WhenAll(fires).ConfigureAwait(false);
        }
        finally
        {
            _activeFireCts = null;
            _fireLock.Release();
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

        await _fireLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lockHeld = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pendingScheduledFires.TryAdd(cts, 0);
        _activeFireCts = cts;

        var releasePrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CueExecutionStatus>[]? fires = null;
        try
        {
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
            // Let unrelated fires and later timeline events prepare while these voices wait for absolute time.
            await preparedEdge.Released.WaitAsync(cts.Token).ConfigureAwait(false);
            _activeFireCts = null;
            _fireLock.Release();
            lockHeld = false;

            await waitForStartEdge(cts.Token).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            releasePrepared.TrySetResult();
            return await Task.WhenAll(fires).ConfigureAwait(false);
        }
        catch
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            releasePrepared.TrySetCanceled(cts.Token);

            // A committed voice owns real layers/routes while it waits. Do not return until every participant
            // has observed cancellation and run the PlayClipAsync rollback path.
            if (fires is not null)
            {
                try { await Task.WhenAll(fires).ConfigureAwait(false); }
                catch { /* preserve the scheduling/cancellation exception that initiated teardown */ }
            }
            throw;
        }
        finally
        {
            _pendingScheduledFires.TryRemove(cts, out _);
            if (lockHeld)
            {
                if (ReferenceEquals(_activeFireCts, cts))
                    _activeFireCts = null;
                _fireLock.Release();
            }
        }
    }

    /// <summary>One cue's fire within a <see cref="FireCuesAsync"/> group: maps cancellation to a non-throwing
    /// <see cref="CueExecutionStatus.Failed"/> (so one cancelled cue doesn't fault the whole <c>WhenAll</c>); a
    /// <see cref="CueFaultPolicy.StopShow"/> fault still propagates, matching single-cue fire.</summary>
    private async Task<CueExecutionStatus> FireForGroupAsync(string cueId, CancellationToken token)
    {
        try { return await FireOnGraphAsync(cueId, token).ConfigureAwait(false); }
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

    /// <summary>Cancels the in-flight cue fire, if any, WITHOUT marshaling onto the dispatcher - so a stop/load/
    /// dispose can unblock the serial loop that a long pre-wait or open is parking, then run promptly (NXT-03).
    /// A no-op when nothing is firing. Note: a synchronous, uninterruptible native open still runs to completion;
    /// this preempts the (common) pre/post-wait and any cancellable stage.</summary>
    public void CancelActiveFire()
    {
        var cts = _activeFireCts;
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* the fire already finished and disposed it */ }
        }

        foreach (var pending in _pendingScheduledFires.Keys)
        {
            try { pending.Cancel(); }
            catch (ObjectDisposedException) { /* completed between snapshot and cancellation */ }
        }
    }

    /// <summary>See <see cref="ShowSession.GoAsync"/> (the public doc lives there).</summary>
    public async Task<CueExecutionStatus> GoAsync(string groupId)
    {
        // Hold the fire-lock across select → fire → advance, so a concurrent GO (e.g. two rapid remote commands)
        // can't read the same cursor and double-fire the same cue. The fire itself still runs off the dispatcher.
        await _fireLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Selection on the dispatcher (reads the cue graph + the group cursor).
            var (next, generation) = await SelectNextGoCueAsync(groupId, _host.DefaultGroupId).ConfigureAwait(false);
            if (next is null)
                return CueExecutionStatus.NotReady;

            // Fire OFF the dispatcher (we already hold the fire-lock - FireCoreAsync is the lock-free core).
            CueExecutionStatus status;
            try { status = await FireCoreAsync(next.Id).ConfigureAwait(false); }
            catch (OperationCanceledException) { return CueExecutionStatus.Failed; } // cancelled - do NOT advance

            // Advance the cursor on the dispatcher - only when the cue actually ran (or faulted), never a skip/cancel.
            if (status is CueExecutionStatus.Fired or CueExecutionStatus.Failed)
                await _host.AdvanceGoCursorAsync(groupId, next.Number, generation).ConfigureAwait(false);
            _ = _host.WarmUpcomingAsync(groupId); // pre-roll the next cue(s) in the background so the next GO is instant
            return status;
        }
        finally
        {
            _fireLock.Release();
        }
    }
}
