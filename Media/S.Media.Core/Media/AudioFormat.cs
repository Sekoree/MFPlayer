using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;

namespace S.Media.Core.Media;

/// <summary>
/// Describes the PCM format of an audio stream.
/// Source formats are independent of the hardware output format;
/// the mixer handles resampling and channel routing between the two.
/// </summary>
public readonly record struct AudioFormat(
    int        SampleRate,
    int        Channels,
    SampleType SampleType = SampleType.Float32)
{
    /// <summary>Number of samples (across all channels) per second.</summary>
    public int SamplesPerSecond => SampleRate * Channels;

    /// <summary>Returns true when this format exactly matches the hardware canonical format.</summary>
    public bool IsCanonical => SampleType == SampleType.Float32;

    public override string ToString() => $"{SampleRate} Hz / {Channels} ch / {SampleType}";

    // ── Negotiation helpers ────────────────────────────────────────────────

    /// <summary>
    /// Negotiates a hardware <see cref="AudioFormat"/> for <paramref name="device"/>
    /// that is compatible with <paramref name="source"/>, clamped to
    /// <paramref name="maxChannels"/> output channels (stereo by default).
    ///
    /// <para>
    /// Returns a tuple of the negotiated hardware format and a
    /// <see cref="ChannelRouteMap"/> that maps every source channel to the
    /// chosen output channels (mono fan-out, passthrough, or downmix).
    /// </para>
    ///
    /// <para>
    /// Replaces the <c>Math.Min(srcFmt.Channels, Math.Min(device.MaxOutputChannels, cap))</c>
    /// + <c>new AudioFormat(...)</c> + hand-rolled route-map block that is duplicated
    /// across every test app. Closes review finding §4.1.
    /// </para>
    /// </summary>
    /// <param name="source">The source PCM format (typically
    /// <c>IAudioChannel.SourceFormat</c>).</param>
    /// <param name="device">The target output device.</param>
    /// <param name="maxChannels">Hard cap on output channel count (default 2 = stereo).
    /// Pass <see cref="int.MaxValue"/> to allow the full device width.</param>
    public static (AudioFormat HardwareFormat, ChannelRouteMap RouteMap) NegotiateFor(
        AudioFormat     source,
        AudioDeviceInfo device,
        int             maxChannels = 2)
    {
        if (maxChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChannels), maxChannels,
                "Must be > 0.");
        if (device is null)
            throw new ArgumentNullException(nameof(device));

        // Mono sources fan out to stereo when the device + cap allow it; otherwise
        // clamp to the min of (source, device, cap).
        int deviceCh = Math.Max(1, device.MaxOutputChannels);
        int dstChannels;
        if (source.Channels == 1 && deviceCh >= 2 && maxChannels >= 2)
            dstChannels = 2;
        else
            dstChannels = Math.Min(source.Channels, Math.Min(deviceCh, maxChannels));

        if (dstChannels < 1) dstChannels = 1;

        var hwFmt = source with { Channels = dstChannels };
        var map   = ChannelRouteMap.AutoStereoDownmix(source.Channels, dstChannels);
        return (hwFmt, map);
    }

    /// <summary>
    /// Negotiates a hardware <see cref="AudioFormat"/> from an
    /// <see cref="S.Media.Core.Audio.IAudioChannel"/> source.
    /// Overload of <see cref="NegotiateFor(AudioFormat, AudioDeviceInfo, int)"/>.
    /// </summary>
    public static (AudioFormat HardwareFormat, ChannelRouteMap RouteMap) NegotiateFor(
        IAudioChannel   source,
        AudioDeviceInfo device,
        int             maxChannels = 2)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        return NegotiateFor(source.SourceFormat, device, maxChannels);
    }
}
