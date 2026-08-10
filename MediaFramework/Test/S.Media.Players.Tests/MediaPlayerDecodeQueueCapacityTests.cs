using S.Media.Core;
using Xunit;

namespace S.Media.Players.Tests;

/// <summary>
/// Pins the decode-jitter-buffer depth choice in <c>MediaPlayer.TryOpenLive</c>.
/// <see cref="MediaPlayerOpenOptions.FileVideoDecodeQueueCapacity"/> has always documented
/// "0 = default (16)", but the old fallback chain collapsed onto 4 for any file open that did not
/// pass the value explicitly - and consulted <see cref="MediaPlayerOpenOptions.LiveVideoDecodeQueueCapacity"/>
/// for file opens on the way down. These tests pin the untangled rule: the source KIND picks the
/// option, each kind consults only its own option, and the documented defaults (file 16, live 4)
/// actually apply.
/// </summary>
public sealed class MediaPlayerDecodeQueueCapacityTests
{
    [Fact]
    public void FileOpen_WithNoExplicitCapacity_DefaultsTo16PerTheOptionDoc()
    {
        using var player = OpenFilePlayer(MediaPlayerOpenOptions.Default);
        Assert.Equal(16, player.Video.QueueCapacity);
    }

    [Fact]
    public void FileOpen_ExplicitFileCapacity_Wins()
    {
        using var player = OpenFilePlayer(MediaPlayerOpenOptions.Default with { FileVideoDecodeQueueCapacity = 8 });
        Assert.Equal(8, player.Video.QueueCapacity);
    }

    [Fact]
    public void FileOpen_IgnoresTheLiveCapacityOption()
    {
        // The old chain fell through to LiveVideoDecodeQueueCapacity for file opens - a live tuning
        // knob silently resizing file jitter buffers. A file open must not see it at all.
        using var player = OpenFilePlayer(MediaPlayerOpenOptions.Default with { LiveVideoDecodeQueueCapacity = 7 });
        Assert.Equal(16, player.Video.QueueCapacity);
    }

    [Fact]
    public void LiveOpen_WithNoExplicitCapacity_KeepsTheShallowDefaultOf4()
    {
        using var player = OpenLivePlayer(MediaPlayerOpenOptions.Default);
        Assert.Equal(4, player.Video.QueueCapacity);
    }

    [Fact]
    public void LiveOpen_ExplicitLiveCapacity_Wins()
    {
        using var player = OpenLivePlayer(MediaPlayerOpenOptions.Default with { LiveVideoDecodeQueueCapacity = 7 });
        Assert.Equal(7, player.Video.QueueCapacity);
    }

    [Fact]
    public void LiveOpen_IgnoresTheFileCapacityOption()
    {
        using var player = OpenLivePlayer(MediaPlayerOpenOptions.Default with { FileVideoDecodeQueueCapacity = 8 });
        Assert.Equal(4, player.Video.QueueCapacity);
    }

    // --- open helpers -------------------------------------------------------

    private static MediaPlayer OpenFilePlayer(MediaPlayerOpenOptions options)
    {
        var registry = MediaRegistry.Build(b => b.AddDecoder(new FileDecoderProvider()));
        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        return player;
    }

    private static MediaPlayer OpenLivePlayer(MediaPlayerOpenOptions options)
    {
        // The sources path is how live (NDI/capture) graphs open; the video source advertising
        // ILiveVideoSource is exactly what TryOpenLive keys the live/file split on.
        Assert.True(
            MediaPlayer.OpenLive(audioSource: null, videoSource: new FakeLiveVideoSource())
                .WithOptions(options)
                .TryBuild(out var player, out var error),
            error);
        return player;
    }

    // --- fakes --------------------------------------------------------------

    private sealed class FileDecoderProvider : IMediaDecoderProvider
    {
        public string Name => "queue-cap-file";
        public double Probe(string uri, MediaKind kind) => kind is MediaKind.Audio or MediaKind.Video ? 1.0 : 0.0;
        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new SyntheticFileVideoSource();
        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new ToneSource();
    }

    private sealed class ToneSource : IAudioSource
    {
        private int _remainingChunks = 8;

        public AudioFormat Format { get; } = new(48_000, 2);
        public bool IsExhausted => Volatile.Read(ref _remainingChunks) <= 0;

        public int ReadInto(Span<float> destination)
        {
            if (Interlocked.Decrement(ref _remainingChunks) < 0)
                return 0;
            destination.Fill(0.1f);
            return destination.Length;
        }
    }

    private sealed class SyntheticFileVideoSource : IVideoSource
    {
        private int _frameIndex;
        private VideoFormat _format = new(4, 4, PixelFormat.Bgra32, new Rational(24, 1));

        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => false;

        public void SelectOutputFormat(PixelFormat format) => _format = _format with { PixelFormat = format };

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var index = Interlocked.Increment(ref _frameIndex);
            var stride = _format.Width * 4;
            frame = new VideoFrame(
                TimeSpan.FromSeconds(index / 24.0), _format, [new byte[stride * _format.Height]], [stride]);
            return true;
        }
    }

    /// <summary>Delivers frames immediately so the live open's first-frame wait returns at once.</summary>
    private sealed class FakeLiveVideoSource : ILiveVideoSource
    {
        private int _frameIndex;
        private VideoFormat _format = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => false;

        public void SelectOutputFormat(PixelFormat format) => _format = _format with { PixelFormat = format };

        public void RebaseToLatest(TimeSpan playClockNow) { }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var index = Interlocked.Increment(ref _frameIndex);
            var stride = _format.Width * 4;
            frame = new VideoFrame(
                TimeSpan.FromSeconds(index / 30.0), _format, [new byte[stride * _format.Height]], [stride]);
            return true;
        }
    }
}
