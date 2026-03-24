namespace S.Media.Core.Mixing;

public sealed class AudioSourceErrorEventArgs : EventArgs
{
    public AudioSourceErrorEventArgs(Guid sourceId, int errorCode, string? message)
    {
        SourceId = sourceId;
        ErrorCode = errorCode;
        Message = message;
    }

    public Guid SourceId { get; }

    public int ErrorCode { get; }

    public string? Message { get; }
}

