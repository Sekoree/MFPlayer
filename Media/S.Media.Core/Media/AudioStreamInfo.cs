namespace S.Media.Core.Media;

public readonly record struct AudioStreamInfo
{
    public string? Codec { get; init; }

    public int? SampleRate { get; init; }

    public int? ChannelCount { get; init; }

    public long? Bitrate { get; init; }

    public TimeSpan? Duration { get; init; }
}
