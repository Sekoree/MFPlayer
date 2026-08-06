using S.Media.Core.Audio;
using S.Media.Routing;
using S.Media.Time;

namespace S.Media.Session;

/// <summary>
/// The session's program-audio collaborator (HaCue extraction plan, "ShowSession redesign"): owns
/// real outputs, the V×R project patch, device clocks and output health, so the session never
/// acquires devices or realizes wide matrices itself. The session keeps clip transport, fades,
/// envelopes and the lifetime of each voice's input lease; per-voice level writes ride the voice's
/// logical N×V sends on its own clip router - they never rebuild or address real output devices.
/// <para>Hosts without a project patch (HaPlay today) simply pass no target: the session then uses
/// its v1 direct-route adapter (<see cref="ShowClipBinding.AudioRoutes"/> / group outputs) exactly
/// as before. New HaCue documents use <see cref="ShowClipBinding.LogicalSends"/>.</para>
/// </summary>
public interface IShowProgramAudioTarget
{
    /// <summary>The project mix rate the program bus runs at.</summary>
    int SampleRate { get; }

    /// <summary>The project's logical channels, in bus order - stable ids, index = bus channel.</summary>
    IReadOnlyList<string> LogicalChannelIds { get; }

    /// <summary>
    /// The show's authoritative time base - the clock every voice should be timed by, whether or not it
    /// has audio of its own. Null when the target has none, which leaves such voices free-running.
    /// </summary>
    /// <remarks>
    /// A silent video cue (audio routed nowhere) acquires no program input, so nothing gives its player
    /// a clock and it falls back to a Stopwatch - wall time, while every sounding voice is on the audio
    /// device's crystal. The two drift apart without bound. The session passes this to the clip's start
    /// as its master so one show has one time base. Default null keeps custom targets compiling.
    /// </remarks>
    IPlaybackClock? MasterClock => null;

    /// <summary>
    /// Acquires the V-wide program input for one voice. <paramref name="format"/> is what the
    /// voice's clip router will submit (its negotiated rate, <see cref="LogicalChannelIds"/>-count
    /// channels); the target bridges a foreign rate internally or throws a named error. The
    /// returned lease's <see cref="ProgramAudioInputLease.Output"/> is attached to the clip's
    /// router; dispose the lease when the voice releases.
    /// </summary>
    ProgramAudioInputLease AcquireInput(string voiceId, AudioFormat format);

    /// <summary>
    /// Acquires a MONITORING output on <paramref name="endpointId"/> (null = the target's default
    /// monitor endpoint) - the preview/audition seam. Monitoring bypasses the program patch and the
    /// show master trim, and auditioning a patched line never double-opens its device.
    /// </summary>
    ProgramAudioInputLease AcquireMonitorOutput(string? endpointId, AudioFormat format);

    /// <summary>Gets the native width/rate of a monitoring endpoint without acquiring it. The default
    /// implementation preserves compatibility for custom targets; callers then use a conservative fallback.</summary>
    bool TryGetMonitorFormat(string? endpointId, out AudioFormat format)
    {
        format = default;
        return false;
    }
}

/// <summary>A leased program (or monitor) input: <see cref="Output"/> is the sink the voice's clip
/// router submits into - exposed directly (not wrapped) so its clocked/latency surface stays
/// visible and the clip router can pace from it. Dispose to release the lease.</summary>
public sealed class ProgramAudioInputLease : IDisposable
{
    private Action? _release;

