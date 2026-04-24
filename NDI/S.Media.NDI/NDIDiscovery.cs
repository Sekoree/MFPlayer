using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NDILib;

namespace S.Media.NDI;

/// <summary>
/// §4.18 / NDI §Required #3 — process-wide NDI discovery registry. Maintains a
/// single shared <see cref="NDIFinder"/> and a watch thread that polls for
/// added/removed sources, so multiple <see cref="NDISource"/> consumers do
/// not each spawn their own mDNS thread. Consumers subscribe to
/// <see cref="Discovered"/> and <see cref="Lost"/> events or read the
/// <see cref="CurrentSources"/> snapshot. Callers are also the lifecycle
/// owners — <see cref="AddRef"/> starts the finder if not already running
/// and <see cref="Release"/> stops it when the last reference drops.
///
/// <para>
/// The registry is lazy-started: the first <see cref="AddRef"/>,
/// <see cref="CurrentSources"/> read, or event subscription brings the
/// finder online. Callers that want deterministic shutdown should pair
/// an <see cref="AddRef"/> with a matching <see cref="Release"/> around
/// their lifetime; event subscribers can <see cref="Shutdown"/> at app
/// exit instead.
/// </para>
///
/// <para>
/// This class is opt-in — the legacy <see cref="NDISource.OpenByNameAsync"/>
/// flow still creates its own finder for source-compatibility. New code
/// that needs multi-source mixing (§7.4) or live discovery UIs should use
/// this registry.
/// </para>
/// </summary>
public static class NDIDiscovery
{
    private static readonly ILogger Log = NDIMediaLogging.GetLogger(nameof(NDIDiscovery));

    private static readonly Lock _gate = new();
    private static NDIFinder? _finder;
    private static int _refCount;
    private static Thread? _watchThread;
    private static CancellationTokenSource? _watchCts;

    // Published snapshot. Swapped wholesale via Interlocked.Exchange so readers
    // never see a torn collection.
    private static ImmutableArray<NDIDiscoveredSource> _snapshot = ImmutableArray<NDIDiscoveredSource>.Empty;

    /// <summary>
    /// Poll interval for the discovery watch thread. The underlying NDI
    /// finder also exposes a blocking-wait API; we poll with a short
    /// timeout so Shutdown is responsive.
    /// </summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Current snapshot of discovered NDI sources. Empty while the finder is stopped.</summary>
    public static IReadOnlyList<NDIDiscoveredSource> CurrentSources
    {
        get
        {
            EnsureStarted();
            return _snapshot;
        }
    }

    /// <summary>Raised once per newly-discovered source.</summary>
    public static event EventHandler<NDIDiscoveredSourceEventArgs>? Discovered
    {
        add    { lock (_gate) { _discoveredHandlers += value; } EnsureStartedNoLock(); }
        remove { lock (_gate) { _discoveredHandlers -= value; } }
    }

    /// <summary>Raised once per source that disappears from the discovery snapshot.</summary>
    public static event EventHandler<NDIDiscoveredSourceEventArgs>? Lost
    {
        add    { lock (_gate) { _lostHandlers += value; } EnsureStartedNoLock(); }
        remove { lock (_gate) { _lostHandlers -= value; } }
    }

    private static EventHandler<NDIDiscoveredSourceEventArgs>? _discoveredHandlers;
    private static EventHandler<NDIDiscoveredSourceEventArgs>? _lostHandlers;

    /// <summary>
    /// Increments the lifecycle refcount. Starts the finder + watch thread
    /// on the first call. Callers should pair with exactly one
    /// <see cref="Release"/>.
    /// </summary>
    public static void AddRef()
    {
        lock (_gate)
        {
            _refCount++;
            EnsureStartedNoLock();
        }
    }

    /// <summary>
    /// Decrements the lifecycle refcount. Stops the finder + watch thread
    /// when the last reference drops. No-op if the registry is already
    /// stopped (e.g. by an explicit <see cref="Shutdown"/>).
    /// </summary>
    public static void Release()
    {
        lock (_gate)
        {
            if (_refCount > 0) _refCount--;
            if (_refCount == 0) StopNoLock();
        }
    }

