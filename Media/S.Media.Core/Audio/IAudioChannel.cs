using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// Pull-mode audio source that feeds into an <see cref="IAudioMixer"/> / router.
/// The source format is independent of the hardware output format;
/// the mixer handles resampling and channel routing.
/// <para>
/// This is the <b>base</b> contract — consumers that only need to read audio
/// (routers, mixers, diagnostics) should take an <see cref="IAudioChannel"/>.
/// Producers that need to write into a channel's ring buffer should accept the
/// subtype <see cref="IWritableAudioChannel"/> instead.
/// </para>
/// </summary>
public interface IAudioChannel : IMediaChannel<float>
{
    /// <summary>The native PCM format of this source (may differ from the hardware output).</summary>
    AudioFormat SourceFormat { get; }

    /// <summary>Per-channel linear volume multiplier. Range: 0.0 – 1.0 (can exceed 1.0 for gain).</summary>
    /// <remarks>
    /// <b>§3.56 / §0.4.5 decision — Keep + legacy:</b> the setter is retained for
    /// legacy direct-channel callers but is marked <c>[Obsolete]</c> as of the
    /// 2026-04-23 fifth pass. Routed playback should use
    /// <c>AVRouter.SetInputVolume(inputId, volume)</c>, which participates in
    /// per-input peak metering and per-route gain automation (§7.3). The
    /// in-tree <c>MediaPlayer</c> facade has already been migrated; future
    /// major versions may remove the setter entirely, but the getter stays
    /// non-obsolete because diagnostic / meter code legitimately reads it.
    /// </remarks>
    float Volume
    {
        get;
        [Obsolete("Channel-level Volume is legacy — use AVRouter.SetInputVolume(inputId, volume) so per-input peak metering and per-route gain automation stay coherent. See Implementation-Checklist.md §3.56.")]
        set;
    }

    /// <summary>
    /// Current playback position, derived from samples consumed by the RT pull
    /// callback. Advances as <see cref="IMediaChannel{T}.FillBuffer"/> runs.
    /// </summary>
    TimeSpan Position { get; }

    /// <summary>
    /// PTS of the <em>next</em> sample that will be produced by <see cref="IMediaChannel{T}.FillBuffer"/>.
    /// For most channels this equals <see cref="Position"/>, but for channels
    /// that pre-read a chunk into a scratch buffer (e.g. decoder-driven pull
    /// channels) <see cref="ReadHeadPosition"/> can be ahead of
    /// <see cref="Position"/> by up to one chunk. Used by diagnostics and by
    /// the router's drift tracker to avoid the "Position updates after the
    /// read" ambiguity flagged by §3.49 / CH2.
    /// <para>
    /// Default implementation returns <see cref="Position"/>; channels that
    /// need the distinction override it.
    /// </para>
    /// </summary>
    TimeSpan ReadHeadPosition => Position;

    /// <summary>
    /// Number of full source-format frames the internal ring buffer can hold.
    /// Configured at construction time.
    /// </summary>
    int BufferDepth { get; }

    /// <summary>Number of frames currently available in the ring buffer.</summary>
    int BufferAvailable { get; }

    /// <summary>
    /// Raised (on a background thread) when the pull path finds the ring buffer empty
    /// and fills frames with silence.
    /// </summary>
    event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;

    /// <summary>
    /// Raised when the channel's backing source signals that no more frames will be produced.
    /// For file-backed channels this fires once after the last audio chunk has been pushed.
    /// Subscribe to trigger auto-stop / looping / next-item logic.
    /// </summary>
    new event EventHandler? EndOfStream;
}

/// <summary>
/// Audio channel that also accepts external writes into its ring buffer
/// (used by <see cref="AudioChannel"/> and user code that pushes PCM data).
/// Decoder-driven channels (e.g. <c>FFmpegAudioChannel</c>) typically implement
/// only the base <see cref="IAudioChannel"/> — their ring is populated internally.
/// </summary>
public interface IWritableAudioChannel : IAudioChannel
{
    /// <summary>
    /// Writes <paramref name="frames"/> into the ring buffer.
    /// Awaits back-pressure when the buffer is full.
    /// <para>
    /// <paramref name="frames"/> must contain a whole number of interleaved frames
    /// (i.e. <c>frames.Length % SourceFormat.Channels == 0</c>).
    /// </para>
    /// </summary>
    ValueTask WriteAsync(ReadOnlyMemory<float> frames, CancellationToken ct = default);

    /// <summary>
    /// Non-blocking write. Returns <see langword="false"/> when the buffer is full.
    /// </summary>
    bool TryWrite(ReadOnlySpan<float> frames);
}

