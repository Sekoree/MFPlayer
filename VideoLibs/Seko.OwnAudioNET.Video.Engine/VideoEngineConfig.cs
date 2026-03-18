using Seko.OwnAudioNET.Video;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Configuration for <see cref="VideoEngine"/> sink behavior.
/// </summary>
public sealed class VideoEngineConfig
{
    /// <summary>
    /// Optional upper FPS bound for frame submissions to the selected output.
    /// <see langword="null"/> means no engine-level throttling.
    /// </summary>
    public double? FpsLimit { get; set; }

    /// <summary>
    /// Pixel format selection policy for incoming frames.
    /// </summary>
    public VideoEnginePixelFormatPolicy PixelFormatPolicy { get; set; } = VideoEnginePixelFormatPolicy.Auto;

    /// <summary>
    /// Required pixel format when <see cref="PixelFormatPolicy"/> is <see cref="VideoEnginePixelFormatPolicy.Fixed"/>.
    /// </summary>
    public VideoPixelFormat FixedPixelFormat { get; set; } = VideoPixelFormat.Rgba32;

    /// <summary>
    /// When <see langword="true"/>, frames rejected by policy/throttling are dropped.
    /// </summary>
    public bool DropRejectedFrames { get; set; } = true;

    internal VideoEngineConfig CloneNormalized()
    {
        var fps = FpsLimit;
        if (fps.HasValue && (double.IsNaN(fps.Value) || double.IsInfinity(fps.Value) || fps.Value <= 0))
            fps = null;

        return new VideoEngineConfig
        {
            FpsLimit = fps,
            PixelFormatPolicy = PixelFormatPolicy,
            FixedPixelFormat = FixedPixelFormat,
            DropRejectedFrames = DropRejectedFrames
        };
    }
}

public enum VideoEnginePixelFormatPolicy
{
    /// <summary>Accept any incoming frame format.</summary>
    Auto = 0,

    /// <summary>Accept only <see cref="VideoEngineConfig.FixedPixelFormat"/>.</summary>
    Fixed = 1
}


