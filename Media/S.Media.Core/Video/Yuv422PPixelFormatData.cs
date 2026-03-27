namespace S.Media.Core.Video;

public readonly record struct Yuv422PPixelFormatData : IPixelFormatData
{
    public VideoPixelFormat Format => VideoPixelFormat.Yuv422P;

    public int ChromaSubsampleX => 2;

    public int ChromaSubsampleY => 1;

    public int BitsPerComponent => 8;
}
