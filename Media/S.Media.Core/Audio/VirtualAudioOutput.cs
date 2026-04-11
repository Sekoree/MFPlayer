using System.Diagnostics;
using S.Media.Core.Clock;
using S.Media.Core.Media;
using S.Media.Core.Mixing;

namespace S.Media.Core.Audio;

/// <summary>
/// A hardware-free <see cref="IAudioOutput"/> that drives a <see cref="StopwatchClock"/> and
/// calls <see cref="IAudioMixer.FillOutputBuffer"/> on a background thread at the requested
/// frame cadence. No audio is written to any hardware device.
///
/// <para>
/// Useful when you want two or more <see cref="IAudioSink"/> instances (e.g. a
/// <c>PortAudioSink</c> and an <c>NDIAvSink</c>) with independent per-sink routing but a
/// shared clock — the <see cref="VirtualAudioOutput"/> acts as the clock master and the sinks
/// receive their own mix buffers via <see cref="AggregateOutput"/>.
/// </para>
///
/// <example>
/// <code>
/// var virtualOut = new VirtualAudioOutput(new AudioFormat(48000, 2), framesPerBuffer: 512);
/// var agg        = new AggregateOutput(virtualOut);
///
/// agg.AddSink(portAudioSink);
/// agg.AddSink(ndiAudioSink);
///
/// agg.Mixer.AddChannel(channelA, ChannelRouteMap.Silence());
/// agg.Mixer.AddChannel(channelB, ChannelRouteMap.Silence());
///
/// // Route A exclusively to the PortAudio sink, B exclusively to the NDI sink.
/// agg.Mixer.RouteTo(channelA.Id, portAudioSink, ChannelRouteMap.Identity(2));
/// agg.Mixer.RouteTo(channelB.Id, ndiAudioSink,  ChannelRouteMap.Identity(2));
///
/// await agg.StartAsync();
/// </code>
/// </example>
/// </summary>
public sealed class VirtualAudioOutput : IAudioOutput
{
    private readonly int    _framesPerBuffer;
    private readonly float[] _silentBuf;

    private AudioMixer?      _mixer;
    private volatile IAudioMixer? _activeMixer;
    private CancellationTokenSource? _cts;
    private Task?            _tickTask;
    private bool             _disposed;
    private bool             _isRunning;

    public AudioFormat  HardwareFormat { get; }
    public IMediaClock  Clock          { get; }
    public bool         IsRunning      => _isRunning;

    public IAudioMixer Mixer => _activeMixer ?? _mixer
        ?? throw new InvalidOperationException("VirtualAudioOutput not initialised.");

    /// <param name="format">Leader audio format (sample rate + channels).</param>
    /// <param name="framesPerBuffer">
    /// Frames per tick. Determines the tick interval and the scratch buffer size.
    /// Default 512.
    /// </param>
    public VirtualAudioOutput(AudioFormat format, int framesPerBuffer = 512)
    {
        HardwareFormat   = format;
        _framesPerBuffer = framesPerBuffer;
        _silentBuf       = new float[framesPerBuffer * format.Channels];
        Clock            = new StopwatchClock(format.SampleRate);
        _mixer           = new AudioMixer(format);
        _activeMixer     = _mixer;
    }

    /// <inheritdoc/>
    /// <remarks>No-op — <see cref="VirtualAudioOutput"/> needs no hardware device.</remarks>
    public void Open(AudioDeviceInfo device, AudioFormat requestedFormat, int framesPerBuffer = 0) { }

    public void OverrideRtMixer(IAudioMixer mixer) => _activeMixer = mixer;

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return Task.CompletedTask;

        var clockImpl = (StopwatchClock)Clock;
        _mixer!.PrepareBuffers(_framesPerBuffer);
        clockImpl.Start();
        _isRunning = true;

        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _tickTask = TickLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return;
        _isRunning = false;
        ((StopwatchClock)Clock).Stop();

        if (_cts != null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_tickTask != null)
            try { await _tickTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        _cts?.Dispose();
        _cts      = null;
        _tickTask = null;
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        var  fmt           = HardwareFormat;
        var  buf           = _silentBuf;
        // Tick-accurate absolute scheduler — same pattern as NDIAudioChannel.CaptureLoop.
        // DateTime.UtcNow has ~15 ms resolution on Windows; Stopwatch has ~100 ns resolution.
        var  sw            = Stopwatch.StartNew();
        long intervalTicks = (long)((double)Stopwatch.Frequency * _framesPerBuffer / fmt.SampleRate);
        long expectedTicks = 0L;

        while (!ct.IsCancellationRequested)
        {
            var mixer = _activeMixer ?? _mixer;
            if (mixer != null)
                mixer.FillOutputBuffer(buf.AsSpan(), _framesPerBuffer, fmt);

            expectedTicks += intervalTicks;
            long nowTicks  = sw.ElapsedTicks;
            long remTicks  = expectedTicks - nowTicks;
            if (remTicks > 0)
            {
                int remMs = (int)(remTicks * 1000L / Stopwatch.Frequency);
                if (remMs > 1)
                    try { await Task.Delay(remMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _mixer?.Dispose();
        _mixer = null;
    }
}

