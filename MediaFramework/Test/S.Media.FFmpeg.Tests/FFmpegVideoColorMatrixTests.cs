using FFmpeg.AutoGen;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public sealed class FFmpegVideoColorMatrixTests
{
    [Fact]
    public void MapSuggestedYuvColorMatrix_Bt709_ReturnsBt709()
        => Assert.Equal(YuvColorMatrix.Bt709,
            FFmpegVideoChannel.MapSuggestedYuvColorMatrix(AVColorSpace.AVCOL_SPC_BT709));

    [Theory]
    [InlineData(AVColorSpace.AVCOL_SPC_BT470BG)]
    [InlineData(AVColorSpace.AVCOL_SPC_SMPTE170M)]
    [InlineData(AVColorSpace.AVCOL_SPC_FCC)]
    public void MapSuggestedYuvColorMatrix_Bt601Family_ReturnsBt601(AVColorSpace cs)
        => Assert.Equal(YuvColorMatrix.Bt601, FFmpegVideoChannel.MapSuggestedYuvColorMatrix(cs));

    [Fact]
    public void MapSuggestedYuvColorMatrix_Unknown_ReturnsAuto()
        => Assert.Equal(YuvColorMatrix.Auto,
            FFmpegVideoChannel.MapSuggestedYuvColorMatrix(AVColorSpace.AVCOL_SPC_UNSPECIFIED));

    [Fact]
    public void MapSuggestedYuvColorRange_Jpeg_ReturnsFull()
        => Assert.Equal(YuvColorRange.Full,
            FFmpegVideoChannel.MapSuggestedYuvColorRange(AVColorRange.AVCOL_RANGE_JPEG));

    [Fact]
    public void MapSuggestedYuvColorRange_Mpeg_ReturnsLimited()
        => Assert.Equal(YuvColorRange.Limited,
            FFmpegVideoChannel.MapSuggestedYuvColorRange(AVColorRange.AVCOL_RANGE_MPEG));

    [Fact]
    public void MapSuggestedYuvColorRange_Unspecified_ReturnsAuto()
        => Assert.Equal(YuvColorRange.Auto,
            FFmpegVideoChannel.MapSuggestedYuvColorRange(AVColorRange.AVCOL_RANGE_UNSPECIFIED));
}

