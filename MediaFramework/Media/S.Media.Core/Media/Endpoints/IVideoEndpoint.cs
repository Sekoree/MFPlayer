using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Receives video frames from the graph. This is the <b>single unified video endpoint
/// contract</b>: there is no separate "output" or "sink" interface. Replaces the legacy
/// <c>IVideoOutput</c>, <c>IVideoSink</c>, and <c>IVideoFrameEndpoint</c> types with
/// one push-based surface.
///
/// <para>
/// Endpoints that are driven by their own render loop (SDL3, Avalonia OpenGL) should
/// additionally implement <see cref="IPullVideoEndpoint"/> — it is an opt-in capability
/// mixin, not a separate kind of endpoint. The router's
/// <c>RegisterEndpoint(IVideoEndpoint)</c> handles both cases via a runtime capability
/// check.
/// </para>
///
/// <para>
/// Endpoints that can provide a presentation clock should additionally implement
/// <see cref="IClockCapableEndpoint"/>; the router auto-registers it at
/// <c>ClockPriority.Hardware</c>.
/// </para>
/// </summary>
public interface IVideoEndpoint : IMediaEndpoint
{
    /// <summary>
    /// Delivers one video frame from the graph. Implementations MUST be non-blocking.
    /// </summary>
    /// <remarks>
    /// <b>Ownership:</b> the router holds one refcount for the duration of this call.
    /// Endpoints that need the pixel data past the call must call
    /// <see cref="VideoFrameHandle.Retain"/> before returning and exactly one matching
    /// <see cref="VideoFrameHandle.Release"/> when they are done. If the frame is not
    /// ref-counted (<see cref="VideoFrameHandle.IsRefCounted"/> is <see langword="false"/>),
    /// implementations must copy the bytes during this call if they need them later.
    /// </remarks>
    void ReceiveFrame(in VideoFrameHandle handle);

    /// <summary>Optional endpoint diagnostics snapshot.</summary>
    VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => VideoEndpointDiagnosticsSnapshot.Empty;

    /// <summary>
    /// §5.5 — preferred router push cadence for this endpoint (typically one
    /// frame-time: 16.7 ms @ 60 fps, 40 ms @ 25 fps, etc.). See
    /// <see cref="IAudioEndpoint.NominalTickCadence"/> for the full
    /// semantics; the router picks the minimum across all endpoints.
    /// <see langword="null"/> (default) means "no preference".
    /// </summary>
    TimeSpan? NominalTickCadence => null;
}
