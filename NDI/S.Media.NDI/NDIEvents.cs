using NDILib;
using S.Media.Core.Media;

namespace S.Media.NDI;

/// <summary>
/// §4.17 / N7 — raised when an NDI capture loop encounters a FourCC the
/// library does not know how to decode. The offending frame is dropped and
/// future occurrences of the same FourCC are silently dropped (log-once), but
/// observers can use this event to surface the incompatibility to the user
/// without scraping logs.
/// </summary>
public sealed class NDIUnsupportedFourCcEventArgs : EventArgs
{
    /// <summary>Human-readable FourCC token (e.g. "P216", "XYZ1").</summary>
    public string FourCc { get; }

    /// <summary>Raw 32-bit FourCC value as reported by the NDI SDK.</summary>
    public uint RawFourCc { get; }

    /// <summary><see langword="true"/> for an audio FourCC, <see langword="false"/> for a video FourCC.</summary>
    public bool IsAudio { get; }

    public NDIUnsupportedFourCcEventArgs(uint rawFourCc, bool isAudio)
    {
        RawFourCc = rawFourCc;
        IsAudio = isAudio;
        // Decode the ASCII bytes that make up a FourCC so logs/UI don't have to.
        Span<char> buf = stackalloc char[4];
        buf[0] = (char)(rawFourCc        & 0xFF);
        buf[1] = (char)((rawFourCc >> 8) & 0xFF);
        buf[2] = (char)((rawFourCc >>16) & 0xFF);
        buf[3] = (char)((rawFourCc >>24) & 0xFF);
        FourCc = new string(buf);
    }
}

/// <summary>
/// §4.17 / N11 — raised when the NDI video source's dimensions, pixel format,
/// or frame rate change between two consecutive frames. Observers typically
/// use this to recreate GPU textures, resize windows, or reinitialise
/// downstream muxers/encoders. The event is not raised on the first frame —
/// there is no previous format to compare against.
/// </summary>
public sealed class NDIVideoFormatChangedEventArgs : EventArgs
{
    public VideoFormat PreviousFormat { get; }
    public VideoFormat NewFormat { get; }

    public NDIVideoFormatChangedEventArgs(VideoFormat previous, VideoFormat current)
    {
        PreviousFormat = previous;
        NewFormat = current;
    }
}