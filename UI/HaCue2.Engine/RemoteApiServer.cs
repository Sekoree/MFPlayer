using System.Net;
using System.Text;
using System.Text.Json;
using HaCue2.Core.Model;
using HaRemote;

namespace HaCue2.Engine;

/// <summary>What a request resolved to. Separated from the socket so it can be tested without one.</summary>
public readonly record struct RemoteApiResult(int Status, string Body, string ContentType = "application/json");

/// <summary>The transport surface exposed remotely, separated so routing can be tested without devices.</summary>
public interface IRemoteApiTransport
{
    Guid? Previewing { get; }
    bool IsPaused { get; }
    Task<ShowState> SnapshotAsync();
    Task<Guid?> GoAsync(CueList list);
    Task<bool> FireAsync(Guid cueId);
    Task StopCueAsync(Guid cueId);
    Task<bool> StandbyAsync(CueList list, Guid? cueId);
    Task StopAllAsync();
    Task PanicAsync();
    Task SetPausedAsync(bool paused);
}

/// <summary>
/// The remote API: an HTTP surface over the same transport verbs the buttons use.
/// </summary>
/// <remarks>
/// <para>
/// <b>Local-only unless the project says otherwise.</b> A cue player that answers the network by
/// default is one that can be fired by anything on the venue wifi. The project's own
/// <see cref="RemoteApiOverride.LanAllowed"/> is what widens it, and a token is required either way.
/// </para>
/// <para>
/// <b>Every route goes through the same verbs the UI calls.</b> A remote GO is a GO - not a second
/// implementation that can drift from the one an operator tested with. That is the same rule external
/// input follows, for the same reason.
/// </para>
/// <para>
/// HaPlay has its own remote API under <c>UI/HaPlay/Remote</c>; HaCue2 cannot reference it (an app may
/// not reference another app) and its dispatcher targets HaPlay's view-models regardless. The ROUTE
/// TABLE idea is worth mirroring and is mirrored; the dispatch is necessarily HaCue2's own.
/// </para>
/// <para>
/// The TRANSPORT (listener lifecycle, admission, deadlines, bounded drain) is the shared
/// <see cref="BoundedControlHttpHost"/> (F-05): the same mechanics HaPlay's REST server runs on, so
/// the two can no longer drift. Routing, token policy and error shapes stay HaCue2's.
/// </para>
/// </remarks>
public sealed class RemoteApiServer : IAsyncDisposable
{
    private readonly IRemoteApiTransport _host;
    private readonly Func<HaCueProject> _project;
    private readonly string _token;
    private BoundedControlHttpHost? _http;

    /// <summary>A dispatch that has not answered in this long is stuck behind something (a wedged
    /// open under a GO, most plausibly); the CALLER gets a 503 and can retry. A transport call that
    /// cannot observe cancellation may run on, but remains charged to the host's admission bound
    /// until it ends. Settable so a test can prove the deadline without waiting half a minute. Read at
    /// <see cref="StartAsync"/> time.</summary>
    internal TimeSpan RequestDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long shutdown waits for in-flight handlers before abandoning them. Unbounded, a
    /// single hung dispatch could hold the whole application's teardown hostage. Settable for the
    /// same test reason as <see cref="RequestDeadline"/>. Read at <see cref="StartAsync"/> time.</summary>
    internal TimeSpan ShutdownDrainBudget { get; set; } = TimeSpan.FromSeconds(2);

    public RemoteApiServer(IRemoteApiTransport host, Func<HaCueProject> project, string token)
    {
        _host = host;
        _project = project;
        _token = token;
    }

    /// <summary>Where it is listening, or empty when it is not.</summary>
    public string Address { get; private set; } = "";

    public bool IsRunning => _http?.IsRunning == true;

    /// <summary>What the last call was, for the Targets tab.</summary>
    public string LastCall { get; private set; } = "";

    /// <summary>Raised when a request could not be served, so the host can report it.</summary>
    public event Action<string>? Problem;

