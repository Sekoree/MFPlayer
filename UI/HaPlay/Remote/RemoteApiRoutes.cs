using System.Collections.Frozen;
using System.Threading;

namespace HaPlay.Remote;

/// <summary>One documented and enforced route family: what it answers to, and what it does.</summary>
public sealed class RemoteApiRoute
{
    private readonly Func<string[], bool> _matchesRest;

    internal RemoteApiRoute(
        string method,
        string pattern,
        string summary,
        string domain,
        Func<string[], bool> matchesRest,
        params string[] probePaths)
    {
        Method = method;
        Pattern = pattern;
        Summary = summary;
        Domain = domain;
        _matchesRest = matchesRest;
        ProbePaths = Array.AsReadOnly(probePaths);
    }

    /// <summary>The single legal application method (<c>GET</c> or <c>POST</c>).</summary>
    public string Method { get; }

    /// <summary>The path as an operator reads it, e.g. <c>/api/v1/cues/{cue}/go</c>.</summary>
    public string Pattern { get; }

    /// <summary>One line describing the effect.</summary>
    public string Summary { get; }

    internal string Domain { get; }

    /// <summary>Concrete paths covering every alternative accepted by this route family. These make the
    /// documentation-to-dispatch relationship executable in tests rather than merely aspirational.</summary>
    internal IReadOnlyList<string> ProbePaths { get; }

    internal bool Matches(string domain, string[] rest) =>
        string.Equals(domain, Domain, StringComparison.Ordinal) && _matchesRest(rest);
}

/// <summary>
/// The remote API's route table: the one place that defines which path shapes exist, which method each
/// takes, and how each is presented to an operator.
/// </summary>
/// <remarks>
/// Dispatch remains a nested switch because its per-domain work is clearer that way. Admission does not:
/// <see cref="TryMatch"/> gates every request through this table before a handler runs, so a documented
/// route cannot accept a different method and an undocumented suffix cannot accidentally reach a handler.
/// The concrete probe paths additionally exercise every accepted verb in the dispatcher tests.
/// </remarks>
public static class RemoteApiRoutes
{
    private static bool Is(string value, string expected) =>
        value.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsOneOf(string value, params string[] expected) =>
        expected.Any(item => Is(value, item));

    /// <summary>Every route family the API answers, in the order an operator would read them.</summary>
    public static IReadOnlyList<RemoteApiRoute> All { get; } = Array.AsReadOnly<RemoteApiRoute>(
    [
        new("GET", "/api/v1/status", "Whether the API is reachable, and its bind/auth posture.",
            "status", static rest => rest.Length == 0,
            "/api/v1/status"),
        new("GET", "/api/v1/endpoints", "This table.",
            "endpoints", static rest => rest.Length == 0,
            "/api/v1/endpoints"),
        new("POST", "/api/v1/cues/go|pause|resume|stop|panic", "Transport for the selected cue list.",
            "cues", static rest => rest.Length == 1 && IsOneOf(rest[0], "go", "pause", "resume", "stop", "panic"),
            "/api/v1/cues/go", "/api/v1/cues/pause", "/api/v1/cues/resume",
            "/api/v1/cues/stop", "/api/v1/cues/panic"),
        new("POST", "/api/v1/cues/{cue}/go|stop", "One cue, resolved in the selected list first.",
            "cues", static rest => rest.Length == 2 && IsOneOf(rest[1], "go", "stop"),
            "/api/v1/cues/__missing__/go", "/api/v1/cues/__missing__/stop"),
        new("GET", "/api/v1/lists", "The loaded cue lists.",
            "lists", static rest => rest.Length == 0,
            "/api/v1/lists"),
        new("POST", "/api/v1/lists/{list}/cues/{cue}/go|stop", "One cue within a named list (unambiguous).",
            "lists", static rest => rest.Length == 4 && Is(rest[1], "cues") && IsOneOf(rest[3], "go", "stop"),
            "/api/v1/lists/__missing__/cues/1/go", "/api/v1/lists/__missing__/cues/1/stop"),
        new("POST", "/api/v1/players/{player}/play|pause|toggle|stop|next|prev|previous", "Deck transport.",
            "players", static rest => rest.Length == 2
                && IsOneOf(rest[1], "play", "pause", "toggle", "stop", "next", "prev", "previous"),
            "/api/v1/players/1/play", "/api/v1/players/1/pause", "/api/v1/players/1/toggle",
            "/api/v1/players/1/stop", "/api/v1/players/1/next", "/api/v1/players/1/prev",
            "/api/v1/players/1/previous"),
        new("POST", "/api/v1/players/{player}/volume?db=-10", "Set a deck's output level in dB.",
            "players", static rest => rest.Length == 2 && Is(rest[1], "volume"),
            "/api/v1/players/1/volume"),
        new("POST", "/api/v1/players/{player}/hold[?on=true|false]", "Toggle a deck's hold-frame fallback.",
            "players", static rest => rest.Length == 2 && Is(rest[1], "hold"),
            "/api/v1/players/1/hold"),
        new("POST", "/api/v1/players/{player}/{playlist}/{item}[/play]", "Play one playlist item.",
            "players", static rest => (rest.Length == 3 || rest.Length == 4 && Is(rest[3], "play"))
                && int.TryParse(rest[1], out _) && int.TryParse(rest[2], out _),
            "/api/v1/players/1/1/1", "/api/v1/players/1/1/1/play"),
        new("POST", "/api/v1/soundboards/stop", "Stop every sounding tile.",
            "soundboards", static rest => rest.Length == 1 && Is(rest[0], "stop"),
            "/api/v1/soundboards/stop"),
        new("POST", "/api/v1/soundboards/{board}/{tile}[/tap|play|stop|fade]", "One soundboard tile.",
            "soundboards", static rest => (rest.Length == 2 || rest.Length == 3
                    && IsOneOf(rest[2], "tap", "play", "stop", "fade"))
                && int.TryParse(rest[0], out _) && int.TryParse(rest[1], out _),
            "/api/v1/soundboards/1/1", "/api/v1/soundboards/1/1/tap",
            "/api/v1/soundboards/1/1/play", "/api/v1/soundboards/1/1/stop",
            "/api/v1/soundboards/1/1/fade"),
        new("POST", "/api/v1/control/arm|enable|disarm|disable", "Arm or disarm the Control workspace.",
            "control", static rest => rest.Length == 1 && IsOneOf(rest[0], "arm", "enable", "disarm", "disable"),
            "/api/v1/control/arm", "/api/v1/control/enable",
            "/api/v1/control/disarm", "/api/v1/control/disable"),
    ]);

