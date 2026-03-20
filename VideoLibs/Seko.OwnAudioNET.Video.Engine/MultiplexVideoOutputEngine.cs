using System.Collections.Concurrent;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Fan-out output engine that forwards pushed frames to multiple child engines and direct outputs.
/// </summary>
public sealed class MultiplexVideoOutputEngine : IVideoOutputEngine
{
    private readonly ConcurrentDictionary<Guid, IVideoOutput> _outputs = new();
    private readonly ConcurrentDictionary<Guid, IVideoOutputEngine> _engines = new();
    private readonly Lock _syncLock = new();

    private Guid? _currentOutputId;
    private bool _disposed;

    public MultiplexVideoOutputEngine(VideoEngineConfig? config = null)
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

    public bool AddEngine(IVideoOutputEngine engine)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(engine);

        return _engines.TryAdd(Guid.NewGuid(), engine);
    }

    public bool RemoveEngine(IVideoOutputEngine engine)
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

    public IVideoOutputEngine[] GetEngines()
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

        var delivered = false;

        foreach (var engine in _engines.Values)
        {
            try
            {
                delivered |= engine.PushFrame(frame, masterTimestamp);
            }
            catch
            {
                // Best effort fan-out.
            }
        }

        foreach (var output in _outputs.Values)
        {
            try
            {
                delivered |= output.PushFrame(frame, masterTimestamp);
            }
            catch
            {
                // Best effort fan-out.
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
            throw new ObjectDisposedException(nameof(MultiplexVideoOutputEngine));
    }
}

