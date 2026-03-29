using S.Media.Core.Media;
using S.Media.Core.Mixing;

namespace S.Media.Core.Playback;

/// <summary>
/// Simplified media player interface. Extends <see cref="IAVMixer"/> with
/// a convenient <see cref="Play"/> method for one-call media playback.
/// Output management (AddAudioOutput, AddVideoOutput, etc.) is inherited from IAVMixer.
/// </summary>
public interface IMediaPlayer : IAVMixer
{
    /// <summary>
    /// Consumer-facing playback config. When set, <see cref="Play"/> will use this config
    /// to start the playback pump threads automatically.
    /// </summary>
    AVMixerConfig? PlaybackConfig { get; set; }

    /// <summary>
    /// Attaches the media item's sources to the mixer and starts playback,
    /// including pump threads via <see cref="IAVMixer.StartPlayback"/>.
    /// </summary>
    int Play(IMediaItem media);
}
