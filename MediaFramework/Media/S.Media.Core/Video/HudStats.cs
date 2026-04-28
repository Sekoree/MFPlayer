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
    /// <summary>Frames the encoded-frame consumer (router subscription) had to drop because of overflow.</summary>
    public long SubscriptionDroppedFrames;
    /// <summary>Frames the decoder skipped before <c>sws_scale</c> because they were too late to present.</summary>
    public long DecoderDroppedFrames;
    public TimeSpan ClockPosition;
    public string? ClockName;
    /// <summary>Smoothed/EMA clock-vs-frame drift used by the upstream UI.</summary>
    public TimeSpan Drift;
    /// <summary>
    /// Raw, unsmoothed lag between the master clock and the *currently presented*
    /// frame's PTS. Larger than <see cref="Drift"/> when the decoder runs below
    /// realtime and the renderer keeps re-using the last frame.
    /// </summary>
    public TimeSpan FrameAge;
    public string? ExtraLine1;
    public string? ExtraLine2;

    /// <summary>Formats the HUD as a multi-line string block.</summary>
    public readonly string[] ToLines()
    {
        var lines = new List<string>(10);
        lines.Add($"{Width}x{Height} {PixelFormat}");
        if (InputWidth > 0 && InputHeight > 0)
            lines.Add($"src: {InputWidth}x{InputHeight} {InputPixelFormat} @ {InputFps:F2} fps");
        if (InputPixelFormat != default)
        {
            if (InputPixelFormat == PixelFormat)
                lines.Add($"fmt: {PixelFormat} (passthrough)");
            else if (IsYuvFormat(InputPixelFormat))
                // No CPU conversion ran on this frame — the decoder produced YUV
                // and the renderer's GPU shader samples it directly. Wording the
                // user can trust as "no software conversion" (see
                // Doc/Heavy-Media-Playback-Fixes-Checklist.md, phase 1).
                lines.Add($"fmt: {InputPixelFormat} (GPU YUV->RGB)");
            else
                lines.Add($"fmt: {InputPixelFormat} -> {PixelFormat} (GPU sample)");
        }
        lines.Add($"{Fps:F1} fps");
        lines.Add($"presented: {PresentedFrames}  black: {BlackFrames}");
        long droppedTotal = DroppedFrames + SubscriptionDroppedFrames + DecoderDroppedFrames;
        if (droppedTotal > 0)
        {
            // Break the count out per-stage so the user can see *where* frames
            // are being lost (renderer catch-up, transport queue, decoder).
            lines.Add($"dropped: render={DroppedFrames}  sub={SubscriptionDroppedFrames}  dec={DecoderDroppedFrames}");
        }
        lines.Add($"clock: {ClockPosition:hh\\:mm\\:ss\\.fff}");
        if (!string.IsNullOrWhiteSpace(ClockName))
            lines.Add($"clock src: {ClockName}");
        if (Drift != TimeSpan.Zero)
            lines.Add($"drift: {FormatTimeMs(Drift)}");
        if (FrameAge != TimeSpan.Zero)
            lines.Add($"frame age: {FormatTimeMs(FrameAge)}");
        if (ExtraLine1 is not null) lines.Add(ExtraLine1);
        if (ExtraLine2 is not null) lines.Add(ExtraLine2);
        return lines.ToArray();
    }

    private static string FormatTimeMs(TimeSpan t)
    {
        // Switch to seconds beyond ±1 s so a 9-second drift readout is legible
        // without dropping leading digits.
        double ms = t.TotalMilliseconds;
        if (Math.Abs(ms) >= 1000)
            return $"{t.TotalSeconds:+0.00;-0.00}s";
        return $"{ms:+0.0;-0.0}ms";
    }

    private static bool IsYuvFormat(PixelFormat pf) => pf switch
    {
        PixelFormat.Nv12      => true,
        PixelFormat.Yuv420p   => true,
        PixelFormat.Yuv420p10 => true,
        PixelFormat.Yuv422p10 => true,
        PixelFormat.Yuv444p   => true,
        PixelFormat.Uyvy422   => true,
        PixelFormat.P010      => true,
        _                     => false
    };
}
