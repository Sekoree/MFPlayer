namespace S.Media.Core.Video;

public readonly record struct Yuv420PPixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Yuv420P;

    public int ChromaSubsampleX => 2;

    public int ChromaSubsampleY => 2;
}
