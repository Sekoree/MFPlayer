using HaCue2.Core.Model;
using S.Media.Core.Video;

namespace HaCue2.Engine;

/// <summary>
/// The frame an output shows when its composition has nothing on it and nothing authored to show.
/// </summary>
/// <remarks>
/// <para>
/// Black is a real answer rather than a placeholder. A cue player's projector is switched on for the
/// evening, and between cues it has to be showing something the audience can look at - black is what
/// every desk in the building means by "nothing", and leaving the last frame of the previous cue up is
/// how a video ends on a freeze-frame of somebody mid-blink.
/// </para>
/// <para>
/// It is also what makes an output EXIST. An <c>IVideoOutput</c> is configured by its first submitted
/// frame, so a composition that submits nothing leaves a window that was never created - no error, no
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

    /// <summary>
    /// A still drawn into the canvas at the authored fit, over black.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A holding slate is rarely the canvas's own shape - it is a logo, or a photograph somebody had -
    /// and it used to be submitted at its own raster and stretched to fill, which is the one option
    /// that always looks wrong. Every fit the app offers a LAYER is offered here for the same reason
    /// and computed the same way.
    /// </para>
    /// <para>
    /// Done once per idle image rather than per frame: <c>ApplyIdleFramesAsync</c> gates the whole
    /// decode on a signature, so this runs when the operator changes the picture and not otherwise.
    /// Bilinear rather than nearest - a slate is a still somebody is looking at for minutes.
    /// </para>
    /// </remarks>
    public static VideoFrame Fitted(VideoFrame source, int width, int height, LayerFit fit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        // Anything that is not a plain CPU BGRA frame is left exactly as it was: the caller submits it
        // and the compositor does what it did before, which is the honest fallback for a frame this
        // routine cannot read.
        if (source.Format.PixelFormat != PixelFormat.Bgra32
            || source.PlaneCount != 1
            || source.HardwareBacking is not null)
            return source;

        var (sourceWidth, sourceHeight) = (source.Format.Width, source.Format.Height);
        if (sourceWidth < 1 || sourceHeight < 1)
            return source;

        var (scaleX, scaleY) = Scale(fit, sourceWidth, sourceHeight, width, height);

        var drawnWidth = sourceWidth * scaleX;
        var drawnHeight = sourceHeight * scaleY;
        var originX = (width - drawnWidth) / 2;
        var originY = (height - drawnHeight) / 2;

        var canvas = Black(width, height);
        var target = canvas.Planes[0].ToArray();
        var targetStride = canvas.Strides[0];

        var pixels = source.Planes[0].Span;
        var sourceStride = source.Strides[0];

        // Only the rows and columns the picture actually covers; everything else keeps the black the
        // canvas was built with.
        var left = Math.Max(0, (int)Math.Floor(originX));
        var top = Math.Max(0, (int)Math.Floor(originY));
        var right = Math.Min(width, (int)Math.Ceiling(originX + drawnWidth));
        var bottom = Math.Min(height, (int)Math.Ceiling(originY + drawnHeight));

        for (var y = top; y < bottom; y++)
        {
            // The centre of the destination pixel, mapped back into the source.
            var sourceY = ((y + 0.5 - originY) / scaleY) - 0.5;
            var y0 = (int)Math.Floor(sourceY);
            var weightY = sourceY - y0;

            for (var x = left; x < right; x++)
            {
                var sourceX = ((x + 0.5 - originX) / scaleX) - 0.5;
                var x0 = (int)Math.Floor(sourceX);
                var weightX = sourceX - x0;

                var at = (y * targetStride) + (x * 4);

                for (var channel = 0; channel < 4; channel++)
                {
                    var top1 = Lerp(
                        Sample(pixels, sourceStride, sourceWidth, sourceHeight, x0, y0, channel),
                        Sample(pixels, sourceStride, sourceWidth, sourceHeight, x0 + 1, y0, channel),
                        weightX);
                    var bottom1 = Lerp(
                        Sample(pixels, sourceStride, sourceWidth, sourceHeight, x0, y0 + 1, channel),
                        Sample(pixels, sourceStride, sourceWidth, sourceHeight, x0 + 1, y0 + 1, channel),
                        weightX);

                    target[at + channel] = (byte)Math.Clamp(Math.Round(Lerp(top1, bottom1, weightY)), 0, 255);
                }

                // Composited OVER the black canvas rather than copied onto it: a slate with a
                // transparent corner must show black there, not the alpha it was authored with.
                var alpha = target[at + 3] / 255d;
                target[at] = (byte)Math.Round(target[at] * alpha);
                target[at + 1] = (byte)Math.Round(target[at + 1] * alpha);
                target[at + 2] = (byte)Math.Round(target[at + 2] * alpha);
                target[at + 3] = 0xFF;
            }
        }

        canvas.Dispose();

        return new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(25, 1)),
            target,
            targetStride,
            new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
    }

    /// <summary>The per-axis scale each fit asks for. The same arithmetic a layer placement uses.</summary>
    private static (double X, double Y) Scale(
        LayerFit fit, int sourceWidth, int sourceHeight, int width, int height)
    {
        var toWidth = (double)width / sourceWidth;
        var toHeight = (double)height / sourceHeight;

        return fit switch
        {
            LayerFit.Cover => (Math.Max(toWidth, toHeight), Math.Max(toWidth, toHeight)),
            LayerFit.Stretch => (toWidth, toHeight),
            // Its own size, centred - neither scaled up nor cropped.
            LayerFit.Center => (1, 1),
            LayerFit.FillWidth => (toWidth, toWidth),
            LayerFit.FillHeight => (toHeight, toHeight),
            _ => (Math.Min(toWidth, toHeight), Math.Min(toWidth, toHeight)),
        };
    }

    /// <summary>One channel of one source pixel, with the edge pixel repeated past the border.</summary>
    private static double Sample(
        ReadOnlySpan<byte> pixels, int stride, int width, int height, int x, int y, int channel)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);

        var at = (y * stride) + (x * 4) + channel;
        return at >= 0 && at < pixels.Length ? pixels[at] : 0;
    }

    private static double Lerp(double from, double to, double weight) => from + ((to - from) * weight);
}
