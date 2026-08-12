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

    private sealed class AutomationRun(
        AutomationCueNode cue,
        AutomationRunClock clock,
        CancellationTokenSource cancellation,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> capturedTargets)
    {
        public AutomationCueNode Cue { get; } = cue;
        public AutomationRunClock Clock { get; } = clock;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        /// <summary>The sounding cue IDs each track resolved when this run fired. In particular, a
        /// group controller does not begin affecting a descendant which starts later.</summary>
        public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> CapturedTargets { get; } = capturedTargets;
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
        var duration = TimeSpan.FromMilliseconds(Math.Max(1, automation.DurationMs));
        var start = initialPosition < TimeSpan.Zero
            ? TimeSpan.Zero
            : initialPosition > duration ? duration : initialPosition;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _life.Token, cancellationToken);
        var clock = new AutomationRunClock((ICueExecutionHost)this, start);
        var run = new AutomationRun(
            automation, clock, cancellation, CaptureAutomationTargets(automation));
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
        Remember(automation.Id, list?.Id ?? Guid.Empty, groupId: "");
        // Keep the outbound drivers alive for the whole controller run. A track may place its final
        // key long before the cue ends; seeking back across that key must still reposition the target.
        _outbound.Start(automation, duration, clock.Read, keepAliveAfterEnd: true);
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
            _outbound.Complete(run.Cue.Id);
            completedNaturally = true;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            run.Started.TrySetResult(false);
            if (run.Cue.Completion == AutomationCompletion.RestoreBase)
                await RestoreAutomationAsync(run).ConfigureAwait(false);
        }
        finally
        {
            lock (_automationRunGate)
                if (ReferenceEquals(_automationRuns.GetValueOrDefault(run.Cue.Id), run))
                    _automationRuns.Remove(run.Cue.Id);
            run.Cancellation.Dispose();
            run.ApplyGate.Dispose();
            run.Started.TrySetResult(false);
            run.Complete.TrySetResult();
            Forget(run.Cue.Id.ToString());
        }

        if (completedNaturally)
            _ = ObserveLifecycleAsync(
                Executor.OnNaturalEndAsync(run.Cue.Id),
                "automation natural-end follow");
    }

    private async Task SampleAutomationAsync(AutomationRun run, TimeSpan position)
    {
        await run.ApplyGate.WaitAsync(run.Cancellation.Token).ConfigureAwait(false);
        try
        {
            var timeMs = Math.Clamp(
                (long)position.TotalMilliseconds, 0, Math.Max(1, run.Cue.DurationMs));
            foreach (var track in run.Cue.AutomationTracks.Where(track => track.Enabled))
            {
                if (AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External)
                    continue; // OutboundEffects owns rate limiting and coalescing on this same clock.
                if (!run.CapturedTargets.TryGetValue(track.Id, out var capturedTargets)
                    || track.Target.CueId is not { } targetId
                    || _project.FindCue(targetId) is not { } target)
                    continue;
                var value = AutomationEvaluator.Sample(track, _project, timeMs, AuthoredValue(target, track));
                await ApplyAutomationValueAsync(target, track, value, capturedTargets).ConfigureAwait(false);
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
                    await RestoreAutomationValueAsync(target, track, capturedTargets).ConfigureAwait(false);
        }
        finally
        {
            run.ApplyGate.Release();
        }
    }

    private IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> CaptureAutomationTargets(
        AutomationCueNode automation)
    {
        var sounding = SoundingIds().ToHashSet();
        var captured = new Dictionary<Guid, IReadOnlyList<Guid>>();
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
            var active = candidates
                .Where(candidate => sounding.Contains(candidate.Id))
                .Select(candidate => candidate.Id)
                .Distinct()
                .ToArray();
            if (active.Length == 0)
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

    private async Task StartVisualizerAutomationAsync(
        VisualizerCueNode cue, TimeSpan initialPosition)
    {
        if (!cue.AutomationTracks.Any(track => track.Enabled))
            return;

        var duration = TimeSpan.FromMilliseconds(Math.Max(1, cue.HoldMs));
        var start = initialPosition < TimeSpan.Zero
            ? TimeSpan.Zero
            : initialPosition > duration ? duration : initialPosition;
        var run = new VisualizerAutomationRun(
            cue,
            new AutomationRunClock((ICueExecutionHost)this, start),
            CancellationTokenSource.CreateLinkedTokenSource(_life.Token));
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
            run.Cancellation.Dispose();
            run.ApplyGate.Dispose();
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
            foreach (var track in run.Cue.AutomationTracks.Where(track => track.Enabled))
            {
                if (AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Domain == AutomationDomain.External)
                    continue;
                var value = AutomationEvaluator.Sample(
                    track, _project, timeMs, AuthoredValue(run.Cue, track));
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
        if (run is null || !await run.Started.Task.ConfigureAwait(false))
            return false;

        var duration = TimeSpan.FromMilliseconds(Math.Max(1, run.Cue.HoldMs));
        var sought = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
        var reading = run.Clock.Seek(sought);
        _outbound.Seek(cueId, reading);
        await SampleVisualizerAutomationAsync(run, reading.Position).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SeekAutomationRunAsync(Guid cueId, TimeSpan position)
    {
        AutomationRun? run;
        lock (_automationRunGate)
            _automationRuns.TryGetValue(cueId, out run);
        if (run is null)
            return false;

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
        CueNode target,
        AutomationTrack track,
        double value,
        IReadOnlyList<Guid> capturedTargets)
    {
        if (TryPlacementProperty(track.Target.PropertyId, out var transform)
            && track.Target.ObjectId is { } transformPlacementId
            && CuePlacements.Of(target).FirstOrDefault(placement => placement.Id == transformPlacementId)
                is { } transformPlacement)
        {
            await _session.ApplyActivePlacementTransformAutomationAsync(
                target.Id.ToString(),
                transformPlacement.CompositionId.ToString(),
                transformPlacement.LayerIndex,
                transform,
                value).ConfigureAwait(false);
            return;
        }
        if (TryEffectProperty(track.Target.PropertyId, out var effectProperty)
            && track.Target.ObjectId is { } effectId
            && CuePlacements.Of(target).FirstOrDefault(placement =>
                placement.ChromaKey?.Id == effectId || placement.ColorAdjust?.Id == effectId) is { } effectPlacement)
        {
            await _session.ApplyActivePlacementEffectAutomationAsync(
                target.Id.ToString(),
                effectPlacement.CompositionId.ToString(),
                effectPlacement.LayerIndex,
                effectId.ToString(),
                effectProperty,
                value).ConfigureAwait(false);
            return;
        }

        switch (track.Target.PropertyId)
        {
            case AutomationPropertyIds.CueVolume when target is MediaCueNode media:
                await _session.ApplyActiveVolumeAsync(media.Id.ToString(), Linear(value)).ConfigureAwait(false);
                break;
            case AutomationPropertyIds.PlacementOpacity
                when track.Target.ObjectId is { } placementId
                     && CuePlacements.Of(target).FirstOrDefault(placement => placement.Id == placementId) is { } placement:
                await _session.ApplyActivePlacementAutomationAsync(
                    target.Id.ToString(), placement.CompositionId.ToString(), placement.LayerIndex,
                    (float)Math.Clamp(value, 0, 1)).ConfigureAwait(false);
                break;
            case AutomationPropertyIds.GroupAudioTrim when target is GroupCueNode group:
                foreach (var mediaChild in Descendants(group).OfType<MediaCueNode>()
                             .Where(child => capturedTargets.Contains(child.Id)))
                    await _session.ApplyActiveAudioModifierAsync(
                        mediaChild.Id.ToString(), Linear(value)).ConfigureAwait(false);
                break;
            case AutomationPropertyIds.GroupVideoOpacity when target is GroupCueNode videoGroup:
                foreach (var child in Descendants(videoGroup)
                             .Where(child => capturedTargets.Contains(child.Id)))
                    await _session.ApplyActiveVideoModifierAsync(
                        child.Id.ToString(), (float)Math.Clamp(value, 0, 1)).ConfigureAwait(false);
                break;
        }
    }

    private async Task RestoreAutomationValueAsync(
        CueNode target,
        AutomationTrack track,
        IReadOnlyList<Guid> capturedTargets)
    {
        if (TryPlacementProperty(track.Target.PropertyId, out var transform)
            && track.Target.ObjectId is { } placementId
            && CuePlacements.Of(target).FirstOrDefault(placement => placement.Id == placementId) is { } placement)
        {
            await _session.ClearActivePlacementTransformAutomationAsync(
                target.Id.ToString(),
                placement.CompositionId.ToString(),
                placement.LayerIndex,
                transform).ConfigureAwait(false);
            return;
        }
        if (TryEffectProperty(track.Target.PropertyId, out var effectProperty)
            && track.Target.ObjectId is { } effectId
            && CuePlacements.Of(target).FirstOrDefault(placement =>
                placement.ChromaKey?.Id == effectId || placement.ColorAdjust?.Id == effectId) is { } effectPlacement)
        {
            await _session.ClearActivePlacementEffectAutomationAsync(
                target.Id.ToString(),
                effectPlacement.CompositionId.ToString(),
                effectPlacement.LayerIndex,
                effectId.ToString(),
                effectProperty).ConfigureAwait(false);
            return;
        }
        await ApplyAutomationValueAsync(
            target, track, AuthoredValue(target, track), capturedTargets).ConfigureAwait(false);
    }

    private static double AuthoredValue(CueNode target, AutomationTrack track)
    {
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
            && CuePlacements.Of(target).FirstOrDefault(candidate =>
                candidate.ChromaKey?.Id == effectId || candidate.ColorAdjust?.Id == effectId) is { } effectPlacement)
            return track.Target.PropertyId switch
            {
                AutomationPropertyIds.ChromaSimilarity => effectPlacement.ChromaKey?.Similarity ?? .4,
                AutomationPropertyIds.ChromaSmoothness => effectPlacement.ChromaKey?.Smoothness ?? .1,
                AutomationPropertyIds.ChromaSpillReduction => effectPlacement.ChromaKey?.SpillReduction ?? .1,
                AutomationPropertyIds.ColorBrightness => effectPlacement.ColorAdjust?.Brightness ?? 0,
                AutomationPropertyIds.ColorContrast => effectPlacement.ColorAdjust?.Contrast ?? 1,
                _ => AutomationPropertyCatalog.Get(track.Target.PropertyId)?.Value.Default ?? 0,
            };
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

    private static bool TryEffectProperty(string propertyId, out ShowPlacementEffectProperty property)
    {
        property = propertyId switch
        {
            AutomationPropertyIds.ChromaSimilarity => ShowPlacementEffectProperty.ChromaSimilarity,
            AutomationPropertyIds.ChromaSmoothness => ShowPlacementEffectProperty.ChromaSmoothness,
            AutomationPropertyIds.ChromaSpillReduction => ShowPlacementEffectProperty.ChromaSpillReduction,
            AutomationPropertyIds.ColorBrightness => ShowPlacementEffectProperty.ColorBrightness,
            AutomationPropertyIds.ColorContrast => ShowPlacementEffectProperty.ColorContrast,
            _ => (ShowPlacementEffectProperty)(-1),
        };
        return (int)property >= 0;
    }

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
            await StartVisualizerAutomationAsync(visualizer, TimeSpan.Zero).ConfigureAwait(false);
            StartClockedOutbound(
                cue,
                TimeSpan.FromMilliseconds(Math.Max(1, visualizer.HoldMs)),
                groupId: "");
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
        StartClockedOutbound(cue, PlayedLength(cue), group);
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
            StartClockedOutbound(cue, PlayedLength(cue), group);
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
            await StartVisualizerAutomationAsync(cue, start.StartPosition)
                .ConfigureAwait(false);
            StartClockedOutbound(
                cue,
                TimeSpan.FromMilliseconds(Math.Max(1, cue.HoldMs)),
                groupId: "",
                initialPosition: start.StartPosition);
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

    private void StartClockedOutbound(
        CueNode cue,
        TimeSpan? duration,
        string groupId,
        TimeSpan initialPosition = default)
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
            _outbound.Start(
                cue,
                runDuration,
                () => TransportAutomationClock(groupId),
                // The media transport, not the track's last key, owns this lifetime. Keeping the
                // driver registered lets a later backward seek reopen an already-completed ramp.
                keepAliveAfterEnd: true);
            return;
        }

        var clock = new AutomationRunClock((ICueExecutionHost)this, initialPosition);
        _outbound.Start(cue, runDuration, clock.Read, duration is null);
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
