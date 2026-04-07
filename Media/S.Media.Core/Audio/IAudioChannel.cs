using S.Media.Core.Media;

namespace S.Media.Core.Audio;

/// <summary>
/// A single audio source that feeds into an <see cref="IAudioMixer"/>.
/// The source format is independent of the hardware output format;
/// the mixer handles resampling and channel routing.
/// </summary>
public interface IAudioChannel : IMediaChannel<float>
{
    /// <summary>The native PCM format of this source (may differ from the hardware output).</summary>
    AudioFormat SourceFormat { get; }

    /// <summary>Per-channel linear volume multiplier. Range: 0.0 – 1.0 (can exceed 1.0 for gain).</summary>
    float Volume { get; set; }

    /// <summary>Current playback position, derived from samples consumed.</summary>
    TimeSpan Position { get; }

    // ── Push mode (decoder-driven, back-pressured) ──────────────────────

    /// <summary>
    /// Number of full source-format frames the internal ring buffer can hold.
    /// Configured at construction time.
    /// </summary>
    int BufferDepth { get; }

    /// <summary>Number of frames currently available in the ring buffer.</summary>
    int BufferAvailable { get; }

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

    /// <summary>
    /// Raised (on a background thread) when the pull path finds the ring buffer empty
    /// and fills frames with silence.
    /// </summary>
    event EventHandler<BufferUnderrunEventArgs>? BufferUnderrun;
}

