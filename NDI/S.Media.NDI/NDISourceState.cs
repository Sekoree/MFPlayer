namespace S.Media.NDI;

/// <summary>
/// Connection state of an <see cref="NDISource"/>.
/// </summary>
public enum NDISourceState
{
    /// <summary>Not connected to any NDI source.</summary>
    Disconnected,

    /// <summary>Searching for an NDI source (name-based discovery).</summary>
    Discovering,

    /// <summary>Connected and actively receiving frames.</summary>
    Connected,

    /// <summary>Source went offline; attempting to reconnect.</summary>
    Reconnecting,
}

/// <summary>
/// Event args for <see cref="NDISource.StateChanged"/>.
/// </summary>
public sealed class NDISourceStateChangedEventArgs : EventArgs
{
    /// <summary>Previous state.</summary>
    public NDISourceState OldState { get; }

    /// <summary>New state.</summary>
    public NDISourceState NewState { get; }

    /// <summary>The NDI source name (if known).</summary>
    public string? SourceName { get; }

    public NDISourceStateChangedEventArgs(NDISourceState oldState, NDISourceState newState, string? sourceName)
    {
        OldState   = oldState;
        NewState   = newState;
        SourceName = sourceName;
    }
}

