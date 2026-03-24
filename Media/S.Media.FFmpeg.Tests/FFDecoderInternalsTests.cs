using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Decoders.Internal;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFDecoderInternalsTests
{
    [Fact]
    public void PacketReader_Seek_ResetsGenerationAndTimeline()
    {
        using var reader = new FFPacketReader();
        Assert.Equal(MediaResult.Success, reader.Initialize(hasAudio: true, hasVideo: true));

        Assert.Equal(MediaResult.Success, reader.ReadVideoPacket(out var beforeSeek));
        Assert.Equal(0, beforeSeek.Generation);
        Assert.Equal(0, beforeSeek.Sequence);
        Assert.Equal(TimeSpan.Zero, beforeSeek.PresentationTime);

        Assert.Equal(MediaResult.Success, reader.Seek(2.0));
        Assert.Equal(MediaResult.Success, reader.ReadVideoPacket(out var afterSeek));

        Assert.Equal(1, afterSeek.Generation);
        Assert.Equal(60, afterSeek.Sequence);
        Assert.Equal(TimeSpan.FromSeconds(2), afterSeek.PresentationTime);
    }

    [Fact]
    public void AudioDecoder_RequiresInitialize()
    {
        using var decoder = new FFAudioDecoder();
        var decodeCode = decoder.Decode(new FFPacket(0, 0, TimeSpan.Zero, true, 0.5f), out _);

        Assert.Equal((int)MediaErrorCode.FFmpegAudioDecodeFailed, decodeCode);
        Assert.Equal(MediaResult.Success, decoder.Initialize());
        Assert.Equal(MediaResult.Success, decoder.Decode(new FFPacket(0, 0, TimeSpan.Zero, true, 0.5f), out var decoded));
        Assert.Equal(256, decoded.FrameCount);
    }

    [Fact]
    public void VideoDecoder_MapsPacketMetadata_ToDecodedFrame()
    {
        using var decoder = new FFVideoDecoder();
        Assert.Equal(MediaResult.Success, decoder.Initialize());

        var packet = new FFPacket(3, 42, TimeSpan.FromSeconds(1.4), IsKeyFrame: false, SampleValue: 0f);
        var code = decoder.Decode(packet, out var decoded);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(3, decoded.Generation);
        Assert.Equal(42, decoded.FrameIndex);
        Assert.Equal(TimeSpan.FromSeconds(1.4), decoded.PresentationTime);
        Assert.False(decoded.IsKeyFrame);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
    }

    [Fact]
    public void PacketReader_NonexistentFileUri_FallsBackToPlaceholderPipeline()
    {
        using var reader = new FFPacketReader();
        var initCode = reader.Initialize(
            hasAudio: true,
            hasVideo: false,
            new FFmpegOpenOptions { InputUri = "file:///tmp/smedia_ffmpeg_missing_asset.mov" },
            audioStreamIndex: null,
            videoStreamIndex: null);

        Assert.Equal(MediaResult.Success, initCode);
        Assert.False(reader.IsNativeDemuxActive);
        Assert.Equal(MediaResult.Success, reader.ReadAudioPacket(out var packet));
        Assert.Equal(0, packet.Sequence);
        Assert.Equal(TimeSpan.Zero, packet.PresentationTime);
    }

    [HeavyFfmpegFact]
    public void PacketReader_HeavyFile_UsesNativeDemuxPath_WhenAvailable()
    {
        using var reader = new FFPacketReader();
        var initCode = reader.Initialize(
            hasAudio: false,
            hasVideo: true,
            new FFmpegOpenOptions { InputUri = new Uri(HeavyFfmpegTestConfig.ResolveVideoPath()).AbsoluteUri },
            audioStreamIndex: null,
            videoStreamIndex: null);

        Assert.Equal(MediaResult.Success, initCode);
        Assert.Equal(MediaResult.Success, reader.ReadVideoPacket(out var packet));
        Assert.True(packet.PresentationTime >= TimeSpan.Zero);
    }

    [Fact]
    public void AudioDecoder_InvalidNativeCodec_FallsBackAndDisablesNativePath()
    {
        using var decoder = new FFAudioDecoder();
        Assert.Equal(MediaResult.Success, decoder.Initialize());

        var packet = new FFPacket(
            Generation: 0,
            Sequence: 0,
            PresentationTime: TimeSpan.Zero,
            IsKeyFrame: true,
            SampleValue: 0.5f,
            NativePacketData: [1, 2, 3],
            NativeCodecId: int.MaxValue);

        var code = decoder.Decode(packet, out var decoded);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(256, decoded.FrameCount);
        Assert.False(decoder.IsNativeDecodeEnabled);
    }

    [Fact]
    public void VideoDecoder_InvalidNativeCodec_FallsBackAndDisablesNativePath()
    {
        using var decoder = new FFVideoDecoder();
        Assert.Equal(MediaResult.Success, decoder.Initialize());

        var packet = new FFPacket(
            Generation: 2,
            Sequence: 7,
            PresentationTime: TimeSpan.FromSeconds(0.2),
            IsKeyFrame: false,
            SampleValue: 0f,
            NativePacketData: [9],
            NativeCodecId: int.MaxValue);

        var code = decoder.Decode(packet, out var decoded);

        Assert.Equal(MediaResult.Success, code);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.False(decoder.IsNativeDecodeEnabled);
    }
}

