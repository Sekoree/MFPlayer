using Microsoft.Extensions.Logging;
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
    private static readonly VideoFormat DefaultVideoFormat = new(1, 1, PixelFormat.Bgra32, 30, 1);
    private static readonly AudioFormat DefaultAudioFormat = new(48000, 2);
    private static readonly ILogger Log = MediaCoreLogging.GetLogger(nameof(AVMixer));

    // Tracks endpoint→adapter pairs so we can unregister cleanly.
    private readonly Dictionary<IVideoFrameEndpoint, IVideoSink> _videoEndpointAdapters = new();
    private readonly Dictionary<IAudioBufferEndpoint, IAudioSink> _audioEndpointAdapters = new();

    // Track channels for A/V drift monitoring.
    private readonly Dictionary<Guid, IAudioChannel> _audioChannels = new();
    private readonly Dictionary<Guid, IVideoChannel> _videoChannels = new();

    private readonly IAudioMixer _audio;
    private readonly IVideoMixer _video;
    private readonly bool _ownsAudio;
    private readonly bool _ownsVideo;
    private bool _disposed;

    private readonly int _audioOutputChannels;

    internal AVMixer(IAudioMixer audioMixer, IVideoMixer videoMixer, bool ownsAudio = false, bool ownsVideo = false)
    {
        _audio = audioMixer ?? throw new ArgumentNullException(nameof(audioMixer));
        _video = videoMixer ?? throw new ArgumentNullException(nameof(videoMixer));
        _ownsAudio = ownsAudio;
        _ownsVideo = ownsVideo;
        _audioOutputChannels = audioMixer.LeaderFormat.Channels;
        Log.LogDebug("AVMixer created (ownsAudio={OwnsAudio}, ownsVideo={OwnsVideo})", ownsAudio, ownsVideo);
    }

    public AVMixer(AudioFormat audioFormat, VideoFormat videoFormat, ChannelFallback audioFallback = ChannelFallback.Silent)
        : this(new AudioMixer(audioFormat, audioFallback), new VideoMixer(videoFormat), ownsAudio: true, ownsVideo: true)
    {
        Log.LogInformation("AVMixer created: audio={SampleRate}Hz/{Channels}ch, video={Width}x{Height}@{Fps}fps",
            audioFormat.SampleRate, audioFormat.Channels, videoFormat.Width, videoFormat.Height, videoFormat.FrameRate);
    }

    public AVMixer(AudioFormat audioFormat, ChannelFallback audioFallback = ChannelFallback.Silent)
        : this(audioFormat, DefaultVideoFormat, audioFallback)
    {
    }

    public AVMixer(VideoFormat videoFormat, ChannelFallback audioFallback = ChannelFallback.Silent)
        : this(DefaultAudioFormat, videoFormat, audioFallback)
    {
    }

    public void AttachAudioOutput(IAudioOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.OverrideRtMixer(_audio);
        Log.LogInformation("Audio output attached: {Type}", output.GetType().Name);
    }

    public void AttachVideoOutput(IVideoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.OverridePresentationMixer(_video);
        Log.LogInformation("Video output attached: {Type}", output.GetType().Name);
    }

    // ── Channel management ────────────────────────────────────────────────

    public void AddAudioChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null)
    {
        _audio.AddChannel(channel, routeMap, resampler);
        _audioChannels[channel.Id] = channel;
    }

    /// <inheritdoc/>
    public void AddAudioChannel(IAudioChannel channel, IAudioResampler? resampler = null)
    {
        var routeMap = ChannelRouteMap.Auto(channel.SourceFormat.Channels, _audioOutputChannels);
        AddAudioChannel(channel, routeMap, resampler);
    }

    public void RemoveAudioChannel(Guid channelId)
    {
        _audio.RemoveChannel(channelId);
        _audioChannels.Remove(channelId);
    }

    public void AddVideoChannel(IVideoChannel channel)
    {
        _video.AddChannel(channel);
        _videoChannels[channel.Id] = channel;
    }

    public void RemoveVideoChannel(Guid channelId)
    {
        _video.RemoveChannel(channelId);
        _videoChannels.Remove(channelId);
    }

    // ── Per-channel time offsets ─────────────────────────────────────────

    public void SetAudioChannelTimeOffset(Guid channelId, TimeSpan offset)
        => _audio.SetChannelTimeOffset(channelId, offset);

    public TimeSpan GetAudioChannelTimeOffset(Guid channelId)
        => _audio.GetChannelTimeOffset(channelId);

    public void SetVideoChannelTimeOffset(Guid channelId, TimeSpan offset)
        => _video.SetChannelTimeOffset(channelId, offset);

    public TimeSpan GetVideoChannelTimeOffset(Guid channelId)
        => _video.GetChannelTimeOffset(channelId);

    // ── A/V drift monitoring ─────────────────────────────────────────────

    public TimeSpan GetAvDrift(Guid audioChannelId, Guid videoChannelId)
    {
        if (!_audioChannels.TryGetValue(audioChannelId, out var audioCh))
            throw new InvalidOperationException("Audio channel is not registered.");
        if (!_videoChannels.TryGetValue(videoChannelId, out var videoCh))
            throw new InvalidOperationException("Video channel is not registered.");

        return audioCh.Position - videoCh.Position;
    }

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
        Log.LogInformation("AVMixer disposing");

        if (_ownsAudio)
            _audio.Dispose();
        if (_ownsVideo)
            _video.Dispose();
        Log.LogDebug("AVMixer disposed");
    }
}
