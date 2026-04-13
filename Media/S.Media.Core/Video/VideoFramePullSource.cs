using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Pull-source adapter that reads frames from a mixer driven by a clock.
/// Replaces the former VideoMixerPullSource and VideoOutputPullSourceAdapter.
/// </summary>
public sealed class VideoFramePullSource : IVideoFramePullSource
{
    private readonly IVideoMixer _mixer;
    private readonly IMediaClock _clock;

    public VideoFramePullSource(IVideoMixer mixer, IMediaClock clock)
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

