using S.Media.Core.Audio.Routing;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Mixing;

namespace S.Media.Core.Audio;

/// <summary>
/// Wraps a "leader" <see cref="IAudioOutput"/> and fans-out the mixed buffer to additional
/// <see cref="IAudioSink"/> instances (e.g. a second hardware device, an NDI sender, a recorder).
///
/// <para>
/// The leader's hardware clock drives all timing.
/// Additional sinks are decoupled via their own ring buffers so the RT callback never blocks.
/// </para>
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// var aggregate = new AggregateOutput(portAudioOutput);
/// aggregate.AddSink(ndiAudioSink);
/// aggregate.Mixer.AddChannel(channel, ChannelRouteMap.Identity(2));
/// await aggregate.StartAsync();
/// </code>
/// </remarks>
public sealed class AggregateOutput : IAudioOutput
{
    private readonly IAudioOutput        _leader;
    private readonly AggregateAudioMixer _aggregateMixer;

    // Snapshot array — replaced atomically on add/remove, never mutated in-place.
    private volatile IAudioSink[] _sinks = [];
    private readonly object       _sinkLock = new();

    // ── IAudioOutput / IMediaOutput ───────────────────────────────────────

    /// <inheritdoc/>
    public AudioFormat HardwareFormat => _leader.HardwareFormat;

    /// <inheritdoc/>
    public IAudioMixer Mixer => _aggregateMixer;

    /// <summary>
    /// Clock from the leader output. All sinks slave to this timeline.
    /// </summary>
    public IMediaClock Clock => _leader.Clock;

    /// <inheritdoc/>
    public bool IsRunning => _leader.IsRunning;

    /// <summary>Initialises the aggregate, delegating hardware concerns to <paramref name="leader"/>.</summary>
    /// <param name="leader">
    /// The output whose RT callback drives timing.
    /// Must be opened (<see cref="IAudioOutput.Open"/>) before calling
    /// <see cref="StartAsync"/>.
    /// </param>
    public AggregateOutput(IAudioOutput leader)
    {
        ArgumentNullException.ThrowIfNull(leader);
        _leader          = leader;
        _aggregateMixer  = new AggregateAudioMixer(leader.Mixer, this);
    }

    // ── Sink management ────────────────────────────���──────────────────────

    /// <summary>Adds a sink that will receive copies of every mixed buffer.</summary>
    public void AddSink(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_sinkLock)
        {
            var old = _sinks;
            var neo = new IAudioSink[old.Length + 1];
            old.CopyTo(neo, 0);
            neo[^1] = sink;
            _sinks  = neo;
        }
    }

    /// <summary>Removes a sink by reference.</summary>
    public void RemoveSink(IAudioSink sink)
    {
        lock (_sinkLock)
        {
            var old = _sinks;
            int idx = Array.IndexOf(old, sink);
            if (idx < 0) return;

            var neo = new IAudioSink[old.Length - 1];
            for (int i = 0, j = 0; i < old.Length; i++)
            {
                if (i != idx) neo[j++] = old[i];
            }
            _sinks = neo;
        }
    }

    /// <summary>Read-only snapshot of currently registered sinks.</summary>
    public IReadOnlyList<IAudioSink> Sinks => _sinks;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the underlying hardware stream.
    /// Call this once before adding channels and starting.
    /// </summary>
    public void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0)
        => _leader.Open(device, requestedFormat, framesPerBuffer);

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _leader.StartAsync(ct).ConfigureAwait(false);

        // Start all sinks in parallel.
        var tasks = _sinks.Select(s => s.StartAsync(ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        // Stop sinks first (they drain their own ring buffers).
        var tasks = _sinks.Select(s => s.StopAsync(ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        await _leader.StopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Called by <see cref="AggregateAudioMixer.FillOutputBuffer"/> after the leader buffer
    /// is filled, to distribute the mixed audio to all registered sinks.
    /// Executes on the RT thread — MUST NOT allocate or block.
    /// </summary>
    internal void DistributeToSinks(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format)
    {
        var sinks = _sinks; // volatile snapshot
        foreach (var sink in sinks)
        {
            if (sink.IsRunning)
                sink.ReceiveBuffer(buffer, frameCount, format);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _aggregateMixer.Dispose();
        foreach (var s in _sinks) s.Dispose();
        _leader.Dispose();
    }
}

/// <summary>
/// Internal mixer that wraps the leader's <see cref="IAudioMixer"/> and additionally
/// calls <see cref="AggregateOutput.DistributeToSinks"/> after each fill.
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

    public IAudioOutput          Output        => _owner;
    public float                 MasterVolume  { get => _inner.MasterVolume; set => _inner.MasterVolume = value; }
    public int                   ChannelCount  => _inner.ChannelCount;
    public IReadOnlyList<float>  PeakLevels    => _inner.PeakLevels;

    public void AddChannel(IAudioChannel channel, ChannelRouteMap routeMap, IAudioResampler? resampler = null)
        => _inner.AddChannel(channel, routeMap, resampler);

    public void RemoveChannel(Guid channelId)
        => _inner.RemoveChannel(channelId);

    public void FillOutputBuffer(Span<float> dest, int frameCount, AudioFormat outputFormat)
    {
        // Fill the leader's PA buffer first.
        _inner.FillOutputBuffer(dest, frameCount, outputFormat);

        // Distribute the same mixed audio to all additional sinks.
        _owner.DistributeToSinks(dest, frameCount, outputFormat);
    }

    public void Dispose() => _inner.Dispose();
}

