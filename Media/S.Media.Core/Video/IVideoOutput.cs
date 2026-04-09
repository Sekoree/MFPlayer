using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// A video output display surface. Owns a window/render context and a clock.
/// Analogous to <see cref="Audio.IAudioOutput"/> for the video pipeline.
/// </summary>
public interface IVideoOutput : IMediaOutput
{
    /// <summary>Format describing the current output surface (resolution, pixel format, frame rate).</summary>
    VideoFormat OutputFormat { get; }

    /// <summary>The video mixer that manages channels and drives frame presentation.</summary>
    IVideoMixer Mixer { get; }

    /// <summary>
    /// Opens the output surface (creates a window / render context).
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="width">Initial window width in pixels.</param>
    /// <param name="height">Initial window height in pixels.</param>
    /// <param name="format">Requested output format (pixel format, frame rate hint).</param>
    void Open(string title, int width, int height, VideoFormat format);
}

