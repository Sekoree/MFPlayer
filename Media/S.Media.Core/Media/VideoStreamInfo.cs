namespace S.Media.Core.Media;

public readonly record struct VideoStreamInfo
{
    public string? Codec { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? FrameRate { get; init; }

    public long? Bitrate { get; init; }

    public TimeSpan? Duration { get; init; }
}

