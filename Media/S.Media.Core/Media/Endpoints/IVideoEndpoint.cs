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
    /// Legacy frame-delivery overload. Called by the graph to deliver a video frame.
    /// Implementations MUST be non-blocking.
    /// </summary>
    /// <remarks>
    /// <b>Ownership:</b> the caller (router) owns <paramref name="frame"/>.<c>MemoryOwner</c>.
    /// Implementations MUST NOT dispose it.  If the endpoint needs to retain the pixel data
    /// past the call (e.g. to render on a later tick), it MUST copy the bytes into its own
    /// buffer.  See <see cref="VideoFrame"/> docs for the full contract.
    ///
    /// <para>
    /// Endpoints that want zero-copy retention should override
    /// <see cref="ReceiveFrame(in VideoFrameHandle)"/> instead — that overload exposes
    /// <see cref="VideoFrameHandle.Retain"/> / <see cref="VideoFrameHandle.Release"/> so
    /// fan-out to N endpoints shares a single pool rental (§3.11 / B15+B16+R18+CH7).
    /// </para>
    /// </remarks>
    void ReceiveFrame(in VideoFrame frame);

    /// <summary>
    /// Ref-counted frame delivery. Called by the router in place of
    /// <see cref="ReceiveFrame(in VideoFrame)"/> so endpoints can opt into zero-copy
    /// retention by calling <see cref="VideoFrameHandle.Retain"/> during the call and
    /// <see cref="VideoFrameHandle.Release"/> later on their own schedule.
    ///
    /// <para>
    /// The default implementation forwards to the legacy <see cref="ReceiveFrame(in VideoFrame)"/>
    /// overload, preserving the existing "router disposes after the call" contract so legacy
    /// endpoints keep working unchanged. Endpoints that need fast-path retention override this
    /// method; endpoints that copy the bytes during the call need not override.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Ownership:</b> the router holds one refcount for the duration of this call.
    /// Endpoints that need the data past the call must call
    /// <see cref="VideoFrameHandle.Retain"/> before returning and exactly one matching
    /// <see cref="VideoFrameHandle.Release"/> when they are done.
    /// </remarks>
    void ReceiveFrame(in VideoFrameHandle handle)
    {
        var frame = handle.Frame;
        ReceiveFrame(in frame);
    }

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
