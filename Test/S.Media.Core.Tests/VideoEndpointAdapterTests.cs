using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.Core.Video.Endpoints;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoEndpointAdapterTests
{
    private sealed class SpyOwner : IDisposable
    {
        public int DisposeCalls;
        public void Dispose() => DisposeCalls++;
    }

    private sealed class SpySink : IVideoSink
    {
        public string Name => nameof(SpySink);
        public bool IsRunning { get; set; }
        public int Calls { get; private set; }
        public VideoFrame? LastFrame { get; private set; }

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

        public void ReceiveFrame(in VideoFrame frame)
        {
            Calls++;
            LastFrame = frame;
        }

        public void Dispose() { }
    }

    private sealed class SpyRawEndpoint : IVideoFrameEndpoint
    {
        public string Name => nameof(SpyRawEndpoint);
        public bool IsRunning { get; private set; }
        public IReadOnlyList<PixelFormat> SupportedPixelFormats { get; } = [PixelFormat.Rgba32];
        public VideoFrame? LastFrame { get; private set; }

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

        public void WriteFrame(in VideoFrame frame) => LastFrame = frame;
        public void Dispose() => IsRunning = false;
    }

    [Fact]
    public async Task VideoSinkEndpointAdapter_ConvertsToSupportedFormat()
    {
        var sink = new SpySink();
        using var adapter = new VideoSinkEndpointAdapter(sink, [PixelFormat.Bgra32]);

        await adapter.StartAsync();
        var src = new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 10, 20, 30, 255 }, TimeSpan.Zero);
        adapter.WriteFrame(src);

        Assert.Equal(1, sink.Calls);
        Assert.True(sink.LastFrame.HasValue);
        Assert.Equal(PixelFormat.Bgra32, sink.LastFrame.Value.PixelFormat);

        var snap = adapter.GetDiagnosticsSnapshot();
        Assert.Equal(0, snap.PassthroughFrames);
        Assert.Equal(1, snap.ConvertedFrames);
        Assert.Equal(0, snap.DroppedFrames);
    }

    [Fact]
    public async Task BufferedVideoFrameEndpoint_PushPullRoundtrip()
    {
        using var endpoint = new BufferedVideoFrameEndpoint("buf", [PixelFormat.Rgba32], capacity: 2);
        await endpoint.StartAsync();

        var src = new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.Zero);
        endpoint.WriteFrame(src);

        var pulled = await endpoint.ReadFrameAsync();
        Assert.True(pulled.HasValue);
        Assert.Equal(PixelFormat.Rgba32, pulled.Value.PixelFormat);
        Assert.Equal(4, pulled.Value.Data.Length);
    }

    [Fact]
    public async Task BufferedVideoFrameEndpoint_DropOldest_DisposesDroppedOwner()
    {
        using var endpoint = new BufferedVideoFrameEndpoint("buf", [PixelFormat.Rgba32], capacity: 1);
        await endpoint.StartAsync();

        var droppedOwner = new SpyOwner();
        endpoint.WriteFrame(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 4 }, TimeSpan.Zero, droppedOwner));
        endpoint.WriteFrame(new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 5, 6, 7, 8 }, TimeSpan.FromMilliseconds(1)));

        Assert.Equal(1, droppedOwner.DisposeCalls);

        var pulled = await endpoint.ReadFrameAsync();
        Assert.True(pulled.HasValue);
        Assert.Equal(TimeSpan.FromMilliseconds(1), pulled.Value.Pts);
    }

    [Fact]
    public void VideoSink_DefaultDiagnosticsSnapshot_IsEmpty()
    {
        IVideoSink sink = new SpySink();

        var snap = sink.GetDiagnosticsSnapshot();

        Assert.Equal(0, snap.PassthroughFrames);
        Assert.Equal(0, snap.ConvertedFrames);
        Assert.Equal(0, snap.DroppedFrames);
        Assert.Equal(0, snap.QueueDepth);
        Assert.Equal(0, snap.QueueDrops);
    }

    [Fact]
    public async Task VideoSinkEndpointAdapter_TracksPassthroughAndDropped()
    {
        var sink = new SpySink();
        using var adapter = new VideoSinkEndpointAdapter(sink, [PixelFormat.Rgba32]);

        var src = new VideoFrame(1, 1, PixelFormat.Rgba32, new byte[] { 1, 2, 3, 255 }, TimeSpan.Zero);

        // Not running yet -> dropped.
        adapter.WriteFrame(src);

        await adapter.StartAsync();
        adapter.WriteFrame(src);

        var snap = adapter.GetDiagnosticsSnapshot();
        Assert.Equal(1, snap.PassthroughFrames);
        Assert.Equal(0, snap.ConvertedFrames);
        Assert.Equal(1, snap.DroppedFrames);
    }

    [Fact]
    public async Task VideoEndpointSinkAdapter_ForwardsFrames()
    {
        using var endpoint = new SpyRawEndpoint();
        using var sink = new VideoEndpointSinkAdapter(endpoint);

        await sink.StartAsync();

        var src = new VideoFrame(2, 2, PixelFormat.Nv12, new byte[] { 10, 20, 30, 40, 128, 64 }, TimeSpan.FromMilliseconds(10));
        sink.ReceiveFrame(src);

        Assert.True(endpoint.LastFrame.HasValue);
        Assert.Equal(PixelFormat.Nv12, endpoint.LastFrame.Value.PixelFormat);
    }
}