    public ProgramAudioInputLease(IAudioOutput output, Action release)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(release);
        Output = output;
        _release = release;
    }

    public IAudioOutput Output { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>
/// The real program-audio target: adapts a BORROWED <see cref="AudioPatchBay"/> (the host owns the
/// bay, its terminals and its lifetime) to <see cref="IShowProgramAudioTarget"/>. Voice inputs are
/// V-wide bay producers with identity sends - the cue's N×V send matrix lives on the voice's own
/// clip router, so fades/envelopes ride there per the plan's gain composition. Monititor outputs
/// map to the bay's monitor inputs (program-patch bypass, no device double-open).
/// <para>Rate bridging: a voice at a foreign clip rate is wrapped through the injected resampler
/// factory (same contract as the bay's - <c>factory(inner, formatTheWrapperAccepts)</c>); without a
/// factory a foreign rate is a named rejection. A wrapped lease hides the producer's clocked
/// surface - the clip router then paces from its wall clock, which is the v1 contract for
/// off-rate clips.</para>
/// </summary>
public sealed class PatchBayShowProgramAudioTarget : IShowProgramAudioTarget
{
    private readonly AudioPatchBay _bay;
    private readonly string[] _logicalChannelIds;
    private readonly Func<IAudioOutput, AudioFormat, IAudioOutput>? _resamplerFactory;
    private readonly string? _defaultMonitorTerminalId;

    /// <param name="bay">The project bay (borrowed - never disposed here).</param>
    /// <param name="logicalChannelIds">Stable logical-channel ids, one per bay bus channel, in bus order.</param>
    /// <param name="resamplerFactory">Wraps a lease to accept a foreign clip rate; null = foreign
    /// rates are rejected with a named error.</param>
    /// <param name="defaultMonitorTerminalId">The bay terminal previews audition on when the caller
    /// names no endpoint; null = a monitor request without an endpoint is a named error.</param>
    public PatchBayShowProgramAudioTarget(
        AudioPatchBay bay,
        IReadOnlyList<string> logicalChannelIds,
        Func<IAudioOutput, AudioFormat, IAudioOutput>? resamplerFactory = null,
        string? defaultMonitorTerminalId = null)
    {
        ArgumentNullException.ThrowIfNull(bay);
        ArgumentNullException.ThrowIfNull(logicalChannelIds);
        if (logicalChannelIds.Count != bay.LogicalChannels)
            throw new ArgumentException(
                $"{logicalChannelIds.Count} logical channel ids for a bay with {bay.LogicalChannels} logical channels",
                nameof(logicalChannelIds));
        _bay = bay;
        _logicalChannelIds = [.. logicalChannelIds];
        _resamplerFactory = resamplerFactory;
        _defaultMonitorTerminalId = defaultMonitorTerminalId;
    }

    public int SampleRate => _bay.MixSampleRate;

    public IReadOnlyList<string> LogicalChannelIds => _logicalChannelIds;

    /// <inheritdoc />
    public IPlaybackClock? MasterClock => _bay.MasterClock;

    public ProgramAudioInputLease AcquireInput(string voiceId, AudioFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(voiceId);
        var channels = _bay.LogicalChannels;
        if (format.Channels != channels)
            throw new ArgumentException(
                $"voice '{voiceId}' requested a {format.Channels}-channel program input but the project has " +
                $"{channels} logical channels", nameof(format));

        // Identity V×V sends: the bus receives exactly what the clip router's N×V matrix produced.
        var sends = new float[channels * channels];
        for (var channel = 0; channel < channels; channel++)
            sends[channel * channels + channel] = 1f;
        var producer = _bay.AcquireProducer(channels, sends, voiceId);
        try
        {
            return Bridge(producer, format, producer.Dispose);
        }
        catch
        {
            producer.Dispose();
            throw;
        }
    }

    public ProgramAudioInputLease AcquireMonitorOutput(string? endpointId, AudioFormat format)
    {
        var terminalId = endpointId ?? _defaultMonitorTerminalId
            ?? throw new InvalidOperationException(
                "no monitor endpoint was named and the program-audio target has no default monitor terminal");
        if (!_bay.TryGetTerminalFormat(terminalId, out var terminalFormat))
            throw new ArgumentException($"unknown monitor endpoint '{terminalId}'", nameof(endpointId));

        // Identity mix over the overlapping channels - a stereo audition on a stereo line is 1:1;
        // extra source or terminal channels stay silent (v1; an authored monitor mix can come later).
        var mix = new float[format.Channels, terminalFormat.Channels];
        for (var channel = 0; channel < Math.Min(format.Channels, terminalFormat.Channels); channel++)
            mix[channel, channel] = 1f;
        var lease = _bay.AcquireMonitorInput(terminalId, format.Channels, mix);
        try
        {
            return Bridge(lease.Input, format, lease.Dispose);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public bool TryGetMonitorFormat(string? endpointId, out AudioFormat format)
    {
        var terminalId = endpointId ?? _defaultMonitorTerminalId;
        if (terminalId is null)
        {
            format = default;
            return false;
        }
        return _bay.TryGetTerminalFormat(terminalId, out format);
    }

    /// <summary>Returns the lease, resampler-wrapping <paramref name="inner"/> when the requested
    /// rate differs from the bay's mix rate. The lease disposes the wrapper it created (if
    /// disposable), then runs <paramref name="release"/>.</summary>
    private ProgramAudioInputLease Bridge(IAudioOutput inner, AudioFormat requested, Action release)
    {
        if (requested.SampleRate == _bay.MixSampleRate)
            return new ProgramAudioInputLease(inner, release);
        if (_resamplerFactory is null)
            throw new InvalidOperationException(
                $"the voice submits at {requested.SampleRate} Hz but the project mixes at {_bay.MixSampleRate} Hz " +
                "and the program-audio target has no resampler factory.");

        var wrapped = _resamplerFactory(inner, requested);
        if (ReferenceEquals(wrapped, inner))
            return new ProgramAudioInputLease(inner, release);
        return new ProgramAudioInputLease(wrapped, () =>
        {
            (wrapped as IDisposable)?.Dispose();
            release();
        });
    }
}
