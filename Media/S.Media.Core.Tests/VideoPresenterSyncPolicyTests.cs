using S.Media.Core.Mixing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoPresenterSyncPolicyTests
{
    [Fact]
    public void StableMode_SelectsNewestFrame_AndReportsCoalescedDrops()
    {
        var options = VideoPresenterSyncPolicyOptions.Default;
        using var queue = BuildQueue([
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(30)], out var releaseCounter);

        var decision = VideoPresenterSyncPolicy.SelectNextFrame(queue, AudioVideoSyncMode.Stable, 0.02, options);

        Assert.NotNull(decision.Frame);
        Assert.Equal(TimeSpan.FromMilliseconds(30), decision.Frame!.PresentationTime);
        Assert.Equal(2, decision.CoalescedDrops);
        Assert.Equal(2, releaseCounter.Value);
        decision.Frame.Dispose();
    }

    [Fact]
    public void StrictMode_WaitsWhenFrameIsEarly()
    {
        var options = VideoPresenterSyncPolicyOptions.Default;
        using var queue = BuildQueue([TimeSpan.FromMilliseconds(40)], out _);

        var decision = VideoPresenterSyncPolicy.SelectNextFrame(queue, AudioVideoSyncMode.StrictAv, 0.0, options);

        Assert.Null(decision.Frame);
        Assert.True(decision.Delay >= options.MinDelay);
        Assert.True(decision.Delay <= options.StrictMaxWait);
    }

    [Fact]
    public void HybridMode_DropsStaleThenSelectsUsableFrame()
    {
        var options = VideoPresenterSyncPolicyOptions.Default;
        using var queue = BuildQueue([
            TimeSpan.FromMilliseconds(-300),
            TimeSpan.FromMilliseconds(1)], out var releaseCounter);

        var decision = VideoPresenterSyncPolicy.SelectNextFrame(queue, AudioVideoSyncMode.Hybrid, 0.0, options);

        Assert.NotNull(decision.Frame);
        Assert.Equal(TimeSpan.FromMilliseconds(1), decision.Frame!.PresentationTime);
        Assert.Equal(1, decision.LateDrops);
        Assert.Equal(1, releaseCounter.Value);
        decision.Frame.Dispose();
    }

    private static FrameQueueScope BuildQueue(IReadOnlyList<TimeSpan> ptsValues, out ReleaseCounter releaseCounter)
    {
        var queue = new Queue<VideoFrame>();
        var counter = new ReleaseCounter();
        releaseCounter = counter;
        foreach (var pts in ptsValues)
        {
            queue.Enqueue(CreateFrame(pts, () => counter.Value++));
        }

        return new FrameQueueScope(queue);
    }

    private static VideoFrame CreateFrame(TimeSpan pts, Action release)
    {
        return new VideoFrame(
            width: 2,
            height: 2,
            pixelFormat: VideoPixelFormat.Rgba32,
            pixelFormatData: new Rgba32PixelFormatData(),
            presentationTime: pts,
            isKeyFrame: true,
            plane0: new byte[2 * 2 * 4],
            plane0Stride: 2 * 4,
            releaseAction: _ => release());
    }

    private sealed class FrameQueueScope : IDisposable
    {
        private readonly Queue<VideoFrame> _queue;

        public FrameQueueScope(Queue<VideoFrame> queue)
        {
            _queue = queue;
        }

        public static implicit operator Queue<VideoFrame>(FrameQueueScope scope) => scope._queue;

        public void Dispose()
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue().Dispose();
            }
        }
    }

    private sealed class ReleaseCounter
    {
        public int Value;
    }
}

