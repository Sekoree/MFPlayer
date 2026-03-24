namespace S.Media.Core.Clock;

public interface IMediaClock
{
    double CurrentSeconds { get; }

    bool IsRunning { get; }

    int Start();

    int Pause();

    int Stop();

    int Seek(double positionSeconds);
}

