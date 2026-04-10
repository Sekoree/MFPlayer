using S.Media.Core.Media;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoMixerTests
{
    private static VideoFormat FmtRgba30 => new(640, 360, PixelFormat.Rgba32, 30, 1);

    private sealed class QueueVideoChannel : IVideoChannel
    {
        private readonly Queue<VideoFrame> _frames = new();
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; }
        public TimeSpan Position { get; private set; }

        public QueueVideoChannel(VideoFormat format) => SourceFormat = format;

        public void Enqueue(VideoFrame frame) => _frames.Enqueue(frame);

        public int FillBuffer(Span<VideoFrame> dest, int frameCount)
        {
            if (frameCount <= 0 || _frames.Count == 0)
                return 0;

            var frame = _frames.Dequeue();
            dest[0] = frame;
            Position = frame.Pts;
            return 1;
        }

        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    private sealed class SpyVideoSink : IVideoSink
    {
        public string Name => nameof(SpyVideoSink);
        public bool IsRunning { get; set; } = true;
        public int Calls { get; private set; }
        public VideoFrame? LastFrame { get; private set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ReceiveFrame(in VideoFrame frame)
        {
            Calls++;
            LastFrame = frame;
        }

        public void Dispose() { }
    }

    [Fact]
    public void RegisterSink_IncrementsSinkCount()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var sink = new SpyVideoSink();

        mixer.RegisterSink(sink);

        Assert.Equal(1, mixer.SinkCount);
    }

    [Fact]
    public void SetActiveChannelForSink_UnregisteredSink_Throws()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var sink = new SpyVideoSink();

        Assert.Throws<InvalidOperationException>(() => mixer.SetActiveChannelForSink(sink, Guid.NewGuid()));
    }

    [Fact]
    public void PresentNextFrame_HoldsUntilDueByPts()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(FmtRgba30);
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromSeconds(1)));
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromSeconds(2)));

        mixer.AddChannel(ch);
        mixer.SetActiveChannel(ch.Id);

        var first = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(100));
        Assert.True(first.HasValue);
        Assert.Equal(TimeSpan.Zero, first.Value.Pts);

        var early = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(100));
        Assert.True(early.HasValue);
        Assert.Equal(TimeSpan.Zero, early.Value.Pts);
        Assert.True(mixer.HeldFrameCount > 0);

        var onTime = mixer.PresentNextFrame(TimeSpan.FromSeconds(1));
        Assert.True(onTime.HasValue);
        Assert.Equal(TimeSpan.FromSeconds(1), onTime.Value.Pts);
    }

    [Fact]
    public void PresentNextFrame_LargeInitialPts_StartsWithoutLongBlackDelay()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(FmtRgba30);
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMinutes(2)));

        mixer.AddChannel(ch);
        mixer.SetActiveChannel(ch.Id);

        var frame = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(10));

        Assert.True(frame.HasValue);
        Assert.True(frame.Value.Pts <= TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void PresentNextFrame_FirstFrameBootstrapsEvenIfClockIsAhead()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(FmtRgba30);
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(0)));

        mixer.AddChannel(ch);
        mixer.SetActiveChannel(ch.Id);

        // Simulate decode/video startup lag where clock is already ahead.
        var frame = mixer.PresentNextFrame(TimeSpan.FromSeconds(2));

        Assert.True(frame.HasValue);
    }

    [Fact]
    public void PresentNextFrame_DropsStaleStagedFrames()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(FmtRgba30);
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(10)));
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(20)));
        ch.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(30)));

        mixer.AddChannel(ch);
        mixer.SetActiveChannel(ch.Id);

        // First frame is bootstrapped immediately by design.
        mixer.PresentNextFrame(TimeSpan.FromSeconds(1));
        // Next call should drop stale staged frames relative to clock.
        mixer.PresentNextFrame(TimeSpan.FromSeconds(1));

        Assert.True(mixer.DroppedStaleFrameCount > 0);
    }

    [Fact]
    public void PresentNextFrame_UnsupportedSourceFormat_IncrementsFallbackCounter()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(new VideoFormat(2, 2, PixelFormat.Nv12, 30, 1));
        ch.Enqueue(new VideoFrame(2, 2, PixelFormat.Nv12, new byte[8], TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(ch);
        mixer.SetActiveChannel(ch.Id);

        var frame = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.True(frame.HasValue);
        Assert.True(mixer.FallbackConversionCount > 0);
    }

    [Fact]
    public void PresentNextFrame_RoutesOneActiveChannelPerSink()
    {
        using var mixer = new VideoMixer(FmtRgba30);

        var chLeader = new QueueVideoChannel(FmtRgba30);
        chLeader.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(10)));

        var chSink = new QueueVideoChannel(new VideoFormat(640, 360, PixelFormat.Bgra32, 30, 1));
        var bgra = new byte[] { 10, 20, 30, 255 }; // B,G,R,A -> should become R,G,B,A = 30,20,10,255
        chSink.Enqueue(new VideoFrame(1, 1, PixelFormat.Bgra32, bgra, TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(chLeader);
        mixer.AddChannel(chSink);
        mixer.SetActiveChannel(chLeader.Id);

        var sink = new SpyVideoSink();
        mixer.RegisterSink(sink);
        mixer.SetActiveChannelForSink(sink, chSink.Id);

        var leader = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.True(leader.HasValue);
        Assert.Equal(chLeader.Id, mixer.ActiveChannel!.Id);
        Assert.Equal(1, sink.Calls);
        Assert.True(sink.LastFrame.HasValue);
        Assert.Equal(PixelFormat.Rgba32, sink.LastFrame.Value.PixelFormat);
        var s = sink.LastFrame.Value.Data.Span;
        Assert.Equal(30, s[0]);
        Assert.Equal(20, s[1]);
        Assert.Equal(10, s[2]);
        Assert.Equal(255, s[3]);
    }
}

