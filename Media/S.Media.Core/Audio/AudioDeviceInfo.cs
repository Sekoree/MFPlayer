namespace S.Media.Core.Audio;

/// <summary>Identifies the audio host API family.</summary>
public enum HostApiType
{
    Unknown       = 0,
    DirectSound   = 1,
    Mme           = 2,
    Asio          = 3,
    CoreAudio     = 5,
    Oss           = 7,
    Alsa          = 8,
    Jack          = 12,
    Wasapi        = 13,
}

/// <summary>Describes an audio host API available on the system.</summary>
public sealed record AudioHostApiInfo(
    int         Index,
    string      Name,
    HostApiType Type,
    int         DeviceCount,
    int         DefaultInputDeviceIndex,
    int         DefaultOutputDeviceIndex);

/// <summary>Describes a physical or virtual audio device.</summary>
public sealed record AudioDeviceInfo(
    int    Index,
    string Name,
    int    HostApiIndex,
    int    MaxInputChannels,
    int    MaxOutputChannels,
    double DefaultSampleRate,
    double DefaultLowOutputLatency,
    double DefaultHighOutputLatency)
{
    /// <summary>
    /// Returns <c>Math.Min(requested, <see cref="MaxOutputChannels"/>)</c>.
    /// Useful for clamping a channel request to what the device actually supports,
    /// including JACK (which may report up to 256 output ports).
    /// </summary>
    public int ClampOutputChannels(int requested) => Math.Min(requested, MaxOutputChannels);

    /// <summary>
    /// Returns <c>Math.Min(requested, <see cref="MaxInputChannels"/>)</c>.
    /// </summary>
    public int ClampInputChannels(int requested) => Math.Min(requested, MaxInputChannels);
}

