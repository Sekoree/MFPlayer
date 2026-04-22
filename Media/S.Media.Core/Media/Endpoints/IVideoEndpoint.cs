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
    /// Called by the graph to deliver a video frame.
    /// Implementations MUST be non-blocking.
    /// </summary>
    /// <remarks>
    /// <b>Ownership:</b> the caller (router) owns <paramref name="frame"/>.<c>MemoryOwner</c>.
    /// Implementations MUST NOT dispose it.  If the endpoint needs to retain the pixel data
    /// past the call (e.g. to render on a later tick), it MUST copy the bytes into its own
    /// buffer.  See <see cref="VideoFrame"/> docs for the full contract.
    ///
    /// <para>
    /// <b>Experimental — ref-counted ownership is planned:</b> a future
    /// <c>VideoFrameHandle</c> will replace the "router disposes after the call" dance with
    /// explicit per-endpoint <c>Retain()</c>/<c>Release()</c> so fan-out to N endpoints
    /// shares a single buffer. Endpoints that copy today will continue to work unchanged.
    /// </para>
    /// </remarks>
    void ReceiveFrame(in VideoFrame frame);

    /// <summary>Optional endpoint diagnostics snapshot.</summary>
    VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => VideoEndpointDiagnosticsSnapshot.Empty;
}
