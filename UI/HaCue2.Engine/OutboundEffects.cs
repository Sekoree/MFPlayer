using System.Diagnostics;
using System.Globalization;
using HaCue2.Core.Model;
using S.Control;
using S.Media.Session;

namespace HaCue2.Engine;

/// <summary>Runs OSC/MIDI automation at the control layer's bounded send rate.</summary>
internal sealed class OutboundEffects : IAsyncDisposable
{
    private readonly ActionSender _sender;
    private readonly Func<HaCueProject> _project;
    private readonly Action<string> _report;
    private readonly CancellationToken _lifetime;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, List<Run>> _running = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public OutboundEffects(
        ActionSender sender,
        Func<HaCueProject> project,
        Action<string> report,
        CancellationToken lifetime)
    {
        _sender = sender;
        _project = project;
        _report = report;
        _lifetime = lifetime;
    }

    public async Task StartAsync(
        CueNode cue,
        TimeSpan duration,
        Func<RunClockSnapshot>? clock = null,
        bool keepAliveAfterEnd = false)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await InterruptAndWaitAsync(cue.Id).ConfigureAwait(false);
            if (duration <= TimeSpan.Zero)
                return;

            var project = _project();
            var tracks = Tracks(cue)
                .Where(track => track.Enabled)
                .Where(track => track.Target.PropertyId is AutomationPropertyIds.OscValue
                    or AutomationPropertyIds.MidiControlValue)
                .Where(track => track.Keyframes.Count > 0)
                .ToList();
            if (tracks.Count == 0)
                return;

            var runs = new List<Run>();
            foreach (var track in tracks)
            {
                if (track.Target.EndpointId is not { } endpointId
                    || project.ActionEndpoints.FirstOrDefault(endpoint => endpoint.Id == endpointId)
                        is not { } endpoint)
                {
                    _report($"“{cue.Label}” has an automation track with no live endpoint");
                    continue;
                }

                var expected = track.Target.PropertyId == AutomationPropertyIds.OscValue
                    ? EndpointKind.OscOut : EndpointKind.MidiOut;
                if (endpoint.Kind != expected)
                {
                    _report($"“{cue.Label}” has automation pointed at {endpoint.Kind}");
                    continue;
                }

                var points = Points(project, track, duration);
                if (points.Count == 0)
                    continue;

                var runner = new OutboundRampRunner(
                    points,
                    (value, _) => SendAsync(cue, track, endpoint, value),
                    Math.Clamp(track.Target.SendRateHz, 1, 120));
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
                var started = Stopwatch.GetTimestamp();
                var run = new Run(
                    runner,
                    cancellation,
                    clock ?? (() => new RunClockSnapshot(Stopwatch.GetElapsedTime(started), 0)),
                    keepAliveAfterEnd,
                    track.Interruption);
                runs.Add(run);
            }

