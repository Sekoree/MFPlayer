using S.Media.Core.Video;

namespace HaCue2.Engine;

/// <summary>
/// The frame an output shows when its composition has nothing on it and nothing authored to show.
/// </summary>
/// <remarks>
/// <para>
/// Black is a real answer rather than a placeholder. A cue player's projector is switched on for the
/// evening, and between cues it has to be showing something the audience can look at — black is what
/// every desk in the building means by "nothing", and leaving the last frame of the previous cue up is
/// how a video ends on a freeze-frame of somebody mid-blink.
/// </para>
/// <para>
/// It is also what makes an output EXIST. An <c>IVideoOutput</c> is configured by its first submitted
/// frame, so a composition that submits nothing leaves a window that was never created — no error, no
/// window, nothing to tell an operator why the projector they just added is dark.
/// </para>
/// </remarks>
public static class IdleFrames
{
    /// <summary>
    /// An opaque black frame at the canvas size.
    /// </summary>
    /// <remarks>
    /// BGRA32 with a plain managed buffer, like the identify pattern: this frame is submitted from the
    /// composition pump and cloned per output through the pooled CPU path, so there is nothing to gain
    /// from a hardware surface and a great deal to lose from needing one on a booth box.
    /// <para>
    /// Zero-filled, which is already black with a zero alpha byte, so the alpha is written explicitly:
    /// a premultiplied frame with alpha 0 composites as nothing at all, which on a sink that blends
    /// would show whatever was underneath rather than black.
    /// </para>
    /// </remarks>
    public static VideoFrame Black(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var at = 3; at < pixels.Length; at += 4)
            pixels[at] = 0xFF;

        return new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(25, 1)),
            pixels,
            stride,
            new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
    }
}
