namespace S.Media.FFmpeg.Audio;

public enum FFAudioChannelMappingPolicy
{
    PreserveSourceLayout = 0,
    ApplyExplicitRouteMap = 1,
    DownmixToStereo = 2,
    DownmixToMono = 3,
}
