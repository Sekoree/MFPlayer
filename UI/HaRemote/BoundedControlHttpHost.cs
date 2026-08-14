using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaRemote;

/// <summary>
/// The one bounded <see cref="HttpListener"/> lifecycle both control APIs run on (F-05): admission
/// limits, request-size guards, per-request deadlines, server-life cancellation, bounded shutdown
/// drain, and socket-error discipline. Built on <see cref="HttpListener"/> deliberately - the apps
/// publish NativeAOT and a web framework would be a dependency with opinions.
/// </summary>
/// <remarks>
/// <para>
/// Everything app-shaped comes in through <see cref="ControlHttpHostOptions"/>: routes and auth
/// live inside <see cref="ControlHttpHostOptions.Dispatch"/>, error bodies come from
/// <see cref="ControlHttpHostOptions.Error"/>, and the two failure-answer policies (deadline,
/// dispatch throw) choose between an app-shaped answer and an aborted connection.
/// </para>
/// <para>
/// Shutdown is BOUNDED: cancel the life token, close the listener, wait for the accept loop, then
/// give in-flight handlers <see cref="ControlHttpHostOptions.ShutdownDrainBudget"/> before
/// abandoning them. The life CTS is disposed only after every handler actually finishes (in the
/// background when the drain times out), because a handler may still be linking its per-request
/// token when the drain gives up - disposing under it would throw where nothing can catch usefully.
/// </para>
/// </remarks>
public sealed class BoundedControlHttpHost : IAsyncDisposable
{
    // Request-size guards, applied before auth or dispatch so a malformed or hostile request is
    // rejected cheaply. The figures are HaPlay's API-03 bounds, which have run shows.
    private const int MaxHeaderCount = 100;
    private const int MaxHeaderValueLength = 8 * 1024;
    private const int MaxQueryLength = 4 * 1024;

    private readonly ControlHttpHostOptions _options;
    private readonly ILogger _log;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _admission;
    private readonly object _handlersGate = new();
    private readonly HashSet<Task> _handlers = [];
    private readonly Task _acceptLoop;
    private Task? _fullDrain;
    private bool _disposed;

    private BoundedControlHttpHost(HttpListener listener, string boundPrefix, ControlHttpHostOptions options)
    {
        _listener = listener;
        _options = options;
        _log = MediaDiagnostics.CreateLogger(options.LogName);
        _admission = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
        BoundPrefix = boundPrefix;
        _acceptLoop = Task.Run(() => AcceptAsync(_life.Token));
    }

    /// <summary>The prefix that actually bound - callers with a fallback list read this to learn
    /// whether they got their first choice.</summary>
    public string BoundPrefix { get; }

    public bool IsRunning => !_disposed && _listener.IsListening;

    /// <summary>
    /// Completes when every handler admitted before disposal has ACTUALLY finished - never faults.
    /// <see cref="DisposeAsync"/> is deliberately bounded and may abandon stragglers; a test that
    /// must prove full quiescence (e.g. that no surviving handler can touch a UI dispatcher after
    /// Stop) awaits this after disposing. Completed before disposal begins.
    /// </summary>
    public Task WhenFullyDrained => _fullDrain ?? Task.CompletedTask;

    /// <summary>
    /// Binds the first prefix in <paramref name="prefixes"/> that the OS accepts and starts
    /// serving. Null (with <paramref name="bindError"/>) when none bound - a control API failing to
    /// listen is reported and survived by both apps, never thrown through startup.
    /// </summary>
    public static BoundedControlHttpHost? TryStart(
        IReadOnlyList<string> prefixes, ControlHttpHostOptions options, out string? bindError)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (prefixes.Count == 0)
            throw new ArgumentException("at least one prefix is required", nameof(prefixes));

