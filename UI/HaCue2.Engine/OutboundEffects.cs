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
        var lanes = EffectiveLanes(project, cue)
            .Where(lane => lane.Kind is EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp)
            .Where(lane => lane.Points.Count >= 2)
            .ToList();
        if (lanes.Count == 0)
            return;

        var runs = new List<Run>();
        foreach (var lane in lanes)
        {
            if (lane.EndpointId is not { } endpointId
                || project.ActionEndpoints.FirstOrDefault(endpoint => endpoint.Id == endpointId) is not { } endpoint)
            {
                _report($"“{cue.Label}” has a {lane.Kind} lane with no live endpoint");
                continue;
            }

            var expected = lane.Kind == EffectLaneKind.OscRamp ? EndpointKind.OscOut : EndpointKind.MidiOut;
            if (endpoint.Kind != expected)
            {
                _report($"“{cue.Label}” has a {lane.Kind} lane pointed at {endpoint.Kind}");
                continue;
            }

            var points = lane.Points
                .OrderBy(point => point.X)
                .Select(point => new OutboundRampPoint(
                    TimeSpan.FromTicks((long)(duration.Ticks * Math.Clamp(point.X, 0, 1))),
                    lane.Kind == EffectLaneKind.MidiRamp
                        ? Math.Round(Math.Clamp(point.Y, 0, 1) * 127, MidpointRounding.AwayFromZero)
                        : Math.Clamp(point.Y, 0, 1),
                    FadeCurve.Linear))
                .ToList();

            var runner = new OutboundRampRunner(
                points,
                (value, _) => SendAsync(cue, lane, endpoint, value));
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

    private async ValueTask SendAsync(CueNode cue, EffectLane lane, ActionEndpoint endpoint, double value)
    {
        var action = new ActionCueNode
        {
            Label = $"{cue.Label} · {lane.Kind}",
            EndpointId = endpoint.Id,
            Address = lane.Address,
            Arguments = lane.Kind == EffectLaneKind.MidiRamp
                ? ((int)value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.######", CultureInfo.InvariantCulture),
        };

        if (await _sender.SendAsync(action, endpoint).ConfigureAwait(false) is { } problem)
        {
            _report(problem);
            throw new IOException(problem);
        }
    }

    private static IReadOnlyList<EffectLane> EffectiveLanes(HaCueProject project, CueNode cue)
    {
        var inherited = Ancestors(project, cue.Id)
            .SelectMany(group => group.EffectLanes)
            .Where(lane => lane.Kind is EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp)
            .GroupBy(lane => lane.Kind)
            .ToDictionary(group => group.Key, group => group.First());

        var own = cue switch
        {
            MediaCueNode media => media.EffectLanes,
            VisualizerCueNode visualizer => visualizer.EffectLanes,
            GroupCueNode group => group.EffectLanes,
            _ => [],
        };

        foreach (var lane in own.Where(lane => lane.Kind is EffectLaneKind.OscRamp or EffectLaneKind.MidiRamp))
            inherited[lane.Kind] = lane;
        return [.. inherited.Values];
    }

    private static IEnumerable<GroupCueNode> Ancestors(HaCueProject project, Guid cueId)
    {
        foreach (var list in project.CueLists)
        {
            var stack = new Stack<(IReadOnlyList<CueNode> Cues, List<GroupCueNode> Parents)>();
            stack.Push((list.Cues, []));
            while (stack.Count > 0)
            {
                var (cues, parents) = stack.Pop();
                foreach (var candidate in cues)
                {
                    if (candidate.Id == cueId)
                    {
                        foreach (var parent in parents.AsEnumerable().Reverse())
                            yield return parent;
                        yield break;
                    }
                    if (candidate is GroupCueNode group)
                        stack.Push((group.Children, [.. parents, group]));
                }
            }
        }
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
