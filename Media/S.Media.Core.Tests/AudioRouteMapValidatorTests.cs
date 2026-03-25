using S.Media.Core.Audio;
using S.Media.Core.Errors;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class AudioRouteMapValidatorTests
{
    [Fact]
    public void ValidatePushFrameMap_ReturnsMissing_WhenRouteMapIsEmpty()
    {
        var frame = new AudioFrame(new float[8], 4, 2, AudioFrameLayout.Interleaved, 48_000, TimeSpan.Zero);

        var code = AudioRouteMapValidator.ValidatePushFrameMap(frame, ReadOnlySpan<int>.Empty, 2);

        Assert.Equal((int)MediaErrorCode.AudioRouteMapMissing, code);
    }

    [Fact]
    public void ValidatePushFrameMap_ReturnsInvalidArgument_WhenSourceChannelCountIsNotPositive()
    {
        var frame = new AudioFrame(new float[8], 4, 2, AudioFrameLayout.Interleaved, 48_000, TimeSpan.Zero);

        var code = AudioRouteMapValidator.ValidatePushFrameMap(frame, [0, 1], 0);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, code);
    }

    [Fact]
    public void ValidatePushFrameMap_ReturnsChannelMismatch_WhenFrameAndCallSourceCountsDiffer()
    {
        var frame = new AudioFrame(new float[8], 4, 2, AudioFrameLayout.Interleaved, 48_000, TimeSpan.Zero);

        var code = AudioRouteMapValidator.ValidatePushFrameMap(frame, [0, 1], 1);

        Assert.Equal((int)MediaErrorCode.AudioChannelCountMismatch, code);
    }

    [Fact]
    public void ValidatePushFrameMap_ReturnsMapInvalid_WhenRouteContainsOutOfRangeIndex()
    {
        var frame = new AudioFrame(new float[8], 4, 2, AudioFrameLayout.Interleaved, 48_000, TimeSpan.Zero);

        var code = AudioRouteMapValidator.ValidatePushFrameMap(frame, [0, 2], 2);

        Assert.Equal((int)MediaErrorCode.AudioRouteMapInvalid, code);
    }

    [Fact]
    public void ValidatePushFrameMap_ReturnsSuccess_ForValidDenseMap()
    {
        var frame = new AudioFrame(new float[8], 4, 2, AudioFrameLayout.Interleaved, 48_000, TimeSpan.Zero);

        var code = AudioRouteMapValidator.ValidatePushFrameMap(frame, [0, 1, 0, -1], 2);

        Assert.Equal(MediaResult.Success, code);
    }
}

