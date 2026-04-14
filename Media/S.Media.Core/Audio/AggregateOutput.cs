using Microsoft.Extensions.Logging;
using S.Media.Core.Media;
using S.Media.Core.Mixing;

namespace S.Media.Core.Audio;

/// <summary>
/// Wraps a "leader" <see cref="IAudioOutput"/> and fans-out audio to additional
/// <see cref="IAudioSink"/> instances (a second hardware device, an NDI sender, a recorder, etc.).
///
/// <para>
/// Each sink can receive a <b>per-channel, per-sink</b> mix — call
/// <see cref="IAudioMixer.RouteTo"/> on the mixer managed by <see cref="Mixing.IAVMixer"/>
/// attached to this output to set explicit routes.
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
/// var avMixer = new AVMixer(aggregate.HardwareFormat);
/// avMixer.AttachAudioOutput(aggregate);
/// aggregate.AddSink(ndiSink);
/// avMixer.RegisterAudioSink(ndiSink, aggregate.HardwareFormat.Channels);
/// avMixer.AddAudioChannel(channel, ChannelRouteMap.Identity(2));
/// avMixer.RouteAudioChannelToSink(channel.Id, ndiSink, ChannelRouteMap.DownmixToMono(2));
/// await aggregate.StartAsync();
/// </code>
/// </remarks>
public sealed class AggregateOutput : IAudioOutput
{
    private static readonly ILogger Log = MediaCoreLogging.GetLogger(nameof(AggregateOutput));

    private readonly record struct SinkRegistration(IAudioSink Sink, int Channels);

    private readonly IAudioOutput _leader;
    private AudioMixer? _mixer;

    private volatile SinkRegistration[] _sinkRegistrations = [];
    private volatile IAudioSink[]       _sinks             = [];
    private readonly Lock _sinkLock = new();

    // ── IAudioOutput / IMediaOutput ───────────────────────────────────────

    public AudioFormat HardwareFormat => _leader.HardwareFormat;

    public IAudioMixer Mixer => _mixer
        ?? throw new InvalidOperationException("Call Open() first, or pass an already-opened leader.");

    public IMediaClock Clock     => _leader.Clock;
    public bool        IsRunning => _leader.IsRunning;

    public AggregateOutput(IAudioOutput leader)
    {
        ArgumentNullException.ThrowIfNull(leader);
        _leader = leader;

        Log.LogInformation("Creating AggregateOutput with leader type={LeaderType}", leader.GetType().Name);

        // If the leader is already open (non-zero sample rate), bind our mixer immediately.
        if (leader.HardwareFormat.SampleRate > 0)
            InitMixer(leader.HardwareFormat);
    }

    // Creates the aggregate AudioMixer and injects it into the leader output via
    // OverrideRtMixer.  After this call the leader's RT callback fills THIS mixer
    // (which fans out to sinks) instead of the leader's own internal mixer.
    // Any sinks registered before Open() are retroactively added to the mixer here.
    private void InitMixer(AudioFormat format)
    {
        _mixer = new AudioMixer(format);
        _leader.OverrideRtMixer(_mixer);

        // Register any sinks added before Open().
        foreach (var reg in _sinkRegistrations)
            _mixer.RegisterSink(reg.Sink, reg.Channels);
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
            var old = _sinkRegistrations;
            if (CopyOnWriteArray.IndexOf(old, r => ReferenceEquals(r.Sink, sink)) >= 0)
                return;

            var neo = CopyOnWriteArray.Add(old, new SinkRegistration(sink, channels));
            _sinkRegistrations = neo;

            var sinkSnapshot = new IAudioSink[neo.Length];
            for (int i = 0; i < neo.Length; i++)
                sinkSnapshot[i] = neo[i].Sink;
            _sinks = sinkSnapshot;

            // Register with mixer immediately if already available.
            _mixer?.RegisterSink(sink, channels);
        }

        Log.LogInformation("Aggregate sink added: type={SinkType}, channels={Channels}, total={SinkCount}",
            sink.GetType().Name, channels, _sinkRegistrations.Length);
    }

    /// <summary>Removes a sink and unregisters it from the mixer. No-op if not present.</summary>
    public void RemoveSink(IAudioSink sink)
    {
        lock (_sinkLock)
        {
            var old = _sinkRegistrations;
            int idx = CopyOnWriteArray.IndexOf(old, r => ReferenceEquals(r.Sink, sink));
            if (idx < 0) return;

            var neo = CopyOnWriteArray.RemoveAt(old, idx);
            _sinkRegistrations = neo;

            var sinkSnapshot = new IAudioSink[neo.Length];
            for (int i = 0; i < neo.Length; i++)
                sinkSnapshot[i] = neo[i].Sink;
            _sinks = sinkSnapshot;

            _mixer?.UnregisterSink(sink);
        }
    }

    /// <summary>Read-only snapshot of currently registered sinks.</summary>
    public IReadOnlyList<IAudioSink> Sinks => _sinks;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0)
    {
        _leader.Open(device, requestedFormat, framesPerBuffer);
        InitMixer(_leader.HardwareFormat);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _leader.StartAsync(ct).ConfigureAwait(false);
        var sinks = _sinks;
        for (int i = 0; i < sinks.Length; i++)
            await sinks[i].StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var sinks = _sinks;
        for (int i = 0; i < sinks.Length; i++)
            await sinks[i].StopAsync(ct).ConfigureAwait(false);
        await _leader.StopAsync(ct).ConfigureAwait(false);
    }

    public void OverrideRtMixer(IAudioMixer mixer) => _leader.OverrideRtMixer(mixer);

    public void Dispose()
    {
        Log.LogInformation("Disposing AggregateOutput: sinks={SinkCount}", _sinks.Length);
        // Do NOT dispose sinks — they are externally-owned and may be shared with other aggregates.
        // Callers are responsible for disposing sinks they injected via AddSink().
        _mixer?.Dispose();
        _leader.Dispose();
        Log.LogDebug("AggregateOutput disposed");
    }
}
