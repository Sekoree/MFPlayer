using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

/// <summary>
/// Unified facade over separate audio and video mixers.
/// </summary>
public interface IAVMixer : IDisposable
{
    void AttachAudioOutput(IAudioOutput output);
    void AttachVideoOutput(IVideoOutput output);

    void AddAudioChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null);

    /// <summary>
    /// Adds an audio channel with an automatically-derived route map.
    /// Mono sources are expanded to both channels of a stereo output; otherwise
    /// channels are routed 1:1 up to the lesser of src and dst channel counts.
    /// </summary>
    void AddAudioChannel(IAudioChannel channel, IAudioResampler? resampler = null);
    void RemoveAudioChannel(Guid channelId);

    void AddVideoChannel(IVideoChannel channel);
    void RemoveVideoChannel(Guid channelId);

    // ── Per-channel time offsets ────────────────────────────────────────────

    /// <summary>Sets a time offset for a registered audio channel.</summary>
    void SetAudioChannelTimeOffset(Guid channelId, TimeSpan offset);

    /// <summary>Gets the current time offset for a registered audio channel.</summary>
    TimeSpan GetAudioChannelTimeOffset(Guid channelId);

    /// <summary>Sets a time offset for a registered video channel.</summary>
    void SetVideoChannelTimeOffset(Guid channelId, TimeSpan offset);

    /// <summary>Gets the current time offset for a registered video channel.</summary>
    TimeSpan GetVideoChannelTimeOffset(Guid channelId);

    /// <summary>
    /// Returns the instantaneous A/V drift between two channels:
    /// <c>audioChannel.Position − videoChannel.Position</c>.
    /// Positive means audio is ahead of video; negative means audio is behind.
    /// </summary>
    TimeSpan GetAvDrift(Guid audioChannelId, Guid videoChannelId);

    // ── Sink registration ──────────────────────────────────────────────────

    void RegisterAudioSink(IAudioSink sink, int channels = 0);
    void UnregisterAudioSink(IAudioSink sink);
    void RouteAudioChannelToSink(Guid channelId, IAudioSink sink, ChannelRouteMap routeMap);
    void UnrouteAudioChannelFromSink(Guid channelId, IAudioSink sink);

    void RegisterVideoSink(IVideoSink sink);
    void UnregisterVideoSink(IVideoSink sink);
    void RouteVideoChannelToSink(Guid channelId, IVideoSink sink);
    void UnrouteVideoChannelFromSink(IVideoSink sink);

    // ── Endpoint registration (preferred API) ──────────────────────────────

    /// <summary>
    /// Registers a video frame endpoint. The adapter wrapping is handled internally.
    /// </summary>
    void RegisterVideoEndpoint(IVideoFrameEndpoint endpoint);
    void UnregisterVideoEndpoint(IVideoFrameEndpoint endpoint);
    void RouteVideoChannelToEndpoint(Guid channelId, IVideoFrameEndpoint endpoint);
    void UnrouteVideoChannelFromEndpoint(IVideoFrameEndpoint endpoint);

    /// <summary>
    /// Registers an audio buffer endpoint. The adapter wrapping is handled internally.
    /// </summary>
    void RegisterAudioEndpoint(IAudioBufferEndpoint endpoint, int channels = 0);
    void UnregisterAudioEndpoint(IAudioBufferEndpoint endpoint);

    // ── Batch helpers ──────────────────────────────────────────────────────

    void RouteVideoChannelToSinks(Guid channelId, IReadOnlyList<IVideoSink> sinks);
    void RouteVideoChannelToEndpoints(Guid channelId, IReadOnlyList<IVideoFrameEndpoint> endpoints);
    void RouteAudioChannelToSinks(Guid channelId, IReadOnlyList<(IAudioSink Sink, ChannelRouteMap RouteMap)> sinkRoutes);
}
