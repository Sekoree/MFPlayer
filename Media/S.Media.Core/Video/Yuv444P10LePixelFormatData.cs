namespace S.Media.Core.Video;

public readonly record struct Yuv444P10LePixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Yuv444P10Le;

    public int ChromaSubsampleX => 1;

    public int ChromaSubsampleY => 1;

    public int BitsPerComponent => 10;

    public int ContainerBitsPerComponent => 16;
}
