using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class YuvAutoPolicyTests
{
    [Theory]
    [InlineData(YuvColorRange.Full, YuvColorRange.Full)]
    [InlineData(YuvColorRange.Limited, YuvColorRange.Limited)]
    [InlineData(YuvColorRange.Auto, YuvColorRange.Full)]
    public void ResolveRange_MapsExpected(YuvColorRange requested, YuvColorRange expected)
    {
        Assert.Equal(expected, YuvAutoPolicy.ResolveRange(requested));
    }

    [Theory]
    [InlineData(YuvColorMatrix.Bt601, 1920, 1080, YuvColorMatrix.Bt601)]
    [InlineData(YuvColorMatrix.Bt709, 640, 480, YuvColorMatrix.Bt709)]
    [InlineData(YuvColorMatrix.Auto, 1920, 1080, YuvColorMatrix.Bt709)]
    [InlineData(YuvColorMatrix.Auto, 1280, 720, YuvColorMatrix.Bt709)]
    [InlineData(YuvColorMatrix.Auto, 1024, 576, YuvColorMatrix.Bt601)]
    [InlineData(YuvColorMatrix.Auto, 720, 576, YuvColorMatrix.Bt601)]
    public void ResolveMatrix_MapsExpected(YuvColorMatrix requested, int width, int height, YuvColorMatrix expected)
    {
        Assert.Equal(expected, YuvAutoPolicy.ResolveMatrix(requested, width, height));
    }
}

