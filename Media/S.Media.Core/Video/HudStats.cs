using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Point-in-time statistics for the on-screen HUD overlay.
/// Populated by the video output before each HUD draw call.
/// </summary>
public record struct HudStats
{
    public int Width;
    public int Height;
    public PixelFormat PixelFormat;
    public double Fps;
    public int InputWidth;
    public int InputHeight;
    public double InputFps;
    public PixelFormat InputPixelFormat;
    public long PresentedFrames;
    public long BlackFrames;
    public long DroppedFrames;
    public TimeSpan ClockPosition;
    public TimeSpan Drift;
    public string? ExtraLine1;
    public string? ExtraLine2;

    /// <summary>Formats the HUD as a multi-line string block.</summary>
    public readonly string[] ToLines()
    {
        var lines = new List<string>(8);
        lines.Add($"{Width}x{Height} {PixelFormat}");
        if (InputWidth > 0 && InputHeight > 0)
            lines.Add($"src: {InputWidth}x{InputHeight} {InputPixelFormat} @ {InputFps:F2} fps");
        if (InputPixelFormat != default)
        {
            if (InputPixelFormat == PixelFormat)
                lines.Add($"fmt: {PixelFormat} (passthrough)");
            else
                lines.Add($"fmt: {InputPixelFormat} -> {PixelFormat} (shader)");
        }
        lines.Add($"{Fps:F1} fps");
        lines.Add($"presented: {PresentedFrames}  black: {BlackFrames}");
        if (DroppedFrames > 0)
            lines.Add($"dropped: {DroppedFrames}");
        lines.Add($"clock: {ClockPosition:hh\\:mm\\:ss\\.fff}");
        if (Drift != TimeSpan.Zero)
            lines.Add($"drift: {Drift.TotalMilliseconds:+0.0;-0.0}ms");
        if (ExtraLine1 is not null) lines.Add(ExtraLine1);
        if (ExtraLine2 is not null) lines.Add(ExtraLine2);
        return lines.ToArray();
    }
}

