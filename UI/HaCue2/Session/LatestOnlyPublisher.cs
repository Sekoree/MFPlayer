using System.Diagnostics;

namespace HaCue2.Session;

/// <summary>
/// Serializes hot UI updates, retaining only the newest pending value for each subject.
/// </summary>
/// <remarks>
/// Pointer input can arrive much faster than a session dispatcher or GPU compositor can adopt new
/// geometry. Queueing every sample makes the picture trail the mouse, starves unrelated control work,
/// and keeps rebuilding mapping state after the gesture has ended. This publisher lets one operation
/// finish, rate-limits the next, and replaces any intermediate value with the latest one.
/// </remarks>
internal sealed class LatestOnlyPublisher<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, TValue> _pending = [];
    private readonly Func<TKey, TValue, Task> _publish;
    private readonly Action<Exception>? _failed;
    private readonly TimeSpan _minimumInterval;
    private bool _running;
    private long _lastStarted;

    public LatestOnlyPublisher(
        Func<TKey, TValue, Task> publish,
        TimeSpan minimumInterval,
        Action<Exception>? failed = null)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _minimumInterval = minimumInterval < TimeSpan.Zero ? TimeSpan.Zero : minimumInterval;
        _failed = failed;
    }

    public void Offer(TKey key, TValue value)
    {
        var start = false;
        lock (_gate)
        {
            _pending[key] = value;
            if (!_running)
            {
                _running = true;
                start = true;
            }
        }

        if (start)
            _ = RunAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            var wait = _lastStarted == 0
                ? TimeSpan.Zero
                : _minimumInterval - Stopwatch.GetElapsedTime(_lastStarted);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait).ConfigureAwait(false);

            KeyValuePair<TKey, TValue> item;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    return;
                }

                item = _pending.First();
                _pending.Remove(item.Key);
            }

            _lastStarted = Stopwatch.GetTimestamp();
            try
            {
                await _publish(item.Key, item.Value).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                try
                {
                    _failed?.Invoke(failure);
                }
                catch
                {
                    // A diagnostics callback must not strand the publisher in its running state.
                }
            }
        }
    }
}
