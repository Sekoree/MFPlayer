using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

/// <summary>
/// Unified facade over separate audio and video mixers.
/// Coexists with <see cref="IAudioMixer"/> and <see cref="IVideoMixer"/> for gradual migration.
/// </summary>
public interface IAVMixer : IDisposable
{
    public enum ClockMasterPolicy
    {
        Audio,
        Video,
        External
    }

    IAudioMixer Audio { get; }
    IVideoMixer Video { get; }
    ClockMasterPolicy MasterPolicy { get; set; }

    void AddAudioChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null);
    void RemoveAudioChannel(Guid channelId);

    void AddVideoChannel(IVideoChannel channel);
    void RemoveVideoChannel(Guid channelId);
    void SetActiveVideoChannel(Guid? channelId);

    void RegisterAudioSink(IAudioSink sink, int channels = 0);
    void UnregisterAudioSink(IAudioSink sink);
    void RouteAudioChannelToSink(Guid channelId, IAudioSink sink, ChannelRouteMap routeMap);
    void UnrouteAudioChannelFromSink(Guid channelId, IAudioSink sink);

    void RegisterVideoSink(IVideoSink sink);
    void UnregisterVideoSink(IVideoSink sink);
    void RouteVideoChannelToSink(Guid channelId, IVideoSink sink);
    void UnrouteVideoChannelFromSink(IVideoSink sink);

    void RouteVideoChannelToSinks(Guid channelId, IReadOnlyList<IVideoSink> sinks);
    void RouteAudioChannelToSinks(Guid channelId, IReadOnlyList<(IAudioSink Sink, ChannelRouteMap RouteMap)> sinkRoutes);

    TimeSpan ResolveMasterPosition(TimeSpan audioPosition, TimeSpan videoPosition, TimeSpan? externalPosition = null);
}

