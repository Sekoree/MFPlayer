using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

public interface IMediaPlayer : IAudioVideoMixer
{
    int Play(IMediaItem media);


    int AddAudioOutput(IAudioOutput output);

    int RemoveAudioOutput(IAudioOutput output);

    int AddVideoOutput(IVideoOutput output);

    int RemoveVideoOutput(IVideoOutput output);

    IReadOnlyList<IAudioOutput> AudioOutputs { get; }

    IReadOnlyList<IVideoOutput> VideoOutputs { get; }
}

