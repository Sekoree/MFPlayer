namespace Seko.OwnAudioNET.Video.Events;

/// <summary>Event data for non-fatal or fatal video-source failures.</summary>
public sealed class VideoErrorEventArgs : EventArgs
{
    /// <summary>Initializes a new instance with an error message and optional exception.</summary>
    public VideoErrorEventArgs(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
    }

    /// <summary>Human-readable error message.</summary>
    public string Message { get; }

    /// <summary>Underlying exception when available.</summary>
    public Exception? Exception { get; }
}

