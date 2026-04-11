using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Video;

namespace S.Media.Core.Mixing;

/// <summary>
/// Composition-based AV mixer that wraps existing audio/video mixers.
/// This enables incremental migration to a unified pipeline API.
/// </summary>
public sealed class AVMixer : IAVMixer
{
    private readonly bool _ownsAudio;
    private readonly bool _ownsVideo;
    private bool _disposed;

    public IAudioMixer Audio { get; }
    public IVideoMixer Video { get; }
    public IAVMixer.ClockMasterPolicy MasterPolicy { get; set; } = IAVMixer.ClockMasterPolicy.Audio;

    public AVMixer(IAudioMixer audioMixer, IVideoMixer videoMixer, bool ownsAudio = false, bool ownsVideo = false)
    {
        Audio = audioMixer ?? throw new ArgumentNullException(nameof(audioMixer));
        Video = videoMixer ?? throw new ArgumentNullException(nameof(videoMixer));
        _ownsAudio = ownsAudio;
        _ownsVideo = ownsVideo;
    }

    public AVMixer(AudioFormat audioFormat, VideoFormat videoFormat, ChannelFallback audioFallback = ChannelFallback.Silent)
        : this(new AudioMixer(audioFormat, audioFallback), new VideoMixer(videoFormat), ownsAudio: true, ownsVideo: true)
    {
    }

    public void AddAudioChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null)
        => Audio.AddChannel(channel, routeMap, resampler);

    public void RemoveAudioChannel(Guid channelId)
        => Audio.RemoveChannel(channelId);

    public void AddVideoChannel(IVideoChannel channel)
        => Video.AddChannel(channel);

    public void RemoveVideoChannel(Guid channelId)
        => Video.RemoveChannel(channelId);

    public void SetActiveVideoChannel(Guid? channelId)
        => Video.SetActiveChannel(channelId);

    public void RegisterAudioSink(IAudioSink sink, int channels = 0)
        => Audio.RegisterSink(sink, channels);

    public void UnregisterAudioSink(IAudioSink sink)
        => Audio.UnregisterSink(sink);

    public void RouteAudioChannelToSink(Guid channelId, IAudioSink sink, ChannelRouteMap routeMap)
        => Audio.RouteTo(channelId, sink, routeMap);

    public void UnrouteAudioChannelFromSink(Guid channelId, IAudioSink sink)
        => Audio.UnrouteTo(channelId, sink);

    public void RegisterVideoSink(IVideoSink sink)
        => Video.RegisterSink(sink);

    public void UnregisterVideoSink(IVideoSink sink)
        => Video.UnregisterSink(sink);

    public void RouteVideoChannelToSink(Guid channelId, IVideoSink sink)
        => Video.SetActiveChannelForSink(sink, channelId);

    public void UnrouteVideoChannelFromSink(IVideoSink sink)
        => Video.SetActiveChannelForSink(sink, null);

    public void RouteVideoChannelToSinks(Guid channelId, IReadOnlyList<IVideoSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        for (int i = 0; i < sinks.Count; i++)
            RouteVideoChannelToSink(channelId, sinks[i]);
    }

    public void RouteAudioChannelToSinks(Guid channelId, IReadOnlyList<(IAudioSink Sink, ChannelRouteMap RouteMap)> sinkRoutes)
    {
        ArgumentNullException.ThrowIfNull(sinkRoutes);
        for (int i = 0; i < sinkRoutes.Count; i++)
            RouteAudioChannelToSink(channelId, sinkRoutes[i].Sink, sinkRoutes[i].RouteMap);
    }

    public TimeSpan ResolveMasterPosition(TimeSpan audioPosition, TimeSpan videoPosition, TimeSpan? externalPosition = null)
    {
        return MasterPolicy switch
        {
            IAVMixer.ClockMasterPolicy.Audio => audioPosition,
            IAVMixer.ClockMasterPolicy.Video => videoPosition,
            IAVMixer.ClockMasterPolicy.External => externalPosition ?? audioPosition,
            _ => audioPosition
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsAudio)
            Audio.Dispose();
        if (_ownsVideo)
            Video.Dispose();
    }
}

