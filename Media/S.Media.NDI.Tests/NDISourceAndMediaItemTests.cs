using NdiLib;
using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.NDI.Media;
using System.Reflection;
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
    public void StoppedReadRejectCodes_MapToSharedConcurrentSemantic()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        Assert.Equal(MediaResult.Success, item.CreateAudioSource(out var audio));
        Assert.Equal(MediaResult.Success, item.CreateVideoSource(out var video));

        Span<float> destination = stackalloc float[16];
        var audioCode = audio!.ReadSamples(destination, requestedFrameCount: 4, out _);
        var videoCode = video!.ReadFrame(out _);

        Assert.Equal((int)MediaErrorCode.NDIAudioReadRejected, audioCode);
        Assert.Equal((int)MediaErrorCode.NDIVideoReadRejected, videoCode);
        Assert.Equal((int)MediaErrorCode.MediaConcurrentOperationViolation, ErrorCodeRanges.ResolveSharedSemantic(audioCode));
        Assert.Equal((int)MediaErrorCode.MediaConcurrentOperationViolation, ErrorCodeRanges.ResolveSharedSemantic(videoCode));
    }

    [Fact]
    public void MetadataPublish_UpdatesSnapshotAndRaisesEvent()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        MediaMetadataSnapshot? observed = null;
        item.MetadataUpdated += (_, snapshot) => observed = snapshot;

        item.PublishMetadata(new Dictionary<string, string> { ["ndi:name"] = "test-source" });

        Assert.True(item.HasMetadata);
        Assert.NotNull(item.Metadata);
        Assert.NotNull(observed);
        Assert.Equal("test-source", observed!.AdditionalMetadata["ndi:name"]);
    }

    [Fact]
    public void CreateSources_WithOptions_PropagatesEffectiveOverrides()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        var options = new S.Media.NDI.Config.NDISourceOptions
        {
            QueueOverflowPolicyOverride = S.Media.NDI.Config.NDIQueueOverflowPolicy.RejectIncoming,
            DiagnosticsTickIntervalOverride = TimeSpan.FromMilliseconds(24),
            VideoJitterBufferFramesOverride = 5,
            AudioJitterBufferMsOverride = 100,
        }.Normalize();

        Assert.Equal(MediaResult.Success, item.CreateAudioSource(options, out var audio));
        Assert.Equal(MediaResult.Success, item.CreateVideoSource(options, out var video));

        Assert.NotNull(audio);
        Assert.NotNull(video);
        Assert.Equal(S.Media.NDI.Config.NDIQueueOverflowPolicy.RejectIncoming, audio!.SourceOptions.QueueOverflowPolicyOverride);
        Assert.Equal(S.Media.NDI.Config.NDIQueueOverflowPolicy.RejectIncoming, video!.SourceOptions.QueueOverflowPolicyOverride);
        Assert.Equal(TimeSpan.FromMilliseconds(24), audio.SourceOptions.DiagnosticsTickIntervalOverride);
        Assert.Equal(TimeSpan.FromMilliseconds(24), video.SourceOptions.DiagnosticsTickIntervalOverride);
        Assert.Equal(5, video.SourceOptions.VideoJitterBufferFramesOverride);
        Assert.Equal(100, audio.SourceOptions.AudioJitterBufferMsOverride);
    }

    [Fact]
    public void AudioSource_Diagnostics_TrackCapturedAndDroppedFrames()
    {
        var item = new NDIMediaItem(new NdiDiscoveredSource("test-source", null));
        Assert.Equal(MediaResult.Success, item.CreateAudioSource(out var audio));

        Span<float> destination = stackalloc float[16];
        var rejected = audio!.ReadSamples(destination, requestedFrameCount: 4, out _);
        Assert.Equal((int)MediaErrorCode.NDIAudioReadRejected, rejected);

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
        Assert.Equal((int)MediaErrorCode.NDIAudioReadRejected, audio!.ReadSamples(destination, requestedFrameCount: 4, out var rejectedBeforeStartFramesRead));
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
        Assert.Equal((int)MediaErrorCode.NDIVideoReadRejected, rejected);

        Assert.Equal(MediaResult.Success, video.Start());
        Assert.Equal(MediaResult.Success, video.ReadFrame(out var frame));
        frame.Dispose();

        var diagnostics = video.Diagnostics;
        Assert.True(diagnostics.FramesCaptured >= 1);
        Assert.True(diagnostics.FramesDropped >= 1);
        Assert.True(diagnostics.LastReadMs >= 0);
    }
    private static void PrimeAudioRing(object audioSource, float[] interleavedSamples)
    {
        var type = audioSource.GetType();
        var ringField = type.GetField("_audioRing", BindingFlags.Instance | BindingFlags.NonPublic);
        var writeField = type.GetField("_ringWriteIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        var countField = type.GetField("_ringSampleCount", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(ringField);
        Assert.NotNull(writeField);
        Assert.NotNull(countField);

        var ring = (float[])ringField!.GetValue(audioSource)!;
        Assert.True(interleavedSamples.Length <= ring.Length);
        interleavedSamples.CopyTo(ring, 0);
        writeField!.SetValue(audioSource, interleavedSamples.Length);
        countField!.SetValue(audioSource, interleavedSamples.Length);
    }
}