    /// <summary>
    /// Forces the registry to stop regardless of refcount. Typically called
    /// at app shutdown. Subsequent <see cref="AddRef"/> or event
    /// subscription calls will lazy-start it again.
    /// </summary>
    public static void Shutdown()
    {
        lock (_gate)
        {
            _refCount = 0;
            StopNoLock();
        }
    }

    /// <summary>
    /// Waits for a source whose name contains <paramref name="namePattern"/>
    /// to appear in the discovery snapshot. The registry is reference-counted
    /// for the duration of the wait so callers don't need to
    /// <see cref="AddRef"/> beforehand.
    /// </summary>
    /// <param name="namePattern">Case-insensitive substring match on source name.</param>
    /// <param name="timeout">Maximum time to wait. <see cref="TimeSpan.Zero"/> or negative = infinite.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matched source, or <see langword="null"/> on timeout.</returns>
    public static async Task<NDIDiscoveredSource?> WaitForAsync(string namePattern, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(namePattern);
        AddRef();
        try
        {
            DateTime deadline = timeout > TimeSpan.Zero ? DateTime.UtcNow + timeout : DateTime.MaxValue;
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                foreach (var src in _snapshot)
                    if (src.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                        return src;
                try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            return null;
        }
        finally { Release(); }
    }

    private static void EnsureStarted()
    {
        lock (_gate) EnsureStartedNoLock();
    }

    private static void EnsureStartedNoLock()
    {
        if (_finder is not null) return;

        int rc = NDIFinder.Create(out var finder);
        if (rc != 0 || finder is null)
        {
            Log.LogWarning("NDIDiscovery: NDIFinder.Create returned {Rc}; discovery disabled", rc);
            return;
        }
        _finder = finder;
        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        _watchThread = new Thread(() => WatchLoop(ct))
        {
            Name = "NDIDiscovery.Watch",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _watchThread.Start();
        Log.LogInformation("NDIDiscovery started");
    }

    private static void StopNoLock()
    {
        if (_finder is null) return;
        Log.LogInformation("NDIDiscovery stopping");
        try { _watchCts?.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        _watchThread?.Join(TimeSpan.FromSeconds(2));
        _watchCts?.Dispose();
        _watchCts = null;
        _watchThread = null;

        try { _finder.Dispose(); } catch { /* best-effort */ }
        _finder = null;
        _snapshot = ImmutableArray<NDIDiscoveredSource>.Empty;
    }

    private static void WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var finder = _finder;
                if (finder is null) return;

                var fresh = finder.GetCurrentSources();
                var previous = _snapshot;
                var freshArr = ImmutableArray.CreateRange(fresh);

                // Detect changes via name-based set membership. Names are the
                // only stable identifier — URL addresses change as the source
                // moves between interfaces.
                var previousNames = new HashSet<string>(previous.Select(s => s.Name), StringComparer.Ordinal);
                var freshNames    = new HashSet<string>(freshArr.Select(s => s.Name), StringComparer.Ordinal);

                foreach (var src in freshArr)
                    if (!previousNames.Contains(src.Name))
                        RaiseSafe(_discoveredHandlers, src);

                foreach (var src in previous)
                    if (!freshNames.Contains(src.Name))
                        RaiseSafe(_lostHandlers, src);

                _snapshot = freshArr;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.LogWarning(ex, "NDIDiscovery watch-loop iteration failed");
            }

            if (ct.WaitHandle.WaitOne(PollInterval))
                break;
        }
    }

    private static void RaiseSafe(EventHandler<NDIDiscoveredSourceEventArgs>? handler, NDIDiscoveredSource src)
    {
        if (handler is null) return;
        try { handler(null, new NDIDiscoveredSourceEventArgs(src)); }
        catch (Exception ex) { Log.LogWarning(ex, "NDIDiscovery handler threw"); }
    }
}

/// <summary>Event payload for <see cref="NDIDiscovery.Discovered"/> / <see cref="NDIDiscovery.Lost"/>.</summary>
public sealed class NDIDiscoveredSourceEventArgs : EventArgs
{
    public NDIDiscoveredSource Source { get; }
    public NDIDiscoveredSourceEventArgs(NDIDiscoveredSource source) => Source = source;
}