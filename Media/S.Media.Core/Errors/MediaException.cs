namespace S.Media.Core;

/// <summary>Base exception for all media pipeline errors.</summary>
public class MediaException : Exception
{
    public MediaException() { }
    public MediaException(string message) : base(message) { }
    public MediaException(string message, Exception inner) : base(message, inner) { }
}

