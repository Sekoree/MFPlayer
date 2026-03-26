using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Sources;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Runtime;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using System.Reflection;
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
    public void OpenOptionsConstructor_WiresSessionBackedSources_WithDefaultMetadata()
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
        // Default stream info when native descriptors are not yet available
        Assert.Equal("pcm_f32le", item.AudioSource!.StreamInfo.Codec);
        Assert.Equal(48_000, item.AudioSource.StreamInfo.SampleRate);
        Assert.Equal("placeholder_rgba", item.VideoSource!.StreamInfo.Codec);
        Assert.Equal(2, item.VideoSource.StreamInfo.Width);
        Assert.Equal(2, item.VideoSource.StreamInfo.Height);

        item.Dispose();
    }

    [Fact]
    public void StreamCtor_PropagatesNonSeekableInput_ToCreatedSources()
    {
        using var stream = new NonSeekableReadableMemoryStream([1, 2, 3, 4]);
        using var item = new FFMediaItem(
            stream,
            new FFmpegOpenOptions
            {
                OpenAudio = true,
                OpenVideo = false,
                UseSharedDecodeContext = true,
                LeaveInputStreamOpen = true,
            });

        Assert.NotNull(item.AudioSource);
        Assert.False(item.AudioSource!.IsSeekable);
        Assert.Equal((int)MediaErrorCode.MediaSourceNonSeekable, item.AudioSource.Seek(0.25));
    }

    [Fact]
    public void OpenOptionsConstructor_PopulatesBaselineMetadataSnapshot()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        Assert.True(item.HasMetadata);
        Assert.NotNull(item.Metadata);
        Assert.Equal("1", item.Metadata!.AdditionalMetadata["stream.audio.count"]);
        Assert.Equal("1", item.Metadata.AdditionalMetadata["stream.video.count"]);
        Assert.Equal("true", item.Metadata.AdditionalMetadata["media.seekable"]);
        Assert.Equal("file:///tmp/fake.mp4", item.Metadata.AdditionalMetadata["open.inputUri"]);
    }

    [Fact]
    public void MetadataUpdated_IsNotRaisedAfterDisposeCompletion()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
            });

        var setMetadata = typeof(FFMediaItem).GetMethod("SetMetadata", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(setMetadata);

        var eventCount = 0;
        item.MetadataUpdated += (_, _) => eventCount++;

        var beforeDispose = new S.Media.Core.Media.MediaMetadataSnapshot
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AdditionalMetadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string> { ["phase"] = "before" }),
        };

        setMetadata!.Invoke(item, [beforeDispose]);
        Assert.Equal(1, eventCount);

        item.Dispose();

        var afterDispose = new S.Media.Core.Media.MediaMetadataSnapshot
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AdditionalMetadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string> { ["phase"] = "after" }),
        };

        setMetadata.Invoke(item, [afterDispose]);
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void MetadataUpdated_DoesNotRaiseForEquivalentSnapshotContent()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
            });

        var setMetadata = typeof(FFMediaItem).GetMethod("SetMetadata", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(setMetadata);

        var eventCount = 0;
        item.MetadataUpdated += (_, _) => eventCount++;

        var snapshotA = new S.Media.Core.Media.MediaMetadataSnapshot
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AdditionalMetadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string> { ["same"] = "value" }),
        };

        var snapshotB = new S.Media.Core.Media.MediaMetadataSnapshot
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
            AdditionalMetadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string> { ["same"] = "value" }),
        };

        setMetadata!.Invoke(item, [snapshotA]);
        setMetadata.Invoke(item, [snapshotB]);

        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void SharedSessionDescriptorRefresh_UpdatesMetadataAndRaisesEvent()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        var eventCount = 0;
        item.MetadataUpdated += (_, _) => eventCount++;

        var sessionProperty = typeof(FFMediaItem).GetProperty("SharedDemuxSession", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sessionProperty);

        var session = sessionProperty!.GetValue(item);
        Assert.NotNull(session);

        var eventField = session!.GetType().GetField("StreamDescriptorsRefreshed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(eventField);

        var delegateValue = eventField!.GetValue(session) as MulticastDelegate;
        Assert.NotNull(delegateValue);

        var snapshotType = session.GetType().Assembly.GetType("S.Media.FFmpeg.Decoders.Internal.FFStreamDescriptorSnapshot");
        Assert.NotNull(snapshotType);

        var refreshedAudio = new FFStreamDescriptor
        {
            StreamIndex = 2,
            CodecName = "aac",
            SampleRate = 44_100,
            ChannelCount = 2,
        };

        var refreshedVideo = new FFStreamDescriptor
        {
            StreamIndex = 3,
            CodecName = "h264",
            Width = 1920,
            Height = 1080,
            FrameRate = 60,
        };

        var snapshot = Activator.CreateInstance(snapshotType!, [refreshedAudio, refreshedVideo]);
        Assert.NotNull(snapshot);

        delegateValue!.DynamicInvoke(session, snapshot);

        Assert.Equal(1, eventCount);
        Assert.NotNull(item.Metadata);
        Assert.Equal("aac", item.Metadata!.AdditionalMetadata["stream.audio.codec"]);
        Assert.Equal("h264", item.Metadata.AdditionalMetadata["stream.video.codec"]);
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

    [HeavyFfmpegFact]
    public void MetadataUpdated_HeavySeekChurn_IsMonotonic_AndAvoidsDuplicateSpam()
    {
        using var item = new FFMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = new Uri(HeavyFfmpegTestConfig.ResolveVideoPath()).AbsoluteUri,
                OpenAudio = true,
                OpenVideo = true,
                UseSharedDecodeContext = true,
            });

        var updates = new List<DateTimeOffset>();
        item.MetadataUpdated += (_, snapshot) => updates.Add(snapshot.UpdatedAtUtc);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(MediaResult.Success, item.VideoSource!.Seek(i * 0.1));
            Assert.Equal(MediaResult.Success, item.AudioSource!.Seek(i * 0.1));
        }

        for (var i = 1; i < updates.Count; i++)
        {
            Assert.True(updates[i] >= updates[i - 1]);
        }

        // Descriptor-only refresh updates are de-duplicated; we expect at most a single update after subscription.
        Assert.True(updates.Count <= 1);
    }

    // ── Convenience factory tests ──────────────────────────────────────────────

    [Fact]
    public void Open_Uri_CreatesMediaItem_WithAudioAndVideo()
    {
        using var item = FFMediaItem.Open("file:///tmp/fake.mp4");

        Assert.NotNull(item.AudioSource);
        Assert.NotNull(item.VideoSource);
        Assert.NotNull(item.ResolvedOpenOptions);
        Assert.Equal("file:///tmp/fake.mp4", item.ResolvedOpenOptions!.InputUri);
        Assert.True(item.ResolvedOpenOptions.OpenAudio);
        Assert.True(item.ResolvedOpenOptions.OpenVideo);
        Assert.True(item.ResolvedOpenOptions.UseSharedDecodeContext);
    }

    [Fact]
    public void Open_Uri_ThrowsForNullOrWhitespace()
    {
        Assert.ThrowsAny<ArgumentException>(() => FFMediaItem.Open((string)null!));
        Assert.ThrowsAny<ArgumentException>(() => FFMediaItem.Open(""));
        Assert.ThrowsAny<ArgumentException>(() => FFMediaItem.Open("   "));
    }

    [Fact]
    public void Open_Stream_CreatesMediaItem()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        using var item = FFMediaItem.Open(stream);

        Assert.NotNull(item.ResolvedOpenOptions);
        Assert.Same(stream, item.ResolvedOpenOptions!.InputStream);
    }

    [Fact]
    public void Open_Stream_ThrowsForNull()
    {
        Assert.Throws<ArgumentNullException>(() => FFMediaItem.Open((Stream)null!));
    }

    [Fact]
    public void TryOpen_Uri_ReturnsTrueForValidUri()
    {
        var result = FFMediaItem.TryOpen("file:///tmp/fake.mp4", out var item);

        Assert.True(result);
        Assert.NotNull(item);
        Assert.NotNull(item!.AudioSource);
        Assert.NotNull(item.VideoSource);
        item.Dispose();
    }

    [Fact]
    public void TryOpen_Uri_ReturnsFalseForNullOrWhitespace()
    {
        Assert.False(FFMediaItem.TryOpen((string)null!, out var item1));
        Assert.Null(item1);

        Assert.False(FFMediaItem.TryOpen("", out var item2));
        Assert.Null(item2);

        Assert.False(FFMediaItem.TryOpen("   ", out var item3));
        Assert.Null(item3);
    }

    [Fact]
    public void TryOpen_Stream_ReturnsTrueForValidStream()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var result = FFMediaItem.TryOpen(stream, out var item);

        Assert.True(result);
        Assert.NotNull(item);
        item!.Dispose();
    }

    [Fact]
    public void TryOpen_Stream_ReturnsFalseForNull()
    {
        Assert.False(FFMediaItem.TryOpen((Stream?)null, out var item));
        Assert.Null(item);
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

    private sealed class NonSeekableReadableMemoryStream : MemoryStream
    {
        public NonSeekableReadableMemoryStream(byte[] buffer)
            : base(buffer)
        {
        }

        public override bool CanSeek => false;
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

