using S.Media.Core.Media;
using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Tests for <see cref="FFmpegDecoderOptions"/> — pure C#, no native libraries.
/// </summary>
public sealed class FFmpegDecoderOptionsTests
{
    [Fact]
    public void Defaults_PacketQueueDepth_Is64()
        => Assert.Equal(64, new FFmpegDecoderOptions().PacketQueueDepth);

    [Fact]
    public void Defaults_AudioBufferDepth_Is16()
        => Assert.Equal(16, new FFmpegDecoderOptions().AudioBufferDepth);

    [Fact]
    public void Defaults_VideoBufferDepth_Is4()
        => Assert.Equal(4, new FFmpegDecoderOptions().VideoBufferDepth);

    [Fact]
    public void Defaults_DecoderThreadCount_IsZero()
        => Assert.Equal(0, new FFmpegDecoderOptions().DecoderThreadCount);

    [Fact]
    public void Defaults_HardwareDeviceType_IsNull()
        => Assert.Null(new FFmpegDecoderOptions().HardwareDeviceType);

    [Fact]
    public void Defaults_EnableAudioAndVideo_AreTrue()
    {
        var opts = new FFmpegDecoderOptions();
        Assert.True(opts.EnableAudio);
        Assert.True(opts.EnableVideo);
    }

    [Fact]
    public void Defaults_VideoTargetPixelFormat_IsBgra32()
        => Assert.Equal(PixelFormat.Bgra32, new FFmpegDecoderOptions().VideoTargetPixelFormat);

    [Fact]
    public void Init_OverridesAllDefaults()
    {
        var opts = new FFmpegDecoderOptions
        {
            PacketQueueDepth   = 128,
            AudioBufferDepth   = 32,
            VideoBufferDepth   = 8,
            DecoderThreadCount = 4,
            HardwareDeviceType = "vaapi",
            EnableAudio        = false,
            EnableVideo        = true,
            VideoTargetPixelFormat = PixelFormat.Rgba32
        };

        Assert.Equal(128,     opts.PacketQueueDepth);
        Assert.Equal(32,      opts.AudioBufferDepth);
        Assert.Equal(8,       opts.VideoBufferDepth);
        Assert.Equal(4,       opts.DecoderThreadCount);
        Assert.Equal("vaapi", opts.HardwareDeviceType);
        Assert.False(opts.EnableAudio);
        Assert.True(opts.EnableVideo);
        Assert.Equal(PixelFormat.Rgba32, opts.VideoTargetPixelFormat);
    }
}
