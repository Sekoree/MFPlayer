namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Playback-clock configuration for implementations of <see cref="IVideoTransportEngine"/>.
/// </summary>
public sealed class VideoTransportEngineConfig
{
    /// <summary>
    /// Chooses how the shared timeline clock should be driven.
    /// </summary>
    public VideoTransportClockSyncMode ClockSyncMode { get; set; } = VideoTransportClockSyncMode.DualModeAuto;

    /// <summary>
    /// Optional upper bound for frame advancement frequency.
    /// <see langword="null"/> means source-driven cadence only.
    /// </summary>
    public double? TargetFpsLimit { get; set; }

    /// <summary>
    /// Polling FPS used when no source provides a valid frame rate.
    /// Default is 120 Hz for responsive seeking and scrubbing.
    /// </summary>
    public double UnknownSourcePollFps { get; set; } = 120;

    /// <summary>
    /// Best-effort presentation policy intended for downstream mixers/outputs that support it.
    /// The transport engine itself remains clock-driven and output-agnostic.
    /// </summary>
    public VideoTransportPresentationSyncMode PresentationSyncMode { get; set; } = VideoTransportPresentationSyncMode.None;

    /// <summary>Lower bound for the frame-advance cadence in milliseconds.</summary>
    public int MinimumAdvanceIntervalMs { get; set; } = 1;

    /// <summary>Upper bound for the frame-advance cadence in milliseconds.</summary>
    public int MaximumAdvanceIntervalMs { get; set; } = 16;

    public VideoTransportEngineConfig CloneNormalized()
    {
        var minimum = Math.Max(1, MinimumAdvanceIntervalMs);
        var maximum = Math.Max(minimum, MaximumAdvanceIntervalMs);

        var normalizedLimit = TargetFpsLimit;
        if (normalizedLimit.HasValue && (double.IsNaN(normalizedLimit.Value) || double.IsInfinity(normalizedLimit.Value) || normalizedLimit.Value <= 0))
            normalizedLimit = null;

        var normalizedUnknownPollFps = UnknownSourcePollFps;
        if (double.IsNaN(normalizedUnknownPollFps) || double.IsInfinity(normalizedUnknownPollFps) || normalizedUnknownPollFps <= 0)
            normalizedUnknownPollFps = 120;

        return new VideoTransportEngineConfig
        {
            ClockSyncMode = ClockSyncMode,
            TargetFpsLimit = normalizedLimit,
            UnknownSourcePollFps = normalizedUnknownPollFps,
            PresentationSyncMode = PresentationSyncMode,
            MinimumAdvanceIntervalMs = minimum,
            MaximumAdvanceIntervalMs = maximum
        };
    }
}

/// <summary>
/// Clock sync policy used by <see cref="IVideoTransportEngine"/> implementations.
/// </summary>
public enum VideoTransportClockSyncMode
{
    /// <summary>Engine drives a local realtime timeline clock.</summary>
    VideoOnly = 0,

    /// <summary>Engine follows an externally-driven clock (typically audio-led).</summary>
    AudioLed = 1,

    /// <summary>
    /// Engine follows an external clock when present; otherwise it falls back to local realtime driving.
    /// </summary>
    DualModeAuto = 2
}

/// <summary>
/// Presentation sync policy for outputs consuming <see cref="IVideoTransportEngine"/> implementations.
/// </summary>
public enum VideoTransportPresentationSyncMode
{
    /// <summary>Pure clock-driven updates with no explicit presentation synchronization.</summary>
    None = 0,

    /// <summary>Prefer syncing presentation to output VSync when the renderer supports it.</summary>
    PreferVSync = 1,

    /// <summary>Require VSync-synced presentation when available; otherwise fall back to clock-only.</summary>
    RequireVSync = 2
}


