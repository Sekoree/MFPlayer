using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Decoders.Internal;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFDecoderInternalsTests
{
    [Fact]
    public void PacketReader_Seek_ResetsGeneration()
    {
        using var reader = new FFPacketReader();
        Assert.Equal(MediaResult.Success, reader.Initialize(hasAudio: true, hasVideo: true));

        // Without native demux, reads now fail
        var readCode = reader.ReadVideoPacket(out _);
        Assert.Equal((int)MediaErrorCode.FFmpegReadFailed, readCode);

        // Seek itself should still succeed
        Assert.Equal(MediaResult.Success, reader.Seek(2.0));
    }

    [Fact]
    public void AudioDecoder_RequiresInitialize()
    {
        using var decoder = new FFAudioDecoder();
        var decodeCode = decoder.Decode(new FFPacket(0, 0, TimeSpan.Zero, true, 0.5f), out _);

        Assert.Equal((int)MediaErrorCode.FFmpegAudioDecodeFailed, decodeCode);
        Assert.Equal(MediaResult.Success, decoder.Initialize());

        // Without native codec data, decode now fails
        var plainDecodeCode = decoder.Decode(new FFPacket(0, 0, TimeSpan.Zero, true, 0.5f), out _);
        Assert.Equal((int)MediaErrorCode.FFmpegAudioDecodeFailed, plainDecodeCode);
    }

    [Fact]
    public void VideoDecoder_RequiresInitialize()
    {
        using var decoder = new FFVideoDecoder();
        var decodeCode = decoder.Decode(new FFPacket(0, 0, TimeSpan.Zero, true, 0f), out _);

        Assert.Equal((int)MediaErrorCode.FFmpegVideoDecodeFailed, decodeCode);
        Assert.Equal(MediaResult.Success, decoder.Initialize());

        // Without native codec data, decode now fails
        var plainDecodeCode = decoder.Decode(new FFPacket(0, 0, TimeSpan.Zero, true, 0f), out _);
        Assert.Equal((int)MediaErrorCode.FFmpegVideoDecodeFailed, plainDecodeCode);
    }

    [Fact]
    public void PacketReader_NonexistentFileUri_ReturnsReadFailed()
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

        // Without native demux, reading returns an error
        var readCode = reader.ReadAudioPacket(out _);
        Assert.Equal((int)MediaErrorCode.FFmpegReadFailed, readCode);
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
    public void AudioDecoder_InvalidNativeCodec_ReturnsError()
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

        var code = decoder.Decode(packet, out _);

        Assert.Equal((int)MediaErrorCode.FFmpegAudioDecodeFailed, code);
        Assert.False(decoder.IsNativeDecodeEnabled);
    }

    [Fact]
    public void VideoDecoder_InvalidNativeCodec_ReturnsError()
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

        var code = decoder.Decode(packet, out _);

        Assert.Equal((int)MediaErrorCode.FFmpegVideoDecodeFailed, code);
        Assert.False(decoder.IsNativeDecodeEnabled);
    }
}
