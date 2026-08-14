namespace HaRemote;

/// <summary>One finished answer: status, body, content type, and an optional <c>Allow</c> header
/// (the 405 story). Apps map their own result types onto this at the adapter seam.</summary>
public readonly record struct ControlHttpResult(
    int Status,
    string Body,
    string ContentType = "application/json",
    string? Allow = null);

/// <summary>
/// One request as the app's dispatch sees it - method, path, parsed query, header access and the
/// remote endpoint - deliberately WITHOUT the listener context, so route/auth code is testable
/// with a hand-built instance and can never reach the socket.
/// </summary>
public sealed record ControlHttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    Func<string, string?> Header,
    string RemoteEndPoint);

/// <summary>What the accept loop does when every admission slot is taken.</summary>
public enum OverCapacityPolicy
{
    /// <summary>Answer 503 immediately and cheaply - no routing, no dispatch, no tracked handler.
    /// The right policy when callers are automation that retries (HaCue2's remote API).</summary>
    Refuse = 0,

    /// <summary>Pause the accept loop until a slot frees; the OS TCP backlog absorbs pending
    /// connections. The right policy for one-or-few trusted controllers (HaPlay's REST API).</summary>
    Backpressure = 1,
}

/// <summary>
/// Everything the host does NOT own an opinion about: dispatch, error shapes, auth (inside
/// <see cref="Dispatch"/>), and the failure-answer policies. Everything with a default is the
/// bound both apps already ran shows with.
/// </summary>
public sealed class ControlHttpHostOptions
{
    /// <summary>Resolves and carries out one request. Auth is this delegate's first job - the host
    /// never sees credentials. The token is a linked server-life + request-deadline token; a
    /// dispatch that observes it cancels at the deadline, one that does not is abandoned by the
    /// host's deadline answer and runs on to whatever end it was going to have.</summary>
    public required Func<ControlHttpRequest, CancellationToken, Task<ControlHttpResult>> Dispatch { get; init; }

    /// <summary>Shapes a host-generated refusal (503 over capacity, 431/414 oversize, shutdown)
    /// into the app's own error body, so remote clients see ONE error grammar.</summary>
    public required Func<int, string, ControlHttpResult> Error { get; init; }

    /// <summary>Answer for a dispatch that threw. Null (or a null return) aborts the connection
    /// instead - the policy for apps that must never leak internals to a remote caller.</summary>
    public Func<Exception, ControlHttpResult?>? DispatchFailure { get; init; }

    /// <summary>Answer for a dispatch that outlived <see cref="RequestDeadline"/>. Null (or a null
    /// return) aborts the connection instead.</summary>
    public Func<TimeSpan, ControlHttpResult?>? DeadlineExceeded { get; init; }

    /// <summary>When set, the host answers OPTIONS itself: 204 with this delegate's
    /// <c>Allow</c> value for the path.</summary>
    public Func<string, string>? OptionsAllow { get; init; }

    /// <summary>Raised (off the socket path's hot loop) when a request could not be served, so a
    /// host app can surface it to the operator.</summary>
    public Action<string>? Problem { get; init; }

    /// <summary>At most this many requests are in flight; excess follows <see cref="OverCapacity"/>.
    /// The default has run shows in both apps.</summary>
    public int MaxConcurrentRequests { get; init; } = 32;

    public TimeSpan RequestDeadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long shutdown waits for in-flight handlers before abandoning them. Unbounded,
    /// a single hung dispatch could hold the whole application's teardown hostage.</summary>
    public TimeSpan ShutdownDrainBudget { get; init; } = TimeSpan.FromSeconds(2);

    public OverCapacityPolicy OverCapacity { get; init; } = OverCapacityPolicy.Refuse;

    /// <summary>Logger category for the host's structured request/lifecycle logging.</summary>
    public string LogName { get; init; } = "HaRemote.ControlHttpHost";
}
