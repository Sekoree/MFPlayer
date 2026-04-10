using S.Media.Core.Media;

namespace S.Media.Core.Video;

/// <summary>
/// Manages video channels and presents the active channel's frames to the output.
/// In v1 this is single-channel (no compositing / layering).
/// Analogous to <see cref="Audio.IAudioMixer"/> in structure.
/// </summary>
public interface IVideoMixer : IDisposable
{
    /// <summary>The format of the output surface.</summary>
    VideoFormat OutputFormat { get; }

    /// <summary>Number of channels currently registered.</summary>
    int ChannelCount { get; }

    /// <summary>
    /// The channel currently being presented. Null if no channel is active.
    /// Only one channel is rendered at a time in v1.
    /// </summary>
    IVideoChannel? ActiveChannel { get; }

    /// <summary>Number of registered secondary sinks.</summary>
    int SinkCount { get; }

    /// <summary>Registers a video channel.</summary>
    void AddChannel(IVideoChannel channel);

    /// <summary>Removes a previously registered channel by its Id.</summary>
    void RemoveChannel(Guid channelId);

    /// <summary>
    /// Sets which registered channel is actively being rendered.
    /// Pass null to show a blank/black frame.
    /// </summary>
    void SetActiveChannel(Guid? channelId);

    /// <summary>
    /// Registers a sink as an additional video target.
    /// </summary>
    void RegisterSink(IVideoSink sink);

    /// <summary>
    /// Removes a previously registered sink.
    /// </summary>
    void UnregisterSink(IVideoSink sink);

    /// <summary>
    /// Selects one active channel for a specific sink target.
    /// Pass null to make the sink output black/no frame updates.
    /// </summary>
    void SetActiveChannelForSink(IVideoSink sink, Guid? channelId);

    /// <summary>
    /// Called by the render loop to pull the next frame from the active channel
    /// and present it. The mixer uses <paramref name="clockPosition"/> for basic
    /// PTS pacing (hold if early, advance when due).
    /// </summary>
    VideoFrame? PresentNextFrame(TimeSpan clockPosition);
}

