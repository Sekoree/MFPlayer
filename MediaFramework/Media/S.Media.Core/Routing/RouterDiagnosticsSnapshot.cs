namespace S.Media.Core.Routing;

/// <summary>Point-in-time diagnostic snapshot of the entire router state.</summary>
public sealed record RouterDiagnosticsSnapshot(
    bool IsRunning,
    TimeSpan ClockPosition,
    IReadOnlyList<InputDiagnostics> Inputs,
    IReadOnlyList<EndpointDiagnostics> Endpoints,
    IReadOnlyList<RouteDiagnostics> Routes);

public sealed record InputDiagnostics(
    InputId Id,
    string Kind,
    bool Enabled,
    float Volume,
    float PeakLevel,
    TimeSpan TimeOffset);

public sealed record EndpointDiagnostics(
    EndpointId Id,
    string Kind,
    float Gain,
    float PeakLevel = 0f,
    long OverflowSamplesTotal = 0L);

public sealed record RouteDiagnostics(
    RouteId Id,
    InputId InputId,
    EndpointId EndpointId,
    string Kind,
    bool Enabled,
    float Gain,
    TimeSpan TimeOffset,
    bool HasResampler,
    bool LiveMode = false,
    PtsDriftTrackerSnapshot? PushVideoDrift = null,
    PtsDriftTrackerSnapshot? PullVideoDrift = null);

/// <summary>
/// Diagnostic snapshot of one <c>PtsDriftTracker</c> instance (push or pull path).
/// </summary>
public readonly record struct PtsDriftTrackerSnapshot(
    bool HasOrigin,
    TimeSpan PtsOrigin,
    TimeSpan ClockOrigin,
    TimeSpan OriginDrift);
