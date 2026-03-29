namespace S.Media.Core.Mixing;

/// <summary>
/// Event arguments for audio or video source errors raised by the mixer.
/// Replaces the separate <c>AudioSourceErrorEventArgs</c> and <c>VideoSourceErrorEventArgs</c>
/// types which were structurally identical.
/// </summary>
public sealed class MediaSourceErrorEventArgs : EventArgs
{
    public MediaSourceErrorEventArgs(Guid sourceId, int errorCode, string? message)
    {
        Id = sourceId;
        ErrorCode = errorCode;
        Message = message;
    }

    /// <summary>Id of the source that produced the error.</summary>
    public Guid Id { get; }

    /// <summary>Error code returned by the source operation.</summary>
    public int ErrorCode { get; }

    /// <summary>Optional diagnostic message.</summary>
    public string? Message { get; }
}

