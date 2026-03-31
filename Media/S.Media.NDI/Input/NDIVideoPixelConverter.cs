using System.Buffers;
using NDILib;
using S.Media.Core.Video;

namespace S.Media.NDI.Input;

/// <summary>
/// Pixel-format conversion helpers shared by <see cref="NDICaptureCoordinator"/>
/// and <see cref="NDIFrameSyncCoordinator"/>.
/// </summary>
internal static class NDIVideoPixelConverter
{
    /// <summary>
    /// Copies a packed 32-bit-per-pixel NDI video frame into a managed byte array,
    /// performing any channel-order remapping required by <paramref name="sourceFormat"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> on success; <see langword="false"/> when the source format is
    /// unsupported or the destination buffer is too small.
    /// </returns>
    internal static unsafe bool TryCopyPacked32(
        nint sourcePtr,
        int sourceStride,
        NdiFourCCVideoType sourceFormat,
        int width,
        int height,
        byte[] destination,
        int destinationLength,
        out VideoPixelFormat outputFormat,
        out string conversionPath)
    {
        switch (sourceFormat)
        {
            case NdiFourCCVideoType.Rgba:
                outputFormat = VideoPixelFormat.Rgba32;
                conversionPath = "passthrough-rgba";
                break;
            case NdiFourCCVideoType.Rgbx:
                outputFormat = VideoPixelFormat.Rgba32;
                conversionPath = "passthrough-rgbx";
                break;
            case NdiFourCCVideoType.Bgra:
                outputFormat = VideoPixelFormat.Bgra32;
                conversionPath = "passthrough-bgra";
                break;
            case NdiFourCCVideoType.Bgrx:
                outputFormat = VideoPixelFormat.Bgra32;
                conversionPath = "passthrough-bgrx";
                break;
            default:
                outputFormat = VideoPixelFormat.Unknown;
                conversionPath = "unsupported-source-format";
                return false;
        }

        var destinationStride = width * 4;
        var pixelsPerRow = Math.Min(width, Math.Max(0, sourceStride / 4));
        var copyBytesPerRow = pixelsPerRow * 4;
        if (destinationLength < destinationStride * height)
        {
            outputFormat = VideoPixelFormat.Unknown;
            conversionPath = "destination-too-small";
            return false;
        }

        if (copyBytesPerRow == destinationStride)
        {
            fixed (byte* destinationBase = destination)
            {
                Buffer.MemoryCopy((void*)sourcePtr, destinationBase, destinationLength, destinationStride * height);
            }

            return true;
        }

        fixed (byte* destinationBase = destination)
        {
            for (var y = 0; y < height; y++)
            {
                var sourceRow = (byte*)sourcePtr + (y * sourceStride);
                var destinationRow = destinationBase + (y * destinationStride);
                if (copyBytesPerRow < destinationStride)
                    new Span<byte>(destinationRow, destinationStride).Clear();
                if (copyBytesPerRow > 0)
                    Buffer.MemoryCopy(sourceRow, destinationRow, destinationStride, copyBytesPerRow);
            }
        }

        return true;
    }

    /// <summary>
    /// Rents a pooled buffer sized for a packed 32-bit frame of <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    internal static byte[] RentFrameBuffer(int width, int height)
        => ArrayPool<byte>.Shared.Rent(checked(width * height * 4));
}

