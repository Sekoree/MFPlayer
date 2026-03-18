namespace Seko.OwnAudioNET.Video.OpenGL;

/// <summary>
/// CPU-side YUV → RGBA conversion helpers and 10-bit down-scaling utilities.
/// <para>
/// These are used as a software fallback when GPU-side YUV conversion is
/// unavailable (e.g. the driver does not support R16 textures or uniform
/// sampler binding on OpenGL ES 2.0).
/// </para>
/// </summary>
public static class VideoGlYuvConverter
{
    /// <summary>Convert an NV12 frame (Y plane + interleaved UV plane) to packed RGBA.</summary>
    public static void ConvertNv12ToRgba(
        byte[] yPlane, byte[] uvPlane,
        int width, int height,
        byte[] destination)
    {
        var uvWidth = (width + 1) / 2;
        for (var y = 0; y < height; y++)
        {
            var yRowOffset  = y * width;
            var uvRowOffset = (y / 2) * uvWidth * 2;
            var dstRowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var yValue  = yPlane[yRowOffset + x];
                var uvOff   = uvRowOffset + (x / 2) * 2;
                var uValue  = uvPlane[uvOff];
                var vValue  = uvPlane[uvOff + 1];
                WriteRgbaPixel(destination, dstRowOffset + x * 4, yValue, uValue, vValue);
            }
        }
    }

    /// <summary>Convert a YUV 4:2:0 planar frame to packed RGBA.</summary>
    public static void ConvertYuv420pToRgba(
        byte[] yPlane, byte[] uPlane, byte[] vPlane,
        int width, int height,
        byte[] destination)
    {
        var chromaWidth = (width + 1) / 2;
        for (var y = 0; y < height; y++)
        {
            var yRowOffset  = y * width;
            var uvRowOffset = (y / 2) * chromaWidth;
            var dstRowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var yValue = yPlane[yRowOffset + x];
                var uvOff  = uvRowOffset + (x / 2);
                WriteRgbaPixel(destination, dstRowOffset + x * 4, yValue, uPlane[uvOff], vPlane[uvOff]);
            }
        }
    }

    /// <summary>Convert a YUV 4:2:2 planar frame to packed RGBA.</summary>
    public static void ConvertYuv422pToRgba(
        byte[] yPlane, byte[] uPlane, byte[] vPlane,
        int width, int height,
        byte[] destination)
    {
        var chromaWidth = (width + 1) / 2;
        for (var y = 0; y < height; y++)
        {
            var yRowOffset  = y * width;
            var uvRowOffset = y * chromaWidth;
            var dstRowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var yValue = yPlane[yRowOffset + x];
                var uvOff  = uvRowOffset + (x / 2);
                WriteRgbaPixel(destination, dstRowOffset + x * 4, yValue, uPlane[uvOff], vPlane[uvOff]);
            }
        }
    }

    /// <summary>Convert a YUV 4:4:4 planar frame to packed RGBA.</summary>
    public static void ConvertYuv444pToRgba(
        byte[] yPlane, byte[] uPlane, byte[] vPlane,
        int width, int height,
        byte[] destination)
    {
        for (var y = 0; y < height; y++)
        {
            var rowOffset    = y * width;
            var dstRowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var idx = rowOffset + x;
                WriteRgbaPixel(destination, dstRowOffset + x * 4, yPlane[idx], uPlane[idx], vPlane[idx]);
            }
        }
    }

    // ── 10-bit down-scaling helpers ──────────────────────────────────────────

    /// <summary>
    /// Converts a 16-bit-per-sample plane (10 significant LSBs) to 8-bit
    /// by shifting right by 2.  Used as a fallback when R16 textures are unavailable.
    /// </summary>
    public static byte[]? Downscale10BitTo8Bit(byte[] source16, int width, int height, ref byte[]? scratch)
    {
        var pixelCount = width * height;
        if (source16.Length < pixelCount * 2)
            return null;

        if (scratch == null || scratch.Length < pixelCount)
            scratch = new byte[pixelCount];

        for (var i = 0; i < pixelCount; i++)
        {
            var value = (ushort)(source16[i * 2] | (source16[i * 2 + 1] << 8));
            scratch[i] = (byte)(value >> 2);
        }

        return scratch;
    }

    /// <summary>
    /// Converts a P010LE luma plane (10 significant MSBs stored in the high byte of
    /// each uint16) to 8-bit by reading only the high byte.
    /// </summary>
    public static byte[]? Downscale10BitMsbTo8Bit(byte[] source16, int width, int height, ref byte[]? scratch)
    {
        var pixelCount = width * height;
        if (source16.Length < pixelCount * 2)
            return null;

        if (scratch == null || scratch.Length < pixelCount)
            scratch = new byte[pixelCount];

        for (var i = 0; i < pixelCount; i++)
            scratch[i] = source16[i * 2 + 1];

        return scratch;
    }

    /// <summary>
    /// Converts a P010LE chroma plane (interleaved U/V, each 10-bit MSB-aligned uint16)
    /// to an 8-bit interleaved RG byte array by reading only the high bytes.
    /// </summary>
    public static byte[]? Downscale10BitMsbDualTo8Bit(
        byte[] source16, int chromaWidth, int chromaHeight, ref byte[]? scratch)
    {
        var texelCount = chromaWidth * chromaHeight;
        var srcBytes   = texelCount * 4;
        if (source16.Length < srcBytes)
            return null;

        var dstBytes = texelCount * 2;
        if (scratch == null || scratch.Length < dstBytes)
            scratch = new byte[dstBytes];

        for (var i = 0; i < texelCount; i++)
        {
            scratch[i * 2]     = source16[i * 4 + 1];
            scratch[i * 2 + 1] = source16[i * 4 + 3];
        }

        return scratch;
    }

    // ── Internal pixel writer ────────────────────────────────────────────────

    /// <summary>
    /// Writes one RGBA pixel to <paramref name="destination"/> using BT.601 limited-range
    /// YCbCr → RGB coefficients.
    /// </summary>
    public static void WriteRgbaPixel(byte[] destination, int offset, int y, int u, int v)
    {
        var c = y - 16;
        var d = u - 128;
        var e = v - 128;

        var r = (298 * c + 409 * e + 128) >> 8;
        var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
        var b = (298 * c + 516 * d + 128) >> 8;

        destination[offset]     = (byte)Math.Clamp(r, 0, 255);
        destination[offset + 1] = (byte)Math.Clamp(g, 0, 255);
        destination[offset + 2] = (byte)Math.Clamp(b, 0, 255);
        destination[offset + 3] = 255;
    }
}

