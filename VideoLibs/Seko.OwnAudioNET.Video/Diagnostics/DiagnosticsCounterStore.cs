using System.Collections.Concurrent;

namespace Seko.OwnAudioNET.Video.Diagnostics;

internal sealed class DiagnosticsCounterStore
{
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);

    public long Increment(string key, long delta = 1)
    {
        return _counters.AddOrUpdate(key, delta, (_, current) => checked(current + delta));
    }

    public long Read(string key)
    {
        return _counters.GetValueOrDefault(key, 0);
    }
}

