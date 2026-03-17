using Seko.OwnAudioNET.Video;

namespace Seko.OwnAudioNET.Video.SDL3;

public sealed partial class VideoSDL
{
    /// <summary>
    /// Returns a byte slice whose row stride equals <paramref name="rowBytes"/>.
    /// If the source plane is already tightly packed the original buffer is returned
    /// directly (zero-copy). Otherwise the data is copied into <paramref name="scratch"/>.
    /// </summary>
    private static byte[]? GetTightlyPackedPlane(VideoFrame frame, int planeIndex,
        int rowBytes, int rows, ref byte[]? scratch)
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
    /// Computes the largest letterboxed / pillarboxed rectangle that fits
    /// <paramref name="videoWidth"/> × <paramref name="videoHeight"/> inside
    /// <paramref name="surfaceWidth"/> × <paramref name="surfaceHeight"/>
    /// while preserving the video's aspect ratio.
    /// </summary>
    private static ViewportRect GetAspectFitRect(
        int surfaceWidth, int surfaceHeight,
        int videoWidth,   int videoHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || videoWidth <= 0 || videoHeight <= 0)
            return new ViewportRect(0, 0, Math.Max(1, surfaceWidth), Math.Max(1, surfaceHeight));

        var surfaceAspect = surfaceWidth  / (double)surfaceHeight;
        var videoAspect   = videoWidth    / (double)videoHeight;

        if (videoAspect > surfaceAspect)
        {
            var h = Math.Max(1, (int)Math.Round(surfaceWidth / videoAspect));
            var y = (surfaceHeight - h) / 2;
            return new ViewportRect(0, y, surfaceWidth, h);
        }

        var w = Math.Max(1, (int)Math.Round(surfaceHeight * videoAspect));
        var x = (surfaceWidth - w) / 2;
        return new ViewportRect(x, 0, w, surfaceHeight);
    }

    private readonly record struct ViewportRect(int X, int Y, int Width, int Height);
}

