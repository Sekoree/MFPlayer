using System.Net;
using System.Text;
using System.Text.Json;
using HaCue2.Core.Model;

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
/// </remarks>
public sealed class RemoteApiServer : IAsyncDisposable
{
    private readonly IRemoteApiTransport _host;
    private readonly Func<HaCueProject> _project;
    private readonly string _token;
    private HttpListener? _listener;
    private CancellationTokenSource? _life;
    private Task? _loop;
    private readonly object _handlersGate = new();
    private readonly HashSet<Task> _handlers = [];

    public RemoteApiServer(IRemoteApiTransport host, Func<HaCueProject> project, string token)
    {
        _host = host;
        _project = project;
        _token = token;
    }

    /// <summary>Where it is listening, or empty when it is not.</summary>
    public string Address { get; private set; } = "";

    public bool IsRunning => _listener?.IsListening == true;

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

        var listener = new HttpListener();
        var wildcard = lanAllowed ? "+" : "localhost";

        try
        {
            listener.Prefixes.Add($"http://{wildcard}:{port}/");
            listener.Start();
        }
        catch (Exception failure) when (
            failure is HttpListenerException or ObjectDisposedException or ArgumentException)
        {
            listener.Close();
            Problem?.Invoke(
                $"the remote API could not listen on port {port} - {failure.Message}"
                + (lanAllowed ? " (a LAN binding may need elevation)" : ""));
            return Task.CompletedTask;
        }

        _listener = listener;
        _life = new CancellationTokenSource();
        Address = $"http://{(lanAllowed ? Dns.GetHostName() : "localhost")}:{port}{RemoteApiRoutes.Prefix}";
        RemoteApiRoutes.ResetCounters();
        _loop = Task.Run(() => AcceptAsync(_life.Token));

        return Task.CompletedTask;
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            // A slow request must not stop acceptance, but handlers remain tracked so shutdown can
            // wait for every response that already entered the server.
            var handler = Task.Run(() => ServeAsync(context), CancellationToken.None);
            lock (_handlersGate)
                _handlers.Add(handler);
            _ = handler.ContinueWith(
                completed =>
                {
                    lock (_handlersGate)
                        _handlers.Remove(completed);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            var result = await HandleAsync(
                context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath ?? "",
                context.Request.Headers["X-HaCue2-Token"]).ConfigureAwait(false);

            var bytes = Encoding.UTF8.GetBytes(result.Body);
            context.Response.StatusCode = result.Status;
            context.Response.ContentType = result.ContentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The caller hung up. Nothing to report and nothing to do.
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Problem?.Invoke($"remote request failed - {failure.Message}");
            try
            {
                // The reason rides along: the API is token-gated, and a controller integrator staring
                // at a bare "could not be completed" has no way to tell a show fault from their own
                // request. (The slot-collision incident fired 12 of 13 cues and answered exactly that.)
                var result = Error(500, $"the request could not be completed - {failure.Message}");
                var bytes = Encoding.UTF8.GetBytes(result.Body);
                context.Response.StatusCode = result.Status;
                context.Response.ContentType = result.ContentType;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception responseFailure) when (
                responseFailure is HttpListenerException or IOException or ObjectDisposedException)
            {
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception failure) when (failure is HttpListenerException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Resolves and carries out one request. Public so it can be tested without a socket.
    /// </summary>
    /// <remarks>
    /// The 404/405 distinction is deliberate: an unknown path is a caller's mistake about WHAT exists,
    /// a known path with the wrong verb is a mistake about HOW to call it, and collapsing them into one
    /// answer sends people looking in the wrong place.
    /// </remarks>
    public async Task<RemoteApiResult> HandleAsync(string method, string path, string? token)
    {
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

        return await DispatchAsync(route, segments).ConfigureAwait(false);
    }

    private async Task<RemoteApiResult> DispatchAsync(RemoteApiRoute route, string[] segments)
    {
        var project = _project();

        switch (route.Pattern)
        {
            case "/api/v1/status":
            {
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

                var fired = await _host.GoAsync(list).ConfigureAwait(false);
                return Ack(new RemoteAck(Fired: fired?.ToString(), Ok: fired is not null));
            }

            case "/api/v1/cues/{cue}/go":
            {
                if (!Guid.TryParse(segments[3], out var cueId) || project.FindCue(cueId) is null)
                    return Error(404, $"no cue '{segments[3]}'");

                var ok = await _host.FireAsync(cueId).ConfigureAwait(false);
                return Ack(new RemoteAck(Fired: ok ? cueId.ToString() : null, Ok: ok));
            }

            case "/api/v1/cues/{cue}/stop":
            {
                if (!Guid.TryParse(segments[3], out var cueId) || project.FindCue(cueId) is null)
                    return Error(404, $"no cue '{segments[3]}'");

                await _host.StopCueAsync(cueId).ConfigureAwait(false);
                return Ack(new RemoteAck(Stopped: cueId.ToString(), Ok: true));
            }

            case "/api/v1/cues/{cue}/standby":
            {
                if (!Guid.TryParse(segments[3], out var cueId)
                    || project.ListOf(cueId) is not { } owner)
                    return Error(404, $"no cue '{segments[3]}'");

                var moved = await _host.StandbyAsync(owner, cueId).ConfigureAwait(false);
                return Ack(new RemoteAck(
                    Standby: moved ? cueId.ToString() : null,
                    List: owner.Id.ToString(),
                    Ok: moved));
            }

            case "/api/v1/transport/stop":
                await _host.StopAllAsync().ConfigureAwait(false);
                return Ack(new RemoteAck(Ok: true));

            case "/api/v1/transport/panic":
                await _host.PanicAsync().ConfigureAwait(false);
                return Ack(new RemoteAck(Ok: true));

            case "/api/v1/transport/pause":
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
        if (_life is { } life)
        {
            await life.CancelAsync().ConfigureAwait(false);
            life.Dispose();
            _life = null;
        }

        _listener?.Close();
        _listener = null;
        Address = "";

        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
            {
            }

            _loop = null;
        }

        Task[] handlers;
        lock (_handlersGate)
            handlers = [.. _handlers];

        try
        {
            await Task.WhenAll(handlers).ConfigureAwait(false);
        }
        catch (Exception failure) when (
            failure is HttpListenerException or IOException or ObjectDisposedException)
        {
        }
    }
}