        bindError = null;
        foreach (var prefix in prefixes)
        {
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add(prefix);
                listener.Start();
                return new BoundedControlHttpHost(listener, prefix, options);
            }
            catch (Exception failure) when (
                failure is HttpListenerException or ObjectDisposedException or ArgumentException)
            {
                try { listener.Close(); }
                catch (ObjectDisposedException) { }
                bindError ??= failure.Message;
            }
        }

        return null;
    }

    private async Task AcceptAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (life.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (Exception failure)
            {
                _log.LogWarning(failure, "control HTTP host: accept failed");
                continue;
            }

            // Admission BEFORE the handler task exists: whichever policy, a flood (authorized or
            // not - credentials are only checked inside the dispatch) can never pile up unbounded
            // work.
            if (_options.OverCapacity == OverCapacityPolicy.Refuse)
            {
                if (!_admission.Wait(0))
                {
                    _ = RefuseBusyAsync(context);
                    continue;
                }
            }
            else
            {
                try
                {
                    await _admission.WaitAsync(life).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryAbort(context.Response);
                    return;
                }
            }

            // A slow request must not stop acceptance, but handlers remain tracked so shutdown can
            // wait (boundedly) for every response that already entered the server.
            var handler = Task.Run(() => ServeAdmittedAsync(context, life), CancellationToken.None);
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

    private async Task ServeAdmittedAsync(HttpListenerContext context, CancellationToken serverLife)
    {
        try
        {
            await ServeAsync(context, serverLife).ConfigureAwait(false);
        }
        finally
        {
            _admission.Release();
        }
    }

    /// <summary>The over-capacity answer, kept deliberately cheap: no routing, no dispatch, no
    /// tracked handler - just a 503 and the connection back.</summary>
    private async Task RefuseBusyAsync(HttpListenerContext context)
    {
        try
        {
            await WriteAsync(context.Response,
                    _options.Error(503, "the API is at its concurrent-request limit - retry shortly"))
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (
            failure is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            TryClose(context.Response);
        }
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken serverLife)
    {
        var started = Stopwatch.GetTimestamp();
        var request = context.Request;
        var response = context.Response;
        var method = request.HttpMethod;
        var path = request.Url?.AbsolutePath ?? "/";
        var remote = request.RemoteEndPoint?.ToString() ?? "<unknown>";
        var statusCode = 500;

        try
        {
            _log.LogTrace("request started method={Method} path={Path} remote={Remote}", method, path, remote);

            if (_options.OptionsAllow is { } allowFor && method == "OPTIONS")
            {
                statusCode = 204;
                response.StatusCode = statusCode;
                response.Headers["Allow"] = allowFor(path);
                TryClose(response);
                return;
            }

            if (TryRejectOversized(request, out var limitStatus, out var limitMessage))
            {
                statusCode = limitStatus;
                await WriteAsync(response, _options.Error(limitStatus, limitMessage)).ConfigureAwait(false);
                return;
            }

            var query = ParseQuery(request);
            var httpRequest = new ControlHttpRequest(
                method, path, query, name => request.Headers[name], remote);

            ControlHttpResult result;
            // The linked token cancels a deadline-observing dispatch at the deadline; WaitAsync
            // bounds one that never looks. Either way the CALLER gets an answer (or a clean abort,
            // per the app's policy) instead of a connection held open indefinitely.
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(serverLife);
            requestCts.CancelAfter(_options.RequestDeadline);
            try
            {
                result = await _options.Dispatch(httpRequest, requestCts.Token)
                    .WaitAsync(_options.RequestDeadline, serverLife).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                statusCode = 503;
                if (_options.DeadlineExceeded?.Invoke(_options.RequestDeadline) is not { } deadlineAnswer)
                {
                    _log.LogWarning(
                        "request {Method} {Path} from {Remote} exceeded the {Timeout}s deadline",
                        method, path, remote, _options.RequestDeadline.TotalSeconds);
                    TryAbort(response);
                    return;
                }

                result = deadlineAnswer;
            }
            catch (OperationCanceledException) when (serverLife.IsCancellationRequested)
            {
                statusCode = 503;
                result = _options.Error(503, "the API is shutting down");
            }
            catch (OperationCanceledException)
            {
                // The linked per-request token fired (a deadline-observing dispatch cancelled
                // itself) - same answer as the host-side deadline.
                statusCode = 503;
                if (_options.DeadlineExceeded?.Invoke(_options.RequestDeadline) is not { } deadlineAnswer)
                {
                    TryAbort(response);
                    return;
                }

                result = deadlineAnswer;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                statusCode = 500;
                _options.Problem?.Invoke($"remote request failed - {failure.Message}");
                if (_options.DispatchFailure?.Invoke(failure) is not { } failureAnswer)
                {
                    _log.LogWarning(failure, "request {Method} {Path} from {Remote} failed", method, path, remote);
                    TryAbort(response);
                    return;
                }

                result = failureAnswer;
            }

            statusCode = result.Status;
            await WriteAsync(response, result).ConfigureAwait(false);
        }
        catch (Exception failure) when (
            failure is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The caller hung up (or the listener is gone). Nothing to report and nothing to do.
        }
        finally
        {
            TryClose(response);
            var elapsedMs = MediaDiagnostics.ElapsedMillisecondsSince(started);
            var level = elapsedMs >= 250 || statusCode >= 500 ? LogLevel.Warning : LogLevel.Debug;
            if (_log.IsEnabled(level))
            {
                _log.Log(level,
                    "request completed method={Method} path={Path} remote={Remote} status={Status} elapsedMs={ElapsedMs:0.00}",
                    method, path, remote, statusCode, elapsedMs);
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(HttpListenerRequest request)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var raw = request.QueryString;
        foreach (var key in raw.AllKeys)
        {
            if (key is not null && raw[key] is { } value)
                query[key] = value;
        }

        return query;
    }

    /// <summary>Cheap header/query-size guard. Returns true (with a status/message) when the
    /// request should be refused before any routing or auth work.</summary>
    private static bool TryRejectOversized(HttpListenerRequest request, out int status, out string message)
    {
        var headers = request.Headers;
        if (headers.Count > MaxHeaderCount)
        {
            status = 431; // Request Header Fields Too Large
            message = "too many request headers";
            return true;
        }

        foreach (var key in headers.AllKeys)
        {
            if (key is null)
                continue;
            if (headers[key] is { } value && value.Length > MaxHeaderValueLength)
            {
                status = 431;
                message = "request header value too large";
                return true;
            }
        }

        if ((request.Url?.Query.Length ?? 0) > MaxQueryLength)
        {
            status = 414; // URI Too Long
            message = "query string too long";
            return true;
        }

        status = 0;
        message = string.Empty;
        return false;
    }

    private static async Task WriteAsync(HttpListenerResponse response, ControlHttpResult result)
    {
        var payload = Encoding.UTF8.GetBytes(result.Body);
        response.StatusCode = result.Status;
        response.ContentType = result.ContentType;
        if (result.Allow is { } allow)
            response.Headers["Allow"] = allow;
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    private static void TryClose(HttpListenerResponse response)
    {
        try { response.Close(); }
        catch (Exception failure) when (
            failure is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static void TryAbort(HttpListenerResponse response)
    {
        try { response.Abort(); }
        catch (Exception failure) when (
            failure is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _life.CancelAsync().ConfigureAwait(false);
        try { _listener.Close(); }
        catch (ObjectDisposedException) { }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
        {
        }

        // No handler can be added after the accept loop exits, so this snapshot is final.
        Task[] handlers;
        lock (_handlersGate)
            handlers = [.. _handlers];

        var allHandlers = Task.WhenAll(handlers);
        _fullDrain = SwallowAsync(allHandlers);
        try
        {
            // BOUNDED: in-flight responses get a short grace, then the shutdown proceeds without
            // them. An unbounded join here once let one hung dispatch hold an application's whole
            // teardown hostage - and the per-request deadline normally ends the handler well before
            // this budget matters.
            await allHandlers.WaitAsync(_options.ShutdownDrainBudget).ConfigureAwait(false);
            _life.Dispose();
        }
        catch (TimeoutException)
        {
            _log.LogWarning(
                "{Count} in-flight request(s) did not finish within the shutdown drain budget and were abandoned",
                handlers.Count(h => !h.IsCompleted));
            // The life CTS stays alive until the stragglers actually end: one may still be linking
            // its per-request token, and disposing underneath it throws where nothing can catch.
            _ = DisposeLifeAfterAsync(allHandlers);
        }
        catch (Exception)
        {
            // Handler faults were already answered/logged per request.
            _life.Dispose();
        }
    }

    private async Task DisposeLifeAfterAsync(Task handlers)
    {
        try { await handlers.ConfigureAwait(false); }
        catch { /* individual handlers already answered/logged their failures */ }
        finally { _life.Dispose(); }
    }

    private static async Task SwallowAsync(Task handlers)
    {
        try { await handlers.ConfigureAwait(false); }
        catch { /* individual handlers already answered/logged their failures */ }
    }

    /// <summary>Best host to advertise in copy-paste URLs: the first up, non-loopback IPv4.</summary>
    public static string ResolveAdvertisedHost(bool preferLan = true)
    {
        if (!preferLan)
            return "localhost";

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch (Exception failure) when (failure is NetworkInformationException or PlatformNotSupportedException)
        {
        }

        return "localhost";
    }

    /// <summary>Constant-time string comparison for access tokens, shared so neither app's check
    /// can quietly regress to an early-out compare.</summary>
    public static bool FixedTimeEquals(string? candidate, string? expected)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(expected))
            return false;

        var max = Math.Max(candidate.Length, expected.Length);
        var diff = candidate.Length ^ expected.Length;
        for (var i = 0; i < max; i++)
        {
            var a = i < candidate.Length ? candidate[i] : 0;
            var b = i < expected.Length ? expected[i] : 0;
            diff |= a ^ b;
        }

        return diff == 0;
    }
}
