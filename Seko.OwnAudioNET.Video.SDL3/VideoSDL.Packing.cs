using Seko.OwnaudioNET.OpenGL;
using Seko.OwnAudioNET.Video;

namespace Seko.OwnAudioNET.Video.SDL3;

public sealed partial class VideoSDL
{
    /// <summary>
    /// Returns a byte slice whose row stride equals <paramref name="rowBytes"/>.
    /// Delegates to <see cref="VideoFramePacking.GetTightlyPackedPlane"/>.
    /// </summary>
    private static byte[]? GetTightlyPackedPlane(
        VideoFrame frame, int planeIndex,
        int rowBytes, int rows,
        ref byte[]? scratch)
        => VideoFramePacking.GetTightlyPackedPlane(frame, planeIndex, rowBytes, rows, ref scratch);

    /// <summary>
    /// Computes the largest aspect-ratio-preserving viewport rectangle.
    /// Delegates to <see cref="VideoFramePacking.GetAspectFitViewport"/>.
    /// </summary>
    private static VideoGlViewport GetAspectFitRect(
        int surfaceWidth, int surfaceHeight,
        int videoWidth,   int videoHeight)
        => VideoFramePacking.GetAspectFitViewport(surfaceWidth, surfaceHeight, videoWidth, videoHeight);
}
