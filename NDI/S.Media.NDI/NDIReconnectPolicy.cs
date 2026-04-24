namespace S.Media.NDI;

/// <summary>
/// §4.19 — reconnect configuration for an <see cref="NDISource"/>. Collapses
/// the legacy <c>AutoReconnect</c> + <c>ConnectionCheckIntervalMs</c> pair into
/// a single record so future knobs (back-off, retry limit, initial delay) can
/// land without another two-flag coordination dance. The record is immutable
/// and safe to share across sources.
/// </summary>
/// <param name="CheckIntervalMs">
/// How often (in ms) the watchdog polls the underlying receiver for activity.
/// Values &lt; 500 are coerced to 500 to keep the background thread off the hot
/// path. Default: 2000 ms.
/// </param>
/// <param name="InitialDelayMs">
/// Grace period (in ms) after <see cref="NDISource.Start"/> before the watchdog
/// starts polling. Gives the SDK time to establish the first frame. Default:
/// 1000 ms (matches the prior hard-coded behaviour).
/// </param>
public sealed record NDIReconnectPolicy(
    int CheckIntervalMs  = 2000,
    int InitialDelayMs   = 1000)
{
    /// <summary>
    /// §4.19 — reasonable defaults for broadcast / live preview use: 2 s
    /// check cadence, 1 s initial grace period. Equivalent to
    /// <c>new NDIReconnectPolicy()</c>.
    /// </summary>
    public static NDIReconnectPolicy Default { get; } = new();

    internal int EffectiveCheckIntervalMs => Math.Max(500, CheckIntervalMs);
    internal int EffectiveInitialDelayMs  => Math.Max(0, InitialDelayMs);
}