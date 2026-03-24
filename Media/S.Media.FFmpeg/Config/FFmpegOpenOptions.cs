namespace S.Media.FFmpeg.Config;

public sealed record FFmpegOpenOptions
{
    public string? InputUri { get; init; }

    public Stream? InputStream { get; init; }

    public bool LeaveInputStreamOpen { get; init; } = true;

    public string? InputFormatHint { get; init; }

    public int? AudioStreamIndex { get; init; }

    public int? VideoStreamIndex { get; init; }

    public bool OpenAudio { get; init; } = true;

    public bool OpenVideo { get; init; } = true;

    public bool UseSharedDecodeContext { get; init; } = true;

    public bool EnableExternalClockCorrection { get; init; }
}

