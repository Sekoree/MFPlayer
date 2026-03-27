namespace S.Media.Core.Video;

public readonly record struct Bgra32PixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Bgra32;

    public int BytesPerPixel => 4;
}
