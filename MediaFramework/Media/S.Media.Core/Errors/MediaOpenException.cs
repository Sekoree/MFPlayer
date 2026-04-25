namespace S.Media.Core.Errors;

/// <summary>
/// Thrown when a media source cannot be opened (e.g. unreadable file, unsupported
/// container, missing codec, malformed network stream).
/// </summary>
/// <remarks>
/// Closes review finding <b>EL1</b>: replaces the previously-used
/// <see cref="System.InvalidOperationException"/> at open-time API boundaries
/// (<c>FFmpegDecoder.Open</c>, <c>MediaPlayer.OpenAsync</c>).
/// </remarks>
public class MediaOpenException : MediaException
{
    /// <summary>The resource path or logical identifier that failed to open (may be null).</summary>
    public string? ResourcePath { get; }

    public MediaOpenException() { }
    public MediaOpenException(string message) : base(message) { }
    public MediaOpenException(string message, Exception inner) : base(message, inner) { }
    public MediaOpenException(string message, string? resourcePath) : base(message) { ResourcePath = resourcePath; }
    public MediaOpenException(string message, string? resourcePath, Exception inner) : base(message, inner) { ResourcePath = resourcePath; }
}

