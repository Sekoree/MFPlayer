using S.Media.Core.Audio;
using S.Media.Core.Routing;

namespace S.Media.Core.Media.Endpoints;

/// <summary>
/// A software-only endpoint that provides a <see cref="IMediaClock"/> and consumes
/// pushed audio silently. Use when no hardware output is available but a clock source
/// is needed (e.g. NDI-send-only, offline render, tests).
/// Replaces <c>VirtualAudioOutput</c>.
/// </summary>
public sealed class VirtualClockEndpoint : IAudioEndpoint, IClockCapableEndpoint
{
    private readonly StopwatchClock _clock;
    private bool _running;
    private bool _disposed;

    /// <param name="tickCadence">
    /// Clock tick interval. Defaults to 10 ms.
    /// </param>
    public VirtualClockEndpoint(TimeSpan? tickCadence = null)
    {
        _clock = new StopwatchClock(tickCadence ?? TimeSpan.FromMilliseconds(10));
    }

    // ── IMediaEndpoint ───────────────────────────────────────────────────

    public string Name => "VirtualClockEndpoint";
    public bool IsRunning => _running;

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return Task.CompletedTask;
        _clock.Start();
        _running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_running) return Task.CompletedTask;
        _running = false;
        _clock.Stop();
        return Task.CompletedTask;
    }

    // ── IAudioEndpoint ───────────────────────────────────────────────────

    /// <summary>Silently consumes audio. The clock advances via its own stopwatch.</summary>
    public void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format, TimeSpan sourcePts)
    {
        // No-op — virtual endpoint discards audio.
    }

    // ── IClockCapableEndpoint ────────────────────────────────────────────

    public IMediaClock Clock => _clock;

    /// <summary>
    /// Virtual endpoints use <see cref="ClockPriority.Internal"/> so they never
    /// outrank a real hardware clock when both are registered on the same router
    /// (review §4.8 / R11).
    /// </summary>
    public ClockPriority DefaultPriority => ClockPriority.Internal;

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _clock.Dispose();
    }
}
