using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SDL = global::SDL3.SDL;

namespace S.Media.SDL3;

/// <summary>
/// §3.39 / §9.1 — single process-wide SDL event pump.
///
/// SDL event queues are process-global; if each window thread polls independently,
/// one window can consume another window's events. This dispatcher owns the single
/// poll loop and routes events to per-window queues by SDL window id.
/// </summary>
internal static class SDL3ProcessEventPump
{
    private static readonly Lock Gate = new();
    private static readonly ILogger Log = SDL3VideoLogging.GetLogger(nameof(SDL3ProcessEventPump));
    private static readonly Dictionary<uint, WindowQueue> Queues = [];

    private static Thread? _thread;
    private static CancellationTokenSource? _cts;

    internal sealed class Subscription : IDisposable
    {
        private readonly uint _windowId;
        private readonly WindowQueue _queue;
        private int _disposed;

        internal Subscription(uint windowId, WindowQueue queue)
        {
            _windowId = windowId;
            _queue = queue;
        }

        public bool TryDequeue(out SDL.Event evt)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                evt = default;
                return false;
            }

            return _queue.Events.TryDequeue(out evt);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            UnregisterWindow(_windowId);
        }
    }

    internal sealed class WindowQueue
    {
        public readonly ConcurrentQueue<SDL.Event> Events = new();
        public int SubscriberCount;
    }

    public static Subscription RegisterWindow(nint window)
    {
        if (window == nint.Zero)
            throw new InvalidOperationException("SDL window handle is invalid.");

        uint windowId = SDL.GetWindowID(window);
        if (windowId == 0)
            throw new InvalidOperationException($"SDL_GetWindowID failed: {SDL.GetError()}");

        lock (Gate)
        {
            if (!Queues.TryGetValue(windowId, out var queue))
            {
                queue = new WindowQueue();
                Queues[windowId] = queue;
            }

            queue.SubscriberCount++;
            EnsurePumpStartedLocked();
            return new Subscription(windowId, queue);
        }
    }

    private static void UnregisterWindow(uint windowId)
    {
        CancellationTokenSource? ctsToDispose = null;
        Thread? threadToJoin = null;

        lock (Gate)
        {
            if (!Queues.TryGetValue(windowId, out var queue))
                return;

            queue.SubscriberCount--;
            if (queue.SubscriberCount <= 0)
                Queues.Remove(windowId);

            if (Queues.Count == 0 && _thread != null)
            {
                ctsToDispose = _cts;
                threadToJoin = _thread;
                _cts = null;
                _thread = null;
            }
        }

        if (ctsToDispose != null)
        {
            ctsToDispose.Cancel();
            if (threadToJoin != null && threadToJoin != Thread.CurrentThread)
                threadToJoin.Join(TimeSpan.FromSeconds(1));
            ctsToDispose.Dispose();
        }
    }

    private static void EnsurePumpStartedLocked()
    {
        if (_thread != null)
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(PumpLoop)
        {
            Name = "SDL3.ProcessEventPump",
            IsBackground = true
        };
        _thread.Start(token);
    }

    private static void PumpLoop(object? state)
    {
        var token = (CancellationToken)state!;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (SDL.WaitEventTimeout(out var evt, 16))
                {
                    Dispatch(evt);

                    while (SDL.PollEvent(out evt))
                        Dispatch(evt);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "SDL3 process event pump terminated due to exception");
        }
    }

    private static void Dispatch(SDL.Event evt)
    {
        var eventType = (SDL.EventType)evt.Type;

        if (eventType == SDL.EventType.Quit)
        {
            WindowQueue[] queues;
            lock (Gate)
                queues = [.. Queues.Values];

            for (int i = 0; i < queues.Length; i++)
                queues[i].Events.Enqueue(evt);

            return;
        }

        nint window = SDL.GetWindowFromEvent(in evt);
        if (window == nint.Zero)
            return;

        uint windowId = SDL.GetWindowID(window);
        if (windowId == 0)
            return;

        WindowQueue? queue;
        lock (Gate)
            Queues.TryGetValue(windowId, out queue);

        queue?.Events.Enqueue(evt);
    }
}
