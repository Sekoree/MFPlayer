using S.Media.Core.Errors;
using S.Media.Core.Video;

namespace S.Media.OpenGL.Conversion;

public sealed class YuvToRgbaConverter
{
    public int Convert(VideoFrame source, Span<byte> rgbaDestination, out int bytesWritten)
    {
        bytesWritten = 0;

        var frameValidation = source.ValidateForPush();
        if (frameValidation != MediaResult.Success)
        {
            return frameValidation;
        }

        var requiredBytes = checked(source.Width * source.Height * 4);
        if (rgbaDestination.Length < requiredBytes)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        var code = source.PixelFormat switch
        {
            VideoPixelFormat.Rgba32 => ConvertRgba(source, rgbaDestination, requiredBytes),
            VideoPixelFormat.Bgra32 => ConvertBgra(source, rgbaDestination, requiredBytes),
            VideoPixelFormat.Yuv420P => ConvertYuv420P(source, rgbaDestination),
            VideoPixelFormat.Nv12 => ConvertNv12(source, rgbaDestination),
            _ => (int)MediaErrorCode.OpenGLClonePixelFormatIncompatible,
        };

        if (code == MediaResult.Success)
        {
            bytesWritten = requiredBytes;
        }

        return code;
    }

    private static int ConvertRgba(VideoFrame source, Span<byte> destination, int requiredBytes)
    {
        if (source.Plane0.Length < requiredBytes)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        source.Plane0.Span[..requiredBytes].CopyTo(destination);
        return MediaResult.Success;
    }

    private static int ConvertBgra(VideoFrame source, Span<byte> destination, int requiredBytes)
    {
        if (source.Plane0.Length < requiredBytes)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        var src = source.Plane0.Span;
        for (var i = 0; i < requiredBytes; i += 4)
        {
            destination[i] = src[i + 2];
            destination[i + 1] = src[i + 1];
            destination[i + 2] = src[i];
            destination[i + 3] = src[i + 3];
        }

        return MediaResult.Success;
    }

    private static int ConvertYuv420P(VideoFrame source, Span<byte> destination)
    {
        if (source.Plane1.Length == 0 || source.Plane2.Length == 0 ||
            source.Plane0Stride <= 0 || source.Plane1Stride <= 0 || source.Plane2Stride <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        var yPlane = source.Plane0.Span;
        var uPlane = source.Plane1.Span;
        var vPlane = source.Plane2.Span;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var yIndex = y * source.Plane0Stride + x;
                var uIndex = (y / 2) * source.Plane1Stride + (x / 2);
                var vIndex = (y / 2) * source.Plane2Stride + (x / 2);
                if ((uint)yIndex >= (uint)yPlane.Length || (uint)uIndex >= (uint)uPlane.Length || (uint)vIndex >= (uint)vPlane.Length)
                {
                    return (int)MediaErrorCode.MediaInvalidArgument;
                }

                var pixelOffset = (y * source.Width + x) * 4;
                WriteRgba(yPlane[yIndex], uPlane[uIndex], vPlane[vIndex], destination, pixelOffset);
            }
        }

        return MediaResult.Success;
    }

    private static int ConvertNv12(VideoFrame source, Span<byte> destination)
    {
        if (source.Plane1.Length == 0 || source.Plane0Stride <= 0 || source.Plane1Stride <= 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        var yPlane = source.Plane0.Span;
        var uvPlane = source.Plane1.Span;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var yIndex = y * source.Plane0Stride + x;
                var uvIndex = (y / 2) * source.Plane1Stride + ((x / 2) * 2);
                if ((uint)yIndex >= (uint)yPlane.Length || (uint)(uvIndex + 1) >= (uint)uvPlane.Length)
                {
                    return (int)MediaErrorCode.MediaInvalidArgument;
                }

                var pixelOffset = (y * source.Width + x) * 4;
                WriteRgba(yPlane[yIndex], uvPlane[uvIndex], uvPlane[uvIndex + 1], destination, pixelOffset);
            }
        }

        return MediaResult.Success;
    }

    private static void WriteRgba(int y, int u, int v, Span<byte> destination, int offset)
    {
        var c = y - 16;
        var d = u - 128;
        var e = v - 128;

        var r = ClampToByte((298 * c + 409 * e + 128) >> 8);
        var g = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
        var b = ClampToByte((298 * c + 516 * d + 128) >> 8);

        destination[offset] = (byte)r;
        destination[offset + 1] = (byte)g;
        destination[offset + 2] = (byte)b;
        destination[offset + 3] = 255;
    }

    private static int ClampToByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        return value > 255 ? 255 : value;
    }
}
