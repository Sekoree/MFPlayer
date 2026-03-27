using System.Diagnostics;
using S.Media.Core.Errors;

namespace S.Media.Core.Clock;

public sealed class CoreMediaClock : IMediaClock
{
    private readonly Lock _gate = new();
    private readonly Stopwatch _stopwatch = new();
    private double _baseSeconds;
    private bool _isRunning;

    public double CurrentSeconds
    {
        get
        {
            lock (_gate)
            {
                return _baseSeconds + (_isRunning ? _stopwatch.Elapsed.TotalSeconds : 0d);
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    public int Start()
    {
        lock (_gate)
        {
            if (_isRunning)
            {
                return MediaResult.Success;
            }

            _stopwatch.Restart();
            _isRunning = true;
            return MediaResult.Success;
        }
    }

    public int Pause()
    {
        lock (_gate)
        {
            if (!_isRunning)
            {
                return MediaResult.Success;
            }

            _baseSeconds += _stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Reset();
            _isRunning = false;
            return MediaResult.Success;
        }
    }

    public int Stop()
    {
        lock (_gate)
        {
            _stopwatch.Reset();
            _baseSeconds = 0;
            _isRunning = false;
            return MediaResult.Success;
        }
    }

    public int Seek(double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            _baseSeconds = positionSeconds;

            if (_isRunning)
            {
                _stopwatch.Restart();
            }

            return MediaResult.Success;
        }
    }
}
