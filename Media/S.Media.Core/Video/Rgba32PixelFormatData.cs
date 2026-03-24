namespace S.Media.Core.Video;

public readonly record struct Rgba32PixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Rgba32;

    public int BytesPerPixel => 4;
}

