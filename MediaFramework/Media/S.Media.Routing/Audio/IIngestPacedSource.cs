namespace S.Media.Routing;

/// <summary>
/// Implemented by a live source (today: an NDI receiver adapter) that owns an ingest timeline and has been
/// <strong>explicitly configured</strong> to pace playback from it - i.e. the host asked for genlock-to-ingest
/// on that source's descriptor/options. A player wires <see cref="AudioRouter.SlaveToIngest"/> to
/// <see cref="IngestPacingClock"/> when it is non-null; every source that was not opted in returns
/// <see langword="null"/> and the router keeps its default wall clock.
/// </summary>
/// <remarks>
/// <para>
/// The interface is the opt-in seam, not a capability probe: sources that merely <em>have</em> an ingest
/// clock (every NDI receiver does) must keep returning <see langword="null"/> unless configured, so ingest
/// pacing is never auto-promoted from the clock's mere existence.
/// </para>
/// <para>
/// Consequence of opting in: production stops advancing whenever the ingest timeline stops (a disconnected
/// or silent sender), because <c>PlaybackSlavedRouterClock</c> waits for ingest media time rather than wall
/// time. That is the point of genlock-to-ingest, but it means the router emits nothing (instead of silence)
/// while the sender is away - hosts that need a continuous stream should leave the opt-in off.
/// </para>
/// </remarks>
public interface IIngestPacedSource
{
    /// <summary>The ingest master to pace the router from, or <see langword="null"/> when this source is not
    /// configured for ingest pacing (the default for every source).</summary>
    IPlaybackClock? IngestPacingClock { get; }
}
