using S.Media.Core.Audio;
using S.Media.Core.Audio.Endpoints;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.Core.Video.Endpoints;

namespace S.Media.Core.Mixing;

/// <summary>
/// Composition-based AV mixer that wraps existing audio/video mixers.
/// </summary>
public sealed class AVMixer : IAVMixer
{
    // Tracks endpoint→adapter pairs so we can unregister cleanly.
    private readonly Dictionary<IVideoFrameEndpoint, IVideoSink> _videoEndpointAdapters = new();
    private readonly Dictionary<IAudioBufferEndpoint, IAudioSink> _audioEndpointAdapters = new();

    private readonly IAudioMixer _audio;
    private readonly IVideoMixer _video;
    private readonly bool _ownsAudio;
    private readonly bool _ownsVideo;
    private bool _disposed;

    public AVMixer(IAudioMixer audioMixer, IVideoMixer videoMixer, bool ownsAudio = false, bool ownsVideo = false)
    {
        _audio = audioMixer ?? throw new ArgumentNullException(nameof(audioMixer));
        _video = videoMixer ?? throw new ArgumentNullException(nameof(videoMixer));
        _ownsAudio = ownsAudio;
        _ownsVideo = ownsVideo;
    }

    public AVMixer(AudioFormat audioFormat, VideoFormat videoFormat, ChannelFallback audioFallback = ChannelFallback.Silent)
        : this(new AudioMixer(audioFormat, audioFallback), new VideoMixer(videoFormat), ownsAudio: true, ownsVideo: true)
    {
    }

    // ── Channel management ────────────────────────────────────────────────

    public void AddAudioChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null)
        => _audio.AddChannel(channel, routeMap, resampler);

    public void RemoveAudioChannel(Guid channelId)
        => _audio.RemoveChannel(channelId);

    public void AddVideoChannel(IVideoChannel channel)
        => _video.AddChannel(channel);

    public void RemoveVideoChannel(Guid channelId)
        => _video.RemoveChannel(channelId);

    // ── Sink registration ─────────────────────────────────────────────────

    public void RegisterAudioSink(IAudioSink sink, int channels = 0)
        => _audio.RegisterSink(sink, channels);

    public void UnregisterAudioSink(IAudioSink sink)
        => _audio.UnregisterSink(sink);

    public void RouteAudioChannelToSink(Guid channelId, IAudioSink sink, ChannelRouteMap routeMap)
        => _audio.RouteTo(channelId, sink, routeMap);

    public void UnrouteAudioChannelFromSink(Guid channelId, IAudioSink sink)
        => _audio.UnrouteTo(channelId, sink);

    public void RegisterVideoSink(IVideoSink sink)
        => _video.RegisterSink(sink);

    public void UnregisterVideoSink(IVideoSink sink)
        => _video.UnregisterSink(sink);

    public void RouteVideoChannelToSink(Guid channelId, IVideoSink sink)
        => _video.SetActiveChannelForSink(sink, channelId);

    public void UnrouteVideoChannelFromSink(IVideoSink sink)
        => _video.SetActiveChannelForSink(sink, null);

    // ── Endpoint registration ─────────────────────────────────────────────

    public void RegisterVideoEndpoint(IVideoFrameEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (_videoEndpointAdapters.ContainsKey(endpoint)) return;
        var adapter = new VideoEndpointSinkAdapter(endpoint);
        _videoEndpointAdapters[endpoint] = adapter;
        _video.RegisterSink(adapter);
    }

    public void UnregisterVideoEndpoint(IVideoFrameEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!_videoEndpointAdapters.Remove(endpoint, out var adapter)) return;
        _video.UnregisterSink(adapter);
    }

    public void RouteVideoChannelToEndpoint(Guid channelId, IVideoFrameEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!_videoEndpointAdapters.TryGetValue(endpoint, out var adapter))
            throw new InvalidOperationException("Endpoint is not registered. Call RegisterVideoEndpoint first.");
        _video.SetActiveChannelForSink(adapter, channelId);
    }

    public void UnrouteVideoChannelFromEndpoint(IVideoFrameEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!_videoEndpointAdapters.TryGetValue(endpoint, out var adapter))
            throw new InvalidOperationException("Endpoint is not registered. Call RegisterVideoEndpoint first.");
        _video.SetActiveChannelForSink(adapter, null);
    }

    public void RegisterAudioEndpoint(IAudioBufferEndpoint endpoint, int channels = 0)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (_audioEndpointAdapters.ContainsKey(endpoint)) return;
        var adapter = new AudioEndpointSinkAdapter(endpoint);
        _audioEndpointAdapters[endpoint] = adapter;
        _audio.RegisterSink(adapter, channels);
    }

    public void UnregisterAudioEndpoint(IAudioBufferEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!_audioEndpointAdapters.Remove(endpoint, out var adapter)) return;
        _audio.UnregisterSink(adapter);
    }

    // ── Batch helpers ─────────────────────────────────────────────────────

    public void RouteVideoChannelToSinks(Guid channelId, IReadOnlyList<IVideoSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        for (int i = 0; i < sinks.Count; i++)
            RouteVideoChannelToSink(channelId, sinks[i]);
    }

    public void RouteVideoChannelToEndpoints(Guid channelId, IReadOnlyList<IVideoFrameEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        for (int i = 0; i < endpoints.Count; i++)
            RouteVideoChannelToEndpoint(channelId, endpoints[i]);
    }

    public void RouteAudioChannelToSinks(Guid channelId, IReadOnlyList<(IAudioSink Sink, ChannelRouteMap RouteMap)> sinkRoutes)
    {
        ArgumentNullException.ThrowIfNull(sinkRoutes);
        for (int i = 0; i < sinkRoutes.Count; i++)
            RouteAudioChannelToSink(channelId, sinkRoutes[i].Sink, sinkRoutes[i].RouteMap);
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsAudio)
            _audio.Dispose();
        if (_ownsVideo)
            _video.Dispose();
    }
}
