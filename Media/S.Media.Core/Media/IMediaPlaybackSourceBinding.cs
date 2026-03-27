using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Media;

// Optional bridge for media items that can provide ready-to-play mixer sources.
public interface IMediaPlaybackSourceBinding
{
    IReadOnlyList<IAudioSource> PlaybackAudioSources { get; }

    IReadOnlyList<IVideoSource> PlaybackVideoSources { get; }

    IVideoSource? InitialActiveVideoSource { get; }
}
