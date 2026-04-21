namespace S.Media.Core.Media;

/// <summary>
/// Base interface for all media source channels.
/// <typeparam name="TFrame">
/// The frame element type — <c>float</c> for audio (interleaved PCM samples),
/// <see cref="VideoFrame"/> for video.
/// </typeparam>
/// </summary>
public interface IMediaChannel<TFrame> : IDisposable
{
    /// <summary>Unique identifier for this channel instance.</summary>
    Guid Id { get; }

    /// <summary>Whether the channel is open and can provide data.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Pull mode — the output/mixer requests up to <paramref name="frameCount"/> frames.
    /// Returns the number of frames with <i>non-silent</i> content actually produced;
    /// any trailing frames up to <paramref name="frameCount"/> must be left zero-filled
    /// by the implementation so callers can treat the tail as silence without an extra
    /// clear.  Implementations must be non-blocking: on underrun, return the partial
    /// count (possibly 0) and zero-fill the remainder.  <paramref name="dest"/> is always
    /// sized to hold <c>frameCount × channels</c> elements for audio, or
    /// <paramref name="frameCount"/> entries for video.
    /// </summary>
    int FillBuffer(Span<TFrame> dest, int frameCount);

    /// <summary>Whether seeking is supported by this channel's source.</summary>
    bool CanSeek { get; }

    /// <summary>Seek to the given position. Flushes internal buffers.</summary>
    void Seek(TimeSpan position);

    /// <summary>
    /// Raised when the channel's backing source signals that no more frames will be produced.
    /// For file-backed channels this fires once after the last frame has been pushed into the buffer.
    /// Subscribe to this event to trigger auto-stop / looping / next-item logic.
    /// </summary>
    event EventHandler? EndOfStream;
}

