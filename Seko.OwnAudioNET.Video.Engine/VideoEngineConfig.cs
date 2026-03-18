namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Playback-clock configuration for <see cref="VideoEngine"/>.
/// </summary>
public sealed class VideoEngineConfig
{
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
    /// Hint for future output-layer integration. The engine itself remains clock-driven.
    /// </summary>
    public VideoPresentationSyncMode PresentationSyncMode { get; set; } = VideoPresentationSyncMode.None;

    /// <summary>Lower bound for the frame-advance cadence in milliseconds.</summary>
    public int MinimumAdvanceIntervalMs { get; set; } = 1;

    /// <summary>Upper bound for the frame-advance cadence in milliseconds.</summary>
    public int MaximumAdvanceIntervalMs { get; set; } = 16;

    internal VideoEngineConfig CloneNormalized()
    {
        var minimum = Math.Max(1, MinimumAdvanceIntervalMs);
        var maximum = Math.Max(minimum, MaximumAdvanceIntervalMs);

        var normalizedLimit = TargetFpsLimit;
        if (normalizedLimit.HasValue && (double.IsNaN(normalizedLimit.Value) || double.IsInfinity(normalizedLimit.Value) || normalizedLimit.Value <= 0))
            normalizedLimit = null;

        var normalizedUnknownPollFps = UnknownSourcePollFps;
        if (double.IsNaN(normalizedUnknownPollFps) || double.IsInfinity(normalizedUnknownPollFps) || normalizedUnknownPollFps <= 0)
            normalizedUnknownPollFps = 120;

        return new VideoEngineConfig
        {
            TargetFpsLimit = normalizedLimit,
            UnknownSourcePollFps = normalizedUnknownPollFps,
            PresentationSyncMode = PresentationSyncMode,
            MinimumAdvanceIntervalMs = minimum,
            MaximumAdvanceIntervalMs = maximum
        };
    }
}

/// <summary>
/// Presentation sync policy hint for outputs consuming <see cref="VideoEngine"/>.
/// </summary>
public enum VideoPresentationSyncMode
{
    /// <summary>Pure clock-driven updates with no explicit presentation synchronization.</summary>
    None = 0,

    /// <summary>Prefer syncing presentation to output VSync when the renderer supports it.</summary>
    PreferVSync = 1,

    /// <summary>Require VSync-synced presentation when available; otherwise fall back to clock-only.</summary>
    RequireVSync = 2
}

