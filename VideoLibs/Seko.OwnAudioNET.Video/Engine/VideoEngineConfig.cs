namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Configuration for <see cref="IVideoEngine"/> behavior.
/// </summary>
public sealed class VideoEngineConfig
{
    public double? FpsLimit { get; set; }

    public VideoEnginePixelFormatPolicy PixelFormatPolicy { get; set; } = VideoEnginePixelFormatPolicy.Auto;

    public VideoPixelFormat FixedPixelFormat { get; set; } = VideoPixelFormat.Rgba32;

    public bool DropRejectedFrames { get; set; } = true;

    // Transport-derived settings, now consolidated here.
    public VideoClockSyncMode ClockSyncMode { get; set; } = VideoClockSyncMode.DualModeAuto;

    public double? TargetFpsLimit { get; set; }

    public double UnknownSourcePollFps { get; set; } = 120;

    public VideoPresentationSyncMode PresentationSyncMode { get; set; } = VideoPresentationSyncMode.None;

    public int MinimumAdvanceIntervalMs { get; set; } = 1;

    public int MaximumAdvanceIntervalMs { get; set; } = 16;

    public VideoNoOutputPolicy NoOutputPolicy { get; set; } = VideoNoOutputPolicy.ImmediateDrop;

    public int SmallCacheFrameCount { get; set; } = 1;

    public VideoEngineConfig CloneNormalized()
    {
        var fps = FpsLimit;
        if (fps.HasValue && (double.IsNaN(fps.Value) || double.IsInfinity(fps.Value) || fps.Value <= 0))
            fps = null;

        var targetFps = TargetFpsLimit;
        if (targetFps.HasValue && (double.IsNaN(targetFps.Value) || double.IsInfinity(targetFps.Value) || targetFps.Value <= 0))
            targetFps = null;

        var unknownPoll = UnknownSourcePollFps;
        if (double.IsNaN(unknownPoll) || double.IsInfinity(unknownPoll) || unknownPoll <= 0)
            unknownPoll = 120;

        var minAdvance = Math.Max(1, MinimumAdvanceIntervalMs);
        var maxAdvance = Math.Max(minAdvance, MaximumAdvanceIntervalMs);

        return new VideoEngineConfig
        {
            FpsLimit = fps,
            PixelFormatPolicy = PixelFormatPolicy,
            FixedPixelFormat = FixedPixelFormat,
            DropRejectedFrames = DropRejectedFrames,
            ClockSyncMode = ClockSyncMode,
            TargetFpsLimit = targetFps,
            UnknownSourcePollFps = unknownPoll,
            PresentationSyncMode = PresentationSyncMode,
            MinimumAdvanceIntervalMs = minAdvance,
            MaximumAdvanceIntervalMs = maxAdvance,
            NoOutputPolicy = NoOutputPolicy,
            SmallCacheFrameCount = Math.Max(1, SmallCacheFrameCount)
        };
    }
}

public enum VideoEnginePixelFormatPolicy
{
    Auto = 0,
    Fixed = 1
}

public enum VideoClockSyncMode
{
    VideoOnly = 0,
    AudioLed = 1,
    DualModeAuto = 2
}

public enum VideoPresentationSyncMode
{
    None = 0,
    PreferVSync = 1,
    RequireVSync = 2
}

public enum VideoNoOutputPolicy
{
    ImmediateDrop = 0,
    SmallCache = 1
}

