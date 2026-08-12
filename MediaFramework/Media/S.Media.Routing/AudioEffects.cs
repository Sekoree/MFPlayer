using System.Collections.Concurrent;
using S.Media.Core.Buses;

namespace S.Media.Routing;

/// <summary>
/// Inserts an <see cref="IAudioBusEffect"/> chain in front of any <see cref="IAudioOutput"/> - the
/// output-side audio insert (the audio twin of <see cref="VideoEffectBusOutput"/>). Runs the chain in
/// <see cref="Submit"/> on the router's per-output pump thread; the samples are copied into a scratch
/// buffer so the router's shared chunk is never mutated (other outputs receive the same span).
///
/// <para><strong>Capabilities (review H3):</strong> create via <see cref="Wrap"/>, which picks a subclass
/// forwarding the inner sink's <see cref="IClockedOutput"/> / <see cref="IPlaybackClock"/> /
/// <see cref="IAudioOutputPlaybackStats"/> faces - the same pattern as the metering/resampling wrappers.
/// A plain construction would erase the hardware clock and the router would stop slaving to the device
/// (wall-clock pacing + drift) the moment an effect is inserted.
/// <see cref="S.Media.Core.Audio.IAudioOutputLatency"/> is deliberately NOT in that matrix - it is
/// implemented on this class unconditionally, for the reason set out on
/// <see cref="AudioOutputLatency"/>.</para>
///
/// <para><strong>Ownership (review H4):</strong> the wrapper always owns its EFFECTS;
/// <paramref name="disposeInner"/> says whether disposing the wrapper also disposes the terminal sink
/// (session-owned device) or leaves it alone (borrowed carrier - the host releases it separately).</para>
///
/// <para><strong>Hot swap (review M5):</strong> <see cref="SetEffects"/> never disposes a removed effect
/// directly - the processing thread may still be inside its <c>Process</c>. Removed effects go to a
/// retire queue drained at the TOP of the next <see cref="Submit"/> (the single processing thread, so
/// nothing can be inside them by then) or, if no further submit comes, in <see cref="Dispose"/>.</para>
/// </summary>
public class AudioEffectOutput
    : IAudioOutput, IAudioOutputChannelCapabilities, IFlushableOutput, IAudioOutputLatency, IDisposable
{
    private readonly IAudioOutput _inner;
    private readonly bool _disposeInner;
    private IAudioBusEffect[] _effects;
    private readonly ConcurrentQueue<IAudioBusEffect> _retired = new();
    private float[] _scratch = [];
    private long _samplePosition;
    private bool _disposed;

    protected AudioEffectOutput(IAudioOutput inner, IReadOnlyList<IAudioBusEffect> effects, bool disposeInner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(effects);
        _disposeInner = disposeInner;
        _effects = effects.ToArray();
        foreach (var effect in _effects)
            effect.Configure(inner.Format);
    }

    /// <summary>Wraps <paramref name="inner"/>, preserving its clock/playback/stats capabilities so the
    /// router still recognizes a hardware sink behind the effects. <paramref name="disposeInner"/>: true
    /// when the caller hands terminal ownership to the wrapper (session-owned device), false for a
    /// borrowed carrier the host releases itself.</summary>
    public static AudioEffectOutput Wrap(
        IAudioOutput inner, IReadOnlyList<IAudioBusEffect> effects, bool disposeInner = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (inner is IClockedOutput clocked)
        {
            if (inner is IPlaybackClock playback)
            {
                return inner is IAudioOutputPlaybackStats stats
                    ? new ClockedPlaybackStatsAudioEffectOutput(inner, effects, disposeInner, clocked, playback, stats)
                    : new ClockedPlaybackAudioEffectOutput(inner, effects, disposeInner, clocked, playback);
            }

            return new ClockedAudioEffectOutput(inner, effects, disposeInner, clocked);
        }

        return new AudioEffectOutput(inner, effects, disposeInner);
    }

    public IAudioOutput InnerOutput => _inner;

    public IReadOnlyList<IAudioBusEffect> Effects => Volatile.Read(ref _effects);

    /// <summary>Atomic chain swap. Removed effects retire on the PROCESSING thread's next pass (or in
    /// <see cref="Dispose"/>) - never here, where the pump may still be inside them (review M5).</summary>
    public void SetEffects(IReadOnlyList<IAudioBusEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = effects.ToArray();
        foreach (var effect in next)
            effect.Configure(_inner.Format);
        var previous = Interlocked.Exchange(ref _effects, next);
        foreach (var old in previous)
        {
            if (!next.Contains(old))
                _retired.Enqueue(old);
        }
    }

    private void DrainRetired()
    {
        while (_retired.TryDequeue(out var old))
            MediaDiagnostics.SwallowDisposeErrors(old.Dispose, "AudioEffectOutput: retired effect");
    }

    public AudioFormat Format => _inner.Format;

    public AudioOutputChannelCapabilities ChannelCapabilities =>
        _inner is IAudioOutputChannelCapabilities caps
            ? caps.ChannelCapabilities
            : AudioOutputChannelCapabilities.Fixed(_inner.Format.Channels);

    /// <summary>The inner sink's delay. The effect chain processes in place inside <see cref="Submit"/>
    /// and queues nothing of its own; an effect with genuine look-ahead would need
    /// <see cref="IAudioBusEffect"/> to declare its own latency, which none currently does. Implemented on
    /// the BASE rather than in the capability matrix; see <see cref="AudioOutputLatency"/>.</summary>
    public TimeSpan SubmitToOutputLatency => AudioOutputLatency.Of(_inner);

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        if (_disposed)
            return;
        // This is the single processing thread: anything retired by an earlier swap is guaranteed out
        // of use once we're here, so this is the safe point to dispose it.
        DrainRetired();

        var effects = Volatile.Read(ref _effects);
        if (effects.Length == 0)
        {
            _inner.Submit(packedSamples);
            _samplePosition += packedSamples.Length / Math.Max(1, _inner.Format.Channels);
            return;
        }

        if (_scratch.Length < packedSamples.Length)
            _scratch = new float[packedSamples.Length];
        var chunk = _scratch.AsSpan(0, packedSamples.Length);
        packedSamples.CopyTo(chunk);
        foreach (var effect in effects)
            effect.Process(chunk, _samplePosition);
        _samplePosition += packedSamples.Length / Math.Max(1, _inner.Format.Channels);
        _inner.Submit(chunk);
    }

    public void Flush() => (_inner as IFlushableOutput)?.Flush();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DrainRetired();
        var effects = Interlocked.Exchange(ref _effects, []);
        foreach (var effect in effects)
            MediaDiagnostics.SwallowDisposeErrors(effect.Dispose, "AudioEffectOutput.Dispose: effect");
        if (_disposeInner)
            MediaDiagnostics.SwallowDisposeErrors(
                () => (_inner as IDisposable)?.Dispose(), "AudioEffectOutput.Dispose: inner");
    }

    // --- capability-forwarding subclasses (the MeteringAudioOutput.Wrap pattern) -------------------

    private class ClockedAudioEffectOutput(
        IAudioOutput inner, IReadOnlyList<IAudioBusEffect> effects, bool disposeInner,
        IClockedOutput clocked) : AudioEffectOutput(inner, effects, disposeInner), IClockedOutput
    {
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) =>
            clocked.WaitForCapacity(chunkSamples, token);
    }

    private class ClockedPlaybackAudioEffectOutput(
        IAudioOutput inner, IReadOnlyList<IAudioBusEffect> effects, bool disposeInner,
        IClockedOutput clocked, IPlaybackClock playback)
        : ClockedAudioEffectOutput(inner, effects, disposeInner, clocked), IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => playback.ElapsedSinceStart;

        public bool IsAdvancing => playback.IsAdvancing;

        // Forwarded, not defaulted: the default would report PlaybackEpoch.Single over a device that
        // re-anchors, and every consumer downstream would stop seeing its epoch boundaries.
        public long EpochId => playback.EpochId;

        public ClockReading Read() => playback.Read();
    }

    private sealed class ClockedPlaybackStatsAudioEffectOutput(
        IAudioOutput inner, IReadOnlyList<IAudioBusEffect> effects, bool disposeInner,
        IClockedOutput clocked, IPlaybackClock playback, IAudioOutputPlaybackStats stats)
        : ClockedPlaybackAudioEffectOutput(inner, effects, disposeInner, clocked, playback), IAudioOutputPlaybackStats
    {
        public long PlayedSamples => stats.PlayedSamples;

        public long UnderrunSamples => stats.UnderrunSamples;

        public long DroppedSamples => stats.DroppedSamples;
    }
}

