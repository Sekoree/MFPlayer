using System.Collections.Frozen;

namespace HaCue2.Engine;

/// <summary>One documented and enforced route: what it answers to, and what it does.</summary>
/// <param name="Domain">Groups routes for the per-domain call counters.</param>
public sealed record RemoteApiRoute(string Method, string Pattern, string Summary, string Domain)
{
    /// <summary>How many times this route has been called since the server started.</summary>
    public long Calls => Interlocked.Read(ref _calls);

    private long _calls;

    internal void Count() => Interlocked.Increment(ref _calls);

    internal void Reset() => Interlocked.Exchange(ref _calls, 0);

    /// <summary>Whether a request's segments match this route, ignoring the id placeholders.</summary>
    /// <remarks>
    /// Shape-matched rather than regex'd: every route here is a fixed number of segments with known
    /// literals, and an exact shape is what lets an unknown path be a clean 404 instead of falling
    /// through to whichever handler happened to be permissive.
    /// </remarks>
    public bool Matches(string method, IReadOnlyList<string> segments)
    {
        if (!string.Equals(method, Method, StringComparison.OrdinalIgnoreCase))
            return false;

        var wanted = Segments;

        if (wanted.Count != segments.Count)
            return false;

        for (var index = 0; index < wanted.Count; index++)
        {
            if (wanted[index].StartsWith('{'))
                continue;

            if (!string.Equals(wanted[index], segments[index], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Whether the PATH matches, whatever the method - the 405-versus-404 distinction.</summary>
    public bool MatchesPath(IReadOnlyList<string> segments)
    {
        var wanted = Segments;

        if (wanted.Count != segments.Count)
            return false;

        for (var index = 0; index < wanted.Count; index++)
        {
            if (!wanted[index].StartsWith('{')
                && !string.Equals(wanted[index], segments[index], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private IReadOnlyList<string> Segments { get; } =
        Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The remote API's route table - the one place that says what the API is.
/// </summary>
/// <remarks>
/// <para>
/// A table rather than a chain of string comparisons, for three reasons the plan asks for: the shapes
/// are ENFORCED (an unknown path is a 404 and a known path with the wrong verb is a 405, rather than
/// both being whatever the last handler did), the API can DOCUMENT itself at
/// <c>GET /api/v1/endpoints</c>, and every route carries its own call counter so the Targets tab can
/// show what is actually being used.
/// </para>
/// <para>
/// <b>Versioned from the first line.</b> A remote API with no version in its path has no way to change
/// without breaking whatever is already driving it, and the things that drive one are other people's
/// show-control systems.
/// </para>
/// </remarks>
public static class RemoteApiRoutes
{
    public const string Prefix = "/api/v1";

    public static FrozenSet<RemoteApiRoute> All { get; } = new HashSet<RemoteApiRoute>
    {
        new("GET", "/api/v1/status", "what is sounding, standby per list, and any problems", "status"),
        new("GET", "/api/v1/endpoints", "this table - every route, with its call count", "status"),
        new("GET", "/api/v1/lists", "the cue lists, with their standby", "lists"),

        // The route the plan singles out. It needs per-list standby, which the session now owns - a
        // bare go against a list is meaningless without a per-list cursor to advance.
        new("POST", "/api/v1/lists/{list}/go", "fire the standby cue of one list", "transport"),

        new("POST", "/api/v1/cues/{cue}/go", "fire one cue by id, whatever the cursor is doing", "transport"),
        new("POST", "/api/v1/cues/{cue}/stop", "stop one cue", "transport"),
        new("POST", "/api/v1/cues/{cue}/standby", "move a list's cursor onto one cue without firing", "transport"),

        new("POST", "/api/v1/transport/stop", "stop everything, over the project's stop fade", "transport"),
        new("POST", "/api/v1/transport/panic", "stop everything, over the panic fade", "transport"),
        new("POST", "/api/v1/transport/pause", "pause or resume", "transport"),
    }.ToFrozenSet();

    /// <summary>The route a request resolves to, or null when nothing matches its shape.</summary>
    public static RemoteApiRoute? Resolve(string method, IReadOnlyList<string> segments) =>
        All.FirstOrDefault(route => route.Matches(method, segments));

    /// <summary>Whether some route would have answered this path under a different verb - a 405.</summary>
    public static bool PathExists(IReadOnlyList<string> segments) =>
        All.Any(route => route.MatchesPath(segments));

    public static void ResetCounters()
    {
        foreach (var route in All)
            route.Reset();
    }
}
