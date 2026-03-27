namespace S.Media.MIDI.Config;

public sealed record MIDIReconnectOptions
{
    public MIDIReconnectMode ReconnectMode { get; init; } = MIDIReconnectMode.AutoReconnect;

    public TimeSpan DisconnectGracePeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxReconnectAttempts { get; init; } = 8;

    public TimeSpan ReconnectAttemptDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public MIDIReconnectOptions Normalize()
    {
        return this with
        {
            MaxReconnectAttempts = Math.Max(1, MaxReconnectAttempts),
            DisconnectGracePeriod = DisconnectGracePeriod < TimeSpan.Zero ? TimeSpan.Zero : DisconnectGracePeriod,
            ReconnectTimeout = ReconnectTimeout < TimeSpan.Zero ? TimeSpan.Zero : ReconnectTimeout,
            ReconnectAttemptDelay = ReconnectAttemptDelay < TimeSpan.Zero ? TimeSpan.Zero : ReconnectAttemptDelay,
        };
    }
}