            if (runs.Count > 0)
            {
                lock (_gate)
                    _running[cue.Id] = runs;
                foreach (var run in runs)
                    run.Task = DriveAsync(cue.Id, run);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void Interrupt(Guid cueId)
    {
        List<Run>? runs;
        lock (_gate)
            _running.TryGetValue(cueId, out runs);

        if (runs is null)
            return;

        foreach (var run in runs)
            CancelRun(run);
    }

    /// <summary>Cancels one run, tolerating the run finishing (and disposing its source) concurrently:
    /// a completed run needs no cancel, and the same race guard exists at every other Cancel site in
    /// the engine (see CueExecutor's prepared follows).</summary>
    private static void CancelRun(Run run)
    {
        try { run.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task InterruptAndWaitAsync(Guid cueId)
    {
        Task[] tasks;
        lock (_gate)
        {
            if (!_running.TryGetValue(cueId, out var runs))
                return;
            foreach (var run in runs)
                CancelRun(run);
            tasks = [.. runs.Select(run => run.Task)];
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Normal media completion always lands the authored final value.</summary>
    public void Complete(Guid cueId)
    {
        List<Run>? runs;
        lock (_gate)
            _running.TryGetValue(cueId, out runs);
        if (runs is null)
            return;
        foreach (var run in runs)
        {
            run.CompleteNaturally = true;
            CancelRun(run);
        }
    }

    /// <summary>Seeds every outbound target at a discontinuous transport position immediately.</summary>
    public void Seek(Guid cueId, RunClockSnapshot reading)
    {
        List<Run>? runs;
        lock (_gate)
            _running.TryGetValue(cueId, out runs);
        if (runs is null)
            return;

        foreach (var run in runs)
        {
            lock (run.Gate)
            {
                run.LastReading = reading;
                run.LastGeneration = reading.Generation;
                run.Runner.Reposition(reading.Position);
            }
        }
    }

    private async Task DriveAsync(Guid cueId, Run run)
    {
        // StartAsync publishes the complete run set before a terminal-at-zero ramp can remove itself.
        await Task.Yield();
        try
        {
            while (run.KeepAliveAfterEnd || !run.Runner.IsFinished)
            {
                var reading = run.Clock();
                lock (run.Gate)
                {
                    run.LastReading = reading;
                    if (reading.Generation != run.LastGeneration)
                    {
                        run.LastGeneration = reading.Generation;
                        run.Runner.Reposition(reading.Position);
                    }
                    else
                    {
                        run.Runner.Advance(reading.Position);
                    }
                }
                if (run.Runner.IsFinished && !run.KeepAliveAfterEnd)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(10), run.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (run.Gate)
            {
                if (run.CompleteNaturally || run.Interruption == AutomationInterruption.LandFinal)
                    run.Runner.Interrupt();
                else
                    run.Runner.Freeze(run.Clock().Position);
            }
        }
        finally
        {
            await run.Runner.WaitForPendingSendAsync().ConfigureAwait(false);

            // Removed from the map BEFORE the source is disposed, and the Cancel call sites guard
            // against the remaining sliver: Interrupt/Complete resolve the run under the gate but
            // cancel outside it, so disposing while still discoverable threw ObjectDisposedException
            // into whatever engine thread happened to be stopping the cue at that moment.
            lock (_gate)
            {
                if (_running.TryGetValue(cueId, out var runs))
                {
                    runs.Remove(run);
                    if (runs.Count == 0)
                        _running.Remove(cueId);
                }
            }

            run.Cancellation.Dispose();
        }
    }

    private async ValueTask SendAsync(CueNode cue, AutomationTrack track, ActionEndpoint endpoint, double value)
    {
        var midi = track.Target.PropertyId == AutomationPropertyIds.MidiControlValue;
        var action = new ActionCueNode
        {
            Label = $"{cue.Label} · automation",
            EndpointId = endpoint.Id,
            Address = track.Target.Address,
            Arguments = midi
                ? ((int)value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.######", CultureInfo.InvariantCulture),
        };

        if (await _sender.SendAsync(action, endpoint).ConfigureAwait(false) is { } problem)
        {
            _report(problem);
            throw new IOException(problem);
        }
    }

    private static IReadOnlyList<AutomationTrack> Tracks(CueNode cue) => CueAutomation.Of(cue);

    /// <summary>Lowers a track to ramp points, TRUNCATED at the run's authored duration.
    /// <para>Keys beyond the cue's duration are preserved in the document but must never be played, and in
    /// particular must never be jumped to on completion: the runner's "land exactly on the final value"
    /// rule would otherwise slam a desk to a value the show never reached (a 1 s automation cue whose track
    /// is keyed out to 5 s sent 0.0…0.2 and then hard-jumped to 1.0). Truncating here means the terminal
    /// value IS the value at the cue's end, so landing on it stays correct.</para></summary>
    private static IReadOnlyList<OutboundRampPoint> Points(
        HaCueProject project, AutomationTrack track, TimeSpan duration)
    {
        var keys = track.Keyframes
            .Where(key => key.TimeMs >= 0 && double.IsFinite(key.Value))
            .OrderBy(key => key.TimeMs)
            .ThenBy(key => key.Id)
            .ToList();
        var points = new List<OutboundRampPoint>();
        for (var index = 0; index + 1 < keys.Count; index++)
        {
            var from = keys[index];
            var to = keys[index + 1];
            if (to.TimeMs <= from.TimeMs)
                continue;
            if (from.Hold)
            {
                points.Add(new OutboundRampPoint(TimeSpan.FromMilliseconds(from.TimeMs), from.Value));
                points.Add(new OutboundRampPoint(
                    TimeSpan.FromMilliseconds(Math.Max(from.TimeMs, to.TimeMs - 1)), from.Value));
                continue;
            }

            FadeShape shape;
            try { shape = from.Curve.Resolve(project); }
            catch (ArgumentException) { shape = FadeCurve.Linear; }
            if (shape.Custom is null)
            {
                points.Add(new OutboundRampPoint(
                    TimeSpan.FromMilliseconds(from.TimeMs), from.Value, shape.Law));
                continue;
            }

            const int samples = 32;
            for (var step = 0; step < samples; step++)
            {
                var progress = (double)step / samples;
                var time = from.TimeMs + ((to.TimeMs - from.TimeMs) * progress);
                var value = from.Value + ((to.Value - from.Value) * shape.Custom.Evaluate(progress));
                points.Add(new OutboundRampPoint(TimeSpan.FromMilliseconds(time), value));
            }
        }
        if (keys.Count > 0)
            points.Add(new OutboundRampPoint(TimeSpan.FromMilliseconds(keys[^1].TimeMs), keys[^1].Value));

        return Truncate(points, project, track, duration);
    }

    /// <summary>Drops everything past <paramref name="duration"/> and lands the ramp on the track's value
    /// AT that instant (sampled through the one shared evaluator, so the truncated endpoint agrees with
    /// what every other actuator reads at the same time).</summary>
    private static IReadOnlyList<OutboundRampPoint> Truncate(
        List<OutboundRampPoint> points,
        HaCueProject project,
        AutomationTrack track,
        TimeSpan duration)
    {
        if (points.Count == 0 || duration <= TimeSpan.Zero || points[^1].Time <= duration)
            return points;

        var kept = points.Where(point => point.Time < duration).ToList();
        var endValue = AutomationEvaluator.Sample(
            track, project, (long)duration.TotalMilliseconds, points[0].Value);

        // Carry the outgoing law of the segment the truncation lands inside, so the final approach keeps
        // its authored shape instead of silently becoming linear.
        var law = kept.Count > 0 ? kept[^1].CurveToNext : FadeCurve.Linear;
        if (kept.Count == 0)
            kept.Add(new OutboundRampPoint(TimeSpan.Zero, points[0].Value, law));
        kept.Add(new OutboundRampPoint(duration, endValue));
        return kept;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            foreach (var run in _running.Values.SelectMany(runs => runs))
                CancelRun(run);
            tasks = [.. _running.Values.SelectMany(runs => runs).Select(run => run.Task)];
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        _startGate.Dispose();
    }

    private sealed class Run(
        OutboundRampRunner runner,
        CancellationTokenSource cancellation,
        Func<RunClockSnapshot> clock,
        bool keepAliveAfterEnd,
        AutomationInterruption interruption)
    {
        public OutboundRampRunner Runner { get; } = runner;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Func<RunClockSnapshot> Clock { get; } = clock;
        public bool KeepAliveAfterEnd { get; } = keepAliveAfterEnd;
        public AutomationInterruption Interruption { get; } = interruption;
        public bool CompleteNaturally { get; set; }
        public object Gate { get; } = new();
        public RunClockSnapshot LastReading { get; set; }
        public long LastGeneration { get; set; } = long.MinValue;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
