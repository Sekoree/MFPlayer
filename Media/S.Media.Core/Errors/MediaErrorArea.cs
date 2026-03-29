namespace S.Media.Core.Errors;

public enum MediaErrorArea
{
    Unknown = 0,
    GenericCommon = 1,
    Playback = 2,
    Decoding = 3,
    Mixing = 4,
    OutputRender = 5,
    NDI = 6,
    PortAudio = 7,
    OpenGL = 8,
    MIDI = 9,
    SDL3 = 10,
}
