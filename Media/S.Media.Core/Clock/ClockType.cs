namespace S.Media.Core.Clock;

/// <summary>
/// Determines the clock source used by a mixer or player for timeline tracking.
/// </summary>
public enum ClockType
{
    /// <summary>
    /// Internal clock driven by the audio pump when audio is active,
    /// falling back to a wall-clock <see cref="CoreMediaClock"/> otherwise.
    /// </summary>
    Hybrid = 0,

    /// <summary>
    /// An externally supplied clock (e.g. NDI timecode).
    /// When configured but unavailable, operations must fail with
    /// <see cref="Errors.MediaErrorCode.MediaExternalClockUnavailable"/>.
    /// </summary>
    External = 1,
}

