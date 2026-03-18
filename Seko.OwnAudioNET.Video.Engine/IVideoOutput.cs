using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Output sink that can be registered to <see cref="IVideoEngine"/> for engine-managed source wiring.
/// </summary>
public interface IVideoOutput : IDisposable
{
    /// <summary>Unique identifier for this output instance.</summary>
    Guid Id { get; }

    /// <summary>Source currently attached to this output, or <see langword="null"/>.</summary>
    IVideoSource? Source { get; }

    /// <summary><see langword="true"/> when a source is currently attached.</summary>
    bool IsAttached { get; }

    /// <summary>Attaches this output to a source.</summary>
    bool AttachSource(IVideoSource source);

    /// <summary>Detaches the currently attached source.</summary>
    void DetachSource();
}

