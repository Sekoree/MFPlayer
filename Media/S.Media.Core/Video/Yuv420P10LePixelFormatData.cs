namespace S.Media.Core.Video;

public readonly record struct Yuv420P10LePixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Yuv420P10Le;

    public int ChromaSubsampleX => 2;

    public int ChromaSubsampleY => 2;

    public int BitsPerComponent => 10;

    public int ContainerBitsPerComponent => 16;
}
