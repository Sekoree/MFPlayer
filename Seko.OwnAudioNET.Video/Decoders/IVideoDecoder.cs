namespace Seko.OwnAudioNET.Video.Decoders;

/// <summary>Provides raw decoded video frames from an underlying media stream.</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>Metadata describing the video stream (dimensions, frame rate, duration).</summary>
    VideoStreamInfo StreamInfo { get; }

    /// <summary><see langword="true"/> once the decoder has consumed and flushed all frames.</summary>
    bool IsEndOfStream { get; }

    /// <summary><see langword="true"/> when hardware-accelerated decoding is active.</summary>
    bool IsHardwareDecoding { get; }

    /// <summary>
    /// Raised when decoder output format metadata changes at runtime (for example, mid-stream resolution changes).
    /// </summary>
    event Action<VideoStreamInfo>? StreamInfoChanged;

    /// <summary>
    /// Reads and converts the next video frame into a pooled <see cref="VideoFrame"/>.
    /// </summary>
    /// <param name="frame">
    /// The decoded RGBA frame on success. Callers must call <see cref="VideoFrame.Dispose"/> when done.
    /// </param>
    /// <param name="error">Human-readable error description on failure, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a frame was produced; <see langword="false"/> on error or end-of-stream.</returns>
    bool TryDecodeNextFrame(out VideoFrame frame, out string? error);

    /// <summary>Seeks the underlying stream to the specified position, flushing internal codec buffers.</summary>
    /// <param name="position">Target playback position. Clamped to <see cref="TimeSpan.Zero"/> if negative.</param>
    /// <param name="error">Human-readable error description on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    bool TrySeek(TimeSpan position, out string error);
}