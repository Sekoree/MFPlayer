namespace S.Media.Core.Routing;

/// <summary>
/// Configuration options for an <see cref="IAVRouter"/> instance.
/// </summary>
public record AVRouterOptions
{
    /// <summary>
    /// Default frames-per-buffer hint applied to all endpoints that don't
    /// specify their own. 0 = let each endpoint decide. Default: 0.
    /// </summary>
    public int DefaultFramesPerBuffer { get; init; } = 0;

    /// <summary>
    /// Internal clock tick cadence when no override clock is set.
    /// Controls push-endpoint delivery rate and channel drain rate.
    /// Default: 10 ms (~100 Hz).
    /// </summary>
    public TimeSpan InternalTickCadence { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Default <see cref="ClockPriority"/> assigned to clocks auto-discovered from
    /// <see cref="S.Media.Core.Media.Endpoints.IClockCapableEndpoint"/> endpoints.
    /// Default: <see cref="ClockPriority.Hardware"/>.
    /// </summary>
    public ClockPriority DefaultEndpointClockPriority { get; init; } = ClockPriority.Hardware;
}

