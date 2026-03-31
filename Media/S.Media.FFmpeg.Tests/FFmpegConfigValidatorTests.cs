using S.Media.Core.Errors;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Config;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFmpegConfigValidatorTests
{
    [Fact]
    public void Validate_ReturnsInvalidConfig_WhenAudioAndVideoAreDisabled()
    {
        var options = new FFmpegOpenOptions
        {
            InputUri = "file:///tmp/fake.mp4",
            OpenAudio = false,
            OpenVideo = false,
        };

        var result = FFmpegConfigValidator.Validate(options);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidConfig, result);
    }

    [Fact]
    public void Validate_ReturnsInvalidConfig_WhenInputUriAndInputStreamAreBothSet()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var options = new FFmpegOpenOptions
        {
            InputUri = "file:///tmp/fake.mp4",
            InputStream = stream,
        };

        var result = FFmpegConfigValidator.Validate(options);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidConfig, result);
    }

    [Fact]
    public void Validate_ReturnsInvalidAudioChannelMap_WhenExplicitMapPolicyHasNoMap()
    {
        var options = new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" };
        var audioOptions = new FFmpegAudioSourceOptions
        {
            MappingPolicy = FFmpegAudioChannelMappingPolicy.ApplyExplicitRouteMap,
            ExplicitChannelMap = null,
        };

        var result = FFmpegConfigValidator.Validate(options, audioOptions);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidAudioChannelMap, result);
    }

    [Fact]
    public void Validate_ReturnsSuccess_ForValidExplicitMap()
    {
        var options = new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" };
        var audioOptions = new FFmpegAudioSourceOptions
        {
            MappingPolicy = FFmpegAudioChannelMappingPolicy.ApplyExplicitRouteMap,
            ExplicitChannelMap = new FFmpegAudioChannelMap(2, 2, [0, 1]),
        };

        var result = FFmpegConfigValidator.Validate(options, audioOptions);

        Assert.Equal(MediaResult.Success, result);
    }

    [Fact]
    public void Validate_ReturnsSuccess_ForValidUriConfig()
    {
        var options = new FFmpegOpenOptions
        {
            InputUri = "file:///tmp/fake.mp4",
            OpenAudio = true,
            OpenVideo = true,
        };

        var result = FFmpegConfigValidator.Validate(options);

        Assert.Equal(MediaResult.Success, result);
    }

    [Fact]
    public void Validate_ReturnsInvalidConfig_ForNegativeDecodeThreadCount()
    {
        var options = new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" };
        var decode = new FFmpegDecodeOptions { DecodeThreadCount = -1 };

        var result = FFmpegConfigValidator.Validate(options, decode);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidConfig, result);
    }

    [Fact]
    public void Validate_ReturnsSuccess_ForPositiveDecodeThreadCount()
    {
        var options = new FFmpegOpenOptions { InputUri = "file:///tmp/fake.mp4" };
        var decode = new FFmpegDecodeOptions { DecodeThreadCount = 6 };

        var result = FFmpegConfigValidator.Validate(options, decode);

        Assert.Equal(MediaResult.Success, result);
    }

    [Fact]
    public void Validate_ReturnsInvalidConfig_ForNegativeAudioStreamIndex()
    {
        var options = new FFmpegOpenOptions
        {
            InputUri = "file:///tmp/fake.mp4",
            AudioStreamIndex = -1,
        };

        var result = FFmpegConfigValidator.Validate(options);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidConfig, result);
    }

    [Fact]
    public void Validate_ReturnsInvalidConfig_ForNegativeVideoStreamIndex()
    {
        var options = new FFmpegOpenOptions
        {
            InputUri = "file:///tmp/fake.mp4",
            VideoStreamIndex = -1,
        };

        var result = FFmpegConfigValidator.Validate(options);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidConfig, result);
    }

    [Fact]
    public void Validate_ReturnsSuccess_WhenInputFormatHintIsSetForUri()
    {
        // N11: InputFormatHint is now passed to av_find_input_format / avformat_open_input
        // for URI-based inputs, so the validator no longer rejects this combination.
        var options = new FFmpegOpenOptions
        {
            InputUri = "file:///tmp/fake.mp4",
            InputFormatHint = "mp4",
        };

        var result = FFmpegConfigValidator.Validate(options);

        Assert.Equal(MediaResult.Success, result);
    }

    [Fact]
    public void Validate_ReturnsInvalidConfig_WhenLeaveInputStreamOpenIsFalseForUri()
    {
        var options = new FFmpegOpenOptions
        {
            InputUri = "file:///tmp/fake.mp4",
            LeaveInputStreamOpen = false,
        };

        var result = FFmpegConfigValidator.Validate(options);

        Assert.Equal((int)MediaErrorCode.FFmpegInvalidConfig, result);
    }
}
