namespace S.Media.Core.Media.Endpoints;

using S.Media.Core.Routing;

/// <summary>
/// Optional capability: this endpoint can provide a clock.
/// Hardware audio outputs, video outputs, and virtual tick endpoints implement this.
///
/// <para>
/// <b>Lifetime contract:</b> implementations MUST make <see cref="Clock"/> return a valid
/// <see cref="IMediaClock"/> from the moment the endpoint instance can be handed to the
/// router (i.e. immediately after construction / a <c>Create(...)</c> factory call —
/// <b>not</b> only after <c>StartAsync</c>). The router reads <see cref="Clock"/> eagerly
/// during <c>RegisterEndpoint</c> to auto-register it at
/// <see cref="DefaultPriority"/> (<see cref="ClockPriority.Hardware"/> by default).
/// </para>
///
/// <para>
/// The endpoint's clock is just one entry in the router's priority-ranked registry; it
/// can be outranked per-session by <c>AVRouter.RegisterClock(otherClock, External)</c> or
/// <c>AVRouter.SetClock(otherClock)</c> (<c>Override</c>). When the higher-priority entry
/// is removed the resolver falls back to this endpoint's clock automatically — no
/// re-plumbing required.
/// </para>
/// </summary>
public interface IClockCapableEndpoint
{
    IMediaClock Clock { get; }

    /// <summary>
    /// The priority at which <see cref="Clock"/> is auto-registered by
    /// <c>AVRouter.RegisterEndpoint</c>. Defaults to <see cref="ClockPriority.Hardware"/>
    /// for local hardware outputs. Network / receive-side endpoints (NDI receive, PTP)
    /// should override this to <see cref="ClockPriority.External"/>. Virtual / stopwatch
    /// endpoints should override to <see cref="ClockPriority.Internal"/>.
    /// Implements review item §4.8 / R11.
    /// </summary>
    ClockPriority DefaultPriority => ClockPriority.Hardware;
}