    /// <summary>The domains the table covers, lower-cased.</summary>
    public static FrozenSet<string> Domains { get; } =
        All.Select(route => route.Domain).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Resolves one exact path shape to its route family.</summary>
    public static bool TryMatch(string domain, string[] rest, out RemoteApiRoute route)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(rest);
        foreach (var candidate in All)
        {
            if (!candidate.Matches(domain, rest))
                continue;
            route = candidate;
            return true;
        }

        route = null!;
        return false;
    }
}

/// <summary>
/// Per-domain request counters, so an operator can tell "the controller never reached us" from "it reached
/// us and we refused it".
/// </summary>
public sealed class RemoteApiCounters
{
    private readonly Dictionary<string, Counter> _byDomain =
        RemoteApiRoutes.Domains.ToDictionary(d => d, _ => new Counter(), StringComparer.Ordinal);

    private readonly Counter _unknown = new();

    private sealed class Counter
    {
        private long _requests;
        private long _failures;

        public void RecordRequest() => Interlocked.Increment(ref _requests);
        public void RecordFailure() => Interlocked.Increment(ref _failures);

        public (long Requests, long Failures) Read() =>
            (Interlocked.Read(ref _requests), Interlocked.Read(ref _failures));
    }

    private Counter CounterFor(string domain) => _byDomain.GetValueOrDefault(domain) ?? _unknown;

    /// <summary>Records a request as soon as it reaches dispatch, before any handler or snapshot runs.</summary>
    public void RecordRequest(string domain) => CounterFor(domain).RecordRequest();

    /// <summary>Records a refused or failed request, including exceptions and cancellation.</summary>
    public void RecordFailure(string domain) => CounterFor(domain).RecordFailure();

    /// <summary>Request/failure totals per domain, plus an <c>(unknown)</c> row for unrouted requests.</summary>
    public IReadOnlyList<(string Domain, long Requests, long Failures)> Snapshot()
    {
        var rows = _byDomain
            .Select(kv => (Domain: kv.Key, Counts: kv.Value.Read()))
            .Select(x => (x.Domain, x.Counts.Requests, x.Counts.Failures))
            .OrderBy(x => x.Domain, StringComparer.Ordinal)
            .ToList();
        var unknown = _unknown.Read();
        rows.Add(("(unknown)", unknown.Requests, unknown.Failures));
        return rows;
    }
}
