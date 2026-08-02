using System.Collections.Frozen;
using System.Threading;

namespace HaPlay.Remote;

/// <summary>One documented route: what it answers to, and what it does.</summary>
/// <param name="Method">The single legal application method (<c>GET</c> or <c>POST</c>).</param>
/// <param name="Pattern">The path as an operator reads it, e.g. <c>/api/v1/cues/{cue}/go</c>.</param>
/// <param name="Summary">One line describing the effect.</param>
public sealed record RemoteApiRoute(string Method, string Pattern, string Summary);

/// <summary>
/// The remote API's route table: the one place that knows which domains exist, which method each takes,
/// and what each does.
/// </summary>
/// <remarks>
/// <para>
/// Dispatch itself stays a nested switch - it does real per-domain work (1-based indices, playlist
/// addressing, query parameters) that a generic matcher would only obscure. What moved here is everything
/// that was previously <em>restated</em>: the method rule and the human-readable surface.
/// </para>
/// <para>
/// The method rule had been written twice - once in the request gate, once in <c>AllowedMethodsFor</c> -
/// and they drifted the moment a domain was added: <c>/lists</c> advertised <c>GET</c> in its <c>Allow</c>
/// header and then answered 405 to a GET. One table means the header and the gate cannot disagree.
/// </para>
/// <para>
/// The surface used to live only in a class comment, which is why the API could not describe itself.
/// <c>GET /api/v1/endpoints</c> now serves this table, and a test asserts every domain named here is one
/// the dispatcher actually handles - a comment cannot be checked, a table can.
/// </para>
/// </remarks>
public static class RemoteApiRoutes
{
    /// <summary>Every route the API answers, in the order an operator would read them.</summary>
    public static IReadOnlyList<RemoteApiRoute> All { get; } =
    [
        new("GET", "/api/v1/status", "Whether the API is reachable, and its bind/auth posture."),
        new("GET", "/api/v1/endpoints", "This table."),
        new("POST", "/api/v1/cues/go|pause|resume|stop|panic", "Transport for the selected cue list."),
        new("POST", "/api/v1/cues/{cue}/go|stop", "One cue, resolved in the selected list first."),
        new("GET", "/api/v1/lists", "The loaded cue lists."),
        new("POST", "/api/v1/lists/{list}/cues/{cue}/go|stop", "One cue within a named list (unambiguous)."),
        new("POST", "/api/v1/players/{player}/play|pause|toggle|stop|next|prev", "Deck transport."),
        new("POST", "/api/v1/players/{player}/volume?db=-10", "Set a deck's output level in dB."),
        new("POST", "/api/v1/players/{player}/hold[?on=true|false]", "Toggle a deck's hold-frame fallback."),
        new("POST", "/api/v1/players/{player}/{playlist}/{item}[/play]", "Play one playlist item."),
        new("POST", "/api/v1/soundboards/stop", "Stop every sounding tile."),
        new("POST", "/api/v1/soundboards/{board}/{tile}[/tap|play|stop|fade]", "One soundboard tile."),
        new("POST", "/api/v1/control/arm|disarm", "Arm or disarm the Control workspace."),
    ];

    /// <summary>The domains the table covers, lower-cased.</summary>
    public static FrozenSet<string> Domains { get; } =
        All.Select(DomainOf).Where(d => d.Length > 0).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The one legal application method for a request. The single source of truth for both the request
    /// gate and the <c>Allow</c> header.
    /// </summary>
    /// <param name="domain">First path segment after <c>/api/v1</c>, lower-cased.</param>
    /// <param name="restSegments">How many segments follow it.</param>
    public static string MethodFor(string domain, int restSegments) => domain switch
    {
        "status" or "endpoints" => "GET",
        // The one mixed domain: the bare inventory reads, anything addressed under a list commands.
        "lists" when restSegments == 0 => "GET",
        _ => "POST",
    };

    private static string DomainOf(RemoteApiRoute route)
    {
        var segments = route.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // api / v1 / {domain} / …
        return segments.Length >= 3 ? segments[2].ToLowerInvariant() : string.Empty;
    }
}

/// <summary>
/// Per-domain request counters, so an operator can tell "the controller never reached us" from "it reached
/// us and we refused it".
/// </summary>
/// <remarks>
/// Counted per DOMAIN rather than per route pattern: dispatch resolves a domain and then does its own
/// per-shape work, so a route-pattern counter would need the matcher this deliberately does not have, and
/// would report zero for anything it failed to match - the exact case worth counting.
/// </remarks>
public sealed class RemoteApiCounters
{
    private readonly Dictionary<string, Counter> _byDomain =
        RemoteApiRoutes.Domains.ToDictionary(d => d, _ => new Counter(), StringComparer.Ordinal);

    private readonly Counter _unknown = new();

    private sealed class Counter
    {
        private long _requests;
        private long _failures;

        public void Record(bool ok)
        {
            Interlocked.Increment(ref _requests);
            if (!ok)
                Interlocked.Increment(ref _failures);
        }

        public (long Requests, long Failures) Read() =>
            (Interlocked.Read(ref _requests), Interlocked.Read(ref _failures));
    }

    /// <summary>Records one dispatched request. <paramref name="status"/> below 400 counts as handled.</summary>
    public void Record(string domain, int status)
    {
        var counter = _byDomain.GetValueOrDefault(domain) ?? _unknown;
        counter.Record(status < 400);
    }

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
