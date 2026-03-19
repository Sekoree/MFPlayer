using OwnaudioNET.Mixing;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Mixing;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Convenience factory for creating an <see cref="AudioVideoMixer"/> whose video transport follows
/// the master clock published by an existing <see cref="AudioMixer"/>.
/// </summary>
public static class AudioVideoMixerFactory
{
    /// <summary>
    /// Creates an <see cref="AudioVideoMixer"/> that keeps video synchronized to the supplied audio mixer.
    /// </summary>
    public static AudioVideoMixer Create(AudioMixer audioMixer, VideoTransportEngineConfig? config = null, bool ownsAudioMixer = false)
    {
        ArgumentNullException.ThrowIfNull(audioMixer);

        var normalizedConfig = (config ?? new VideoTransportEngineConfig()).CloneNormalized();
        normalizedConfig.ClockSyncMode = VideoTransportClockSyncMode.AudioLed;

        var videoClock = new MasterClockVideoClockAdapter(audioMixer.MasterClock);
        var videoTransport = new VideoTransportEngine(videoClock, normalizedConfig, ownsClock: false);
        var videoMixer = new VideoMixer(videoTransport, ownsEngine: true);
        return new AudioVideoMixer(audioMixer, videoMixer, ownsAudioMixer: ownsAudioMixer, ownsVideoMixer: true);
    }
}

