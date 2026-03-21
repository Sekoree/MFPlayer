namespace Seko.OwnAudioNET.Video.NDI;

using Seko.OwnAudioNET.Video.Clocks;

/// <summary>
/// Configuration for outbound NDI sender engines.
/// </summary>
public sealed class NDIEngineConfig
{
    /// <summary>Advertised NDI sender name (discoverable on the network).</summary>
    public string SenderName { get; init; } = "MFPlayer";

    /// <summary>Optional NDI group list.</summary>
    public string? Groups { get; init; }

    /// <summary>Whether the native sender should internally clock video submission.</summary>
    public bool ClockVideo { get; init; } = true;

    /// <summary>Whether the native sender should internally clock audio submission.</summary>
    public bool ClockAudio { get; init; } = true;

    /// <summary>
    /// Optional external timeline clock. When present and <see cref="UseIncomingVideoTimestamps"/> is false,
    /// this is used for outbound video timestamps unless audio timeline is already active.
    /// </summary>
    public IExternalClock? ExternalClock { get; init; }

    /// <summary>
    /// When true, <see cref="Engine.IVideoEngine.PushFrame"/> master timestamps are used for outbound video.
    /// When false, the engine prefers audio-master/internal timeline.
    /// </summary>
    public bool UseIncomingVideoTimestamps { get; init; }

    /// <summary>Outbound audio sample rate expected by the audio engine path.</summary>
    public int AudioSampleRate { get; init; } = 48000;

    /// <summary>Outbound audio channel count expected by the audio engine path.</summary>
    public int AudioChannels { get; init; } = 2;

    /// <summary>
    /// Preferred outbound format for incoming <see cref="VideoPixelFormat.Rgba32"/> frames.
    /// Auto keeps RGBA to avoid conversion.
    /// </summary>
    public NDIVideoRgbaSendFormat RgbaSendFormat { get; init; } = NDIVideoRgbaSendFormat.Auto;

    public NDIEngineConfig CloneNormalized()
    {
        return new NDIEngineConfig
        {
            SenderName = string.IsNullOrWhiteSpace(SenderName) ? "MFPlayer" : SenderName,
            Groups = Groups,
            ClockVideo = ClockVideo,
            ClockAudio = ClockAudio,
            ExternalClock = ExternalClock,
            UseIncomingVideoTimestamps = UseIncomingVideoTimestamps,
            AudioSampleRate = AudioSampleRate > 0 ? AudioSampleRate : 48000,
            AudioChannels = AudioChannels > 0 ? AudioChannels : 2,
            RgbaSendFormat = RgbaSendFormat
        };
    }
}

public enum NDIVideoRgbaSendFormat
{
    Auto = 0,
    Rgba = 1,
    Bgra = 2
}

