namespace S.Media.Core.Video;

public readonly record struct Yuv444PPixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Yuv444P;

    public int ChromaSubsampleX => 1;

    public int ChromaSubsampleY => 1;

    public int BitsPerComponent => 8;
}
