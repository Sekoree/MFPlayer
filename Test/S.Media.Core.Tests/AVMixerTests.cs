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

        Assert.Equal(2, av.Audio.LeaderFormat.Channels);
        Assert.Equal(PixelFormat.Rgba32, av.Video.OutputFormat.PixelFormat);
    }

    [Fact]
    public void Delegates_ToUnderlyingMixers()
    {
        using var av = new AVMixer(new AudioFormat(48000, 2), new VideoFormat(640, 360, PixelFormat.Rgba32, 30, 1));

        var audioChannel = new DummyAudioChannel();
        var videoChannel = new DummyVideoChannel();

        av.AddAudioChannel(audioChannel, ChannelRouteMap.Identity(2));
        av.AddVideoChannel(videoChannel);
        av.SetActiveVideoChannel(videoChannel.Id);

        Assert.Equal(1, av.Audio.ChannelCount);
        Assert.Equal(1, av.Video.ChannelCount);
        Assert.Equal(videoChannel.Id, av.Video.ActiveChannel?.Id);

        av.RemoveAudioChannel(audioChannel.Id);
        av.RemoveVideoChannel(videoChannel.Id);

        Assert.Equal(0, av.Audio.ChannelCount);
        Assert.Equal(0, av.Video.ChannelCount);
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
    public void ResolveMasterPosition_UsesSelectedPolicy()
    {
        using var av = new AVMixer(new AudioFormat(48000, 2), new VideoFormat(640, 360, PixelFormat.Rgba32, 30, 1));

        var audioPos = TimeSpan.FromSeconds(1);
        var videoPos = TimeSpan.FromSeconds(2);
        var extPos = TimeSpan.FromSeconds(3);

        av.MasterPolicy = IAVMixer.ClockMasterPolicy.Audio;
        Assert.Equal(audioPos, av.ResolveMasterPosition(audioPos, videoPos, extPos));

        av.MasterPolicy = IAVMixer.ClockMasterPolicy.Video;
        Assert.Equal(videoPos, av.ResolveMasterPosition(audioPos, videoPos, extPos));

        av.MasterPolicy = IAVMixer.ClockMasterPolicy.External;
        Assert.Equal(extPos, av.ResolveMasterPosition(audioPos, videoPos, extPos));
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
        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public void Seek(TimeSpan position) { }
        public void Dispose() { }
    }

    private sealed class SpyAudioMixer : IAudioMixer
    {
        public bool Disposed { get; private set; }
        public int RouteToCalls { get; private set; }
        public AudioFormat LeaderFormat { get; } = new(48000, 2);
        public float MasterVolume { get; set; }
        public int ChannelCount => 0;
        public IReadOnlyList<float> PeakLevels { get; } = [0f, 0f];
        public ChannelFallback DefaultFallback => ChannelFallback.Silent;
        public void AddChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null) { }
        public void RemoveChannel(Guid channelId) { }
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
        public int SetActiveChannelForSinkCalls { get; private set; }
        public VideoFormat OutputFormat { get; } = new(640, 360, PixelFormat.Rgba32, 30, 1);
        public int ChannelCount => 0;
        public IVideoChannel? ActiveChannel => null;
        public int SinkCount => 0;
        public void AddChannel(IVideoChannel channel) { }
        public void RemoveChannel(Guid channelId) { }
        public void SetActiveChannel(Guid? channelId) { }
        public void RegisterSink(IVideoSink sink) { }
        public void UnregisterSink(IVideoSink sink) { }
        public void SetActiveChannelForSink(IVideoSink sink, Guid? channelId) => SetActiveChannelForSinkCalls++;
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
}

