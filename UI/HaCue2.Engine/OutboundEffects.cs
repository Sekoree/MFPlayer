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

    public void Start(CueNode cue, TimeSpan duration)
    {
        Interrupt(cue.Id);
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
                || project.ActionEndpoints.FirstOrDefault(endpoint => endpoint.Id == endpointId) is not { } endpoint)
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

            var points = Points(project, track);
            if (points.Count == 0)
                continue;

            var runner = new OutboundRampRunner(
                points,
                (value, _) => SendAsync(cue, track, endpoint, value),
                Math.Clamp(track.Target.SendRateHz, 1, 120));
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            var run = new Run(runner, cancellation);
            run.Task = DriveAsync(cue.Id, run);
            runs.Add(run);
        }

        if (runs.Count > 0)
            lock (_gate)
                _running[cue.Id] = runs;
    }

    public void Interrupt(Guid cueId)
    {
        List<Run>? runs;
        lock (_gate)
            _running.TryGetValue(cueId, out runs);

        if (runs is null)
            return;

        foreach (var run in runs)
            run.Cancellation.Cancel();
    }

    private async Task DriveAsync(Guid cueId, Run run)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (!run.Runner.IsFinished)
            {
                run.Runner.Advance(Stopwatch.GetElapsedTime(started));
                if (run.Runner.IsFinished)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(10), run.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            run.Runner.Interrupt();
        }
        finally
        {
            await run.Runner.WaitForPendingSendAsync().ConfigureAwait(false);
            run.Cancellation.Dispose();
            lock (_gate)
            {
                if (_running.TryGetValue(cueId, out var runs))
                {
                    runs.Remove(run);
                    if (runs.Count == 0)
                        _running.Remove(cueId);
                }
            }
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

    private static IReadOnlyList<OutboundRampPoint> Points(HaCueProject project, AutomationTrack track)
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
        return points;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            foreach (var run in _running.Values.SelectMany(runs => runs))
                run.Cancellation.Cancel();
            tasks = [.. _running.Values.SelectMany(runs => runs).Select(run => run.Task)];
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private sealed class Run(OutboundRampRunner runner, CancellationTokenSource cancellation)
    {
        public OutboundRampRunner Runner { get; } = runner;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
