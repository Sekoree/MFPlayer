using S.Media.Core.Clock;
using S.Media.Core.Errors;

namespace S.Media.NDI.Clock;

/// <summary>
/// An <see cref="IMediaClock"/> that tracks time from incoming NDI timecodes.
/// <para>
/// <b>Integration status:</b> The clock is fully implemented but not yet wired into
/// <c>NDICaptureCoordinator</c> or <c>NDIVideoSource</c>. <see cref="OnAudioFrame"/> and
/// <see cref="ResolveVideoPtsSeconds"/> currently have no call-sites inside <c>S.Media.NDI</c>.
/// Consumers may advance the clock manually by calling <see cref="OnAudioFrame"/> from their
/// own capture loop, or use <see cref="IMediaClock.Seek"/> to set the position directly.
/// </para>
/// </summary>
public sealed class NDIExternalTimelineClock : IMediaClock
{
    private readonly Lock _gate = new();
    private bool _running;
    private double _currentSeconds;

    public double CurrentSeconds
    {
        get
        {
            lock (_gate)
            {
                return _currentSeconds;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public int Start()
    {
        lock (_gate)
        {
            _running = true;
            return MediaResult.Success;
        }
    }

    public int Pause()
    {
        lock (_gate)
        {
            _running = false;
            return MediaResult.Success;
        }
    }

    public int Stop()
    {
        lock (_gate)
        {
            _running = false;
            _currentSeconds = 0;
            return MediaResult.Success;
        }
    }

    public int Seek(double positionSeconds)
    {
        if (double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds) || positionSeconds < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            _currentSeconds = positionSeconds;
            return MediaResult.Success;
        }
    }

    public void OnAudioFrame(long timecode100ns, int frameCount, int sampleRate)
    {
        if (sampleRate <= 0 || frameCount <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _currentSeconds = timecode100ns / 10_000_000.0;
        }
    }

    public double ResolveVideoPtsSeconds(long timestamp100ns, long timecode100ns, double frameDurationSeconds)
    {
        var baseSeconds = timecode100ns != 0
            ? timecode100ns / 10_000_000.0
            : timestamp100ns / 10_000_000.0;

        if (frameDurationSeconds > 0)
        {
            return baseSeconds + frameDurationSeconds;
        }

        return baseSeconds;
    }
}
