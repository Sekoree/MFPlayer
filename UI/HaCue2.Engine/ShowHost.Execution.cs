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
    private readonly Lock _automationRunGate = new();
    private readonly Dictionary<Guid, AutomationRun> _automationRuns = [];
    private readonly Dictionary<Guid, VisualizerAutomationRun> _visualizerAutomationRuns = [];

    private readonly record struct CapturedAutomationTarget(
        Guid CueId,
        ShowCueInstance? SessionInstance,
        VisualizerCueInstance? VisualizerInstance)
    {
        public Guid InstanceId => SessionInstance?.InstanceId
                                  ?? VisualizerInstance?.InstanceId
                                  ?? Guid.Empty;
    }

    private sealed class AutomationRun(
        AutomationCueNode cue,
        AutomationRunClock clock,
        CancellationTokenSource cancellation,
        IReadOnlyDictionary<Guid, IReadOnlyList<CapturedAutomationTarget>> capturedTargets)
    {
        private readonly HashSet<(Guid TrackId, Guid InstanceId)> _claimedTargets = [];

        public AutomationCueNode Cue { get; } = cue;
        public Guid OwnerId { get; } = Guid.NewGuid();
        public AutomationRunClock Clock { get; } = clock;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        /// <summary>The sounding cue IDs each track resolved when this run fired. In particular, a
        /// group controller does not begin affecting a descendant which starts later.</summary>
        public IReadOnlyDictionary<Guid, IReadOnlyList<CapturedAutomationTarget>> CapturedTargets { get; } = capturedTargets;
        public SemaphoreSlim ApplyGate { get; } = new(1, 1);
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public bool Claim(Guid trackId, CapturedAutomationTarget target) =>
            _claimedTargets.Add((trackId, target.InstanceId));

        /// <summary>One internal track resolved ONCE at fire time: its target cue, its prepared curve and
        /// the instances it captured. The drive loop runs 40 times a second, and re-resolving this per
        /// tick meant a full document flatten plus linear scan per track (<c>FindCue</c>) and a re-sort of
        /// every keyframe (<c>AutomationEvaluator.Sample</c>). None of it can change while the run is in
        /// flight - an authored edit recompiles and restarts the show document.</summary>
        public readonly record struct PlannedTrack(
            AutomationTrack Track,
            CueNode Target,
            AutomationCurve? Curve,
            IReadOnlyList<CapturedAutomationTarget> Captured);

        public IReadOnlyList<PlannedTrack> Plan { get; set; } = [];

        // --- reader leases -------------------------------------------------------------------------
        //
        // A seek looks the run up under the runs gate, releases it, and only THEN touches Cancellation /
        // ApplyGate. The drive loop's finally disposed both right after removing the run from that same
        // dictionary, so a run completing inside that window made the seek throw ObjectDisposedException
        // straight out to the operator. Disposal now waits for readers that already hold a lease.

        private readonly object _leaseGate = new();
        private int _leases;
        private bool _finished;
        private bool _disposed;

        /// <summary>Takes a reader lease, or fails when the run has already finished. A caller that gets
        /// true MUST call <see cref="ReleaseLease"/>.</summary>
        public bool TryLease()
        {
            lock (_leaseGate)
            {
                if (_finished)
                    return false;
                _leases++;
                return true;
            }
        }

        public void ReleaseLease()
        {
            lock (_leaseGate)
            {
                if (--_leases > 0 || !_finished)
                    return;
            }

            DisposeResources();
        }

        /// <summary>Marks the run finished and disposes its resources once no reader still holds a lease.
        /// After this no new lease is granted, so a late seek reports "no such run" instead of faulting.</summary>
        public void Finish()
        {
            lock (_leaseGate)
            {
                _finished = true;
                if (_leases > 0)
                    return;
            }

            DisposeResources();
        }

        private void DisposeResources()
        {
            lock (_leaseGate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            Cancellation.Dispose();
            ApplyGate.Dispose();
        }
    }

    /// <summary>A visualizer has no session transport, so its cue-owned lanes retain this show-clock
    /// coordinate for as long as the renderer remains on air. It deliberately survives its last key:
    /// the operator can seek backwards while the visualizer is still active.</summary>
    private sealed class VisualizerAutomationRun(
        VisualizerCueNode cue,
        AutomationRunClock clock,
        CancellationTokenSource cancellation)
    {
        public VisualizerCueNode Cue { get; } = cue;
        public AutomationRunClock Clock { get; } = clock;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public SemaphoreSlim ApplyGate { get; } = new(1, 1);
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Internal tracks with their curves prepared once - see AutomationRun.Plan.</summary>
        public IReadOnlyList<(AutomationTrack Track, AutomationCurve? Curve)> Plan { get; set; } = [];

        // Same reader-lease discipline as AutomationRun, for the same reason: a seek resolves the run
        // outside the runs gate and must not have its CTS/gate disposed underneath it.
        private readonly object _leaseGate = new();
        private int _leases;
        private bool _finished;
        private bool _disposed;

        public bool TryLease()
        {
            lock (_leaseGate)
            {
                if (_finished)
                    return false;
                _leases++;
                return true;
            }
        }

        public void ReleaseLease()
        {
            lock (_leaseGate)
            {
                if (--_leases > 0 || !_finished)
                    return;
            }

            DisposeResources();
        }

        public void Finish()
        {
            lock (_leaseGate)
            {
                _finished = true;
                if (_leases > 0)
                    return;
            }

            DisposeResources();
        }

        private void DisposeResources()
        {
            lock (_leaseGate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            Cancellation.Dispose();
            ApplyGate.Dispose();
        }
    }
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
        await StopAutomationRunAsync(cueId).ConfigureAwait(false);
        await StopVisualizerAutomationRunAsync(cueId).ConfigureAwait(false);
        await _visualizers.StopAsync(cueId).ConfigureAwait(false);
        await _session.StopCueAsync(cueId.ToString()).ConfigureAwait(false);
        Executor.OnStopped(cueId);
    }

    Task<string?> ICueExecutionHost.SendActionAsync(ActionCueNode action, ActionEndpoint? endpoint) =>
        _actions.SendAsync(action, endpoint);

    async Task<bool> ICueExecutionHost.RunAutomationAsync(
        AutomationCueNode automation,
        CueList? list,
        TimeSpan initialPosition,
        CancellationToken cancellationToken)
    {
        var snapshot = automation with
        {
            AutomationTracks = automation.AutomationTracks.Select(CloneAutomationTrack).ToList(),
        };
        var duration = TimeSpan.FromMilliseconds(Math.Max(1, snapshot.DurationMs));
        var start = initialPosition < TimeSpan.Zero
            ? TimeSpan.Zero
            : initialPosition > duration ? duration : initialPosition;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _life.Token, cancellationToken);
        var clock = new AutomationRunClock((ICueExecutionHost)this, start);
        var run = new AutomationRun(
            snapshot, clock, cancellation, await CaptureAutomationTargetsAsync(snapshot).ConfigureAwait(false));
        run.Plan = PlanInternalTracks(run);
        AutomationRun? previous;
        lock (_automationRunGate)
        {
            _automationRuns.TryGetValue(automation.Id, out previous);
            _automationRuns[automation.Id] = run;
        }

        // Refire is a handover, not two competing writers. Let an explicit RestoreBase from the old
        // run land before the new run samples its authored start.
        if (previous is not null)
        {
            previous.Cancel();
            await previous.Complete.Task.ConfigureAwait(false);
        }
        Remember(snapshot.Id, list?.Id ?? Guid.Empty, groupId: "");
        // Keep the outbound drivers alive for the whole controller run. A track may place its final
        // key long before the cue ends; seeking back across that key must still reposition the target.
        await _outbound.StartAsync(snapshot, duration, clock.Read, keepAliveAfterEnd: true).ConfigureAwait(false);
        _ = DriveAutomationAsync(run, duration);
        return await run.Started.Task.ConfigureAwait(false);
    }

    private async Task DriveAutomationAsync(AutomationRun run, TimeSpan duration)
    {
        var completedNaturally = false;
        try
        {
            run.Cancellation.Token.ThrowIfCancellationRequested();
            while (true)
            {
                var elapsed = run.Clock.Position;
                await SampleAutomationAsync(run, elapsed).ConfigureAwait(false);
                run.Started.TrySetResult(true);
                if (elapsed >= duration)
                    break;
                await ((ICueExecutionHost)this).DelayTimelineAsync(
                    TimeSpan.FromMilliseconds(25), run.Cancellation.Token).ConfigureAwait(false);
            }

            if (run.Cue.Completion == AutomationCompletion.RestoreBase)
                await RestoreAutomationAsync(run).ConfigureAwait(false);
            else
                await RelinquishAutomationAsync(run).ConfigureAwait(false);
            _outbound.Complete(run.Cue.Id);
            completedNaturally = true;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            run.Started.TrySetResult(false);
            if (run.Cue.Completion == AutomationCompletion.RestoreBase)
                await RestoreAutomationAsync(run).ConfigureAwait(false);
            else
                await RelinquishAutomationAsync(run).ConfigureAwait(false);
        }
        finally
        {
            lock (_automationRunGate)
                if (ReferenceEquals(_automationRuns.GetValueOrDefault(run.Cue.Id), run))
                    _automationRuns.Remove(run.Cue.Id);
            // Disposes only once no in-flight seek still holds a lease on this run.
            run.Finish();
            run.Started.TrySetResult(false);
            Forget(run.Cue.Id.ToString());
            run.Complete.TrySetResult();
        }

        if (completedNaturally)
            _ = ObserveLifecycleAsync(
                Executor.OnNaturalEndAsync(run.Cue.Id),
                "automation natural-end follow");
    }

    /// <summary>Resolves every internal track's target and curve ONCE, at fire time.</summary>
    private IReadOnlyList<AutomationRun.PlannedTrack> PlanInternalTracks(AutomationRun run)
    {
        var plan = new List<AutomationRun.PlannedTrack>();
        foreach (var track in run.Cue.AutomationTracks.Where(track => track.Enabled))
        {
            if (AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External)
                continue; // OutboundEffects owns rate limiting and coalescing on this same clock.
            if (!run.CapturedTargets.TryGetValue(track.Id, out var capturedTargets)
                || track.Target.CueId is not { } targetId
                || _project.FindCue(targetId) is not { } target)
                continue;
            plan.Add(new AutomationRun.PlannedTrack(
                track, target, AutomationCurve.Prepare(track, _project), capturedTargets));
        }

        return plan;
    }

    private async Task SampleAutomationAsync(AutomationRun run, TimeSpan position)
    {
        await run.ApplyGate.WaitAsync(run.Cancellation.Token).ConfigureAwait(false);
        try
        {
            var timeMs = Math.Clamp(
                (long)position.TotalMilliseconds, 0, Math.Max(1, run.Cue.DurationMs));
            foreach (var (track, target, curve, capturedTargets) in run.Plan)
            {
                var authored = AuthoredValue(target, track);
                var value = curve?.Sample(timeMs, authored) ?? authored;
                await ApplyAutomationValueAsync(run, target, track, value, capturedTargets).ConfigureAwait(false);
            }
        }
        finally
        {
            run.ApplyGate.Release();
        }
    }

    private async Task RestoreAutomationAsync(AutomationRun run)
    {
        await run.ApplyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var track in run.Cue.AutomationTracks.Where(track => track.Enabled))
                if (run.CapturedTargets.TryGetValue(track.Id, out var capturedTargets)
                    && track.Target.CueId is { } targetId
                    && _project.FindCue(targetId) is { } target)
                    await RestoreAutomationValueAsync(run, target, track, capturedTargets).ConfigureAwait(false);
        }
        finally
        {
            run.ApplyGate.Release();
        }
    }

    /// <summary>The <c>HoldFinal</c> counterpart of <see cref="RestoreAutomationAsync"/>: the values this
    /// run drove stay exactly where they are, but its CLAIMS are given up. A finished run that keeps its
    /// claims owns those slots forever - nothing else can ever write them again, so the target cue's own
    /// automation and the operator's own edits are both permanently shadowed by an owner that no longer
    /// exists. Where giving up a claim reveals an older run that is still going, that run's value returns.
    /// </summary>
    private async Task RelinquishAutomationAsync(AutomationRun run)
    {
        await run.ApplyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Distinct: several tracks of one run commonly target the same cue instance, and relinquishing
            // is per-owner across every slot, so once per instance is enough.
            foreach (var instance in run.CapturedTargets.Values
                         .SelectMany(targets => targets)
                         .Select(target => target.SessionInstance)
                         .OfType<ShowCueInstance>()
                         .Distinct())
                await _session.RelinquishControllerAsync(instance, run.OwnerId).ConfigureAwait(false);

            foreach (var visualizer in run.CapturedTargets.Values
                         .SelectMany(targets => targets)
                         .Select(target => target.VisualizerInstance)
                         .OfType<VisualizerCueInstance>()
                         .Distinct())
                _visualizers.RelinquishController(visualizer, run.OwnerId);
        }
        finally
        {
            run.ApplyGate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CapturedAutomationTarget>>> CaptureAutomationTargetsAsync(
        AutomationCueNode automation)
    {
        var sounding = SoundingIds().ToHashSet();
        var captured = new Dictionary<Guid, IReadOnlyList<CapturedAutomationTarget>>();
        foreach (var track in automation.AutomationTracks.Where(track => track.Enabled))
        {
            if (AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External)
                continue;
            if (track.Target.CueId is not { } targetId || _project.FindCue(targetId) is not { } target)
            {
                Report($"“{automation.Label}” has an automation track with no target cue");
                continue;
            }

            IEnumerable<CueNode> candidates = (track.Target.PropertyId, target) switch
            {
                (AutomationPropertyIds.GroupAudioTrim, GroupCueNode group) =>
                    Descendants(group).OfType<MediaCueNode>(),
                (AutomationPropertyIds.GroupVideoOpacity, GroupCueNode group) =>
                    Descendants(group).Where(child => CuePlacements.Of(child).Any()),
                _ => [target],
            };
            var activeCandidates = candidates
                .Where(candidate => sounding.Contains(candidate.Id))
                .DistinctBy(candidate => candidate.Id)
                .ToArray();
            var active = new List<CapturedAutomationTarget>(activeCandidates.Length);
            foreach (var candidate in activeCandidates)
            {
                if (candidate is VisualizerCueNode
                    && _visualizers.CaptureInstance(candidate.Id) is { } visualizerInstance)
                {
                    active.Add(new CapturedAutomationTarget(candidate.Id, null, visualizerInstance));
                    continue;
                }

                if (await _session.CaptureActiveCueInstanceAsync(candidate.Id.ToString()).ConfigureAwait(false)
                    is { } sessionInstance)
                    active.Add(new CapturedAutomationTarget(candidate.Id, sessionInstance, null));
            }
            if (active.Count == 0)
            {
                Report($"“{automation.Label}” skipped “{target.Label}” because it is not sounding");
                continue;
            }
            captured[track.Id] = active;
        }
        return captured;
    }

    private async Task<bool> StopAutomationRunAsync(Guid cueId)
    {
        AutomationRun? run;
        lock (_automationRunGate)
            _automationRuns.TryGetValue(cueId, out run);
        if (run is null)
            return false;
        run.Cancel();
        await run.Complete.Task.ConfigureAwait(false);
        return true;
    }

    private async Task StopAllAutomationRunsAsync()
    {
        AutomationRun[] runs;
        lock (_automationRunGate)
            runs = [.. _automationRuns.Values];
        foreach (var run in runs)
            run.Cancel();
        await Task.WhenAll(runs.Select(run => run.Complete.Task)).ConfigureAwait(false);
    }

    /// <returns>The run's clock, so the SAME clock can drive this cue's outbound ramps. A visualizer used
    /// to get two independent <see cref="AutomationRunClock"/>s - one for its surface lanes, one for its
    /// OSC/MIDI lanes - and seeking bumped only the first: the outbound driver then saw a generation it did
    /// not recognise and repositioned itself back to its own un-sought position, silently undoing the seek
    /// within 10 ms. Null when the cue has no enabled tracks (there is nothing to drive).</returns>
    private async Task<AutomationRunClock?> StartVisualizerAutomationAsync(
        VisualizerCueNode cue, TimeSpan initialPosition)
    {
        if (!cue.AutomationTracks.Any(track => track.Enabled))
            return null;

        var duration = TimeSpan.FromMilliseconds(Math.Max(1, cue.HoldMs));
        var start = initialPosition < TimeSpan.Zero
            ? TimeSpan.Zero
            : initialPosition > duration ? duration : initialPosition;
        var run = new VisualizerAutomationRun(
            cue,
            new AutomationRunClock((ICueExecutionHost)this, start),
            CancellationTokenSource.CreateLinkedTokenSource(_life.Token))
        {
            Plan =
            [
                .. cue.AutomationTracks
                    .Where(track => track.Enabled
                        && AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain
                            != AutomationDomain.External)
                    .Select(track => (track, AutomationCurve.Prepare(track, _project))),
            ],
        };
        VisualizerAutomationRun? previous;
        lock (_automationRunGate)
        {
            _visualizerAutomationRuns.TryGetValue(cue.Id, out previous);
            _visualizerAutomationRuns[cue.Id] = run;
        }

        if (previous is not null)
        {
            previous.Cancel();
            await previous.Complete.Task.ConfigureAwait(false);
        }

        _ = DriveVisualizerAutomationAsync(run, duration);
        await run.Started.Task.ConfigureAwait(false);
        return run.Clock;
    }

    private async Task DriveVisualizerAutomationAsync(
        VisualizerAutomationRun run, TimeSpan duration)
    {
        var landedAtEnd = false;
        var lastGeneration = -1L;
        try
        {
            while (true)
            {
                run.Cancellation.Token.ThrowIfCancellationRequested();
                var reading = run.Clock.Read();
                if (!landedAtEnd || reading.Generation != lastGeneration)
                {
                    var position = reading.Position > duration ? duration : reading.Position;
                    await SampleVisualizerAutomationAsync(run, position).ConfigureAwait(false);
                    run.Started.TrySetResult(true);
                    landedAtEnd = reading.Position >= duration;
                    lastGeneration = reading.Generation;
                }

                await ((ICueExecutionHost)this).DelayTimelineAsync(
                        landedAtEnd ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(25),
                        run.Cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            run.Started.TrySetResult(false);
        }
        finally
        {
            lock (_automationRunGate)
                if (ReferenceEquals(_visualizerAutomationRuns.GetValueOrDefault(run.Cue.Id), run))
                    _visualizerAutomationRuns.Remove(run.Cue.Id);
            run.Finish();
            run.Started.TrySetResult(false);
            run.Complete.TrySetResult();
        }
    }

    private async Task SampleVisualizerAutomationAsync(
        VisualizerAutomationRun run, TimeSpan position)
    {
        await run.ApplyGate.WaitAsync(run.Cancellation.Token).ConfigureAwait(false);
        try
        {
            var timeMs = Math.Clamp(
                (long)position.TotalMilliseconds, 0, Math.Max(1, run.Cue.HoldMs));
            foreach (var (track, curve) in run.Plan)
            {
                var authored = AuthoredValue(run.Cue, track);
                var value = curve?.Sample(timeMs, authored) ?? authored;
                await _visualizers.ApplyAutomationAsync(run.Cue, track, value).ConfigureAwait(false);
            }
        }
        finally
        {
            run.ApplyGate.Release();
        }
    }

    private async Task<bool> StopVisualizerAutomationRunAsync(Guid cueId)
    {
        VisualizerAutomationRun? run;
        lock (_automationRunGate)
            _visualizerAutomationRuns.TryGetValue(cueId, out run);
        if (run is null)
            return false;
        run.Cancel();
        await run.Complete.Task.ConfigureAwait(false);
        return true;
    }

    private async Task StopAllVisualizerAutomationRunsAsync()
    {
        VisualizerAutomationRun[] runs;
        lock (_automationRunGate)
            runs = [.. _visualizerAutomationRuns.Values];
        foreach (var run in runs)
            run.Cancel();
        await Task.WhenAll(runs.Select(run => run.Complete.Task)).ConfigureAwait(false);
    }

    private async Task<bool> SeekVisualizerAutomationRunAsync(Guid cueId, TimeSpan position)
    {
        VisualizerAutomationRun? run;
        lock (_automationRunGate)
            _visualizerAutomationRuns.TryGetValue(cueId, out run);
        if (run is null || !run.TryLease())
            return false;

        try
        {
            if (!await run.Started.Task.ConfigureAwait(false))
                return false;

            var duration = TimeSpan.FromMilliseconds(Math.Max(1, run.Cue.HoldMs));
            var sought = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
            var reading = run.Clock.Seek(sought);
            _outbound.Seek(cueId, reading);
            await SampleVisualizerAutomationAsync(run, reading.Position).ConfigureAwait(false);
            return true;
        }
        finally
        {
            run.ReleaseLease();
        }
    }

    private async Task<bool> SeekAutomationRunAsync(Guid cueId, TimeSpan position)
    {
        AutomationRun? run;
        lock (_automationRunGate)
            _automationRuns.TryGetValue(cueId, out run);
        // The lease is what makes the rest of this method safe: without it the run could complete and
        // dispose its CTS/gate between the lookup above and the first touch below.
        if (run is null || !run.TryLease())
            return false;

        try
        {
            if (!await run.Started.Task.ConfigureAwait(false)
                || run.Cancellation.IsCancellationRequested)
                return false;
            var duration = TimeSpan.FromMilliseconds(Math.Max(1, run.Cue.DurationMs));
            var sought = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
            var reading = run.Clock.Seek(sought);
            _outbound.Seek(cueId, reading);
            await SampleAutomationAsync(run, reading.Position).ConfigureAwait(false);
            return true;
        }
        finally
        {
            run.ReleaseLease();
        }
    }

    private IReadOnlyDictionary<Guid, (TimeSpan Position, TimeSpan Duration)> AutomationRunSnapshots()
    {
        AutomationRun[] runs;
        VisualizerAutomationRun[] visualizers;
        lock (_automationRunGate)
        {
            runs = [.. _automationRuns.Values];
            visualizers = [.. _visualizerAutomationRuns.Values];
        }
        var snapshots = runs.ToDictionary(
            run => run.Cue.Id,
            run => (
                run.Clock.Position,
                TimeSpan.FromMilliseconds(Math.Max(1, run.Cue.DurationMs))));
        foreach (var run in visualizers)
            snapshots[run.Cue.Id] = (
                run.Clock.Position,
                TimeSpan.FromMilliseconds(Math.Max(1, run.Cue.HoldMs)));
        return snapshots;
    }

    private async Task ApplyAutomationValueAsync(
        AutomationRun run,
        CueNode target,
        AutomationTrack track,
        double value,
        IReadOnlyList<CapturedAutomationTarget> capturedTargets)
    {
        if (target is VisualizerCueNode visualizer
            && capturedTargets.FirstOrDefault(item => item.VisualizerInstance.HasValue)
                is { VisualizerInstance: { } visualizerInstance } visualizerTarget)
        {
            await _visualizers.ApplyControllerAutomationAsync(
                    visualizer,
                    track,
                    value,
                    visualizerInstance,
                    run.OwnerId,
                    run.Claim(track.Id, visualizerTarget))
                .ConfigureAwait(false);
            return;
        }

        if (TryPlacementProperty(track.Target.PropertyId, out var transform)
            && track.Target.ObjectId is { } transformPlacementId
            && CuePlacements.Of(target).FirstOrDefault(placement => placement.Id == transformPlacementId)
                is { } transformPlacement)
        {
            foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                await _session.ApplyControllerPlacementTransformAsync(
                    captured.SessionInstance!.Value,
                    run.OwnerId,
                    transformPlacement.CompositionId.ToString(),
                    transformPlacement.LayerIndex,
                    transform,
                    value,
                    run.Claim(track.Id, captured)).ConfigureAwait(false);
            return;
        }
        if (TryAudioEffectParameter(track.Target.PropertyId, out var audioParameter)
            && target is MediaCueNode audioTarget
            && track.Target.ObjectId is { } audioEffectId
            && audioTarget.AudioEffects.Any(effect => effect.Id == audioEffectId))
        {
            foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                await _session.ApplyControllerAudioEffectAsync(
                    captured.SessionInstance!.Value,
                    run.OwnerId,
                    audioEffectId.ToString(),
                    audioParameter,
                    value,
                    run.Claim(track.Id, captured)).ConfigureAwait(false);
            return;
        }
        if (TryEffectParameter(track.Target.PropertyId, out var effectParameter)
            && track.Target.ObjectId is { } effectId
            && CuePlacements.Of(target).FirstOrDefault(placement =>
                LayerEffectRack.Effective(placement).Any(effect => effect.Id == effectId)) is { } effectPlacement)
        {
            foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                await _session.ApplyControllerPlacementEffectAsync(
                    captured.SessionInstance!.Value,
                    run.OwnerId,
                    effectPlacement.CompositionId.ToString(),
                    effectPlacement.LayerIndex,
                    effectId.ToString(),
                    effectParameter,
                    value,
                    run.Claim(track.Id, captured)).ConfigureAwait(false);
            return;
        }

        switch (track.Target.PropertyId)
        {
            case AutomationPropertyIds.CueVolume when target is MediaCueNode media:
                foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                    await _session.ApplyControllerVolumeAsync(
                        captured.SessionInstance!.Value,
                        run.OwnerId,
                        Linear(value),
                        run.Claim(track.Id, captured)).ConfigureAwait(false);
                break;
            case AutomationPropertyIds.PlacementOpacity
                when track.Target.ObjectId is { } placementId
                     && CuePlacements.Of(target).FirstOrDefault(placement => placement.Id == placementId) is { } placement:
                foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                    await _session.ApplyControllerPlacementOpacityAsync(
                        captured.SessionInstance!.Value,
                        run.OwnerId,
                        placement.CompositionId.ToString(),
                        placement.LayerIndex,
                        (float)Math.Clamp(value, 0, 1),
                        run.Claim(track.Id, captured)).ConfigureAwait(false);
                break;
            case AutomationPropertyIds.GroupAudioTrim when target is GroupCueNode group:
                foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                    await _session.ApplyControllerAudioModifierAsync(
                        captured.SessionInstance!.Value,
                        run.OwnerId,
                        group.Id,
                        Linear(value),
                        run.Claim(track.Id, captured)).ConfigureAwait(false);
                break;
            case AutomationPropertyIds.GroupVideoOpacity when target is GroupCueNode videoGroup:
                foreach (var captured in capturedTargets)
                {
                    var claim = run.Claim(track.Id, captured);
                    if (captured.SessionInstance is { } sessionInstance)
                        await _session.ApplyControllerVideoModifierAsync(
                            sessionInstance,
                            run.OwnerId,
                            videoGroup.Id,
                            (float)Math.Clamp(value, 0, 1),
                            claim).ConfigureAwait(false);
                    else if (captured.VisualizerInstance is { } groupVisualizerInstance)
                        await _visualizers.ApplyControllerVideoModifierAsync(
                            groupVisualizerInstance,
                            run.OwnerId,
                            (float)Math.Clamp(value, 0, 1),
                            claim).ConfigureAwait(false);
                }
                break;
        }
    }

    private async Task RestoreAutomationValueAsync(
        AutomationRun run,
        CueNode target,
        AutomationTrack track,
        IReadOnlyList<CapturedAutomationTarget> capturedTargets)
    {
        if (target is VisualizerCueNode visualizer
            && capturedTargets.FirstOrDefault(item => item.VisualizerInstance.HasValue)
                is { VisualizerInstance: { } visualizerInstance })
        {
            await _visualizers.ClearControllerAutomationAsync(
                    visualizer, track, visualizerInstance, run.OwnerId)
                .ConfigureAwait(false);
            return;
        }

        if (TryPlacementProperty(track.Target.PropertyId, out var transform)
            && track.Target.ObjectId is { } placementId
            && CuePlacements.Of(target).FirstOrDefault(placement => placement.Id == placementId) is { } placement)
        {
            foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                await _session.ClearControllerPlacementTransformAsync(
                    captured.SessionInstance!.Value,
                    run.OwnerId,
                    placement.CompositionId.ToString(),
                    placement.LayerIndex,
                    transform).ConfigureAwait(false);
            return;
        }
        if (TryAudioEffectParameter(track.Target.PropertyId, out var audioParameter)
            && target is MediaCueNode audioTarget
            && track.Target.ObjectId is { } audioEffectId
            && audioTarget.AudioEffects.Any(effect => effect.Id == audioEffectId))
        {
            foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                await _session.ClearControllerAudioEffectAsync(
                    captured.SessionInstance!.Value,
                    run.OwnerId,
                    audioEffectId.ToString(),
                    audioParameter).ConfigureAwait(false);
            return;
        }
        if (TryEffectParameter(track.Target.PropertyId, out var effectParameter)
            && track.Target.ObjectId is { } effectId
            && CuePlacements.Of(target).FirstOrDefault(placement =>
                LayerEffectRack.Effective(placement).Any(effect => effect.Id == effectId)) is { } effectPlacement)
        {
            foreach (var captured in capturedTargets.Where(item => item.SessionInstance.HasValue))
                await _session.ClearControllerPlacementEffectAsync(
                    captured.SessionInstance!.Value,
                    run.OwnerId,
                    effectPlacement.CompositionId.ToString(),
                    effectPlacement.LayerIndex,
                    effectId.ToString(),
                    effectParameter).ConfigureAwait(false);
            return;
        }

        foreach (var captured in capturedTargets)
        {
            if (captured.SessionInstance is { } sessionInstance)
            {
                switch (track.Target.PropertyId)
                {
                    case AutomationPropertyIds.CueVolume:
                        await _session.ClearControllerVolumeAsync(sessionInstance, run.OwnerId).ConfigureAwait(false);
                        break;
                    case AutomationPropertyIds.PlacementOpacity
                        when track.Target.ObjectId is { } opacityPlacementId
                             && CuePlacements.Of(target).FirstOrDefault(item => item.Id == opacityPlacementId)
                                 is { } opacityPlacement:
                        await _session.ClearControllerPlacementOpacityAsync(
                            sessionInstance,
                            run.OwnerId,
                            opacityPlacement.CompositionId.ToString(),
                            opacityPlacement.LayerIndex).ConfigureAwait(false);
                        break;
                    // The trim is keyed by the GROUP that set it, so releasing clears only this group's
                    // contribution and leaves an outer group's trim on the same voice intact.
                    case AutomationPropertyIds.GroupAudioTrim:
                        await _session.ClearControllerAudioModifierAsync(
                            sessionInstance, run.OwnerId, target.Id).ConfigureAwait(false);
                        break;
                    case AutomationPropertyIds.GroupVideoOpacity:
                        await _session.ClearControllerVideoModifierAsync(
                            sessionInstance, run.OwnerId, target.Id).ConfigureAwait(false);
                        break;
                }
            }
            else if (track.Target.PropertyId == AutomationPropertyIds.GroupVideoOpacity
                     && captured.VisualizerInstance is { } groupVisualizerInstance)
            {
                await _visualizers.ClearControllerVideoModifierAsync(groupVisualizerInstance, run.OwnerId)
                    .ConfigureAwait(false);
            }
        }
    }

    private static double AuthoredValue(CueNode target, AutomationTrack track)
    {
        if (target is MediaCueNode audioTarget
            && track.Target.ObjectId is { } audioEffectId
            && audioTarget.AudioEffects.FirstOrDefault(effect => effect.Id == audioEffectId) is { } audioEffect
            && AudioEffectCatalog.TryResolveProperty(
                track.Target.PropertyId, out var audioType, out var audioParameter)
            && audioEffect.EffectTypeId == audioType.TypeId)
            return audioEffect.Read(audioParameter.Id, audioParameter.Default);
        if (track.Target.ObjectId is { } placementId
            && CuePlacements.Of(target).FirstOrDefault(placement => placement.Id == placementId) is { } placement)
            return track.Target.PropertyId switch
            {
                AutomationPropertyIds.PlacementOpacity => placement.Opacity,
                AutomationPropertyIds.PlacementX => placement.X,
                AutomationPropertyIds.PlacementY => placement.Y,
                AutomationPropertyIds.PlacementWidth => placement.Width,
                AutomationPropertyIds.PlacementHeight => placement.Height,
                AutomationPropertyIds.PlacementRotation => placement.RotationDegrees,
                _ => AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Value.Default ?? 0,
            };
        if (track.Target.ObjectId is { } effectId
            && CuePlacements.Of(target)
                .SelectMany(LayerEffectRack.Effective)
                .FirstOrDefault(effect => effect.Id == effectId) is { } effect
            && LayerEffectCatalog.TryResolveProperty(
                track.Target.PropertyId, out var effectType, out var parameter)
            && string.Equals(effect.EffectTypeId, effectType.TypeId, StringComparison.Ordinal))
            return effect.Read(parameter.Id, parameter.Default);
        return track.Target.PropertyId switch
        {
            AutomationPropertyIds.CueVolume when target is MediaCueNode media => media.LevelDb,
            AutomationPropertyIds.GroupAudioTrim => 0,
            AutomationPropertyIds.GroupVideoOpacity => 1,
            _ => AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Value.Default ?? 0,
        };
    }

    private static bool TryPlacementProperty(string propertyId, out ShowPlacementProperty property)
    {
        property = propertyId switch
        {
            AutomationPropertyIds.PlacementX => ShowPlacementProperty.DestX,
            AutomationPropertyIds.PlacementY => ShowPlacementProperty.DestY,
            AutomationPropertyIds.PlacementWidth => ShowPlacementProperty.DestWidth,
            AutomationPropertyIds.PlacementHeight => ShowPlacementProperty.DestHeight,
            AutomationPropertyIds.PlacementRotation => ShowPlacementProperty.RotationDegrees,
            _ => (ShowPlacementProperty)(-1),
        };
        return (int)property >= 0;
    }

    private static bool TryEffectParameter(string propertyId, out string parameterId)
    {
        if (LayerEffectCatalog.TryResolveProperty(propertyId, out _, out var parameter))
        {
            parameterId = parameter.Id;
            return true;
        }
        parameterId = "";
        return false;
    }

    private static bool TryAudioEffectParameter(string propertyId, out string parameterId)
    {
        if (AudioEffectCatalog.TryResolveProperty(propertyId, out _, out var parameter))
        {
            parameterId = parameter.Id;
            return true;
        }
        parameterId = "";
        return false;
    }

    private static AutomationTrack CloneAutomationTrack(AutomationTrack track) => track with
    {
        Target = track.Target with { },
        Keyframes = track.Keyframes.Select(key => key with
        {
            Curve = key.Curve with
            {
                Points = key.Curve.Points is null ? null : [.. key.Curve.Points],
            },
        }).ToList(),
    };

    private static IEnumerable<CueNode> Descendants(GroupCueNode group)
    {
        foreach (var child in group.Children)
        {
            yield return child;
            if (child is GroupCueNode nested)
                foreach (var descendant in Descendants(nested))
                    yield return descendant;
        }
    }

    private static float Linear(double decibels) => decibels <= GainRange.SilenceFloorDb
        ? 0f
        : (float)Math.Pow(10, Math.Clamp(decibels, GainRange.SilenceFloorDb, 12) / 20);

    /// <summary>The inverse of <see cref="Linear"/>, for reporting a runtime gain back in authoring units.</summary>
    internal static double LinearToDb(float linear) => linear <= 0f
        ? GainRange.SilenceFloorDb
        : Math.Clamp(20 * Math.Log10(linear), GainRange.SilenceFloorDb, 12);

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
            await StopVisualizerAutomationRunAsync(cue.Id).ConfigureAwait(false);
            var problem = await _visualizers.FireAsync(_project, visualizer).ConfigureAwait(false);

            if (problem is not null)
                Report(problem);

            // A partial start is still a start: the canvases that came up are showing something, and
            // the ones that did not have just been reported by name.
            if (!_visualizers.Running.Contains(cue.Id))
                return false;

            // A visualizer holds a renderer rather than a transport, so it has no group to seek.
            Remember(cue.Id, list?.Id ?? Guid.Empty, groupId: "");
            var visualizerClock = await StartVisualizerAutomationAsync(visualizer, TimeSpan.Zero)
                .ConfigureAwait(false);
            await StartClockedOutboundAsync(
                cue,
                TimeSpan.FromMilliseconds(Math.Max(1, visualizer.HoldMs)),
                groupId: "",
                sharedClock: visualizerClock).ConfigureAwait(false);
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
        await StartClockedOutboundAsync(cue, PlayedLength(cue), group).ConfigureAwait(false);
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
            // The session timeline already began at StartPosition. Sampling that authoritative cue
            // coordinate keeps outbound values aligned with volume/opacity during rehearsal.
            await StartClockedOutboundAsync(cue, PlayedLength(cue), group).ConfigureAwait(false);
            started.Add(cue.Id);
        }

        return started;
    }

    async Task<IReadOnlyList<Guid>> ICueExecutionHost.PlayTimelineVisualizersAsync(
        IReadOnlyList<TimelineVisualizerStart> cues,
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

        foreach (var start in cues.Where(start => started.Contains(start.Cue.Id)))
        {
            var cue = start.Cue;
            Remember(cue.Id, list?.Id ?? Guid.Empty, groupId: "");
            var visualizerClock = await StartVisualizerAutomationAsync(cue, start.StartPosition)
                .ConfigureAwait(false);
            await StartClockedOutboundAsync(
                cue,
                TimeSpan.FromMilliseconds(Math.Max(1, cue.HoldMs)),
                groupId: "",
                initialPosition: start.StartPosition,
                sharedClock: visualizerClock).ConfigureAwait(false);
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

    /// <param name="sharedClock">The clock this cue's other automation already runs on, when it has one.
    /// Passing it is what keeps a cue on ONE timebase: a second clock for the same cue means a seek bumps
    /// only one of them, and the driver that missed it repositions back to its own stale position.</param>
    private async Task StartClockedOutboundAsync(
        CueNode cue,
        TimeSpan? duration,
        string groupId,
        TimeSpan initialPosition = default,
        AutomationRunClock? sharedClock = null)
    {
        var outboundKeys = CueAutomation.Of(cue)
            .Where(track => track.Enabled
                && AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External)
            .SelectMany(track => track.Keyframes)
            .Where(key => key.TimeMs >= 0)
            .ToList();
        if (outboundKeys.Count == 0)
            return;

        var runDuration = duration is { Ticks: > 0 }
            ? duration.Value
            : TimeSpan.FromMilliseconds(Math.Max(1, outboundKeys.Max(key => key.TimeMs)));
        if (groupId.Length > 0)
        {
            await _outbound.StartAsync(
                cue,
                runDuration,
                () => TransportAutomationClock(groupId),
                // The media transport, not the track's last key, owns this lifetime. Keeping the
                // driver registered lets a later backward seek reopen an already-completed ramp.
                keepAliveAfterEnd: true).ConfigureAwait(false);
            return;
        }

        var clock = sharedClock ?? new AutomationRunClock((ICueExecutionHost)this, initialPosition);
        // Same rule as the transport branch above: when something else owns the clock (a visualizer run
        // here), that owner owns the lifetime too - keeping the driver registered after the last key lets a
        // later backward seek reopen an already-completed ramp instead of finding nothing to reposition.
        await _outbound
            .StartAsync(cue, runDuration, clock.Read, duration is null || sharedClock is not null)
            .ConfigureAwait(false);
    }

    private RunClockSnapshot TransportAutomationClock(string groupId)
    {
        var snapshot = _session.Snapshot()
            .FirstOrDefault(candidate => string.Equals(candidate.GroupId, groupId, StringComparison.Ordinal));
        return snapshot is null
            ? default
            : new RunClockSnapshot(snapshot.Timeline.CueTime, snapshot.TimelineGeneration);
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

        // Send trims and cue volume are independent authorities. The latter belongs in the voice's
        // envelope component so fades/master trim keep composing with it, exactly as compiled tracks do.
        await _session.ApplyActiveLogicalSendsAsync(
                cueId.ToString(), ShowCompiler.LogicalSends(media))
            .ConfigureAwait(false);
        await _session.ApplyActiveVolumeAsync(
                cueId.ToString(),
                levelDb <= GainRange.SilenceFloorDb
                    ? 0f
                    : (float)Math.Pow(10, Math.Clamp(levelDb, GainRange.SilenceFloorDb, 12) / 20))
            .ConfigureAwait(false);
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
