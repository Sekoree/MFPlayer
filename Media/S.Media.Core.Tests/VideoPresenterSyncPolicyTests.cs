using S.Media.Core.Mixing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoPresenterSyncPolicyTests
{
    [Fact]
    public void RealtimeMode_SelectsNewestFrame_AndReportsCoalescedDrops()
    {
        var options = VideoSyncOptions.Default;
        using var queue = BuildQueue([
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(30)], out var releaseCounter);

        var decision = VideoSyncPolicy.SelectNextFrame(queue, AVSyncMode.Realtime, 0.02, options);

        Assert.NotNull(decision.Frame);
        Assert.Equal(TimeSpan.FromMilliseconds(30), decision.Frame!.PresentationTime);
        Assert.Equal(2, decision.CoalescedDrops);
        Assert.Equal(2, releaseCounter.Value);
        decision.Frame.Dispose();
    }

    [Fact]
    public void SyncedMode_WaitsWhenFrameIsEarly()
    {
        var options = VideoSyncOptions.Default;
        using var queue = BuildQueue([TimeSpan.FromMilliseconds(40)], out _);

        var decision = VideoSyncPolicy.SelectNextFrame(queue, AVSyncMode.Synced, 0.0, options);

        Assert.Null(decision.Frame);
        Assert.True(decision.Delay >= options.MinDelay);
        Assert.True(decision.Delay <= options.MaxWait);
    }

    [Fact]
    public void SyncedMode_DropsStaleThenSelectsUsableFrame()
    {
        var options = VideoSyncOptions.Default;
        using var queue = BuildQueue([
            TimeSpan.FromMilliseconds(-300),
            TimeSpan.FromMilliseconds(1)], out var releaseCounter);

        var decision = VideoSyncPolicy.SelectNextFrame(queue, AVSyncMode.Synced, 0.0, options);

        Assert.NotNull(decision.Frame);
        Assert.Equal(TimeSpan.FromMilliseconds(1), decision.Frame!.PresentationTime);
        Assert.Equal(1, decision.LateDrops);
        Assert.Equal(1, releaseCounter.Value);
        decision.Frame.Dispose();
    }

    [Fact]
    public void AudioLedMode_WaitsWhenFrameIsAheadOfAudioClock()
    {
        var options = VideoSyncOptions.Default;
        // Frame at 500ms, audio clock at 0ms → frame is way ahead
        using var queue = BuildQueue([TimeSpan.FromMilliseconds(500)], out _);

        var decision = VideoSyncPolicy.SelectNextFrame(queue, AVSyncMode.AudioLed, 0.0, options);

        // Should NOT present — frame is early
        Assert.Null(decision.Frame);
        // Should return a wait delay between 1ms and 50ms
        Assert.True(decision.Delay.TotalMilliseconds >= 1.0);
        Assert.True(decision.Delay.TotalMilliseconds <= 50.0);
        // Frame should still be in the queue
        Assert.Single((Queue<VideoFrame>)queue);
    }

    [Fact]
    public void AudioLedMode_PresentsFrameWhenAudioClockReachesIt()
    {
        var options = VideoSyncOptions.Default;
        // Frame at 100ms, audio clock at 100ms → frame is on time
        using var queue = BuildQueue([TimeSpan.FromMilliseconds(100)], out _);

        var decision = VideoSyncPolicy.SelectNextFrame(queue, AVSyncMode.AudioLed, 0.100, options);

        Assert.NotNull(decision.Frame);
        Assert.Equal(TimeSpan.FromMilliseconds(100), decision.Frame!.PresentationTime);
        decision.Frame.Dispose();
    }

    [Fact]
    public void AudioLedMode_DropsStaleThenPresentsUsable()
    {
        var options = VideoSyncOptions.Default;
        // Frame at -300ms is stale, frame at 50ms is usable when clock is at 50ms
        using var queue = BuildQueue([
            TimeSpan.FromMilliseconds(-300),
            TimeSpan.FromMilliseconds(50)], out var releaseCounter);

        var decision = VideoSyncPolicy.SelectNextFrame(queue, AVSyncMode.AudioLed, 0.050, options);

        Assert.NotNull(decision.Frame);
        Assert.Equal(TimeSpan.FromMilliseconds(50), decision.Frame!.PresentationTime);
        Assert.Equal(1, decision.LateDrops);
        Assert.Equal(1, releaseCounter.Value);
        decision.Frame.Dispose();
    }

    [Fact]
    public void AudioLedMode_CoalescesMultipleReadyFrames()
    {
        var options = VideoSyncOptions.Default;
        // Three frames all within tolerance of audio clock at 100ms
        using var queue = BuildQueue([
            TimeSpan.FromMilliseconds(90),
            TimeSpan.FromMilliseconds(95),
            TimeSpan.FromMilliseconds(100)], out var releaseCounter);

        var decision = VideoSyncPolicy.SelectNextFrame(queue, AVSyncMode.AudioLed, 0.100, options);

        Assert.NotNull(decision.Frame);
        // Should present the newest that's within tolerance
        Assert.Equal(TimeSpan.FromMilliseconds(100), decision.Frame!.PresentationTime);
        Assert.Equal(2, decision.CoalescedDrops);
        Assert.Equal(2, releaseCounter.Value);
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
