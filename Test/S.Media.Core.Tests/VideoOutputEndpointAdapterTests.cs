using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.Core.Video.Endpoints;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoOutputEndpointAdapterTests
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
    }

    private sealed class StubOutput : IVideoOutput
    {
        public VideoFormat OutputFormat { get; private set; }
        public IVideoMixer Mixer { get; private set; }
        public IMediaClock Clock { get; }
        public bool IsRunning { get; private set; }

        public StubOutput(VideoFormat fmt)
        {
            OutputFormat = fmt;
            Mixer = new VideoMixer(fmt);
            Clock = new StubClock();
        }

        public void Open(string title, int width, int height, VideoFormat format)
            => OutputFormat = format;

        public void OverridePresentationMixer(IVideoMixer mixer)
            => Mixer = mixer;

        public Task StartAsync(CancellationToken ct = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void Dispose() => Mixer.Dispose();
    }

    [Fact]
    public void WriteFrame_QueuesFrameIntoOutputMixer()
    {
        var fmt = new VideoFormat(1, 1, PixelFormat.Rgba32, 30, 1);
        using var output = new StubOutput(fmt);
        using var adapter = new VideoOutputEndpointAdapter(output, output.Mixer);

        adapter.WriteFrame(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.FromMilliseconds(10)));

        var frame = output.Mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));
        Assert.True(frame.HasValue);
        Assert.Equal(PixelFormat.Rgba32, frame.Value.PixelFormat);
    }

    [Fact]
    public void WriteFrame_ConvertsToOutputFormat()
    {
        var fmt = new VideoFormat(1, 1, PixelFormat.Bgra32, 30, 1);
        using var output = new StubOutput(fmt);
        using var adapter = new VideoOutputEndpointAdapter(output, output.Mixer);

        adapter.WriteFrame(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 9, 8, 7, 255 }, TimeSpan.FromMilliseconds(10)));

        var frame = output.Mixer.PresentNextFrame(TimeSpan.FromMilliseconds(20));
        Assert.True(frame.HasValue);
        Assert.Equal(PixelFormat.Bgra32, frame.Value.PixelFormat);
    }
}

