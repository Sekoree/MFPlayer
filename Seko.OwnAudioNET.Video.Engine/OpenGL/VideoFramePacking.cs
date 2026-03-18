namespace Seko.OwnAudioNET.Video.OpenGL;

/// <summary>
/// CPU-side helpers for extracting tightly-packed plane data from a
/// <see cref="VideoFrame"/> and computing aspect-ratio-preserving viewport
/// rectangles.  Both the SDL3 and Avalonia renderers share this logic.
/// </summary>
public static class VideoFramePacking
{
    /// <summary>
    /// Returns a byte array whose row stride equals <paramref name="rowBytes"/>.
    /// <para>
    /// If the source plane is already tightly packed the original backing array is
    /// returned directly (zero-copy).  Otherwise the rows are copied into
    /// <paramref name="scratch"/>, which is resized as needed.
    /// </para>
    /// </summary>
    /// <returns>The tightly-packed plane data, or <c>null</c> if the frame data is invalid.</returns>
    public static byte[]? GetTightlyPackedPlane(
        VideoFrame frame, int planeIndex,
        int rowBytes, int rows,
        ref byte[]? scratch)
    {
        if (rowBytes <= 0 || rows <= 0)
            return null;

        var stride = frame.GetPlaneStride(planeIndex);
        var source = frame.GetPlaneData(planeIndex);
        if (source.Length == 0 || stride <= 0)
            return null;

        var tightLength = checked(rowBytes * rows);
        if (stride == rowBytes && source.Length >= tightLength)
            return source;

        if (scratch == null || scratch.Length < tightLength)
            scratch = new byte[tightLength];

        var dstOffset = 0;
        var srcOffset = 0;
        for (var row = 0; row < rows; row++)
        {
            if (srcOffset + rowBytes > source.Length)
                return null;

            Buffer.BlockCopy(source, srcOffset, scratch, dstOffset, rowBytes);
            srcOffset += stride;
            dstOffset += rowBytes;
        }

        return scratch;
    }

    /// <summary>
    /// Computes the largest letterboxed/pillarboxed rectangle that fits
    /// <paramref name="videoWidth"/> × <paramref name="videoHeight"/> inside
    /// <paramref name="surfaceWidth"/> × <paramref name="surfaceHeight"/>
    /// while preserving the video's aspect ratio.
    /// <para>
    /// If aspect-ratio preservation is not desired the caller should construct a
    /// <see cref="VideoGlViewport"/> that fills the full surface instead.
    /// </para>
    /// </summary>
    public static VideoGlViewport GetAspectFitViewport(
        int surfaceWidth, int surfaceHeight,
        int videoWidth,   int videoHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || videoWidth <= 0 || videoHeight <= 0)
            return new VideoGlViewport(0, 0, Math.Max(1, surfaceWidth), Math.Max(1, surfaceHeight));

        var surfaceAspect = surfaceWidth  / (double)surfaceHeight;
        var videoAspect   = videoWidth    / (double)videoHeight;

        if (videoAspect > surfaceAspect)
        {
            var h = Math.Max(1, (int)Math.Round(surfaceWidth / videoAspect));
            var y = (surfaceHeight - h) / 2;
            return new VideoGlViewport(0, y, surfaceWidth, h);
        }

        var w = Math.Max(1, (int)Math.Round(surfaceHeight * videoAspect));
        var x = (surfaceWidth - w) / 2;
        return new VideoGlViewport(x, 0, w, surfaceHeight);
    }
}

/// <summary>
/// An integer rectangle used as a GL viewport (x, y, width, height).
/// The origin is at the bottom-left corner as per OpenGL convention.
/// </summary>
public readonly record struct VideoGlViewport(int X, int Y, int Width, int Height);

