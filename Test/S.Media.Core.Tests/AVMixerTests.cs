using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AVMixerTests
{
    [Fact]
    public void Ctor_WithFormats_CreatesOwnedMixers()
    {
        using var av = new AVMixer(new AudioFormat(48000, 2), new VideoFormat(640, 360, PixelFormat.Rgba32, 30, 1));

        var audioSink = new StubAudioSink("A");
        var videoSink = new StubVideoSink("V");

        av.RegisterAudioSink(audioSink, channels: 2);
        av.RegisterVideoSink(videoSink);
    }

    [Fact]
    public void Ctor_WithFormats_AllowsChannelRegistration()
    {
        using var av = new AVMixer(new AudioFormat(48000, 2), new VideoFormat(640, 360, PixelFormat.Rgba32, 30, 1));

        var audioChannel = new DummyAudioChannel();
        var videoChannel = new DummyVideoChannel();

        av.AddAudioChannel(audioChannel, ChannelRouteMap.Identity(2));
        av.AddVideoChannel(videoChannel);

        av.RemoveAudioChannel(audioChannel.Id);
        av.RemoveVideoChannel(videoChannel.Id);
    }

    [Fact]
    public void Delegates_ToUnderlyingMixers()
    {
        var audio = new SpyAudioMixer();
        var video = new SpyVideoMixer();
        using var av = new AVMixer(audio, video);

        var audioChannel = new DummyAudioChannel();
        var videoChannel = new DummyVideoChannel();

        av.AddAudioChannel(audioChannel, ChannelRouteMap.Identity(2));
        av.AddVideoChannel(videoChannel);

        Assert.Equal(1, audio.AddChannelCalls);
        Assert.Equal(1, video.AddChannelCalls);

        av.RemoveAudioChannel(audioChannel.Id);
        av.RemoveVideoChannel(videoChannel.Id);

        Assert.Equal(1, audio.RemoveChannelCalls);
        Assert.Equal(1, video.RemoveChannelCalls);
    }

    [Fact]
    public void Dispose_DoesNotDisposeExternalMixers_ByDefault()
    {
        var audio = new SpyAudioMixer();
        var video = new SpyVideoMixer();

        var av = new AVMixer(audio, video);
        av.Dispose();

        Assert.False(audio.Disposed);
        Assert.False(video.Disposed);
    }

    [Fact]
    public void RouteVideoChannelToSinks_DelegatesForAllSinks()
    {
        var audio = new SpyAudioMixer();
        var video = new SpyVideoMixer();
        using var av = new AVMixer(audio, video);

        var sinkA = new StubVideoSink("A");
        var sinkB = new StubVideoSink("B");

        av.RouteVideoChannelToSinks(Guid.NewGuid(), [sinkA, sinkB]);

        Assert.Equal(2, video.SetActiveChannelForSinkCalls);
    }

    [Fact]
    public void RouteAudioChannelToSinks_DelegatesForAllRoutes()
    {
        var audio = new SpyAudioMixer();
        var video = new SpyVideoMixer();
        using var av = new AVMixer(audio, video);

        var sinkA = new StubAudioSink("A");
        var sinkB = new StubAudioSink("B");

        av.RouteAudioChannelToSinks(Guid.NewGuid(),
        [
            (sinkA, ChannelRouteMap.Identity(2)),
            (sinkB, ChannelRouteMap.Identity(2))
        ]);

        Assert.Equal(2, audio.RouteToCalls);
    }

    [Fact]
    public void RouteVideoChannelToEndpoint_AndUnroute_DelegatesToVideoMixer()
    {
        var audio = new SpyAudioMixer();
        var video = new SpyVideoMixer();
        using var av = new AVMixer(audio, video);

        var endpoint = new StubVideoEndpoint("VEP", [PixelFormat.Rgba32]);
        var channelId = Guid.NewGuid();

        av.RegisterVideoEndpoint(endpoint);
        av.RouteVideoChannelToEndpoint(channelId, endpoint);
        av.UnrouteVideoChannelFromEndpoint(endpoint);

        Assert.Equal(2, video.SetActiveChannelForSinkCalls);
        Assert.Equal(channelId, video.LastSinkRouteChannelId);
        Assert.Null(video.LastSinkUnrouteChannelId);
    }

    [Fact]
    public void RouteVideoChannelToEndpoint_UnregisteredEndpoint_Throws()
    {
        var audio = new SpyAudioMixer();
        var video = new SpyVideoMixer();
        using var av = new AVMixer(audio, video);

        var endpoint = new StubVideoEndpoint("VEP", [PixelFormat.Rgba32]);

        Assert.Throws<InvalidOperationException>(() => av.RouteVideoChannelToEndpoint(Guid.NewGuid(), endpoint));
        Assert.Throws<InvalidOperationException>(() => av.UnrouteVideoChannelFromEndpoint(endpoint));
    }

    [Fact]
    public void RouteVideoChannelToEndpoints_DelegatesForAllEndpoints()
    {
        var audio = new SpyAudioMixer();
        var video = new SpyVideoMixer();
        using var av = new AVMixer(audio, video);

        var epA = new StubVideoEndpoint("A", [PixelFormat.Rgba32]);
        var epB = new StubVideoEndpoint("B", [PixelFormat.Rgba32]);

        av.RegisterVideoEndpoint(epA);
        av.RegisterVideoEndpoint(epB);

        av.RouteVideoChannelToEndpoints(Guid.NewGuid(), [epA, epB]);

        Assert.Equal(2, video.SetActiveChannelForSinkCalls);
    }


    private sealed class DummyAudioChannel : IAudioChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public AudioFormat SourceFormat { get; } = new(48000, 2);
        public float Volume { get; set; } = 1f;
        public TimeSpan Position => TimeSpan.Zero;
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun
        {
            add { }
            remove { }
        }
        public event EventHandler? EndOfStream { add { } remove { } }

        public int FillBuffer(Span<float> dest, int frameCount)
        {
            dest.Clear();
            return frameCount;
        }
        public void Seek(TimeSpan position) { }
        public ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default) => ValueTask.CompletedTask;
        public bool TryWrite(ReadOnlySpan<float> frames) => true;
        public void Dispose() { }
    }

    private sealed class DummyVideoChannel : IVideoChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; } = new(640, 360, PixelFormat.Rgba32, 30, 1);
        public TimeSpan Position => TimeSpan.Zero;
        public int BufferDepth => 4;
        public int BufferAvailable => 0;
        public event EventHandler? EndOfStream { add { } remove { } }
        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    private sealed class SpyAudioMixer : IAudioMixer
    {
        public bool Disposed { get; private set; }
        public int AddChannelCalls { get; private set; }
        public int RemoveChannelCalls { get; private set; }
        public int RouteToCalls { get; private set; }
        public AudioFormat LeaderFormat { get; } = new(48000, 2);
        public float MasterVolume { get; set; }
        public int ChannelCount => 0;
        public IReadOnlyList<float> PeakLevels { get; } = [0f, 0f];
        public ChannelFallback DefaultFallback => ChannelFallback.Silent;
        public void AddChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null) => AddChannelCalls++;
        public void RemoveChannel(Guid channelId) => RemoveChannelCalls++;
        public void SetChannelTimeOffset(Guid channelId, TimeSpan offset) { }
        public TimeSpan GetChannelTimeOffset(Guid channelId) => TimeSpan.Zero;
        public void RouteTo(Guid channelId, IAudioSink sink, ChannelRouteMap routeMap) => RouteToCalls++;
        public void UnrouteTo(Guid channelId, IAudioSink sink) { }
        public void RegisterSink(IAudioSink sink, int channels = 0) { }
        public void UnregisterSink(IAudioSink sink) { }
        public void FillOutputBuffer(Span<float> dest, int frameCount, AudioFormat outputFormat) { }
        public void Dispose() => Disposed = true;
    }

    private sealed class SpyVideoMixer : IVideoMixer
    {
        public bool Disposed { get; private set; }
        public int AddChannelCalls { get; private set; }
        public int RemoveChannelCalls { get; private set; }
        public int RoutePrimaryCalls { get; private set; }
        public int UnroutePrimaryCalls { get; private set; }
        public int SetActiveChannelForSinkCalls { get; private set; }
        public Guid? LastSinkRouteChannelId { get; private set; }
        public Guid? LastSinkUnrouteChannelId { get; private set; }
        public VideoFormat OutputFormat { get; } = new(640, 360, PixelFormat.Rgba32, 30, 1);
        public int ChannelCount => 0;
        public int SinkCount => 0;
        public void AddChannel(IVideoChannel channel) => AddChannelCalls++;
        public void RemoveChannel(Guid channelId) => RemoveChannelCalls++;
        public void SetChannelTimeOffset(Guid channelId, TimeSpan offset) { }
        public TimeSpan GetChannelTimeOffset(Guid channelId) => TimeSpan.Zero;
        public void RouteChannelToPrimaryOutput(Guid channelId) => RoutePrimaryCalls++;
        public void UnroutePrimaryOutput() => UnroutePrimaryCalls++;
        public void RegisterSink(IVideoSink sink) { }
        public void UnregisterSink(IVideoSink sink) { }
        public void SetActiveChannelForSink(IVideoSink sink, Guid? channelId)
        {
            SetActiveChannelForSinkCalls++;
            if (channelId.HasValue)
                LastSinkRouteChannelId = channelId;
            else
                LastSinkUnrouteChannelId = channelId;
        }
        public VideoFrame? PresentNextFrame(TimeSpan clockPosition) => null;
        public void Dispose() => Disposed = true;
    }

    private sealed class StubAudioSink(string name) : IAudioSink
    {
        public string Name { get; } = name;
        public bool IsRunning => true;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat sourceFormat) { }
        public void Dispose() { }
    }

    private sealed class StubVideoSink(string name) : IVideoSink
    {
        public string Name { get; } = name;
        public bool IsRunning => true;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void ReceiveFrame(in VideoFrame frame) { }
        public void Dispose() { }
    }

    private sealed class StubVideoEndpoint(string name, IReadOnlyList<PixelFormat> supportedPixelFormats) : IVideoFrameEndpoint
    {
        public string Name { get; } = name;
        public bool IsRunning => true;
        public IReadOnlyList<PixelFormat> SupportedPixelFormats { get; } = supportedPixelFormats;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void WriteFrame(in VideoFrame frame) { }
        public void Dispose() { }
    }
}
