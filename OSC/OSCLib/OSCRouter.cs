namespace OSCLib;

public sealed class OSCRouter
{
    private readonly object _gate = new();
    private readonly List<Route> _routes = [];

    public IDisposable Register(string addressPattern, OSCMessageHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addressPattern);
        ArgumentNullException.ThrowIfNull(handler);

        var route = new Route(addressPattern, handler);
        lock (_gate)
            _routes.Add(route);

        return new Unsubscriber(this, route);
    }

    public async ValueTask<int> DispatchAsync(OSCMessageContext context, CancellationToken cancellationToken)
    {
        Route[] snapshot;
        lock (_gate)
            snapshot = [.. _routes];

        var hits = 0;
        foreach (var route in snapshot)
        {
            if (!OSCAddressMatcher.IsMatch(route.AddressPattern, context.Message.Address))
                continue;

            hits++;
            await route.Handler(context, cancellationToken).ConfigureAwait(false);
        }

        return hits;
    }

    private void Unregister(Route route)
    {
        lock (_gate)
            _routes.Remove(route);
    }

    private sealed record Route(string AddressPattern, OSCMessageHandler Handler);

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

