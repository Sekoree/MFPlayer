using System.Collections.Concurrent;
using System.Diagnostics;
using Seko.OwnAudioNET.Video;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Output-only engine that accepts pushed frames and forwards them to the selected output sink.
/// </summary>
public class VideoEngine : IVideoOutputEngine
{
    private readonly ConcurrentDictionary<Guid, IVideoOutput> _outputs = new();
    private readonly Lock _syncLock = new();

    private Guid? _currentOutputId;
    private long _nextPushTimestampTicks;
    private bool _disposed;

    public VideoEngine(VideoEngineConfig? config = null)
    {
        Config = (config ?? new VideoEngineConfig()).CloneNormalized();
    }

    public VideoEngineConfig Config { get; }

    public int OutputCount => _outputs.Count;

    public Guid? CurrentOutputId => _currentOutputId;

    public IVideoOutput? CurrentOutput
        => _currentOutputId.HasValue && _outputs.TryGetValue(_currentOutputId.Value, out var output)
            ? output
            : null;

    public bool AddOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);

        if (!_outputs.TryAdd(output.Id, output))
            return false;

        lock (_syncLock)
        {
            if (_currentOutputId == null)
                _currentOutputId = output.Id;
        }

        return true;
    }

    public bool RemoveOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return RemoveOutput(output.Id);
    }

    public bool RemoveOutput(Guid outputId)
    {
        ThrowIfDisposed();

        if (!_outputs.TryRemove(outputId, out var output))
            return false;

        try
        {
            output.DetachSource();
        }
        catch
        {
            // Best effort cleanup.
        }

        lock (_syncLock)
        {
            if (_currentOutputId == outputId)
            {
                _currentOutputId = _outputs.Keys.FirstOrDefault();
                if (_currentOutputId == Guid.Empty)
                    _currentOutputId = null;
            }
        }

        return true;
    }

    public IVideoOutput[] GetOutputs()
    {
        ThrowIfDisposed();
        return _outputs.Values.ToArray();
    }

    public void ClearOutputs()
    {
        ThrowIfDisposed();

        foreach (var output in _outputs.Values.ToArray())
            RemoveOutput(output.Id);
    }

    public bool SetCurrentOutput(IVideoOutput output)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return SetCurrentOutput(output.Id);
    }

    public bool SetCurrentOutput(Guid outputId)
    {
        ThrowIfDisposed();

        if (!_outputs.ContainsKey(outputId))
            return false;

        lock (_syncLock)
            _currentOutputId = outputId;

        return true;
    }

    public void ClearCurrentOutput()
    {
        ThrowIfDisposed();

        lock (_syncLock)
            _currentOutputId = null;
    }

    public bool PushFrame(VideoFrame frame, double masterTimestamp)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);

        if (!SynchronizePushTiming())
            return false;

        if (!IsFrameFormatAccepted(frame.PixelFormat))
            return false;

        IVideoOutput? output;
        lock (_syncLock)
        {
            if (_currentOutputId == null || !_outputs.TryGetValue(_currentOutputId.Value, out output))
                return false;
        }

        try
        {
            return output.PushFrame(frame, masterTimestamp);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var output in _outputs.Values.ToArray())
        {
            try
            {
                output.DetachSource();
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        _outputs.Clear();
        _disposed = true;
    }

    private bool SynchronizePushTiming()
    {
        if (Config.FpsLimit is not > 0)
            return true;

        var nowTicks = Stopwatch.GetTimestamp();
        var nextTicks = Volatile.Read(ref _nextPushTimestampTicks);
        if (nowTicks < nextTicks)
        {
            if (Config.DropRejectedFrames)
                return false;

            var remainingTicks = nextTicks - nowTicks;
            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 0)
                Thread.Sleep(Math.Max(0, (int)Math.Floor(remainingMs)));

            nowTicks = Stopwatch.GetTimestamp();
        }

        var frameIntervalTicks = Math.Max(1L, (long)(Stopwatch.Frequency / Config.FpsLimit.Value));
        var baseTicks = Math.Max(nextTicks, nowTicks);
        Volatile.Write(ref _nextPushTimestampTicks, baseTicks + frameIntervalTicks);
        return true;
    }

    private bool IsFrameFormatAccepted(VideoPixelFormat format)
    {
        if (Config.PixelFormatPolicy == VideoEnginePixelFormatPolicy.Auto)
            return true;

        return format == Config.FixedPixelFormat;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoEngine));
    }
}


