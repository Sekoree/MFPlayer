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

    /// <summary>Number of registered secondary sinks.</summary>
    int SinkCount { get; }

    /// <summary>
    /// The channel currently routed to the primary output path, or <see langword="null"/>
    /// if no channel is active. Outputs can use this to auto-apply
    /// <see cref="IVideoColorMatrixHint"/> values without requiring the caller to set them manually.
    /// </summary>
    IVideoChannel? ActiveChannel => null;

    /// <summary>Registers a video channel.</summary>
    void AddChannel(IVideoChannel channel);

    /// <summary>Removes a previously registered channel by its Id.</summary>
    void RemoveChannel(Guid channelId);

    /// <summary>
    /// Routes a registered channel to the primary output path.
    /// </summary>
    void RouteChannelToPrimaryOutput(Guid channelId);

    /// <summary>
    /// Removes any channel route from the primary output path.
    /// </summary>
    void UnroutePrimaryOutput();

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
    /// Sets a time offset for a registered video channel.
    /// Positive values delay the channel (frames present later relative to the clock);
    /// negative values advance it (frames present earlier).
    /// </summary>
    /// <param name="channelId">The channel's <see cref="IVideoChannel.Id"/>.</param>
    /// <param name="offset">Time offset to apply. <see cref="TimeSpan.Zero"/> removes any offset.</param>
    void SetChannelTimeOffset(Guid channelId, TimeSpan offset);

    /// <summary>
    /// Gets the current time offset for a registered video channel.
    /// Returns <see cref="TimeSpan.Zero"/> if no offset has been set.
    /// </summary>
    TimeSpan GetChannelTimeOffset(Guid channelId);

    /// <summary>
    /// When <see langword="true"/>, <see cref="PresentNextFrame"/> bypasses PTS-based
    /// scheduling and always presents the newest available frame, dropping all older frames.
    /// Appropriate for live NDI monitoring where "show the latest picture" is always correct;
    /// PTS-based scheduling only adds delay for live sources.
    /// Default: <see langword="false"/> (PTS-based scheduling for file playback).
    /// </summary>
    bool LiveMode { get; set; }

    /// <summary>
    /// Called by the render loop to pull the next frame from the active channel
    /// and present it. The mixer uses <paramref name="clockPosition"/> for basic
    /// PTS pacing (hold if early, advance when due).
    /// When <see cref="LiveMode"/> is <see langword="true"/>, the clock position is ignored
    /// and the newest available frame is presented unconditionally.
    /// </summary>
    VideoFrame? PresentNextFrame(TimeSpan clockPosition);
}
