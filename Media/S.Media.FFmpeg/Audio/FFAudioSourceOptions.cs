namespace S.Media.FFmpeg.Audio;

public sealed record FFAudioSourceOptions
{
    public FFAudioChannelMappingPolicy MappingPolicy { get; init; } = FFAudioChannelMappingPolicy.PreserveSourceLayout;

    public FFAudioChannelMap? ExplicitChannelMap { get; init; }

    public int? OutputChannelCountOverride { get; init; }
}
