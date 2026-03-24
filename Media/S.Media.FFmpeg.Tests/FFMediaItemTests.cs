using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Sources;
using S.Media.FFmpeg.Config;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFMediaItemTests
{
    [Fact]
    public void AudioVideoConstructor_ExposesPlaybackSources_AndInitialActiveVideo()
    {
        var audio = new FFAudioSource();
        var video = new FFVideoSource();
        var item = new FFMediaItem(audio, video);

        Assert.Single(item.PlaybackAudioSources);
        Assert.Single(item.PlaybackVideoSources);
        Assert.Equal(video.SourceId, item.InitialActiveVideoSource?.SourceId);
        Assert.Null(item.ResolvedOpenOptions);
    }

    [Fact]
    public void Dispose_WhenOwningSources_DisposesProvidedSources()
    {
        var audio = new TrackableAudioSource();
        var video = new TrackableVideoSource();
        var item = new FFMediaItem([audio], [video], video, ownsSources: true);

        item.Dispose();

        Assert.True(audio.Disposed);
        Assert.True(video.Disposed);
    }

    [Fact]
    public void OpenOptionsConstructor_ThrowsDecodingException_ForInvalidConfig()
    {
        var options = new FFmpegOpenOptions
        {
            InputUri = "file:///tmp/fake.mp4",
            OpenAudio = false,
            OpenVideo = false,
        };

        var ex = Assert.Throws<DecodingException>(() => new FFMediaItem(options));

        Assert.Equal(MediaErrorCode.FFmpegInvalidConfig, ex.ErrorCode);
    }

    [Fact]
    public void OpenOptionsConstructor_ThrowsDecodingException_ForNegativeDecodeThreadCount()
    {
        var options = new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" };
        var decodeOptions = new FFmpegDecodeOptions { DecodeThreadCount = -1 };

        var ex = Assert.Throws<DecodingException>(() => new FFMediaItem(options, decodeOptions));

        Assert.Equal(MediaErrorCode.FFmpegInvalidConfig, ex.ErrorCode);
    }

    [Fact]
    public void AudioVideoConstructor_ExposesTypedSources_AndStreamLists()
    {
        var audio = new FFAudioSource();
        var video = new FFVideoSource();
        var item = new FFMediaItem(audio, video);

        Assert.NotNull(item.AudioSource);
        Assert.NotNull(item.VideoSource);
        Assert.Single(item.AudioStreams);
        Assert.Single(item.VideoStreams);
    }

    [Fact]
    public void StreamCtor_DisposesOwnedStream_WhenLeaveInputStreamOpenIsFalse()
    {
        var stream = new TrackingMemoryStream();
        var item = new FFMediaItem(stream, leaveInputStreamOpen: false);

        item.Dispose();

        Assert.True(stream.Disposed);
    }

    [Fact]
    public void StreamCtor_DoesNotDisposeOwnedStream_WhenLeaveInputStreamOpenIsTrue()
    {
        var stream = new TrackingMemoryStream();
        var item = new FFMediaItem(stream, leaveInputStreamOpen: true);

        item.Dispose();

        Assert.False(stream.Disposed);
    }

    [Fact]
    public void StreamCtor_ThrowsDecodingException_WhenStreamIsNotReadable()
    {
        using var stream = new NonReadableMemoryStream();

        var ex = Assert.Throws<DecodingException>(() => new FFMediaItem(stream));

        Assert.Equal(MediaErrorCode.FFmpegInvalidConfig, ex.ErrorCode);
    }

    [Fact]
    public void StreamAndOptionsCtor_ThrowsDecodingException_WhenOptionsAlreadyContainInputSource()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var openOptions = new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" };

        var ex = Assert.Throws<DecodingException>(() => new FFMediaItem(stream, openOptions));

        Assert.Equal(MediaErrorCode.FFmpegInvalidConfig, ex.ErrorCode);
    }

    [Fact]
    public void StreamAndOptionsCtor_DisposesStream_WhenLeaveInputStreamOpenIsFalse()
    {
        var stream = new TrackingMemoryStream();
        var openOptions = new FFmpegOpenOptions
        {
            OpenAudio = true,
            OpenVideo = false,
            LeaveInputStreamOpen = false,
        };

        var item = new FFMediaItem(stream, openOptions);
        item.Dispose();

        Assert.True(stream.Disposed);
    }

    [Fact]
    public void StreamAndOptionsCtor_DoesNotDisposeStream_WhenLeaveInputStreamOpenIsTrue()
    {
        var stream = new TrackingMemoryStream();
        var openOptions = new FFmpegOpenOptions
        {
            OpenAudio = true,
            OpenVideo = false,
            LeaveInputStreamOpen = true,
        };

        var item = new FFMediaItem(stream, openOptions);
        item.Dispose();

        Assert.False(stream.Disposed);
    }

    [Fact]
    public void StreamCtor_ExposesResolvedOpenOptions_WithInputFormatHint()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        var item = new FFMediaItem(stream, leaveInputStreamOpen: true, inputFormatHint: "mpegts");

        Assert.NotNull(item.ResolvedOpenOptions);
        Assert.Equal("mpegts", item.ResolvedOpenOptions!.InputFormatHint);
        Assert.Same(stream, item.ResolvedOpenOptions.InputStream);
    }

    [Fact]
    public void StreamAndOptionsCtor_ExposesResolvedOpenOptions_WithNormalizedInputStream()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var options = new FFmpegOpenOptions
        {
            OpenAudio = false,
            OpenVideo = true,
            LeaveInputStreamOpen = true,
            InputFormatHint = "matroska",
        };

        var item = new FFMediaItem(stream, options);

        Assert.NotNull(item.ResolvedOpenOptions);
        Assert.True(item.ResolvedOpenOptions!.OpenVideo);
        Assert.False(item.ResolvedOpenOptions.OpenAudio);
        Assert.Equal("matroska", item.ResolvedOpenOptions.InputFormatHint);
        Assert.Same(stream, item.ResolvedOpenOptions.InputStream);
        Assert.Null(item.ResolvedOpenOptions.InputUri);
    }

    [Fact]
    public void OpenOptionsConstructor_ExposesResolvedDecodeOptions_WithDeterministicClamping()
    {
        var options = new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" };
        var decodeOptions = new FFmpegDecodeOptions
        {
            DecodeThreadCount = int.MaxValue,
            MaxQueuedFrames = 0,
            MaxQueuedPackets = -2,
        };

        var item = new FFMediaItem(options, decodeOptions);

        Assert.NotNull(item.ResolvedDecodeOptions);
        Assert.Equal(Math.Max(1, Environment.ProcessorCount), item.ResolvedDecodeOptions!.DecodeThreadCount);
        Assert.Equal(1, item.ResolvedDecodeOptions.MaxQueuedFrames);
        Assert.Equal(1, item.ResolvedDecodeOptions.MaxQueuedPackets);
    }

    [Fact]
    public void OpenOptionsConstructor_WiresSessionBackedSources_WithDeterministicPlaceholderMetadata()
    {
        var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        Assert.NotNull(item.AudioSource);
        Assert.NotNull(item.VideoSource);
        Assert.Equal("pcm_f32le", item.AudioSource!.StreamInfo.Codec);
        Assert.Equal(48_000, item.AudioSource.StreamInfo.SampleRate);
        Assert.Equal("placeholder_rgba", item.VideoSource!.StreamInfo.Codec);
        Assert.Equal(2, item.VideoSource.StreamInfo.Width);
        Assert.Equal(2, item.VideoSource.StreamInfo.Height);

        item.Dispose();
    }

    [HeavyFfmpegFact]
    public void OpenOptionsConstructor_HeavyFile_CanExposeNativeResolvedStreamMetadata()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = new Uri(HeavyFfmpegTestConfig.ResolveVideoPath()).AbsoluteUri,
                OpenAudio = true,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        Assert.NotNull(item.AudioSource);
        Assert.NotNull(item.VideoSource);

        Assert.False(string.IsNullOrWhiteSpace(item.VideoSource!.StreamInfo.Codec));
        Assert.True(item.VideoSource.StreamInfo.Width.GetValueOrDefault(0) >= 2);
        Assert.True(item.VideoSource.StreamInfo.Height.GetValueOrDefault(0) >= 2);
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class NonReadableMemoryStream : MemoryStream
    {
        public override bool CanRead => false;
    }

    private sealed class TrackableAudioSource : IAudioSource
    {
        public Guid SourceId { get; } = Guid.NewGuid();
        public AudioSourceState State => AudioSourceState.Stopped;
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public bool Disposed { get; private set; }

        public int Start() => 0;
        public int Stop() => 0;
        public int ReadSamples(Span<float> destination, int requestedFrameCount, out int framesRead) { framesRead = 0; return 0; }
        public int Seek(double positionSeconds) => 0;
        public void Dispose() => Disposed = true;
    }

    private sealed class TrackableVideoSource : IVideoSource
    {
        public Guid SourceId { get; } = Guid.NewGuid();
        public VideoSourceState State => VideoSourceState.Stopped;
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public long CurrentFrameIndex => 0;
        public long? CurrentDecodeFrameIndex => null;
        public long? TotalFrameCount => null;
        public bool IsSeekable => true;
        public bool Disposed { get; private set; }

        public int Start() => 0;
        public int Stop() => 0;
        public int ReadFrame(out VideoFrame frame)
        {
            frame = new VideoFrame(2, 2, VideoPixelFormat.Rgba32, new Rgba32PixelFormatData(), TimeSpan.Zero, true, new byte[16], 8);
            return 0;
        }
        public int Seek(double positionSeconds) => 0;
        public int SeekToFrame(long frameIndex) => 0;
        public int SeekToFrame(long frameIndex, out long currentFrameIndex, out long? totalFrameCount) { currentFrameIndex = 0; totalFrameCount = 0; return 0; }
        public void Dispose() => Disposed = true;
    }
}

