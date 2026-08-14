using HaRemote;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Remote;

/// <summary>
/// Minimal HTTP front-end for <see cref="RemoteApiDispatcher"/>. Binds loopback by default; LAN
/// binding is an explicit operator choice. The access token is <strong>optional</strong>: this
/// control surface targets closed-LAN automation (e.g. Bitfocus Companion), so when no token is
/// configured every request is allowed; set a token to require it (compared in constant time).
/// Status is read with GET; commands require POST.
/// </summary>
/// <remarks>
/// The TRANSPORT (listener lifecycle, API-03 bounds: 32-request admission, 30 s deadlines,
/// size guards, 2 s shutdown drain) is the shared <see cref="BoundedControlHttpHost"/> (F-05) -
/// the same mechanics HaCue2's remote API runs on, so the two can no longer drift. What stays
/// here is HaPlay policy: the optional token and its three credential locations, the wildcard →
/// loopback bind fallback, backpressure over 503-refusal (a controller is one or a few trusted
/// clients), and abort-not-answer on deadline/dispatch failure so remote callers never see
/// internals.
/// </remarks>
public sealed class RestApiServer : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Remote.RestApiServer");

    private BoundedControlHttpHost? _host;

    /// <summary>Human-facing base URL (LAN address when available) - what Copy-API-URL uses.</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>Non-null when the last <see cref="Start"/> failed or degraded (e.g. loopback fallback).</summary>
    public string? StatusNote { get; private set; }

    public bool IsRunning => _host is not null;

    /// <summary>Starts (or restarts) the listener. Returns false when no prefix could be bound.
    /// A null/empty <paramref name="accessToken"/> means no authentication is required (optional token).</summary>
    public bool Start(
        int port,
        RemoteApiDispatcher dispatcher,
        string? accessToken,
        bool bindAllInterfaces = false)
    {
        Stop();
        StatusNote = null;

        var options = new ControlHttpHostOptions
        {
            Dispatch = (request, token) => DispatchAuthorizedAsync(dispatcher, accessToken, request, token),
            Error = (status, message) => ToHostResult(RemoteApiResult.Fail(status, message)),
            OptionsAllow = RemoteApiDispatcher.AllowedMethodsFor,
            // Abort, never answer: a hung or failed dispatch tells a remote caller nothing about
            // internals (both delegates default to abort when absent).
            DeadlineExceeded = null,
            DispatchFailure = null,
            OverCapacity = OverCapacityPolicy.Backpressure,
            LogName = "HaPlay.Remote.RestApiServer",
        };

        // Windows refuses wildcard prefixes without an ACL (netsh http add urlacl) - degrade to
        // loopback so the API still works locally rather than not at all.
        string[] prefixes = bindAllInterfaces
            ? [$"http://*:{port}/", $"http://localhost:{port}/"]
            : [$"http://localhost:{port}/"];

        var host = BoundedControlHttpHost.TryStart(prefixes, options, out var bindError);
        if (host is null)
        {
            StatusNote = bindError;
            Trace.LogWarning("RestApiServer: could not bind port {Port}: {Error}", port, StatusNote);
            return false;
        }

        if (bindAllInterfaces && host.BoundPrefix.Contains("localhost", StringComparison.Ordinal))
            StatusNote = "Bound to localhost only (wildcard binding was refused; on Windows add a URL ACL for LAN access).";

        _host = host;
        var lan = bindAllInterfaces && StatusNote is null;
        BaseUrl = $"http://{ResolveAdvertisedHost(lan)}:{port}";
        Trace.LogInformation(
            "RestApiServer: listening on port {Port} (advertised {BaseUrl}, lan={Lan})", port, BaseUrl, lan);
        return true;
    }

    // Fire-and-forget: Stop is called by UI-thread settings hooks, and the host's disposal is
    // internally BOUNDED (accept loop join + drain budget) - synchronously waiting here deadlocked
    // once with a handler queued to Dispatcher.UIThread (API-03).
    public void Stop() => _ = StopAndDrainAsync();

    /// <summary>Test seam: stop and hand back the host's bounded drain, so a listener test can pump
    /// the UI dispatcher until every in-flight handler has actually left (a surviving handler
    /// rebinding <c>Dispatcher.UIThread</c> is how the old cross-test flake started).</summary>
    internal Task StopAndDrainAsync()
    {
        var drain = Task.CompletedTask;
        if (_host is { } host)
        {
            _host = null;
            drain = DrainFullyAsync(host);
        }

        BaseUrl = null;
        return drain;

        // The bounded DisposeAsync may abandon a straggler; the full-drain await behind it is what
        // proves no surviving handler can rebind the UI dispatcher after a test's Stop.
        static async Task DrainFullyAsync(BoundedControlHttpHost host)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            await host.WhenFullyDrained.ConfigureAwait(false);
        }
    }

    public void Dispose() => Stop();

    private static async Task<ControlHttpResult> DispatchAuthorizedAsync(
        RemoteApiDispatcher dispatcher,
        string? accessToken,
        ControlHttpRequest request,
        CancellationToken requestToken)
    {
        if (!IsAuthorized(request, accessToken))
            return ToHostResult(RemoteApiResult.Fail(401, "Remote API token required."));

        var result = await dispatcher
            .ExecuteAsync(request.Method, request.Path, request.Query, requestToken)
            .ConfigureAwait(false);
        return ToHostResult(result);
    }

    private static ControlHttpResult ToHostResult(RemoteApiResult result) =>
        new(result.Status, result.Body, Allow: result.Allow);

    private static bool IsAuthorized(ControlHttpRequest request, string? accessToken)
    {
        // Optional token: no token configured ⇒ no auth required (closed-LAN automation).
        if (string.IsNullOrEmpty(accessToken))
            return true;

        if (request.Query.TryGetValue("key", out var key)
            && BoundedControlHttpHost.FixedTimeEquals(key, accessToken))
            return true;
        if (request.Query.TryGetValue("token", out var token)
            && BoundedControlHttpHost.FixedTimeEquals(token, accessToken))
            return true;

        var header = request.Header("Authorization");
        if (header is not null
            && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && BoundedControlHttpHost.FixedTimeEquals(header["Bearer ".Length..].Trim(), accessToken))
            return true;

        return BoundedControlHttpHost.FixedTimeEquals(request.Header("X-HaPlay-Api-Key"), accessToken);
    }

    /// <summary>Best host to advertise in copy-paste URLs: the first up, non-loopback IPv4.</summary>
    internal static string ResolveAdvertisedHost(bool preferLan = true) =>
        BoundedControlHttpHost.ResolveAdvertisedHost(preferLan);
}
