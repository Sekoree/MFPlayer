namespace S.Media.Core.Video;

public readonly record struct P010LePixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.P010Le;

    public int ChromaSubsampleX => 2;

    public int ChromaSubsampleY => 2;

    public int BitsPerComponent => 10;

    public int ContainerBitsPerComponent => 16;
}
