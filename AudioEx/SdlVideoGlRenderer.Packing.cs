using Seko.OwnAudioNET.Video;

namespace AudioEx;

internal sealed partial class SdlVideoGlRenderer
{
    private static byte[]? GetTightlyPackedPlane(VideoFrame frame, int planeIndex, int rowBytes, int rows, ref byte[]? scratch)
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

    private static ViewportRect GetAspectFitRect(int surfaceWidth, int surfaceHeight, int videoWidth, int videoHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || videoWidth <= 0 || videoHeight <= 0)
            return new ViewportRect(0, 0, Math.Max(1, surfaceWidth), Math.Max(1, surfaceHeight));

        var surfaceAspect = surfaceWidth / (double)surfaceHeight;
        var videoAspect = videoWidth / (double)videoHeight;

        if (videoAspect > surfaceAspect)
        {
            var targetHeight = Math.Max(1, (int)Math.Round(surfaceWidth / videoAspect));
            var y = (surfaceHeight - targetHeight) / 2;
            return new ViewportRect(0, y, surfaceWidth, targetHeight);
        }

        var targetWidth = Math.Max(1, (int)Math.Round(surfaceHeight * videoAspect));
        var x = (surfaceWidth - targetWidth) / 2;
        return new ViewportRect(x, 0, targetWidth, surfaceHeight);
    }

    private readonly record struct ViewportRect(int X, int Y, int Width, int Height);
}

