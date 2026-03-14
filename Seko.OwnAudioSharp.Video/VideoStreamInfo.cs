namespace Seko.OwnAudioSharp.Video;

public enum VideoPixelFormat
{
    Rgba32
}

public readonly struct VideoStreamInfo
{
    public VideoStreamInfo(
        int width,
        int height,
        double frameRate,
        TimeSpan duration,
        VideoPixelFormat pixelFormat)
    {
        Width = width;
        Height = height;
        FrameRate = frameRate;
        Duration = duration;
        PixelFormat = pixelFormat;
    }

    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public TimeSpan Duration { get; }
    public VideoPixelFormat PixelFormat { get; }
}