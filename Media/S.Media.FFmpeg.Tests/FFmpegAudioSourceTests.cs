using S.Media.Core.Errors;
using S.Media.Core.Media;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Sources;
using System.Reflection;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFmpegAudioSourceTests
{
    [Fact]
    public void Seek_ReturnsInvalidArgument_ForNegativePosition()
    {
        var source = new FFmpegAudioSource();

        var result = source.Seek(-0.5);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, result);
    }

    [Fact]
    public void Seek_ReturnsNonSeekable_WhenSourceIsNotSeekable()
    {
        var source = new FFmpegAudioSource(isSeekable: false);

        var result = source.Seek(1.0);

        Assert.Equal((int)MediaErrorCode.MediaSourceNonSeekable, result);
    }

    [Fact]
    public void ReadSamples_WithNonPositiveRequest_IsSuccessAndReadsZero()
    {
        var source = new FFmpegAudioSource();

        var result = source.ReadSamples(Span<float>.Empty, 0, out var framesRead);

        Assert.Equal(MediaResult.Success, result);
        Assert.Equal(0, framesRead);
    }

    [Fact]
    public void Constructor_ExposesStreamInfo()
    {
        var info = new AudioStreamInfo { Codec = "aac", SampleRate = 48000, ChannelCount = 2 };
        var source = new FFmpegAudioSource(info, durationSeconds: 3.5);

        Assert.Equal("aac", source.StreamInfo.Codec);
        Assert.Equal(48000, source.StreamInfo.SampleRate);
        Assert.Equal(2, source.StreamInfo.ChannelCount);
    }

    [Fact]
    public void TryGetEffectiveChannelMap_ReturnsInvalid_ForExplicitPolicyWithoutMap()
    {
        var source = new FFmpegAudioSource(
            new AudioStreamInfo { ChannelCount = 2 },
            options: new FFmpegAudioSourceOptions { MappingPolicy = FFmpegAudioChannelMappingPolicy.ApplyExplicitRouteMap });

        var code = source.TryGetEffectiveChannelMap(out _);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidAudioChannelMap, code);
    }

    [Fact]
    public void TryGetEffectiveChannelMap_ReturnsIdentity_ForPreserveLayout()
    {
        var source = new FFmpegAudioSource(new AudioStreamInfo { ChannelCount = 2 });

        var code = source.TryGetEffectiveChannelMap(out var map);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal([0, 1], map.SourceChannelByOutputIndex);
    }

    [Fact]
    public void ReadSamples_FromMediaItemSharedSession_ReturnsError_WhenNativeUnavailable()
    {
        var item = new FFmpegMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedPackets = 2 });

        var source = item.AudioSource;
        Assert.NotNull(source);

        var buffer = new float[256 * 2];
        var result = source.ReadSamples(buffer, 256, out _);

        // Without native FFmpeg, shared session cannot produce audio frames
        Assert.NotEqual(MediaResult.Success, result);

        item.Dispose();
    }

    [Fact]
    public void Seek_FromMediaItemSharedSession_SucceedsButReadReturnsError_WhenNativeUnavailable()
    {
        using var item = new FFmpegMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
                UseSharedDecodeContext = true,
            });

        var source = item.AudioSource;
        Assert.NotNull(source);

        // Seek updates position tracking even without native decode
        var seekCode = source.Seek(1.0);
        Assert.Equal(MediaResult.Success, seekCode);
        Assert.Equal(1.0, source.PositionSeconds, 3);

        // But read fails without native decode pipeline
        var buffer = new float[256 * 2];
        var readCode = source.ReadSamples(buffer, 256, out _);
        Assert.NotEqual(MediaResult.Success, readCode);
    }

    [Fact]
    public void ReadSamples_ReturnsConcurrentReadViolation_WhenReadAlreadyInProgress()
    {
        using var item = new FFmpegMediaItem(
            new FFmpegOpenOptions
            {
                InputUri = "file:///tmp/fake.mp4",
                OpenAudio = true,
                OpenVideo = false,
                UseSharedDecodeContext = true,
            },
            new FFmpegDecodeOptions { MaxQueuedPackets = 8 });

        var source = item.AudioSource;
        Assert.NotNull(source);

        var field = typeof(FFmpegAudioSource).GetField("_readInProgress", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(source, 1);

        var buffer = new float[64 * 2];
        var code = source.ReadSamples(buffer, 64, out _);

        Assert.Equal((int)MediaErrorCode.FFmpegConcurrentReadViolation, code);
    }

    [Fact]
    public void Constructor_FromMediaItemWithoutAudio_ThrowsDecodingException()
    {
        using var videoOnly = new FFmpegMediaItem([], [new FFmpegVideoSource()]);

        var ex = Assert.Throws<DecodingException>(() => new FFmpegAudioSource(videoOnly));

        Assert.Equal(MediaErrorCode.FFmpegInvalidConfig, ex.ErrorCode);
    }

    [Fact]
    public void DisposedSource_Start_ReturnsMediaObjectDisposed()
    {
        var source = new FFmpegAudioSource();
        source.Dispose();

        Assert.Equal((int)MediaErrorCode.MediaObjectDisposed, source.Start());
    }

    [Fact]
    public void DisposedSource_Stop_ReturnsMediaObjectDisposed()
    {
        var source = new FFmpegAudioSource();
        source.Dispose();

        Assert.Equal((int)MediaErrorCode.MediaObjectDisposed, source.Stop());
    }

    [Fact]
    public void DisposedSource_ReadSamples_ReturnsMediaObjectDisposed()
    {
        var source = new FFmpegAudioSource();
        source.Dispose();

        var buffer = new float[64 * 2];
        var code = source.ReadSamples(buffer, 64, out var framesRead);

        Assert.Equal((int)MediaErrorCode.MediaObjectDisposed, code);
        Assert.Equal(0, framesRead);
    }

    [Fact]
    public void DisposedSource_Seek_ReturnsMediaObjectDisposed()
    {
        var source = new FFmpegAudioSource();
        source.Dispose();

        Assert.Equal((int)MediaErrorCode.MediaObjectDisposed, source.Seek(1.0));
    }
}
