using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Optional sink capability interface used by <see cref="VideoMixer"/>.
/// Implement to request a specific pixel format for <see cref="IVideoSink.ReceiveFrame"/>.
/// </summary>
public interface IVideoSinkFormatPreference
{
    /// <summary>
    /// Preferred sink input pixel format.
    /// </summary>
    PixelFormat PreferredPixelFormat { get; }
}

