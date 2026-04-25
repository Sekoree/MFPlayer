namespace S.Media.Core.Routing;

/// <summary>
/// Defines the priority tier for a clock registered with an <see cref="IAVRouter"/>.
/// When multiple clocks are registered, the router uses the highest-priority one
/// that is available. If two clocks share the same tier, the most recently registered
/// one wins. If the active clock is removed or stops, the router falls back to the
/// next-highest priority clock (ultimately the built-in internal clock).
/// </summary>
public enum ClockPriority
{
    /// <summary>
    /// Built-in software clock (StopwatchClock). Always present as the ultimate fallback.
    /// Users should not register clocks at this level.
    /// </summary>
    Internal = 0,

    /// <summary>
    /// Local hardware clocks: PortAudio output, SDL3 video output, virtual tick endpoint.
    /// Auto-assigned when a <see cref="S.Media.Core.Media.Endpoints.IClockCapableEndpoint"/>
    /// is registered as an endpoint.
    /// </summary>
    Hardware = 100,

    /// <summary>
    /// External / network clocks: NDI source clock, PTP/genlock, remote transport control.
    /// Use for clocks that originate from outside the local machine.
    /// </summary>
    External = 200,

    /// <summary>
    /// Manual override. A clock at this level always wins regardless of other registrations.
    /// Equivalent to the old <c>SetClock(clock)</c> behaviour.
    /// </summary>
    Override = 300,
}

