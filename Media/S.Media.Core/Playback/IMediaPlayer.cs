using S.Media.Core.Media;
using S.Media.Core.Mixing;

namespace S.Media.Core.Playback;

/// <summary>
/// Simplified media player interface. Extends <see cref="IAudioVideoMixer"/> with
/// a convenient <see cref="Play"/> method for one-call media playback.
/// Output management (AddAudioOutput, AddVideoOutput, etc.) is inherited from IAudioVideoMixer.
/// </summary>
public interface IMediaPlayer : IAudioVideoMixer
{
    /// <summary>
    /// Attaches the media item's sources to the mixer and starts playback.
    /// </summary>
    int Play(IMediaItem media);
}
