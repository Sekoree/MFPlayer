using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The remote API's route table.
/// </summary>
/// <remarks>
/// Pure shape-matching, no socket. This is the half where a mistake is silent — a route that matches
/// too loosely answers requests it should refuse, and one that matches too tightly makes a documented
/// path 404 for somebody's show-control system in the middle of a get-in.
/// </remarks>
public class RemoteApiRouteTests
{
    private static string[] Path(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void EveryRouteIsVersioned() =>
        // A remote API with no version in its path cannot change without breaking whatever is already
        // driving it — and the things that drive one are other people's systems.
        Assert.All(RemoteApiRoutes.All, route =>
            Assert.StartsWith(RemoteApiRoutes.Prefix, route.Pattern, StringComparison.Ordinal));

    [Fact]
    public void EveryRouteDocumentsItself() =>
        Assert.All(RemoteApiRoutes.All, route =>
        {
            Assert.False(string.IsNullOrWhiteSpace(route.Summary), route.Pattern);
            Assert.False(string.IsNullOrWhiteSpace(route.Domain), route.Pattern);
        });

    [Fact]
    public void AKnownRouteResolves() =>
        Assert.NotNull(RemoteApiRoutes.Resolve("GET", Path("/api/v1/status")));

    [Fact]
    public void APlaceholderMatchesAnyValue()
    {
        var byGuid = RemoteApiRoutes.Resolve("POST", Path($"/api/v1/lists/{Guid.NewGuid()}/go"));
        var byName = RemoteApiRoutes.Resolve("POST", Path("/api/v1/lists/Act%201/go"));

        Assert.NotNull(byGuid);
        Assert.Same(byGuid, byName);
    }

    [Fact]
    public void TheBareListGoRouteExists() =>
        // The one the plan singles out. It needs per-list standby, which the session now owns — a bare
        // go against a list is meaningless without a per-list cursor to advance.
        Assert.NotNull(RemoteApiRoutes.Resolve("POST", Path("/api/v1/lists/main/go")));

    [Fact]
    public void TheWrongSegmentCountDoesNotMatch()
    {
        // Shape-matched, not prefix-matched: "/api/v1/lists/x/go/extra" is not a longer form of a
        // route, it is a different path, and answering it would be answering something nobody defined.
        Assert.Null(RemoteApiRoutes.Resolve("POST", Path("/api/v1/lists/x/go/extra")));
        Assert.Null(RemoteApiRoutes.Resolve("POST", Path("/api/v1/lists/go")));
    }

    [Fact]
    public void AWrongLiteralDoesNotMatch() =>
        Assert.Null(RemoteApiRoutes.Resolve("POST", Path("/api/v1/lists/x/fire")));

    [Fact]
    public void TheWrongVerbDoesNotResolveButThePathStillExists()
    {
        // The 404/405 distinction: an unknown path is a mistake about WHAT exists, a known path with
        // the wrong verb is a mistake about HOW to call it, and collapsing them sends people looking
        // in the wrong place.
        Assert.Null(RemoteApiRoutes.Resolve("GET", Path("/api/v1/transport/panic")));
        Assert.True(RemoteApiRoutes.PathExists(Path("/api/v1/transport/panic")));
    }

    [Fact]
    public void AnUnknownPathDoesNotExist() =>
        Assert.False(RemoteApiRoutes.PathExists(Path("/api/v1/nonsense")));

    [Fact]
    public void MethodsAreMatchedCaseInsensitively() =>
        // HttpListener reports the verb as the client sent it.
        Assert.NotNull(RemoteApiRoutes.Resolve("get", Path("/api/v1/status")));

    [Fact]
    public void PathsAreMatchedCaseSensitively() =>
        // Unlike verbs. A path is an identifier, and matching "/Status" would mean the API answered to
        // things it never documented.
        Assert.Null(RemoteApiRoutes.Resolve("GET", Path("/api/v1/Status")));

    [Fact]
    public void CountersMoveOnlyForTheRouteThatWasCalled()
    {
        RemoteApiRoutes.ResetCounters();

        var status = RemoteApiRoutes.Resolve("GET", Path("/api/v1/status"))!;
        var lists = RemoteApiRoutes.Resolve("GET", Path("/api/v1/lists"))!;

        Assert.Equal(0L, status.Calls);

        status.Count();
        status.Count();

        Assert.Equal(2L, status.Calls);
        Assert.Equal(0L, lists.Calls);

        RemoteApiRoutes.ResetCounters();
        Assert.Equal(0L, status.Calls);
    }

    [Fact]
    public void NoTwoRoutesShareAMethodAndShape()
    {
        // Two routes of one shape would make dispatch depend on enumeration order, which is the kind
        // of thing that works until the set is edited.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var route in RemoteApiRoutes.All)
            Assert.True(seen.Add($"{route.Method} {route.Pattern}"), route.Pattern);
    }
}
