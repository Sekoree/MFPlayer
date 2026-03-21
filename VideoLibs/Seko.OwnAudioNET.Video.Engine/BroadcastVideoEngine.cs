using System.Collections.Concurrent;
using Seko.OwnAudioNET.Video.Events;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Fan-out output engine that forwards pushed frames to multiple child engines and direct outputs.
/// </summary>
public sealed class BroadcastVideoEngine : IVideoEngine, ISupportsOutputSwitching
{
    private readonly ConcurrentDictionary<Guid, IVideoOutput> _outputs = new();
    private readonly ConcurrentDictionary<Guid, IVideoEngine> _engines = new();
    private readonly Lock _syncLock = new();

    private Guid? _currentOutputId;
    private bool _disposed;

    public BroadcastVideoEngine(VideoEngineConfig? config = null)
    {
        Config = (config ?? new VideoEngineConfig()).CloneNormalized();
    }

    public VideoEngineConfig Config { get; }

    public int OutputCount => _outputs.Count;

    public int EngineCount => _engines.Count;

    public Guid? CurrentOutputId => _currentOutputId;

    public IVideoOutput? CurrentOutput
        => _currentOutputId.HasValue && _outputs.TryGetValue(_currentOutputId.Value, out var output)
            ? output
            : null;

    public event EventHandler<VideoErrorEventArgs>? Error;

    public event EventHandler<VideoOutputChangedEventArgs>? VideoOutputChanged;

    public bool AddEngine(IVideoEngine engine)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);

        return _engines.TryAdd(Guid.NewGuid(), engine);
    }

    public bool RemoveEngine(IVideoEngine engine)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);

        foreach (var pair in _engines)
        {
            if (!ReferenceEquals(pair.Value, engine))
                continue;

            return _engines.TryRemove(pair.Key, out _);
        }

        return false;
    }

    public IVideoEngine[] GetEngines()
    {
        ThrowIfDisposed();
        return _engines.Values.ToArray();
    }

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

        if (!_outputs.TryRemove(outputId, out _))
            return false;

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
        _outputs.Clear();
        lock (_syncLock)
            _currentOutputId = null;
    }

    public bool SetVideoOutput(IVideoOutput output, VideoOutputSwitchMode mode = VideoOutputSwitchMode.PauseAndSwitch)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(output);
        return SetVideoOutput(output.Id, mode);
    }

    public bool SetVideoOutput(Guid outputId, VideoOutputSwitchMode mode = VideoOutputSwitchMode.PauseAndSwitch)
    {
        ThrowIfDisposed();

        if (!_outputs.ContainsKey(outputId))
            return false;

        IVideoOutput? oldOutput;
        lock (_syncLock)
        {
            oldOutput = CurrentOutput;
            _currentOutputId = outputId;
        }

        VideoOutputChanged?.Invoke(this, new VideoOutputChangedEventArgs(oldOutput, CurrentOutput));

        return true;
    }

    public bool ClearVideoOutput(VideoOutputSwitchMode mode = VideoOutputSwitchMode.PauseAndSwitch)
    {
        ThrowIfDisposed();

        IVideoOutput? oldOutput;
        lock (_syncLock)
        {
            oldOutput = CurrentOutput;
            _currentOutputId = null;
        }

        VideoOutputChanged?.Invoke(this, new VideoOutputChangedEventArgs(oldOutput, null));
        return true;
    }

    public bool PushFrame(VideoFrame frame, double masterTimestamp)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);

        var delivered = false;

        foreach (var engine in _engines.Values)
        {
            try
            {
                using var perTargetFrame = frame.AddRef();
                delivered |= engine.PushFrame(perTargetFrame, masterTimestamp);
            }
            catch
            {
                // Best effort fan-out.
                Error?.Invoke(this, new VideoErrorEventArgs("Broadcast engine failed to push frame to a child engine."));
            }
        }

        foreach (var output in _outputs.Values)
        {
            try
            {
                using var perTargetFrame = frame.AddRef();
                delivered |= output.PushFrame(perTargetFrame, masterTimestamp);
            }
            catch
            {
                // Best effort fan-out.
                Error?.Invoke(this, new VideoErrorEventArgs("Broadcast engine failed to push frame to an output."));
            }
        }

        return delivered;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _outputs.Clear();
        _engines.Clear();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BroadcastVideoEngine));
    }
}


