using Seko.OwnAudioNET.Video.Mixing;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Convenience factory for creating a core <see cref="VideoMixer"/> backed by the default
/// <see cref="VideoTransportEngine"/> implementation.
/// </summary>
public static class VideoMixerFactory
{
    /// <summary>
    /// Creates a <see cref="VideoMixer"/> with a newly constructed <see cref="VideoTransportEngine"/>.
    /// </summary>
    public static VideoMixer Create(VideoTransportEngineConfig? config = null)
    {
        return new VideoMixer(new VideoTransportEngine(config), ownsEngine: true);
    }
}