/// <summary>The proof-of-concept audio effect: a click-free smoothed gain (also genuinely useful as a
/// bus trim). Set <see cref="GainDb"/> from any thread; the ramp applies over ~10 ms of samples.
/// Registry config blob: <c>{"gainDb": -6.0}</c>.</summary>
public sealed class GainAudioEffect : IAutomatableAudioBusEffect
{
    public const string GainParameterId = "gainDb";

    private static readonly IReadOnlyList<S.Media.Core.Effects.EffectParameterDescriptor> ParameterCatalog =
    [
        new(GainParameterId, "Gain", -96, 12, 0, "dB",
            S.Media.Core.Effects.EffectParameterScale.Decibels),
    ];

    public IReadOnlyList<S.Media.Core.Effects.EffectParameterDescriptor> Parameters => ParameterCatalog;

    /// <summary>Builds from the registry's opaque config blob (unknown/absent fields = unity).</summary>
    public static GainAudioEffect FromJson(string? configJson)
    {
        var effect = new GainAudioEffect();
        if (string.IsNullOrWhiteSpace(configJson))
            return effect;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("gainDb", out var gain) && gain.TryGetDouble(out var db))
                effect.GainDb = db;
        }
        catch (System.Text.Json.JsonException)
        {
            // opaque blob didn't parse - unity gain beats faulting the line
        }

        return effect;
    }

    private float _targetLinear = 1f;
    private float _currentLinear = 1f;
    private float _rampPerFrame;
    private float _requestedSmoothingSeconds = .01f;
    private int _sampleRate = 48_000;
    private int _channels = 2;

    /// <summary>Gain in dB (0 = unity, -inf via <see cref="float.NegativeInfinity"/> = mute).
    /// NaN / +inf are rejected (kept at the previous target) - a corrupt config must not emit NaN audio.</summary>
    public double GainDb
    {
        get
        {
            var linear = Volatile.Read(ref _targetLinear);
            return linear <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(linear);
        }
        set
        {
            if (double.IsNaN(value) || double.IsPositiveInfinity(value))
                return;
            if (double.IsNegativeInfinity(value))
            {
                Volatile.Write(ref _targetLinear, 0f);
                return;
            }

            var linear = Math.Pow(10.0, value / 20.0);
            // A very large but finite dB value can overflow Pow (or the float conversion) to infinity.
            // Reject it like an explicit +∞ input; non-finite samples must never enter the audio graph.
            if (!double.IsFinite(linear) || linear > float.MaxValue)
                return;
            Volatile.Write(ref _targetLinear, (float)linear);
        }
    }

    public void Configure(AudioFormat format)
    {
        _channels = Math.Max(1, format.Channels);
        _sampleRate = Math.Max(1, format.SampleRate);
        RefreshRamp();
    }

    public bool TrySetParameter(string parameterId, float value, TimeSpan smoothing)
    {
        if (!string.Equals(parameterId, GainParameterId, StringComparison.Ordinal))
            return false;

        var descriptor = ParameterCatalog[0];
        var seconds = double.IsFinite(smoothing.TotalSeconds)
            ? Math.Clamp(smoothing.TotalSeconds, 0, 10)
            : .01;
        Volatile.Write(ref _requestedSmoothingSeconds, (float)seconds);
        RefreshRamp();
        GainDb = descriptor.Clamp(value);
        return true;
    }

    private void RefreshRamp()
    {
        var frames = Math.Max(1, _sampleRate * Volatile.Read(ref _requestedSmoothingSeconds));
        Volatile.Write(ref _rampPerFrame, 1f / frames);
    }

    public void Process(Span<float> interleaved, long samplePosition)
    {
        var target = Volatile.Read(ref _targetLinear);
        var rampPerFrame = Volatile.Read(ref _rampPerFrame);
        var current = _currentLinear;
        if (Math.Abs(current - target) < 1e-6f)
        {
            if (Math.Abs(target - 1f) < 1e-6f)
                return; // unity, settled - nothing to do
            for (var i = 0; i < interleaved.Length; i++)
                interleaved[i] *= target;
            return;
        }

        // Ramp advances once per FRAME with the same gain applied to every channel of that frame -
        // per-float stepping made the ramp CHANNELS× too fast (5 ms on stereo, 0.6 ms on 16ch) and
        // skewed channels within a frame (review M5).
        for (var f = 0; f < interleaved.Length; f += _channels)
        {
            current = current < target
                ? Math.Min(target, current + rampPerFrame)
                : Math.Max(target, current - rampPerFrame);
            var end = Math.Min(f + _channels, interleaved.Length);
            for (var c = f; c < end; c++)
                interleaved[c] *= current;
        }

        _currentLinear = current;
    }

    public void Dispose()
    {
    }
}
