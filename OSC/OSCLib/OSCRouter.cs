namespace OSCLib;

/// <summary>
/// Routes dispatched OSC messages to registered address-pattern handlers.
/// </summary>
/// <remarks>
/// <para>
/// Instances are owned by <see cref="OSCServer"/>. Route registration compiles the address
/// pattern into a reusable matcher (zero allocation on the hot dispatch path). The route table
/// is stored as a volatile immutable array; dispatch is therefore lock-free.
/// </para>
/// </remarks>
internal sealed class OSCRouter
{
    private readonly Lock _gate = new();
    private readonly int _maxRoutes;
    private volatile Route[] _routes = [];

    public OSCRouter(int maxRoutes = 1024)
    {
        _maxRoutes = maxRoutes;
    }

    public IDisposable Register(string addressPattern, OSCMessageHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressPattern);
        ArgumentNullException.ThrowIfNull(handler);

        var route = new Route(addressPattern, OSCAddressMatcher.Compile(addressPattern), handler);

        lock (_gate)
        {
            if (_routes.Length >= _maxRoutes)
                throw new InvalidOperationException(
                    $"OSCRouter route limit ({_maxRoutes}) reached.");

            _routes = [.. _routes, route];
        }

        return new Unsubscriber(this, route);
    }

    public async ValueTask<int> DispatchAsync(OSCMessageContext context, CancellationToken cancellationToken)
    {
        // Single volatile read — no lock required, route table is replaced atomically.
        var routes = _routes;
        var hits = 0;

        foreach (var route in routes)
        {
            if (!route.Matcher(context.Message.Address))
                continue;

            hits++;
            await route.Handler(context, cancellationToken).ConfigureAwait(false);
        }

        return hits;
    }

    private void Unregister(Route route)
    {
        lock (_gate)
            _routes = _routes.Where(r => !ReferenceEquals(r, route)).ToArray();
    }

    private sealed record Route(string AddressPattern, Func<string, bool> Matcher, OSCMessageHandler Handler);

    private sealed class Unsubscriber : IDisposable
    {
        private OSCRouter? _router;
        private readonly Route _route;

        public Unsubscriber(OSCRouter router, Route route)
        {
            _router = router;
            _route = route;
        }

        public void Dispose()
        {
            var router = Interlocked.Exchange(ref _router, null);
            router?.Unregister(_route);
        }
    }
}
