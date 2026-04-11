using S.Media.Core.Clock;
using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Pull-source adapter that reads frames from a mixer using a clock position.
/// </summary>
public sealed class VideoMixerPullSource : IVideoFramePullSource
{
    private readonly IVideoMixer _mixer;
    private readonly IMediaClock _clock;

    public VideoMixerPullSource(IVideoMixer mixer, IMediaClock clock)
    {
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask<VideoFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled<VideoFrame?>(ct);

        return ValueTask.FromResult(_mixer.PresentNextFrame(_clock.Position));
    }
}

