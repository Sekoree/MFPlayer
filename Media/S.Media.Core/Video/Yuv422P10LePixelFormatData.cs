namespace S.Media.Core.Video;

public readonly record struct Yuv422P10LePixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Yuv422P10Le;

    public int ChromaSubsampleX => 2;

    public int ChromaSubsampleY => 1;

    public int BitsPerComponent => 10;

    public int ContainerBitsPerComponent => 16;
}

