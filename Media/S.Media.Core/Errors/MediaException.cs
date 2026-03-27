namespace S.Media.Core.Errors;

public class MediaException : Exception
{
    public MediaException(MediaErrorCode errorCode, string message, string? correlationId = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        CorrelationId = correlationId;
    }

    public MediaErrorCode ErrorCode { get; }

    public string? CorrelationId { get; }
}
