using PALib;
using PALib.Runtime;
using PALib.Types.Core;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.PortAudio.Output;

namespace S.Media.PortAudio.Engine;

public sealed class PortAudioEngine : IAudioEngine
{
    private readonly Lock _gate = new();
    private readonly List<AudioDeviceInfo> _outputDevices;
    private readonly List<AudioDeviceInfo> _inputDevices;
    private readonly List<IAudioOutput> _outputs = [];
    private bool _nativeInitialized;
    private bool _disposed;

    public PortAudioEngine()
    {
        _outputDevices =
        [
            new AudioDeviceInfo(new AudioDeviceId("default-output"), "Default Output"),
            new AudioDeviceInfo(new AudioDeviceId("monitor-output"), "Monitor Output"),
        ];

        _inputDevices =
        [
            new AudioDeviceInfo(new AudioDeviceId("default-input"), "Default Input"),
        ];

        Config = new AudioEngineConfig();
    }

    public AudioEngineState State { get; private set; } = AudioEngineState.Uninitialized;

    public bool IsInitialized => State is AudioEngineState.Initialized or AudioEngineState.Running;

    public AudioEngineConfig Config { get; private set; }

    public IReadOnlyList<IAudioOutput> Outputs
    {
        get
        {
            lock (_gate)
            {
                return _outputs.ToArray();
            }
        }
    }

    public event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;

    public int Initialize(AudioEngineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.PortAudioInitializeFailed;
            }

            if (config.SampleRate <= 0 || config.OutputChannelCount <= 0 || config.FramesPerBuffer <= 0)
            {
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            }