    /// <summary>
    /// Starts listening.
    /// </summary>
    /// <remarks>
    /// <c>localhost</c> unless the project allows the LAN. Binding a wildcard prefix needs elevation on
    /// Windows and would be a surprising thing for a cue player to ask for, so a failure to bind is
    /// reported and survived rather than thrown - the show does not depend on this.
    /// </remarks>
    public Task StartAsync(int port, bool lanAllowed)
    {
        if (IsRunning)
            return Task.CompletedTask;

        if (port is < 1 or > 65_535)
        {
            Problem?.Invoke($"the remote API port {port} is outside 1–65535");
            return Task.CompletedTask;
        }

        var wildcard = lanAllowed ? "+" : "localhost";
        var deadline = RequestDeadline;
        var options = new ControlHttpHostOptions
        {
            // Deadlined, and cancelled by server shutdown: a dispatch stuck behind a wedged
            // transport call answers the CALLER with a 503 instead of holding the connection (and
            // shutdown) open indefinitely. The token prevents work that has already expired from
            // entering a transport verb; once a verb has started, the shared host keeps it inside
            // the admission bound until it really ends.
            Dispatch = (request, cancellationToken) => ToHostResult(
                HandleAsync(
                    request.Method,
                    request.Path,
                    request.Header("X-HaCue2-Token"),
                    cancellationToken)),
            Error = (status, message) => ToHostResult(Error(status, message)),
            DeadlineExceeded = budget => ToHostResult(
                Error(503, $"the request did not complete within {budget.TotalSeconds:0} s")),
            // Keep the remote answer generic: exception details can contain paths/device names and
            // belong in the structured host log plus the local Problem event, not on the wire.
            DispatchFailure = () => ToHostResult(
                Error(500, "the request could not be completed")),
            // Late-bound: subscribers may attach after StartAsync.
            Problem = message => Problem?.Invoke(message),
            OverCapacity = OverCapacityPolicy.Refuse,
            RequestDeadline = deadline,
            ShutdownDrainBudget = ShutdownDrainBudget,
            LogName = "HaCue2.Engine.RemoteApiServer",
        };

        var http = BoundedControlHttpHost.TryStart([$"http://{wildcard}:{port}/"], options, out var bindError);
        if (http is null)
        {
            Problem?.Invoke(
                $"the remote API could not listen on port {port} - {bindError}"
                + (lanAllowed ? " (a LAN binding may need elevation)" : ""));
            return Task.CompletedTask;
        }

        _http = http;
        Address = $"http://{(lanAllowed ? Dns.GetHostName() : "localhost")}:{port}{RemoteApiRoutes.Prefix}";
        RemoteApiRoutes.ResetCounters();

        return Task.CompletedTask;
    }

    private static async Task<ControlHttpResult> ToHostResult(Task<RemoteApiResult> pending)
    {
        var result = await pending.ConfigureAwait(false);
        return ToHostResult(result);
    }

    private static ControlHttpResult ToHostResult(RemoteApiResult result) =>
        new(result.Status, result.Body, result.ContentType);

