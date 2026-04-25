using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Video;
using S.Media.Playback;
using Xunit;

namespace S.Media.FFmpeg.Tests;

[Collection("FFmpeg")]
public sealed class MediaPlayerExternalInputTests
{
    [Fact]
    public async Task PlayAsync_WorksWithExternalAudioInput_WithoutOpenAsync()
    {
        using var channel = new AudioChannel(new AudioFormat(48_000, 2));
        var endpoint = new FakeAudioEndpoint();

        using var player = MediaPlayer.Create()
            .WithAudioOutput(endpoint)
            .WithAudioInput(channel)
            .Build();

        await player.PlayAsync();
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.True(endpoint.IsRunning);

        await player.StopAsync();
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.False(endpoint.IsRunning);
    }

    [Fact]
    public async Task DriftCorrectionLoop_StartsAndStops_WithExternalAvInputs()
    {
        using var audio = new AudioChannel(new AudioFormat(48_000, 2));
        using var video = new VideoChannelStub();
        var endpoint = new FakeAudioEndpoint();

        var drift = new AvDriftCorrectionOptions
        {
            InitialDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(10),
            MinDriftMs = 0,
            IgnoreOutlierDriftMs = 10_000,
            MaxStepMs = 5,
            MaxAbsOffsetMs = 50
        };

        using var player = MediaPlayer.Create()
            .WithAudioOutput(endpoint)
            .WithAudioInput(audio)
            .WithVideoInput(video)
            .WithAutoAvDriftCorrection(drift)
            .Build();

        await player.PlayAsync();
        await Task.Delay(50);
        await player.StopAsync();

        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    private sealed class FakeAudioEndpoint : IAudioEndpoint
    {
        public string Name { get; } = "fake-audio";
        public bool IsRunning { get; private set; }

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

        public void ReceiveBuffer(ReadOnlySpan<float> interleaved, int frameCount, AudioFormat format, TimeSpan sourcePts)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class VideoChannelStub : IVideoChannel
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsOpen => true;
        public bool CanSeek => false;
        public VideoFormat SourceFormat { get; } = new(1920, 1080, PixelFormat.Yuv420p, 30, 1);
        public TimeSpan Position => TimeSpan.Zero;
        public int BufferDepth => 4;
        public int BufferAvailable => 0;

        public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
        public event EventHandler? EndOfStream { add { } remove { } }

        public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
        public void Seek(TimeSpan position) { }
        public IVideoSubscription Subscribe(VideoSubscriptionOptions options) => new NopSub();
        public void Dispose() { }

        private sealed class NopSub : IVideoSubscription
        {
            public int FillBuffer(Span<VideoFrame> dest, int frameCount) => 0;
            public bool TryRead(out VideoFrame frame)
            {
                frame = default;
                return false;
            }

            public int Count => 0;
            public int Capacity => 4;
            public bool IsCompleted => false;
            public event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun { add { } remove { } }
            public void Dispose() { }
        }
    }
}
