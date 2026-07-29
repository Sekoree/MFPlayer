using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using S.Media.Core.Audio;

namespace HaViz.Core;

/// <summary>
/// Decouples PCM submission to the NDI SDK from the thread that produced the audio. The NDI audio
/// sender clocks itself (<c>clockAudio: true</c>) and BLOCKS its caller to pace the stream, so an
/// inline submit put a receiver-side stall straight onto the decoder/capture thread feeding the
/// engine. Producers now only copy into a ring; this class's own thread does the blocking submit.
/// </summary>
/// <remarks>
/// Single producer (the engine serializes submits under its audio gate) / single consumer (the
/// sender thread). On overflow the OLDEST audio is dropped - once the SDK unblocks, what it should
/// send is the newest PCM, not a backlog the receiver already missed - and the dropped frames are
/// counted for the status line.
/// </remarks>
internal sealed class NdiAudioSubmitRing : IDisposable
{
    /// <summary>Ring depth: enough to ride out SDK/receiver stalls without becoming audible latency.</summary>
    private const int DefaultCapacityMs = 250;
    /// <summary>Bounded idle wake-up so cancellation is observed even if a Set is ever lost.</summary>
    private const int IdleWaitMs = 100;

    private readonly IAudioOutput _sink;
    private readonly int _channels;
    private readonly FrameAlignedFloatRing _ring;
    private readonly float[] _drainScratch;
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly ILogger _log;

    private long _droppedFrames;
    private int _submitFailures;
    private int _disposed;

    /// <param name="capacityFrames">Ring depth in frames; 0 = <see cref="DefaultCapacityMs"/> at the format's rate.</param>
    public NdiAudioSubmitRing(IAudioOutput sink, AudioFormat format, int capacityFrames = 0, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        format.Validate(nameof(format));

        _sink = sink;
        _channels = format.Channels;
        _log = log ?? NullLogger.Instance;
        if (capacityFrames <= 0)
            capacityFrames = Math.Max(1, format.SampleRate * DefaultCapacityMs / 1000);

        _ring = new FrameAlignedFloatRing(_channels, (long)capacityFrames * _channels);
        // One drain batch stays well under the ring depth: a blocked sink then holds at most a
        // fraction of the buffered audio outside the ring, where drop-oldest cannot reach it.
        _drainScratch = new float[Math.Max(_channels, _ring.CapacityFrames / 8 * _channels)];

        _thread = new Thread(() => DrainLoop(_cts.Token)) { IsBackground = true, Name = "HaVizNdiAudio" };
        _thread.Start();
    }

    /// <summary>Frames dropped because the sender could not keep up (drop-oldest overflow).</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>Ring capacity in frames (test/diagnostics).</summary>
    public int CapacityFrames => _ring.CapacityFrames;

    public bool IsSenderAlive => _thread.IsAlive;

    /// <summary>
    /// Copies interleaved PCM into the ring and returns immediately - never blocks on the SDK.
    /// Drops the oldest buffered frames when the chunk does not fit.
    /// </summary>
    public void Submit(ReadOnlySpan<float> interleaved)
    {
        if (Volatile.Read(ref _disposed) != 0 || interleaved.IsEmpty)
            return;
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException(
                $"length {interleaved.Length} is not a multiple of channel count {_channels}", nameof(interleaved));

        var capacity = _ring.CapacityFloats;
        // A chunk larger than the whole ring: keep its newest tail, count the head as dropped.
        if (interleaved.Length > capacity)
        {
            Interlocked.Add(ref _droppedFrames, (interleaved.Length - capacity) / _channels);
            interleaved = interleaved[^capacity..];
        }

        var dropped = _ring.DropOldestKeepingFloats(capacity - interleaved.Length);
        if (dropped > 0)
            Interlocked.Add(ref _droppedFrames, dropped / _channels);

        _ring.Write(interleaved); // room was just made - a short write is impossible
        _dataAvailable.Set();
    }

    /// <summary>
    /// Stops the sender and waits for it to leave the SDK. Returns false when it is still inside a
    /// blocked <c>Submit</c> at the deadline - the caller must then leak the native sender rather
    /// than free it under a live thread.
    /// </summary>
    public bool StopAndJoin(TimeSpan timeout)
    {
        Volatile.Write(ref _disposed, 1);
        _cts.Cancel();
        _dataAvailable.Set();

        var stopped = !_thread.IsAlive || _thread.Join(timeout);
        if (!stopped)
        {
            _log.LogError(
                "HaViz NDI audio sender did not stop within {TimeoutMs} ms (blocked in the SDK); leaving its handles alive",
                (long)timeout.TotalMilliseconds);
            return false;
        }

        // Only safe once the sender is gone: it waits on the event and reads the token.
        _dataAvailable.Dispose();
        _cts.Dispose();
        return true;
    }

    public void Dispose() => StopAndJoin(TimeSpan.FromSeconds(5));

    private void DrainLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var floats = _ring.Read(_drainScratch);
            if (floats <= 0)
            {
                // Reset then re-check: a producer that wrote between the read and the reset must not
                // have its Set erased into a full idle wait.
                _dataAvailable.Reset();
                if (_ring.BufferedFloats > 0)
                    continue;
                try
                {
                    _dataAvailable.Wait(IdleWaitMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return; // raced StopAndJoin's disposal after the token fired
                }

                continue;
            }

            try
            {
                _sink.Submit(_drainScratch.AsSpan(0, floats)); // may block in the SDK audio clock
            }
            catch (Exception ex)
            {
                // A failing sender must not kill the thread: the engine keeps rendering video and the
                // next submit may succeed. Log the first failure and every 100th after it.
                if (Interlocked.Increment(ref _submitFailures) % 100 == 1)
                    _log.LogError(ex, "HaViz NDI audio submit failed ({Failures} total)", Volatile.Read(ref _submitFailures));
            }
        }
    }
}
