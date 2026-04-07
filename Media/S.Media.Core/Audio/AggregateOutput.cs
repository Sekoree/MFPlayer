using S.Media.Core.Audio.Routing;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Mixing;

namespace S.Media.Core.Audio;

/// <summary>
/// Wraps a "leader" <see cref="IAudioOutput"/> and fans-out audio to additional
/// <see cref="IAudioSink"/> instances (a second hardware device, an NDI sender, a recorder, etc.).
///
/// <para>
/// Each sink can receive a <b>per-channel, per-sink</b> mix — call
/// <see cref="IAudioMixer.RouteTo"/> on <see cref="Mixer"/> to set explicit routes.
/// Channels with no explicit route for a given sink follow the mixer's
/// <see cref="IAudioMixer.DefaultFallback"/> policy (<see cref="ChannelFallback.Silent"/> by default).
/// </para>
///
/// <para>
/// Sink distribution happens directly inside <see cref="IAudioMixer.FillOutputBuffer"/> on the RT
/// thread, with no extra cross-thread hops.
/// </para>
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// var aggregate = new AggregateOutput(portAudioOutput);
/// aggregate.Open(device, format);
/// aggregate.AddSink(ndiSink);
/// aggregate.Mixer.AddChannel(channel, ChannelRouteMap.Identity(2));
/// aggregate.Mixer.RouteTo(channel.Id, ndiSink, ChannelRouteMap.DownmixToMono(2));
/// await aggregate.StartAsync();
/// </code>
/// </remarks>
public sealed class AggregateOutput : IAudioOutput
{
    private readonly IAudioOutput        _leader;
    private AggregateAudioMixer? _aggregateMixer;

    private volatile IAudioSink[] _sinks    = [];
    private readonly object       _sinkLock = new();

    // ── IAudioOutput / IMediaOutput ───────────────────────────────────────

    public AudioFormat HardwareFormat => _leader.HardwareFormat;

    public IAudioMixer Mixer => _aggregateMixer
        ?? throw new InvalidOperationException("Call Open() first, or pass an already-opened leader.");

    public IMediaClock Clock     => _leader.Clock;
    public bool        IsRunning => _leader.IsRunning;

    public AggregateOutput(IAudioOutput leader)
    {
        ArgumentNullException.ThrowIfNull(leader);
        _leader = leader;

        try
        {
            _aggregateMixer = new AggregateAudioMixer(leader.Mixer, this);
            leader.OverrideRtMixer(_aggregateMixer);
        }
        catch (InvalidOperationException)
        {
            // Leader not yet opened — Open() will complete the wiring.
        }
    }

    // ── Sink management ────────────────────────────────────────────────────

    /// <summary>
    /// Adds a sink to the fan-out list and registers it with the mixer.
    /// May be called before or after <see cref="Open"/>; registration with the mixer
    /// is deferred to <see cref="Open"/> if the mixer is not yet available.
    /// </summary>
    /// <param name="sink">The sink to add.</param>
    /// <param name="channels">
    /// Number of output channels in the sink's mix buffer.
    /// 0 (default) uses the leader's hardware channel count.
    /// </param>
    public void AddSink(IAudioSink sink, int channels = 0)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_sinkLock)
        {
            var old = _sinks;
            var neo = new IAudioSink[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = sink;
            _sinks  = neo;

            // Register with mixer immediately if already available.
            _aggregateMixer?.RegisterSink(sink, channels);
        }
    }

    /// <summary>Removes a sink and unregisters it from the mixer. No-op if not present.</summary>
    public void RemoveSink(IAudioSink sink)
    {
        lock (_sinkLock)
        {
            var old = _sinks;
            int idx = Array.IndexOf(old, sink);
            if (idx < 0) return;

            var neo = new IAudioSink[old.Length - 1];
            for (int i = 0, j = 0; i < old.Length; i++)
                if (i != idx) neo[j++] = old[i];
            _sinks = neo;

            _aggregateMixer?.UnregisterSink(sink);
        }
    }

    /// <summary>Read-only snapshot of currently registered sinks.</summary>
    public IReadOnlyList<IAudioSink> Sinks => _sinks;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0)
    {
        _leader.Open(device, requestedFormat, framesPerBuffer);
        _aggregateMixer = new AggregateAudioMixer(_leader.Mixer, this);
        _leader.OverrideRtMixer(_aggregateMixer);

        // Register any sinks that were added before Open().
        foreach (var sink in _sinks)
            _aggregateMixer.RegisterSink(sink, 0); // 0 → leader channel count
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _leader.StartAsync(ct).ConfigureAwait(false);
        var tasks = _sinks.Select(s => s.StartAsync(ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var tasks = _sinks.Select(s => s.StopAsync(ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        await _leader.StopAsync(ct).ConfigureAwait(false);
    }

    public void OverrideRtMixer(IAudioMixer mixer) => _leader.OverrideRtMixer(mixer);

    public void Dispose()
    {
        _aggregateMixer?.Dispose();
        foreach (var s in _sinks) s.Dispose();
        _leader.Dispose();
    }
}

/// <summary>
/// Internal thin mixer wrapper that overrides <see cref="Output"/> to point to the
/// <see cref="AggregateOutput"/> rather than the leader, and delegates all operations
/// (including the new sink-registration and routing methods) to the inner mixer.
/// Distribution to sinks happens inside the inner <see cref="AudioMixer.FillOutputBuffer"/>.
/// </summary>
internal sealed class AggregateAudioMixer : IAudioMixer
{
    private readonly IAudioMixer     _inner;
    private readonly AggregateOutput _owner;

    public AggregateAudioMixer(IAudioMixer inner, AggregateOutput owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public AudioFormat          LeaderFormat    => _inner.LeaderFormat;
    public float                MasterVolume    { get => _inner.MasterVolume; set => _inner.MasterVolume = value; }
    public int                  ChannelCount    => _inner.ChannelCount;
    public IReadOnlyList<float> PeakLevels      => _inner.PeakLevels;
    public ChannelFallback      DefaultFallback => _inner.DefaultFallback;

    public void AddChannel(IAudioChannel ch, ChannelRouteMap map, IAudioResampler? rs = null)
        => _inner.AddChannel(ch, map, rs);

    public void RemoveChannel(Guid id) => _inner.RemoveChannel(id);

    public void RouteTo(Guid channelId, IAudioSink sink, ChannelRouteMap routeMap)
        => _inner.RouteTo(channelId, sink, routeMap);

    public void UnrouteTo(Guid channelId, IAudioSink sink)
        => _inner.UnrouteTo(channelId, sink);

    public void RegisterSink(IAudioSink sink, int channels = 0)
        => _inner.RegisterSink(sink, channels);

    public void UnregisterSink(IAudioSink sink)
        => _inner.UnregisterSink(sink);

    // Inner mixer now handles all sink distribution — this is a pure pass-through.
    public void FillOutputBuffer(Span<float> dest, int frameCount, AudioFormat outputFormat)
        => _inner.FillOutputBuffer(dest, frameCount, outputFormat);

    public void Dispose() => _inner.Dispose();
}
