namespace Seko.OwnAudioSharp.Video;


/// <summary>Immutable metadata snapshot describing a video stream.</summary>
public readonly struct VideoStreamInfo
{
    /// <summary>Initializes a new instance of the <see cref="VideoStreamInfo"/> struct.</summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="frameRate">Nominal frame rate in frames per second.</param>
    /// <param name="duration">Total stream duration. <see cref="TimeSpan.Zero"/> when the container does not report a duration.</param>
    /// <param name="pixelFormat">Pixel format of the decoded frames.</param>
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

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Nominal frame rate in frames per second.</summary>
    public double FrameRate { get; }

    /// <summary>Total stream duration. <see cref="TimeSpan.Zero"/> when the container does not report a duration.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Pixel format of the decoded frames.</summary>
    public VideoPixelFormat PixelFormat { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Width}x{Height} @ {FrameRate:F2} fps, {Duration:g}, {PixelFormat}";
}