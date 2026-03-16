namespace Seko.OwnAudioNET.Video;

/// <summary>Pixel format of a decoded video frame.</summary>
public enum VideoPixelFormat
{
    /// <summary>32-bit RGBA, 8 bits per channel, in <c>R G B A</c> byte order.</summary>
    Rgba32,

    /// <summary>Planar 4:2:0 with separate Y, U, V planes (8-bit components).</summary>
    Yuv420p,

    /// <summary>Semi-planar 4:2:0 with Y plane + interleaved UV plane (8-bit components).</summary>
    Nv12,

    /// <summary>Planar 4:2:2 with separate Y, U, V planes (8-bit components, chroma at full height).</summary>
    Yuv422p,

    /// <summary>Planar 4:2:2 with separate Y, U, V planes (10-bit LE stored as uint16, lower 10 bits, chroma at full height).</summary>
    Yuv422p10le,

    /// <summary>Semi-planar 4:2:0 with Y plane + interleaved UV plane (10-bit MSB-aligned, stored as uint16).</summary>
    P010le,

    /// <summary>Planar 4:2:0 with separate Y, U, V planes (10-bit LE stored as uint16, lower 10 bits).</summary>
    Yuv420p10le,

    /// <summary>Planar 4:4:4 with separate Y, U, V planes (8-bit components, all planes full size).</summary>
    Yuv444p,

    /// <summary>Planar 4:4:4 with separate Y, U, V planes (10-bit LE stored as uint16, lower 10 bits, all planes full size).</summary>
    Yuv444p10le,
}

