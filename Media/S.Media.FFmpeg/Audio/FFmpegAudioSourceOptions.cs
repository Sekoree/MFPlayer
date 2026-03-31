namespace S.Media.FFmpeg.Audio;

public sealed record FFmpegAudioSourceOptions
{
    public FFmpegAudioChannelMappingPolicy MappingPolicy { get; init; } = FFmpegAudioChannelMappingPolicy.PreserveSourceLayout;

    public FFmpegAudioChannelMap? ExplicitChannelMap { get; init; }

    public int? OutputChannelCountOverride { get; init; }
}
