namespace Seko.OwnAudioNET.Video;

/// <summary>Pixel format of a decoded video frame.</summary>
public enum VideoPixelFormat
{
    /// <summary>32-bit RGBA, 8 bits per channel, in <c>R G B A</c> byte order.</summary>
    Rgba32,

    /// <summary>Planar 4:2:0 with separate Y, U, V planes (8-bit components).</summary>
    Yuv420p,

    /// <summary>Semi-planar 4:2:0 with Y plane + interleaved UV plane (8-bit components).</summary>
    Nv12
}