            Config = config;
            TryInitializeNativeRuntimeAndRefreshDevices();
            TransitionTo(AudioEngineState.Initialized);
            return MediaResult.Success;
        }
    }

    public int Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.PortAudioNotInitialized;
            }

            if (State == AudioEngineState.Uninitialized || State == AudioEngineState.Terminated)
            {
                return (int)MediaErrorCode.PortAudioNotInitialized;
            }

            TransitionTo(AudioEngineState.Running);
            return MediaResult.Success;
        }
    }

    public int Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            if (State == AudioEngineState.Running)
            {
                TransitionTo(AudioEngineState.Initialized);
            }

            return MediaResult.Success;
        }
    }

    public int Terminate()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return MediaResult.Success;
            }

            foreach (var output in _outputs)
            {
                output.Stop();
            }

            _outputs.Clear();
            if (_nativeInitialized)
            {
                try
                {
                    var terminate = Native.Pa_Terminate();
                    if (terminate != PaError.paNoError && terminate != PaError.paNotInitialized)
                    {
                        return (int)MediaErrorCode.PortAudioTerminateFailed;
                    }
                }
                catch (DllNotFoundException)
                {
                    // Native runtime may disappear between init/terminate during environment changes.
                }
                catch (EntryPointNotFoundException)
                {
                }
                catch (TypeInitializationException)
                {
                }
                finally
                {
                    _nativeInitialized = false;
                }
            }

            TransitionTo(AudioEngineState.Terminated);
            return MediaResult.Success;
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => _outputDevices;

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => _inputDevices;

    public int CreateOutput(AudioDeviceId deviceId, out IAudioOutput? output)
    {
        output = null;

        lock (_gate)
        {
            if (_disposed || (State != AudioEngineState.Initialized && State != AudioEngineState.Running))
            {
                return (int)MediaErrorCode.PortAudioNotInitialized;
            }

            var device = TryFindOutputDevice(deviceId);
            if (!device.HasValue)
            {
                return (int)MediaErrorCode.PortAudioDeviceNotFound;
            }

            output = new PortAudioOutput(device.Value, () => _outputDevices);
            _outputs.Add(output);
            return MediaResult.Success;
        }
    }

    public int CreateOutputByName(string deviceName, out IAudioOutput? output)
    {
        output = null;

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return (int)MediaErrorCode.MediaInvalidArgument;
        }

        lock (_gate)
        {
            if (_disposed || (State != AudioEngineState.Initialized && State != AudioEngineState.Running))
            {
                return (int)MediaErrorCode.PortAudioNotInitialized;
            }

            for (var i = 0; i < _outputDevices.Count; i++)
            {
                if (!string.Equals(_outputDevices[i].Name, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                output = new PortAudioOutput(_outputDevices[i], () => _outputDevices);
                _outputs.Add(output);
                return MediaResult.Success;
            }

            return (int)MediaErrorCode.PortAudioDeviceNotFound;
        }
    }

    public int CreateOutputByIndex(int deviceIndex, out IAudioOutput? output)
    {
        output = null;

        lock (_gate)
        {
            if (_disposed || (State != AudioEngineState.Initialized && State != AudioEngineState.Running))
            {
                return (int)MediaErrorCode.PortAudioNotInitialized;
            }

            if (deviceIndex < 0 || deviceIndex >= _outputDevices.Count)
            {
                return (int)MediaErrorCode.PortAudioDeviceNotFound;
            }

            output = new PortAudioOutput(_outputDevices[deviceIndex], () => _outputDevices);
            _outputs.Add(output);
            return MediaResult.Success;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (State == AudioEngineState.Running)
            {
                TransitionTo(AudioEngineState.Initialized);
            }

            foreach (var output in _outputs)
            {
                output.Stop();
                output.Dispose();
            }

            _outputs.Clear();
            TransitionTo(AudioEngineState.Terminated);
            _disposed = true;
            StateChanged = null;
        }
    }

    private AudioDeviceInfo? TryFindOutputDevice(AudioDeviceId deviceId)
    {
        for (var i = 0; i < _outputDevices.Count; i++)
        {
            if (_outputDevices[i].Id == deviceId)
            {
                return _outputDevices[i];
            }
        }

        return null;
    }

    private void TransitionTo(AudioEngineState next)
    {
        if (State == next)
        {
            return;
        }

        var previous = State;
        State = next;
        StateChanged?.Invoke(this, new AudioEngineStateChangedEventArgs(previous, next));
    }

    private void TryInitializeNativeRuntimeAndRefreshDevices()
    {
        try
        {
            PortAudioLibraryResolver.Install();
            var init = Native.Pa_Initialize();
            if (init != PaError.paNoError)
            {
                _nativeInitialized = false;
                return;
            }

            _nativeInitialized = true;
            RefreshNativeDevices();
        }
        catch (DllNotFoundException)
        {
            _nativeInitialized = false;
        }
        catch (EntryPointNotFoundException)
        {
            _nativeInitialized = false;
        }
        catch (TypeInitializationException)
        {
            _nativeInitialized = false;
        }
    }

    private void RefreshNativeDevices()
    {
        var deviceCount = Native.Pa_GetDeviceCount();
        if (deviceCount <= 0)
        {
            return;
        }

        var discoveredOutputs = new List<AudioDeviceInfo>();
        var discoveredInputs = new List<AudioDeviceInfo>();

        for (var i = 0; i < deviceCount; i++)
        {
            var info = Native.Pa_GetDeviceInfo(i);
            if (!info.HasValue)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(info.Value.Name)
                ? $"PortAudio Device {i}"
                : info.Value.Name!;
            var id = new AudioDeviceId($"pa:{i}");

            if (info.Value.maxOutputChannels > 0)
            {
                discoveredOutputs.Add(new AudioDeviceInfo(id, name));
            }

            if (info.Value.maxInputChannels > 0)
            {
                discoveredInputs.Add(new AudioDeviceInfo(id, name));
            }
        }

        if (discoveredOutputs.Count > 0)
        {
            _outputDevices.Clear();
            _outputDevices.AddRange(discoveredOutputs);
        }

        if (discoveredInputs.Count > 0)
        {
            _inputDevices.Clear();
            _inputDevices.AddRange(discoveredInputs);
        }
    }
}

