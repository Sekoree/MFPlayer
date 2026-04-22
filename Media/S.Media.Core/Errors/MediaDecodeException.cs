namespace S.Media.Core.Errors;

/// <summary>
/// Thrown when a decoder (audio or video) fails while producing frames from an
/// already-opened media source.
/// </summary>
/// <remarks>
/// Closes review finding <b>EL1</b>: replaces <see cref="System.InvalidOperationException"/>
/// at decode-time API boundaries (FFmpeg / NDI channels). Separate from
/// <see cref="MediaOpenException"/> so callers can distinguish "cannot start"
/// from "broke mid-playback".
/// </remarks>
public class MediaDecodeException : MediaException
{
    /// <summary>Optional PTS (in the source timebase) at which decoding failed.</summary>
    public TimeSpan? Position { get; }

    public MediaDecodeException() { }
    public MediaDecodeException(string message) : base(message) { }
    public MediaDecodeException(string message, Exception inner) : base(message, inner) { }
    public MediaDecodeException(string message, TimeSpan? position) : base(message) { Position = position; }
    public MediaDecodeException(string message, TimeSpan? position, Exception inner) : base(message, inner) { Position = position; }
}

