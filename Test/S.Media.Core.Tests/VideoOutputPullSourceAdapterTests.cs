using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoOutputPullSourceAdapterTests
{
    private sealed class StubClock : IMediaClock
    {
        public TimeSpan Position { get; set; }
        public double SampleRate => 30;
        public bool IsRunning => true;
        public event Action<TimeSpan>? Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Reset() => Position = TimeSpan.Zero;
        public void Dispose() { }
    }

    private sealed class StubMixer : IVideoMixer
    {
        public VideoFormat OutputFormat => new(640, 360, PixelFormat.Rgba32, 30, 1);
        public int ChannelCount => 0;
        public int SinkCount => 0;

        public TimeSpan LastClockPosition { get; private set; }
        public VideoFrame? NextFrame { get; set; }

        public void AddChannel(IVideoChannel channel) { }
        public void RemoveChannel(Guid channelId) { }
        public void RouteChannelToPrimaryOutput(Guid channelId) { }
        public void UnroutePrimaryOutput() { }
        public void RegisterSink(IVideoSink sink) { }
        public void UnregisterSink(IVideoSink sink) { }
        public void SetActiveChannelForSink(IVideoSink sink, Guid? channelId) { }

        public VideoFrame? PresentNextFrame(TimeSpan clockPosition)
        {
            LastClockPosition = clockPosition;
            return NextFrame;
        }

        public void Dispose() { }
    }

    private sealed class StubOutput : IVideoOutput
    {
        public VideoFormat OutputFormat => new(640, 360, PixelFormat.Rgba32, 30, 1);
        public IVideoMixer Mixer { get; }
        public IMediaClock Clock { get; }
        public bool IsRunning => true;

        public StubOutput(IVideoMixer mixer, IMediaClock clock)
        {
            Mixer = mixer;
            Clock = clock;
        }

        public void Open(string title, int width, int height, VideoFormat format) { }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public async Task ReadFrameAsync_UsesOutputClockAndMixer()
    {
        var mixer = new StubMixer
        {
            NextFrame = new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.FromMilliseconds(10))
        };
        var clock = new StubClock { Position = TimeSpan.FromMilliseconds(123) };
        var adapter = new VideoFramePullSource(mixer, clock);

        var frame = await adapter.ReadFrameAsync();

        Assert.True(frame.HasValue);
        Assert.Equal(TimeSpan.FromMilliseconds(123), mixer.LastClockPosition);
        Assert.Equal(PixelFormat.Rgba32, frame.Value.PixelFormat);
    }
}
