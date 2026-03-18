namespace Seko.OwnAudioNET.Video.Probing;

/// <summary>Describes one container stream discovered by <see cref="MediaStreamCatalog"/>.</summary>
public readonly record struct MediaStreamInfoEntry(
    int Index,
    MediaStreamKind Kind,
    string Codec,
    string? Language,
    int? Channels,
    int? SampleRate,
    int? Width,
    int? Height,
    double? FrameRate,
    TimeSpan? Duration,
    long? BitRate);

