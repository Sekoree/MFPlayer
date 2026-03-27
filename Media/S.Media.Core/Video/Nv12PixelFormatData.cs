namespace S.Media.Core.Video;

public readonly record struct Nv12PixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Nv12;

    public int ChromaSubsampleX => 2;

    public int ChromaSubsampleY => 2;
}
