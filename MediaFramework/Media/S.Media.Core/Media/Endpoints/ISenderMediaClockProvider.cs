namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// A video or AV sink (e.g. an NDI sender) that drives a <see cref="S.Media.Core.IMediaClock"/>
/// in the same time domain as stream PTS stamped onto outgoing frames. When
/// <see cref="S.Media.Core.Routing.IAVRouter.Clock"/> is the same object as this
/// <see cref="SenderMediaClock"/>, push-side video scheduling should treat the
/// stream as "live" for that route (bypass the generic PTS↔wall drift gate) so the
/// clock and the gate are not in a fight.
/// </summary>
public interface ISenderMediaClockProvider
{
    /// <summary>Clock advanced from the sender's A/V time domain (e.g. <c>NDIClock</c>).</summary>
    IMediaClock SenderMediaClock { get; }
}
