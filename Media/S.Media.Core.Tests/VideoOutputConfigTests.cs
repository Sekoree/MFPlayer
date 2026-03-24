using S.Media.Core.Errors;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class VideoOutputConfigTests
{
    [Fact]
    public void Validate_ReturnsInvalidArgument_WhenQueueCapacityLessThanOne()
    {
        var config = new VideoOutputConfig { QueueCapacity = 0 };

        var result = config.Validate(hasEffectiveFrameDuration: true);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, result);
    }

    [Fact]
    public void Validate_ReturnsInvalidArgument_WhenWaitMultiplierIsNotPositive()
    {
        var config = new VideoOutputConfig
        {
            BackpressureMode = VideoOutputBackpressureMode.Wait,
            BackpressureWaitFrameMultiplier = 0,
        };

        var result = config.Validate(hasEffectiveFrameDuration: true);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, result);
    }

    [Fact]
    public void Validate_ReturnsInvalidArgument_WhenWaitHasNoTimeoutAndNoCadence()
    {
        var config = new VideoOutputConfig
        {
            BackpressureMode = VideoOutputBackpressureMode.Wait,
            BackpressureTimeout = null,
        };

        var result = config.Validate(hasEffectiveFrameDuration: false);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, result);
    }

    [Fact]
    public void Validate_ReturnsSuccess_WhenWaitHasExplicitTimeoutWithoutCadence()
    {
        var config = new VideoOutputConfig
        {
            BackpressureMode = VideoOutputBackpressureMode.Wait,
            BackpressureTimeout = TimeSpan.FromMilliseconds(10),
        };

        var result = config.Validate(hasEffectiveFrameDuration: false);

        Assert.Equal(MediaResult.Success, result);
    }
}

