namespace S.Media.Core.Media;

/// <summary>
/// Describes the audio properties of a media stream.
/// <para>
/// <b>Nullable semantics:</b> All properties are nullable because live/network sources may not
/// know their properties until capture begins. Consumers that use <c>GetValueOrDefault()</c>
/// should treat a <c>0</c> sample rate or channel count as "unknown/uninitialized" rather than
/// a valid configuration. Check <see cref="SampleRate"/><c>.HasValue</c> and
/// <see cref="ChannelCount"/><c>.HasValue</c> before relying on the values.
/// </para>
/// </summary>
public readonly record struct AudioStreamInfo
{
    public string? Codec { get; init; }

    public int? SampleRate { get; init; }

    public int? ChannelCount { get; init; }

    public long? Bitrate { get; init; }

    public TimeSpan? Duration { get; init; }
}
