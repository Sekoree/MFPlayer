namespace S.Media.Core.Video;

/// <summary>
/// Controls how the output normalises frame timestamps to ensure a monotonically-increasing
/// presentation timeline.
/// </summary>
public enum VideoTimestampMode
{
    /// <summary>Timestamps are passed through unchanged.</summary>
    Passthrough = 0,

    /// <summary>Timestamps that go backwards are clamped to the last presented value.</summary>
    ClampForward = 1,

    /// <summary>
    /// When a timestamp discontinuity larger than the configured threshold is detected,
    /// the timeline is rebased so presentation continues smoothly.
    /// </summary>
    RebaseOnDiscontinuity = 2,
}
