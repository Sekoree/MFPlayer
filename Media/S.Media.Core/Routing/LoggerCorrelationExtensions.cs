using Microsoft.Extensions.Logging;

namespace S.Media.Core.Routing;

/// <summary>
/// §10.2 / EL2 — correlation-scoped logging helpers. Attach <see cref="RouteId"/>
/// / <see cref="InputId"/> / <see cref="EndpointId"/> to every log record
/// emitted inside a <c>using</c> block so log consumers (Seq, Elastic, etc.)
/// can filter by route/input without scraping the message template.
///
/// <para>
/// Usage:
/// </para>
/// <code>
/// using var _ = Log.BeginRouteScope(routeId);
/// Log.LogWarning("Push audio route skipped — format mismatch");
/// </code>
///
/// <para>
/// The returned <see cref="IDisposable"/> is allocated via
/// <see cref="ILogger.BeginScope{TState}(TState)"/>; in the default
/// MEL stack it reuses an internal pool so the cost is negligible for
/// non-hot paths. Do not call from the per-tick push loops — the scope
/// allocation would dominate RT-path CPU.
/// </para>
/// </summary>
public static class LoggerCorrelationExtensions
{
    /// <summary>Scope carrying <c>{ RouteId = … }</c>.</summary>
    public static IDisposable? BeginRouteScope(this ILogger logger, RouteId routeId)
        => logger.BeginScope(new Dictionary<string, object> { ["RouteId"] = routeId.ToString() });

    /// <summary>Scope carrying <c>{ InputId = … }</c>.</summary>
    public static IDisposable? BeginInputScope(this ILogger logger, InputId inputId)
        => logger.BeginScope(new Dictionary<string, object> { ["InputId"] = inputId.ToString() });

    /// <summary>Scope carrying <c>{ EndpointId = … }</c>.</summary>
    public static IDisposable? BeginEndpointScope(this ILogger logger, EndpointId endpointId)
        => logger.BeginScope(new Dictionary<string, object> { ["EndpointId"] = endpointId.ToString() });

    /// <summary>
    /// Scope carrying both <c>{ InputId, EndpointId }</c> — handy at route
    /// creation before a <see cref="RouteId"/> is materialised.
    /// </summary>
    public static IDisposable? BeginRouteScope(this ILogger logger, InputId inputId, EndpointId endpointId)
        => logger.BeginScope(new Dictionary<string, object>
        {
            ["InputId"] = inputId.ToString(),
            ["EndpointId"] = endpointId.ToString(),
        });
}