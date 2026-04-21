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
    float Volume { get; set; }

    /// <summary>Current playback position, derived from samples consumed.</summary>
    TimeSpan Position { get; }

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

