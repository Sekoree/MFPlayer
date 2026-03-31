namespace S.Media.FFmpeg.Runtime;

internal readonly record struct FFStreamDescriptor
{
    public int StreamIndex { get; init; }

    public string? CodecName { get; init; }

    public TimeSpan? Duration { get; init; }

    public int? SampleRate { get; init; }

    public int? ChannelCount { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? FrameRate { get; init; }
}
