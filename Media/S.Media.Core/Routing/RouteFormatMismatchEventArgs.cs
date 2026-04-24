using S.Media.Core.Media;

namespace S.Media.Core.Routing;

/// <summary>
/// §6.5 — Raised when a route's input format no longer matches the format that
/// was active when the route was created. This typically happens when an NDI
/// source renegotiates (resolution change, sample rate switch) or when a decoder
/// reopens on a different stream. The event fires once per format change; it does
/// not re-fire until the format changes again.
/// </summary>
public sealed record RouteFormatMismatchEventArgs(
    RouteId RouteId,
    InputId InputId,
    EndpointId EndpointId,
    AudioFormat? OriginalAudioFormat,
    AudioFormat? CurrentAudioFormat);
