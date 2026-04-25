namespace S.Media.Core.Media;

/// <summary>
/// Capability contract for inputs that support transport seeking.
/// </summary>
public interface ISeekableInput
{
    /// <summary>Whether seeking is supported by this input source.</summary>
    bool CanSeek { get; }

    /// <summary>Seek to the given source position.</summary>
    void Seek(TimeSpan position);
}

