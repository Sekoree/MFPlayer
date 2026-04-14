using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoMixerTests
{
    private static VideoFormat FmtRgba30 => new(640, 360, PixelFormat.Rgba32, 30, 1);
    private static VideoFormat FmtBgra30 => new(640, 360, PixelFormat.Bgra32, 30, 1);

    private sealed class SpyOwner : IDisposable
    {
        public int DisposeCalls { get; private set; }
        public void Dispose() => DisposeCalls++;
    }

    private sealed class QueueVideoChannel : IVideoChannel
    {
        private readonly Queue<VideoFrame> _frames = new();
        public int FillCalls { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public int BufferDepth => 64;
        public int BufferAvailable => _frames.Count;
        public event EventHandler? EndOfStream { add { } remove { } }
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public VideoFormat SourceFormat { get; }
        public TimeSpan Position { get; private set; }

        public QueueVideoChannel(VideoFormat format) => SourceFormat = format;

        public void Enqueue(VideoFrame frame) => _frames.Enqueue(frame);

        public int FillBuffer(Span<VideoFrame> dest, int frameCount)
        {
            FillCalls++;
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

    private sealed class SpyPreferredFormatSink : IVideoSink, IVideoSinkFormatCapabilities
    {
        public string Name => nameof(SpyPreferredFormatSink);
        public bool IsRunning { get; set; } = true;
        public IReadOnlyList<PixelFormat> PreferredPixelFormats { get; }
        public VideoFrame? LastFrame { get; private set; }

        public SpyPreferredFormatSink(PixelFormat preferredPixelFormat)
            => PreferredPixelFormats = [preferredPixelFormat];

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ReceiveFrame(in VideoFrame frame) => LastFrame = frame;

        public void Dispose() { }
    }

    private sealed class SpyFormatCapabilitiesSink : IVideoSink, IVideoSinkFormatCapabilities
    {
        public string Name => nameof(SpyFormatCapabilitiesSink);
        public bool IsRunning { get; set; } = true;
        public IReadOnlyList<PixelFormat> PreferredPixelFormats { get; }
        public VideoFrame? LastFrame { get; private set; }

        public SpyFormatCapabilitiesSink(params PixelFormat[] formats)
            => PreferredPixelFormats = formats;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ReceiveFrame(in VideoFrame frame) => LastFrame = frame;
        public void Dispose() { }
    }

    private sealed class SpyRawPassthroughSink : IVideoSink, IVideoSinkFormatCapabilities
    {
        public string Name => nameof(SpyRawPassthroughSink);
        public bool IsRunning { get; set; } = true;
        public IReadOnlyList<PixelFormat> PreferredPixelFormats { get; } = [PixelFormat.Rgba32];
        public VideoFrame? LastFrame { get; private set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ReceiveFrame(in VideoFrame frame) => LastFrame = frame;
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
        mixer.RouteChannelToPrimaryOutput(ch.Id);

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
        mixer.RouteChannelToPrimaryOutput(ch.Id);

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
        mixer.RouteChannelToPrimaryOutput(ch.Id);

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
        mixer.RouteChannelToPrimaryOutput(ch.Id);

        // First frame is bootstrapped immediately by design.
        mixer.PresentNextFrame(TimeSpan.FromSeconds(1));
        // Next call should drop stale staged frames relative to clock.
        mixer.PresentNextFrame(TimeSpan.FromSeconds(1));

        Assert.True(mixer.DroppedStaleFrameCount > 0);
    }

    [Fact]
    public void PresentNextFrame_UnsupportedSourceFormat_PassesThroughWithoutFallbackConversion()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var ch = new QueueVideoChannel(new VideoFormat(2, 2, PixelFormat.Nv12, 30, 1));
        ch.Enqueue(new VideoFrame(2, 2, PixelFormat.Nv12, new byte[8], TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(ch);
        mixer.RouteChannelToPrimaryOutput(ch.Id);

        var frame = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.True(frame.HasValue);
        Assert.Equal(PixelFormat.Nv12, frame.Value.PixelFormat);
        Assert.Equal(0, mixer.FallbackConversionCount);
    }

    [Fact]
    public void PresentNextFrame_RoutesOneActiveChannelPerSink()
    {
        using var mixer = new VideoMixer(FmtRgba30);

        var chLeader = new QueueVideoChannel(FmtRgba30);
        chLeader.Enqueue(new VideoFrame(640, 360, PixelFormat.Rgba32, new byte[640 * 360 * 4], TimeSpan.FromMilliseconds(10)));

        var chSink = new QueueVideoChannel(new VideoFormat(640, 360, PixelFormat.Bgra32, 30, 1));
        var bgra = new byte[] { 10, 20, 30, 255 };
        chSink.Enqueue(new VideoFrame(1, 1, PixelFormat.Bgra32, bgra, TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(chLeader);
        mixer.AddChannel(chSink);
        mixer.RouteChannelToPrimaryOutput(chLeader.Id);

        var sink = new SpyVideoSink();
        mixer.RegisterSink(sink);
        mixer.SetActiveChannelForSink(sink, chSink.Id);

        var leader = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.True(leader.HasValue);
        Assert.Equal(PixelFormat.Rgba32, leader.Value.PixelFormat);
        Assert.Equal(1, sink.Calls);
        Assert.True(sink.LastFrame.HasValue);
        Assert.Equal(PixelFormat.Bgra32, sink.LastFrame.Value.PixelFormat);
        var s = sink.LastFrame.Value.Data.Span;
        Assert.Equal(10, s[0]);
        Assert.Equal(20, s[1]);
        Assert.Equal(30, s[2]);
        Assert.Equal(255, s[3]);
    }

    [Fact]
    public void PresentNextFrame_LeaderBgraOutput_DoesNotRoundTripConvert()
    {
        using var mixer = new VideoMixer(FmtBgra30);
        var ch = new QueueVideoChannel(FmtBgra30);
        var owner = new SpyOwner();
        var data = new byte[] { 1, 2, 3, 4 };
        ch.Enqueue(new VideoFrame(1, 1, PixelFormat.Bgra32, data, TimeSpan.FromMilliseconds(10), owner));

        mixer.AddChannel(ch);
        mixer.RouteChannelToPrimaryOutput(ch.Id);

        var frame = mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.True(frame.HasValue);
        Assert.Equal(PixelFormat.Bgra32, frame.Value.PixelFormat);
        Assert.Same(owner, frame.Value.MemoryOwner);
        Assert.Equal(0, owner.DisposeCalls);
    }

    [Fact]
    public void PresentNextFrame_SinkPreferredFormat_IsHonored()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var chLeader = new QueueVideoChannel(FmtRgba30);
        chLeader.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.FromMilliseconds(10)));

        var chSink = new QueueVideoChannel(new VideoFormat(1, 1, PixelFormat.Bgra32, 30, 1));
        chSink.Enqueue(new VideoFrame(1, 1, PixelFormat.Bgra32, new byte[] { 10, 20, 30, 255 }, TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(chLeader);
        mixer.AddChannel(chSink);
        mixer.RouteChannelToPrimaryOutput(chLeader.Id);

        var sink = new SpyPreferredFormatSink(PixelFormat.Bgra32);
        mixer.RegisterSink(sink);
        mixer.SetActiveChannelForSink(sink, chSink.Id);

        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.True(sink.LastFrame.HasValue);
        Assert.Equal(PixelFormat.Bgra32, sink.LastFrame.Value.PixelFormat);
    }

    [Fact]
    public void PresentNextFrame_SinkCapabilities_FallsBackToFirstSupportedFormat()
    {
        using var mixer = new VideoMixer(FmtRgba30);

        var chLeader = new QueueVideoChannel(FmtRgba30);
        chLeader.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.FromMilliseconds(10)));

        var chSink = new QueueVideoChannel(new VideoFormat(1, 1, PixelFormat.Bgra32, 30, 1));
        chSink.Enqueue(new VideoFrame(1, 1, PixelFormat.Bgra32, new byte[] { 10, 20, 30, 255 }, TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(chLeader);
        mixer.AddChannel(chSink);
        mixer.RouteChannelToPrimaryOutput(chLeader.Id);

        var sink = new SpyFormatCapabilitiesSink(PixelFormat.Nv12, PixelFormat.Bgra32);
        mixer.RegisterSink(sink);
        mixer.SetActiveChannelForSink(sink, chSink.Id);

        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.True(sink.LastFrame.HasValue);
        Assert.Equal(PixelFormat.Bgra32, sink.LastFrame.Value.PixelFormat);
    }

    [Fact]
    public void PresentNextFrame_SinkFormatDiagnostics_CountsHitAndMiss()
    {
        using var mixer = new VideoMixer(FmtRgba30);

        var chLeader = new QueueVideoChannel(FmtRgba30);
        chLeader.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.FromMilliseconds(10)));

        var chSink = new QueueVideoChannel(new VideoFormat(1, 1, PixelFormat.Bgra32, 30, 1));
        chSink.Enqueue(new VideoFrame(1, 1, PixelFormat.Bgra32, new byte[] { 10, 20, 30, 255 }, TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(chLeader);
        mixer.AddChannel(chSink);
        mixer.RouteChannelToPrimaryOutput(chLeader.Id);

        var sink = new SpyPreferredFormatSink(PixelFormat.Bgra32);
        mixer.RegisterSink(sink);
        mixer.SetActiveChannelForSink(sink, chSink.Id);

        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        var snap = mixer.GetDiagnosticsSnapshot();
        Assert.Equal(1, snap.SinkFormatHits);
        Assert.Equal(0, snap.SinkFormatMisses);

        chSink.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 10, 20, 30, 255 }, TimeSpan.FromMilliseconds(40)));
        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(60));

        snap = mixer.GetDiagnosticsSnapshot();
        Assert.True(snap.SinkFormatMisses > 0);
    }

    [Fact]
    public void PresentNextFrame_RouteDiagnostics_CountsRawPassthroughOnly()
    {
        using var mixer = new VideoMixer(FmtRgba30);

        var ch = new QueueVideoChannel(new VideoFormat(1, 1, PixelFormat.Bgra32, 30, 1));
        ch.Enqueue(new VideoFrame(1, 1, PixelFormat.Bgra32, new byte[] { 10, 20, 30, 255 }, TimeSpan.FromMilliseconds(10)));
        ch.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 30, 20, 10, 255 }, TimeSpan.FromMilliseconds(40)));

        mixer.AddChannel(ch);
        mixer.RouteChannelToPrimaryOutput(ch.Id);

        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));
        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(60));

        var snap = mixer.GetDiagnosticsSnapshot();
        Assert.Equal(0, snap.Converted);
        Assert.Equal(0, snap.SameFormatPassthrough);
        Assert.True(snap.RawMarkerPassthrough > 0);
    }

    [Fact]
    public void PresentNextFrame_SinkRawPassthroughMarker_SkipsMixerConversion()
    {
        using var mixer = new VideoMixer(FmtRgba30);

        var chLeader = new QueueVideoChannel(FmtRgba30);
        chLeader.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.FromMilliseconds(10)));

        var chSink = new QueueVideoChannel(new VideoFormat(2, 2, PixelFormat.Nv12, 30, 1));
        chSink.Enqueue(new VideoFrame(2, 2, PixelFormat.Nv12, new byte[] { 10, 20, 30, 40, 128, 64 }, TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(chLeader);
        mixer.AddChannel(chSink);
        mixer.RouteChannelToPrimaryOutput(chLeader.Id);

        var sink = new SpyRawPassthroughSink();
        mixer.RegisterSink(sink);
        mixer.SetActiveChannelForSink(sink, chSink.Id);

        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.True(sink.LastFrame.HasValue);
        Assert.Equal(PixelFormat.Nv12, sink.LastFrame.Value.PixelFormat);

        var snap = mixer.GetDiagnosticsSnapshot();
        Assert.True(snap.RawMarkerPassthrough > 0);
        Assert.Equal(0, snap.Converted);
    }

    [Fact]
    public void SetActiveChannelForSink_RerouteWithoutUnroute_AutoUnroutes()
    {
        using var mixer = new VideoMixer(FmtRgba30);
        var sink = new SpyVideoSink();
        var chA = new QueueVideoChannel(FmtRgba30);
        var chB = new QueueVideoChannel(FmtRgba30);

        mixer.AddChannel(chA);
        mixer.AddChannel(chB);
        mixer.RegisterSink(sink);

        mixer.SetActiveChannelForSink(sink, chA.Id);
        // Should NOT throw — auto-unroutes chA and routes chB instead
        mixer.SetActiveChannelForSink(sink, chB.Id);

        // Verify the re-route succeeded by pushing a frame through chB
        chB.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[4], TimeSpan.FromMilliseconds(10)));
        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));
        Assert.Equal(1, sink.Calls);
    }

    [Fact]
    public void PresentNextFrame_NonLeaderSharedByTwoSinks_PullsChannelOncePerTick()
    {
        using var mixer = new VideoMixer(FmtRgba30);

        var leader = new QueueVideoChannel(FmtRgba30);
        leader.Enqueue(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.FromMilliseconds(10)));

        var shared = new QueueVideoChannel(new VideoFormat(1, 1, PixelFormat.Bgra32, 30, 1));
        shared.Enqueue(new VideoFrame(1, 1, PixelFormat.Bgra32, new byte[] { 10, 20, 30, 255 }, TimeSpan.FromMilliseconds(10)));

        mixer.AddChannel(leader);
        mixer.AddChannel(shared);
        mixer.RouteChannelToPrimaryOutput(leader.Id);

        var sinkA = new SpyVideoSink();
        var sinkB = new SpyVideoSink();
        mixer.RegisterSink(sinkA);
        mixer.RegisterSink(sinkB);
        mixer.SetActiveChannelForSink(sinkA, shared.Id);
        mixer.SetActiveChannelForSink(sinkB, shared.Id);

        mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));

        Assert.Equal(1, shared.FillCalls);
        Assert.Equal(1, sinkA.Calls);
        Assert.Equal(1, sinkB.Calls);
    }
}