    /// <summary>
    /// Resolves and carries out one request. Public so it can be tested without a socket.
    /// </summary>
    /// <remarks>
    /// The 404/405 distinction is deliberate: an unknown path is a caller's mistake about WHAT exists,
    /// a known path with the wrong verb is a mistake about HOW to call it, and collapsing them into one
    /// answer sends people looking in the wrong place.
    /// </remarks>
    public async Task<RemoteApiResult> HandleAsync(
        string method,
        string path,
        string? token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A missing configured token REFUSES every call rather than waving them through. The app
        // always supplies one (AppSettings.EnsureRemoteToken mints it), so this can only be reached by
        // a hand-edited settings file or a future call site - and either of those combined with
        // LanAllowed, which binds a wildcard prefix, would leave anyone on the network able to fire
        // cues. A credential check that disappears when the credential is absent is the wrong way to
        // fail.
        if (_token.Length == 0)
            return Error(503, "the remote API has no token configured");

        // Compared in fixed time so the token cannot be probed a character at a time.
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token ?? ""), Encoding.UTF8.GetBytes(_token)))
            return Error(401, "a valid X-HaCue2-Token header is required");

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (RemoteApiRoutes.Resolve(method, segments) is not { } route)
        {
            return RemoteApiRoutes.PathExists(segments)
                ? Error(405, $"{method} is not allowed on {path}")
                : Error(404, $"no route for {path}");
        }

        route.Count();
        LastCall = $"{method} {path} · {DateTime.Now:HH:mm:ss}";

        cancellationToken.ThrowIfCancellationRequested();
        return await DispatchAsync(route, segments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteApiResult> DispatchAsync(
        RemoteApiRoute route, string[] segments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = _project();

        switch (route.Pattern)
        {
            case "/api/v1/status":
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await _host.SnapshotAsync().ConfigureAwait(false);

                return new RemoteApiResult(200, JsonSerializer.Serialize(
                    new RemoteStatus(
                        [.. state.Sounding.Select(id => id.ToString())],
                        state.IsPaused,
                        _host.Previewing?.ToString(),
                        [.. state.Problems],
                        state.Standby.ToDictionary(
                            entry => entry.Key.ToString(), entry => entry.Value.ToString())),
                    RemoteApiJsonContext.Default.RemoteStatus));
            }

            case "/api/v1/endpoints":
                return new RemoteApiResult(200, JsonSerializer.Serialize(
                    RemoteApiRoutes.All
                        .OrderBy(item => item.Pattern, StringComparer.Ordinal)
                        .Select(item => new RemoteRoute(
                            item.Method, item.Pattern, item.Summary, item.Domain, item.Calls))
                        .ToArray(),
                    RemoteApiJsonContext.Default.RemoteRouteArray));

            case "/api/v1/lists":
                return new RemoteApiResult(200, JsonSerializer.Serialize(
                    project.CueLists.Select(list => new RemoteList(
                        list.Id.ToString(),
                        list.Name,
                        list.Flatten().Count(),
                        list.StandbyCueId?.ToString())).ToArray(),
                    RemoteApiJsonContext.Default.RemoteListArray));

            case "/api/v1/lists/{list}/go":
            {
                if (Find(project.CueLists, segments[3], list => list.Id, list => list.Name) is not { } list)
                    return Error(404, $"no cue list '{segments[3]}'");

                cancellationToken.ThrowIfCancellationRequested();
                var fired = await _host.GoAsync(list).ConfigureAwait(false);
                return Ack(new RemoteAck(Fired: fired?.ToString(), Ok: fired is not null));
            }

            case "/api/v1/cues/{cue}/go":
            {
                if (!Guid.TryParse(segments[3], out var cueId) || project.FindCue(cueId) is null)
                    return Error(404, $"no cue '{segments[3]}'");

                cancellationToken.ThrowIfCancellationRequested();
                var ok = await _host.FireAsync(cueId).ConfigureAwait(false);
                return Ack(new RemoteAck(Fired: ok ? cueId.ToString() : null, Ok: ok));
            }

            case "/api/v1/cues/{cue}/stop":
            {
                if (!Guid.TryParse(segments[3], out var cueId) || project.FindCue(cueId) is null)
                    return Error(404, $"no cue '{segments[3]}'");

                cancellationToken.ThrowIfCancellationRequested();
                await _host.StopCueAsync(cueId).ConfigureAwait(false);
                return Ack(new RemoteAck(Stopped: cueId.ToString(), Ok: true));
            }

            case "/api/v1/cues/{cue}/standby":
            {
                if (!Guid.TryParse(segments[3], out var cueId)
                    || project.ListOf(cueId) is not { } owner)
                    return Error(404, $"no cue '{segments[3]}'");

                cancellationToken.ThrowIfCancellationRequested();
                var moved = await _host.StandbyAsync(owner, cueId).ConfigureAwait(false);
                return Ack(new RemoteAck(
                    Standby: moved ? cueId.ToString() : null,
                    List: owner.Id.ToString(),
                    Ok: moved));
            }

            case "/api/v1/transport/stop":
                cancellationToken.ThrowIfCancellationRequested();
                await _host.StopAllAsync().ConfigureAwait(false);
                return Ack(new RemoteAck(Ok: true));

            case "/api/v1/transport/panic":
                cancellationToken.ThrowIfCancellationRequested();
                await _host.PanicAsync().ConfigureAwait(false);
                return Ack(new RemoteAck(Ok: true));

            case "/api/v1/transport/pause":
                cancellationToken.ThrowIfCancellationRequested();
                await _host.SetPausedAsync(!_host.IsPaused).ConfigureAwait(false);
                return Ack(new RemoteAck(Paused: _host.IsPaused, Ok: true));

            default:
                return Error(500, "the route table names a route nothing dispatches");
        }
    }

    /// <summary>
    /// Finds a list by id, or failing that by name.
    /// </summary>
    /// <remarks>
    /// By NAME as a fallback because a show-control system is configured by a human typing a cue list's
    /// name, not a GUID - and a remote API that only accepted GUIDs would be one nobody could set up
    /// from a lighting desk's macro editor.
    /// </remarks>
    private static T? Find<T>(
        IEnumerable<T> items, string key, Func<T, Guid> id, Func<T, string> name) where T : class
    {
        var all = items.ToList();

        if (Guid.TryParse(key, out var parsed) && all.FirstOrDefault(item => id(item) == parsed) is { } byId)
            return byId;

        return all.FirstOrDefault(item =>
            string.Equals(name(item), key, StringComparison.OrdinalIgnoreCase));
    }

    private static RemoteApiResult Error(int status, string message) =>
        new(status, JsonSerializer.Serialize(
            new RemoteError(message), RemoteApiJsonContext.Default.RemoteError));

    private static RemoteApiResult Ack(RemoteAck ack) =>
        new(200, JsonSerializer.Serialize(ack, RemoteApiJsonContext.Default.RemoteAck));

    public async ValueTask DisposeAsync()
    {
        Address = "";
        if (_http is { } http)
        {
            _http = null;
            // The host's disposal is BOUNDED by ShutdownDrainBudget - one hung dispatch can no
            // longer hold the whole application's teardown hostage.
            await http.DisposeAsync().ConfigureAwait(false);
        }
    }
}
