using S.Media.Core.Media;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// Receives audio buffers from the graph. This is the <b>single unified audio endpoint
/// contract</b>: there is no separate "output" or "sink" interface. Replaces the legacy
/// <c>IAudioOutput</c>, <c>IAudioSink</c>, and <c>IAudioBufferEndpoint</c> types with
/// one push-based surface.
///
/// <para>
/// Endpoints that are driven by a real-time hardware callback (e.g. a PortAudio callback
/// stream) should additionally implement <see cref="IPullAudioEndpoint"/> — it is an
/// opt-in capability mixin, not a separate kind of endpoint. The router's
/// <c>RegisterEndpoint(IAudioEndpoint)</c> handles both cases via a runtime capability
/// check, so users register every audio destination through one method.
/// </para>
///
/// <para>
/// Endpoints that can provide a hardware or software clock should additionally implement
/// <see cref="IClockCapableEndpoint"/>; the router auto-registers it at
/// <c>ClockPriority.Hardware</c>. The clock can be overridden per-session via
/// <c>AVRouter.SetClock(...)</c> (priority <c>Override</c>) — e.g. to slave both PA
/// playback and NDI send to a PTP genlock source. When the override is removed the
/// resolver falls back to this endpoint's clock automatically.
/// </para>
/// </summary>
public interface IAudioEndpoint : IMediaEndpoint
{
    /// <summary>
    /// Called by the graph to deliver mixed/forwarded audio.
    /// Implementations MUST be non-blocking on the RT thread.
    /// </summary>
    void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format);

    /// <summary>
    /// Timestamped variant: delivers mixed audio together with the <b>stream-time PTS
    /// of the first sample</b> in <paramref name="buffer"/>.  Sinks that need to emit
    /// media timecodes (e.g. NDI, SMPTE ST 2110) should override this and ignore the
    /// simpler <see cref="ReceiveBuffer(ReadOnlySpan{float}, int, AudioFormat)"/> path.
    ///
    /// <para>
    /// The default implementation discards the PTS and forwards to
    /// <see cref="ReceiveBuffer(ReadOnlySpan{float}, int, AudioFormat)"/> so plain
    /// playback sinks continue to work unchanged.
    /// </para>
    /// </summary>
    /// <param name="buffer">Interleaved PCM data, <c>frameCount × format.Channels</c> samples.</param>
    /// <param name="frameCount">Number of frames in <paramref name="buffer"/>.</param>
    /// <param name="format">Audio format of <paramref name="buffer"/>.</param>
    /// <param name="sourcePts">
    /// Stream-time PTS of the first sample in <paramref name="buffer"/>, as reported by
    /// the upstream channel's <c>Position</c> at the moment of the read.  May be
    /// <see cref="TimeSpan.Zero"/> before the first successful decoder read.
    /// </param>
    void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts)
        => ReceiveBuffer(buffer, frameCount, format);

    /// <summary>
    /// Optional: the audio format this push endpoint prefers to receive. When
    /// non-<see langword="null"/>, the router uses this as the target of per-route
    /// resampling/channel mapping so
    /// <see cref="ReceiveBuffer(ReadOnlySpan{float}, int, AudioFormat)"/> is called at the
    /// negotiated rate/channel count with no further conversion expected.
    ///
    /// <para>
    /// For pull endpoints (<see cref="IPullAudioEndpoint"/>) the router always uses
    /// <see cref="IPullAudioEndpoint.EndpointFormat"/> instead and this value is ignored.
    /// </para>
    ///
    /// <para>
    /// Default: <see langword="null"/> — endpoint accepts whatever the upstream mixer
    /// produces (the pre-existing behaviour; per-route resamplers are not created
    /// unless the endpoint advertises a format).
    /// </para>
    /// </summary>
    AudioFormat? NegotiatedFormat => null;
}
