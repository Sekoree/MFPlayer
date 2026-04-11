using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Pull-source adapter for existing <see cref="IVideoOutput"/> implementations.
/// This allows endpoint-pull flows without changing output APIs.
/// </summary>
public sealed class VideoOutputPullSourceAdapter : IVideoFramePullSource
{
    private readonly IVideoOutput _output;

    public VideoOutputPullSourceAdapter(IVideoOutput output)
        => _output = output ?? throw new ArgumentNullException(nameof(output));

    public ValueTask<VideoFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled<VideoFrame?>(ct);

        return ValueTask.FromResult(_output.Mixer.PresentNextFrame(_output.Clock.Position));
    }
}

