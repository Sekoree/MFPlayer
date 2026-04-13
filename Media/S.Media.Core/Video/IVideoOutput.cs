using System.ComponentModel;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// A video output display surface. Owns a window/render context and a clock.
/// Routing is managed externally via <see cref="IVideoMixer"/> — the output itself
/// does not expose a mixer; wire channels through <see cref="Mixing.IAVMixer"/> instead.
/// </summary>
public interface IVideoOutput : IMediaOutput
{
    /// <summary>Format describing the current output surface (resolution, pixel format, frame rate).</summary>
    VideoFormat OutputFormat { get; }

    /// <summary>
    /// Opens the output surface (creates a window / render context).
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="width">Initial window width in pixels.</param>
    /// <param name="height">Initial window height in pixels.</param>
    /// <param name="format">Requested output format (pixel format, frame rate hint).</param>
    void Open(string title, int width, int height, VideoFormat format);

    /// <summary>
    /// Replaces the presentation mixer used by the render loop.
    /// Called by <see cref="Mixing.IAVMixer"/>; not intended for direct app use.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    void OverridePresentationMixer(IVideoMixer mixer);
}
