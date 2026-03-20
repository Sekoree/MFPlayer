using System.Buffers;
using Ownaudio.Core;
using Ownaudio.Core.Common;

namespace Seko.OwnAudioNET.Video.Engine;

/// <summary>
/// Fan-out <see cref="IAudioEngine"/> wrapper that sends the same mixed buffer to multiple engines.
/// </summary>
public sealed class MultiplexAudioEngine : IAudioEngine
{
    private readonly IAudioEngine[] _engines;
    private readonly IAudioEngine _primary;
    private bool _disposed;

    public MultiplexAudioEngine(IAudioEngine primary, params IAudioEngine[] additionalEngines)
        : this(BuildEngineArray(primary, additionalEngines))
    {
    }

    public MultiplexAudioEngine(params IAudioEngine[] engines)
    {
        if (engines == null || engines.Length == 0)
            throw new ArgumentException("At least one audio engine is required.", nameof(engines));

        if (engines.Any(static e => e == null))
            throw new ArgumentException("Audio engine entries cannot be null.", nameof(engines));

        _engines = engines;
        _primary = _engines[0];
    }

    public int EngineCount => _engines.Length;

    public IntPtr GetStream()
    {
        ThrowIfDisposed();
        return _primary.GetStream();
    }

    public int FramesPerBuffer => _primary.FramesPerBuffer;

    public int OwnAudioEngineActivate()
    {
        ThrowIfDisposed();
        return _primary.OwnAudioEngineActivate();
    }

    public int OwnAudioEngineStopped()
    {
        ThrowIfDisposed();
        return _primary.OwnAudioEngineStopped();
    }

    public int Initialize(AudioConfig config)
    {
        ThrowIfDisposed();

        var result = 0;
        foreach (var engine in _engines)
        {
            var current = engine.Initialize(config);
            if (current < 0)
                result = current;
        }

        return result;
    }

    public int Start()
    {
        ThrowIfDisposed();

        var result = 0;
        foreach (var engine in _engines)
        {
            var current = engine.Start();
            if (current < 0)
                result = current;
        }

        return result;
    }

    public int Stop()
    {
        ThrowIfDisposed();

        var result = 0;
        foreach (var engine in _engines)
        {
            var current = engine.Stop();
            if (current < 0)
                result = current;
        }

        return result;
    }

    public void Send(Span<float> samples)
    {
        ThrowIfDisposed();

        if (_engines.Length == 1)
        {
            _primary.Send(samples);
            return;
        }

        _primary.Send(samples);

        var rented = ArrayPool<float>.Shared.Rent(samples.Length);
        try
        {
            samples.CopyTo(rented);
            var copy = rented.AsSpan(0, samples.Length);

            for (var i = 1; i < _engines.Length; i++)
                _engines[i].Send(copy);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented, clearArray: false);
        }
    }

    public int Receives(out float[] samples)
    {
        ThrowIfDisposed();
        return _primary.Receives(out samples);
    }

    public List<AudioDeviceInfo> GetOutputDevices()
    {
        ThrowIfDisposed();
        return _primary.GetOutputDevices();
    }

    public List<AudioDeviceInfo> GetInputDevices()
    {
        ThrowIfDisposed();
        return _primary.GetInputDevices();
    }

    public int SetOutputDeviceByName(string deviceName)
    {
        ThrowIfDisposed();

        var result = 0;
        foreach (var engine in _engines)
        {
            var current = engine.SetOutputDeviceByName(deviceName);
            if (current < 0)
                result = current;
        }

        return result;
    }

    public int SetOutputDeviceByIndex(int deviceIndex)
    {
        ThrowIfDisposed();

        var result = 0;
        foreach (var engine in _engines)
        {
            var current = engine.SetOutputDeviceByIndex(deviceIndex);
            if (current < 0)
                result = current;
        }

        return result;
    }

    public int SetInputDeviceByName(string deviceName)
    {
        ThrowIfDisposed();
        return _primary.SetInputDeviceByName(deviceName);
    }

    public int SetInputDeviceByIndex(int deviceIndex)
    {
        ThrowIfDisposed();
        return _primary.SetInputDeviceByIndex(deviceIndex);
    }

    public event EventHandler<AudioDeviceChangedEventArgs> OutputDeviceChanged
    {
        add => _primary.OutputDeviceChanged += value;
        remove => _primary.OutputDeviceChanged -= value;
    }

    public event EventHandler<AudioDeviceChangedEventArgs> InputDeviceChanged
    {
        add => _primary.InputDeviceChanged += value;
        remove => _primary.InputDeviceChanged -= value;
    }

    public event EventHandler<AudioDeviceStateChangedEventArgs> DeviceStateChanged
    {
        add => _primary.DeviceStateChanged += value;
        remove => _primary.DeviceStateChanged -= value;
    }

    public void PauseDeviceMonitoring()
    {
        ThrowIfDisposed();
        foreach (var engine in _engines)
            engine.PauseDeviceMonitoring();
    }

    public void ResumeDeviceMonitoring()
    {
        ThrowIfDisposed();
        foreach (var engine in _engines)
            engine.ResumeDeviceMonitoring();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var engine in _engines)
            engine.Dispose();

        _disposed = true;
    }

    private static IAudioEngine[] BuildEngineArray(IAudioEngine primary, IAudioEngine[] additionalEngines)
    {
        ArgumentNullException.ThrowIfNull(primary);
        additionalEngines ??= [];

        var engines = new IAudioEngine[additionalEngines.Length + 1];
        engines[0] = primary;
        Array.Copy(additionalEngines, 0, engines, 1, additionalEngines.Length);
        return engines;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MultiplexAudioEngine));
    }
}

