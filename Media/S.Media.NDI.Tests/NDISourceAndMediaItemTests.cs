using System.Buffers;
using NDILib;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.NDI.Input;
using S.Media.NDI.Media;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NDISourceAndMediaItemTests
{
    [Fact]
    public void CreatedSources_ReportLiveDurationAsNaN()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));

        Assert.Equal(MediaResult.Success, item.CreateAudioSource(out var audio));
        Assert.Equal(MediaResult.Success, item.CreateVideoSource(out var video));

        Assert.NotNull(audio);
        Assert.NotNull(video);
        Assert.True(double.IsNaN(audio.DurationSeconds));
        Assert.True(double.IsNaN(video.DurationSeconds));
    }

    [Fact]
    public void StoppedReadRejectCodes_ReturnMediaSourceNotRunning()
    {
        // §5.4: a stopped (never-started) source returns MediaSourceNotRunning, not the
        // concurrent-read code.  Callers must inspect source.State to distinguish the two.
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        Assert.Equal(MediaResult.Success, item.CreateAudioSource(out var audio));
        Assert.Equal(MediaResult.Success, item.CreateVideoSource(out var video));

        Span<float> destination = stackalloc float[16];
        var audioCode = audio!.ReadSamples(destination, requestedFrameCount: 4, out _);
        var videoCode = video!.ReadFrame(out _);

        Assert.Equal((int)MediaErrorCode.MediaSourceNotRunning, audioCode);
        Assert.Equal((int)MediaErrorCode.MediaSourceNotRunning, videoCode);
        // MediaSourceNotRunning passes through ResolveSharedSemantic unchanged.
        Assert.Equal((int)MediaErrorCode.MediaSourceNotRunning, ErrorCodeRanges.ResolveSharedSemantic(audioCode));
        Assert.Equal((int)MediaErrorCode.MediaSourceNotRunning, ErrorCodeRanges.ResolveSharedSemantic(videoCode));
    }

    [Fact]
    public void MetadataPublish_UpdatesSnapshotAndRaisesEvent()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        MediaMetadataSnapshot? observed = null;
        item.MetadataChanged += (_, snapshot) => observed = snapshot;

        item.PublishMetadata(new Dictionary<string, string> { ["ndi:name"] = "test-source" });

        Assert.True(item.HasMetadata);
        Assert.NotNull(item.Metadata);
        Assert.NotNull(observed);
        Assert.Equal("test-source", observed!.AdditionalMetadata["ndi:name"]);
    }

    [Fact]
    public void CreateSources_WithOptions_PropagatesEffectiveValues()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        var options = new S.Media.NDI.Config.NDISourceOptions
        {
            QueueOverflowPolicy = S.Media.NDI.Config.NDIQueueOverflowPolicy.RejectIncoming,
            DiagnosticsTickInterval = TimeSpan.FromMilliseconds(24),
            VideoJitterBufferFrames = 5,
            AudioJitterBufferMs = 100,
        }.Normalize();

        Assert.Equal(MediaResult.Success, item.CreateAudioSource(options, out var audio));
        Assert.Equal(MediaResult.Success, item.CreateVideoSource(options, out var video));

        Assert.NotNull(audio);
        Assert.NotNull(video);
        Assert.Equal(S.Media.NDI.Config.NDIQueueOverflowPolicy.RejectIncoming, audio!.SourceOptions.QueueOverflowPolicy);
        Assert.Equal(S.Media.NDI.Config.NDIQueueOverflowPolicy.RejectIncoming, video!.SourceOptions.QueueOverflowPolicy);
        Assert.Equal(TimeSpan.FromMilliseconds(24), audio.SourceOptions.DiagnosticsTickInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(24), video.SourceOptions.DiagnosticsTickInterval);
        Assert.Equal(5, video.SourceOptions.VideoJitterBufferFrames);
        Assert.Equal(100, audio.SourceOptions.AudioJitterBufferMs);
    }

    [Fact]
    public void AudioSource_Diagnostics_TrackCapturedAndDroppedFrames()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        Assert.Equal(MediaResult.Success, item.CreateAudioSource(out var audio));

        Span<float> destination = stackalloc float[16];
        var rejected = audio!.ReadSamples(destination, requestedFrameCount: 4, out _);
        Assert.Equal((int)MediaErrorCode.MediaSourceNotRunning, rejected); // §5.4: stopped → not running

        Assert.Equal(MediaResult.Success, audio.Start());
        Assert.Equal(MediaResult.Success, audio.ReadSamples(destination, requestedFrameCount: 4, out var framesRead));
        Assert.Equal(0, framesRead);
        Assert.True(destination.ToArray().All(sample => sample == 0f));

        var diagnostics = audio.Diagnostics;
        Assert.Equal(0, diagnostics.FramesCaptured);
        Assert.True(diagnostics.FramesDropped >= 2);
        Assert.True(diagnostics.LastReadMs >= 0);
        Assert.Equal(0, audio.PositionSeconds);
    }

    [Fact]
    public void AudioSource_ReadSamples_PartialBufferedAudio_ReportsAvailableFramesOnly()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        Assert.Equal(MediaResult.Success, item.CreateAudioSource(out var audio));

        PrimeAudioRing(audio!, [0.25f, 0.5f, 0.75f, 1.0f]); // Two stereo frames.

        Span<float> destination = stackalloc float[8]; // Four requested stereo frames.
        Assert.Equal((int)MediaErrorCode.MediaSourceNotRunning, audio!.ReadSamples(destination, requestedFrameCount: 4, out var rejectedBeforeStartFramesRead));
        Assert.Equal(0, rejectedBeforeStartFramesRead);

        Assert.Equal(MediaResult.Success, audio.Start());
        Assert.Equal(MediaResult.Success, audio.ReadSamples(destination, requestedFrameCount: 4, out var framesRead));

        Assert.Equal(2, framesRead);
        Assert.Equal(0.25f, destination[0]);
        Assert.Equal(0.5f, destination[1]);
        Assert.Equal(0.75f, destination[2]);
        Assert.Equal(1.0f, destination[3]);
        Assert.True(destination.Slice(4).ToArray().All(sample => sample == 0f));

        var diagnostics = audio.Diagnostics;
        Assert.Equal(2, diagnostics.FramesCaptured);
        Assert.True(audio.PositionSeconds > 0);
    }

    [Fact]
    public void VideoSource_Diagnostics_TrackCapturedAndDroppedFrames()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        Assert.Equal(MediaResult.Success, item.CreateVideoSource(out var video));

        var rejected = video!.ReadFrame(out _);
        Assert.Equal((int)MediaErrorCode.MediaSourceNotRunning, rejected);

        Assert.Equal(MediaResult.Success, video.Start());
        Assert.Equal(MediaResult.Success, video.ReadFrame(out var frame));
        frame.Dispose();

        var diagnostics = video.Diagnostics;
        Assert.True(diagnostics.FramesCaptured >= 1);
        Assert.True(diagnostics.FramesDropped >= 1);
        Assert.True(diagnostics.LastReadMs >= 0);
    }

    [Fact]
    public void VideoSource_JitterBuffer_PrimesOnceThenDequeuesAtAnyDepth()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        var options = new S.Media.NDI.Config.NDISourceOptions
        {
            VideoJitterBufferFrames = 3,
        };

        Assert.Equal(MediaResult.Success, item.CreateVideoSource(options, out var video));
        Assert.NotNull(video);

        EnqueueTestVideoFrame(video!, 11);
        EnqueueTestVideoFrame(video, 22);

        Assert.False(TryDequeueBufferedFrame(video, out _));

        EnqueueTestVideoFrame(video, 33);
        Assert.True(TryDequeueBufferedFrame(video, out _));
        Assert.True(TryDequeueBufferedFrame(video, out _));

        video.Dispose();
    }

    [Fact]
    public void VideoSource_OverflowPolicy_DropNewestDiffersFromDropOldest()
    {
        var newestItem = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        Assert.Equal(
            MediaResult.Success,
            newestItem.CreateVideoSource(new S.Media.NDI.Config.NDISourceOptions
            {
                QueueOverflowPolicy = S.Media.NDI.Config.NDIQueueOverflowPolicy.DropNewest,
                VideoJitterBufferFrames = 3,
            }, out var dropNewestSource));

        Assert.NotNull(dropNewestSource);

        var oldestItem = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        Assert.Equal(
            MediaResult.Success,
            oldestItem.CreateVideoSource(new S.Media.NDI.Config.NDISourceOptions
            {
                QueueOverflowPolicy = S.Media.NDI.Config.NDIQueueOverflowPolicy.DropOldest,
                VideoJitterBufferFrames = 3,
            }, out var dropOldestSource));

        Assert.NotNull(dropOldestSource);

        var queueCapacity = 9; // maxQueueDepth for jitter=3

        for (var i = 1; i <= queueCapacity; i++)
        {
            EnqueueTestVideoFrame(dropNewestSource!, (byte)i);
            EnqueueTestVideoFrame(dropOldestSource!, (byte)i);
        }

        EnqueueTestVideoFrame(dropNewestSource!, 100);
        EnqueueTestVideoFrame(dropOldestSource!, 100);

        var newestFront = PeekBufferedFrameMarker(dropNewestSource!);
        var oldestFront = PeekBufferedFrameMarker(dropOldestSource!);

        Assert.Equal(1, newestFront);
        Assert.Equal(2, oldestFront);

        Assert.Equal(queueCapacity, GetJitterQueueCount(dropNewestSource!));
        Assert.Equal(queueCapacity, GetJitterQueueCount(dropOldestSource!));

        dropNewestSource!.Dispose();
        dropOldestSource!.Dispose();
    }

    private static void PrimeAudioRing(NDIAudioSource audioSource, float[] interleavedSamples)
    {
        audioSource.TestPrimeAudioRing(interleavedSamples);
    }

    private static void EnqueueTestVideoFrame(NDIVideoSource videoSource, byte marker)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4);
        buffer[0] = marker;
        buffer[1] = (byte)(marker + 1);
        buffer[2] = (byte)(marker + 2);
        buffer[3] = (byte)(marker + 3);
        videoSource.TestEnqueueCapturedFrame(buffer, 4, 1, 1, 0L, "test", VideoPixelFormat.Rgba32, "test");
    }

    private static bool TryDequeueBufferedFrame(NDIVideoSource videoSource, out byte[]? rgba)
    {
        return videoSource.TestTryDequeueBufferedFrame(out rgba);
    }

    private static int GetJitterQueueCount(NDIVideoSource videoSource)
    {
        return videoSource.TestGetJitterQueueCount();
    }

    private static byte PeekBufferedFrameMarker(NDIVideoSource videoSource)
    {
        return videoSource.TestPeekBufferedFrameMarker();
    }
}
