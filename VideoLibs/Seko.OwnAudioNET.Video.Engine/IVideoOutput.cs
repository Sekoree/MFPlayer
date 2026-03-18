using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Output sink that can be used by source-driven transport engines and sink-style output engines.
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

    /// <summary>
    /// Pushes a frame into the output sink.
    /// Returns <see langword="false"/> when the output rejects the frame.
    /// </summary>
    bool PushFrame(VideoFrame frame, double masterTimestamp);
}

