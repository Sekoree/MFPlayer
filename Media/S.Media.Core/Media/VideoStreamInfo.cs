namespace S.Media.Core.Media;

/// <summary>
/// Describes the video properties of a media stream.
/// <para>
/// <b>Nullable semantics:</b> All properties are nullable because live/network sources may not
/// know their properties until capture begins. Consumers should check <c>.HasValue</c> before
/// relying on <see cref="Width"/>, <see cref="Height"/>, or <see cref="FrameRate"/>.
/// A <c>0</c> or <see langword="null"/> value means "unknown/uninitialized".
/// </para>
/// </summary>
public readonly record struct VideoStreamInfo
{
    public string? Codec { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? FrameRate { get; init; }

    public long? Bitrate { get; init; }

    public TimeSpan? Duration { get; init; }
}
