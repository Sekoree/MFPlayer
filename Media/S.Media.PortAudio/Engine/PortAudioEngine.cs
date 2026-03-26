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
    private readonly List<AudioHostApiInfo> _hostApis;
    private readonly List<IAudioOutput> _outputs = [];
    private AudioDeviceInfo? _defaultOutputDevice;
    private AudioDeviceInfo? _defaultInputDevice;
    private bool _nativeInitialized;
    private bool _disposed;

    public PortAudioEngine()
    {
        _outputDevices =
        [
            new AudioDeviceInfo(new AudioDeviceId("default-output"), "Default Output", HostApi: "fallback", IsDefaultOutput: true),
            new AudioDeviceInfo(new AudioDeviceId("monitor-output"), "Monitor Output", HostApi: "fallback"),
        ];

        _inputDevices =
        [
            new AudioDeviceInfo(new AudioDeviceId("default-input"), "Default Input", HostApi: "fallback", IsDefaultInput: true),
        ];

        _hostApis =
        [
            new AudioHostApiInfo("fallback", "Fallback", IsDefault: true, DeviceCount: _outputDevices.Count + _inputDevices.Count),
        ];

        _defaultOutputDevice = _outputDevices.FirstOrDefault(device => device.IsDefaultOutput);
        _defaultInputDevice = _inputDevices.FirstOrDefault(device => device.IsDefaultInput);

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
            var discoveryOk = TryInitializeNativeRuntimeAndRefreshDevices();
            if (!discoveryOk)
            {
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            }

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

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        lock (_gate)
        {
            return _outputDevices.ToArray();
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        lock (_gate)
        {
            return _inputDevices.ToArray();
        }
    }

    public IReadOnlyList<AudioHostApiInfo> GetHostApis()
    {
        lock (_gate)
        {
            return _hostApis.ToArray();
        }
    }

    public AudioDeviceInfo? GetDefaultOutputDevice()
    {
        lock (_gate)
        {
            return _defaultOutputDevice;
        }
    }

    public AudioDeviceInfo? GetDefaultInputDevice()
    {
        lock (_gate)
        {
            return _defaultInputDevice;
        }
    }

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

            output = new PortAudioOutput(device.Value, () => _outputDevices, Config, () => _defaultOutputDevice);
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

                output = new PortAudioOutput(_outputDevices[i], () => _outputDevices, Config, () => _defaultOutputDevice);
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

            if (deviceIndex == -1)
            {
                var defaultOutput = _defaultOutputDevice;
                if (!defaultOutput.HasValue)
                {
                    return (int)MediaErrorCode.PortAudioDeviceNotFound;
                }

                output = new PortAudioOutput(defaultOutput.Value, () => _outputDevices, Config, () => _defaultOutputDevice);
                _outputs.Add(output);
                return MediaResult.Success;
            }

            if (deviceIndex < 0 || deviceIndex >= _outputDevices.Count)
            {
                return (int)MediaErrorCode.PortAudioDeviceNotFound;
            }

            output = new PortAudioOutput(_outputDevices[deviceIndex], () => _outputDevices, Config, () => _defaultOutputDevice);
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

    private bool TryInitializeNativeRuntimeAndRefreshDevices()
    {
        try
        {
            PortAudioLibraryResolver.Install();
            var init = Native.Pa_Initialize();
            if (init != PaError.paNoError)
            {
                _nativeInitialized = false;
                return string.IsNullOrWhiteSpace(Config.PreferredHostApi);
            }

            _nativeInitialized = true;
            return RefreshNativeDevices();
        }
        catch (DllNotFoundException)
        {
            _nativeInitialized = false;
            return string.IsNullOrWhiteSpace(Config.PreferredHostApi);
        }
        catch (EntryPointNotFoundException)
        {
            _nativeInitialized = false;
            return string.IsNullOrWhiteSpace(Config.PreferredHostApi);
        }
        catch (TypeInitializationException)
        {
            _nativeInitialized = false;
            return string.IsNullOrWhiteSpace(Config.PreferredHostApi);
        }
    }

    private bool RefreshNativeDevices()
    {
        var preferredHostApi = NormalizePreferredHostApi(Config.PreferredHostApi);
        var preferPulseOutput = IsPulseAlias(Config.PreferredHostApi);
        var deviceCount = Native.Pa_GetDeviceCount();
        var hostApiCount = Native.Pa_GetHostApiCount();
        if (deviceCount <= 0 || hostApiCount <= 0)
        {
            return string.IsNullOrWhiteSpace(preferredHostApi);
        }

        var discoveredHostApis = new List<(int Index, AudioHostApiInfo Info, int DefaultInputLocalIndex, int DefaultOutputLocalIndex)>();
        var defaultHostApiIndex = Native.Pa_GetDefaultHostApi();
        for (var i = 0; i < hostApiCount; i++)
        {
            var apiInfo = Native.Pa_GetHostApiInfo(i);
            if (!apiInfo.HasValue)
            {
                continue;
            }

            var hostApiId = ToHostApiId(apiInfo.Value.type);
            var hostApiName = string.IsNullOrWhiteSpace(apiInfo.Value.Name) ? hostApiId : apiInfo.Value.Name!;
            var host = new AudioHostApiInfo(
                Id: hostApiId,
                Name: hostApiName,
                IsDefault: i == defaultHostApiIndex,
                DeviceCount: Math.Max(0, apiInfo.Value.deviceCount));

            discoveredHostApis.Add((i, host, apiInfo.Value.defaultInputDevice, apiInfo.Value.defaultOutputDevice));
        }

        if (discoveredHostApis.Count == 0)
        {
            return string.IsNullOrWhiteSpace(preferredHostApi);
        }

        var selectedHostApis = string.IsNullOrWhiteSpace(preferredHostApi)
            ? SelectDefaultHostApis(discoveredHostApis, defaultHostApiIndex)
            : discoveredHostApis
                .Where(host =>
                    string.Equals(host.Info.Id, preferredHostApi, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(host.Info.Name, preferredHostApi, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (!string.IsNullOrWhiteSpace(preferredHostApi) && selectedHostApis.Count == 0)
        {
            return false;
        }

        var discoveredOutputs = new List<AudioDeviceInfo>();
        var discoveredInputs = new List<AudioDeviceInfo>();
        var globalDefaultOutput = Native.Pa_GetDefaultOutputDevice();
        var globalDefaultInput = Native.Pa_GetDefaultInputDevice();

        foreach (var host in selectedHostApis)
        {
            for (var localIndex = 0; localIndex < host.Info.DeviceCount; localIndex++)
            {
                var deviceIndex = Native.Pa_HostApiDeviceIndexToDeviceIndex(host.Index, localIndex);
                if (deviceIndex < 0)
                {
                    continue;
                }

                var info = Native.Pa_GetDeviceInfo(deviceIndex);
                if (!info.HasValue)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(info.Value.Name)
                    ? $"PortAudio Device {deviceIndex}"
                    : info.Value.Name!;
                var id = new AudioDeviceId($"pa:{deviceIndex}");
                var isDefaultOutput = deviceIndex == globalDefaultOutput || localIndex == host.DefaultOutputLocalIndex;
                var isDefaultInput = deviceIndex == globalDefaultInput || localIndex == host.DefaultInputLocalIndex;

                if (info.Value.maxOutputChannels > 0)
                {
                    discoveredOutputs.Add(new AudioDeviceInfo(id, name, HostApi: host.Info.Id, IsDefaultOutput: isDefaultOutput));
                }

                if (info.Value.maxInputChannels > 0)
                {
                    discoveredInputs.Add(new AudioDeviceInfo(id, name, HostApi: host.Info.Id, IsDefaultInput: isDefaultInput));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredHostApi) && discoveredOutputs.Count == 0)
        {
            return false;
        }

        PromoteDefaultFirst(discoveredOutputs, static device => device.IsDefaultOutput);
        PromoteDefaultFirst(discoveredInputs, static device => device.IsDefaultInput);

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

        _hostApis.Clear();
        _hostApis.AddRange(selectedHostApis.Select(host => host.Info));

        _defaultOutputDevice = ResolvePreferredDefaultOutput(_outputDevices, preferPulseOutput);
        if (_defaultOutputDevice is null && _outputDevices.Count > 0)
        {
            _defaultOutputDevice = _outputDevices[0];
        }

        _defaultInputDevice = _inputDevices.FirstOrDefault(device => device.IsDefaultInput);
        if (_defaultInputDevice is null && _inputDevices.Count > 0)
        {
            _defaultInputDevice = _inputDevices[0];
        }

        return true;
    }

    private static AudioDeviceInfo? ResolvePreferredDefaultOutput(IReadOnlyList<AudioDeviceInfo> devices, bool preferPulseOutput)
    {
        if (devices.Count == 0)
        {
            return null;
        }

        if (preferPulseOutput)
        {
            for (var i = 0; i < devices.Count; i++)
            {
                var name = devices[i].Name;
                if (name.Contains("pulse", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
                {
                    return devices[i];
                }
            }
        }

        for (var i = 0; i < devices.Count; i++)
        {
            if (devices[i].IsDefaultOutput)
            {
                return devices[i];
            }
        }

        return null;
    }

    private static List<(int Index, AudioHostApiInfo Info, int DefaultInputLocalIndex, int DefaultOutputLocalIndex)> SelectDefaultHostApis(
        List<(int Index, AudioHostApiInfo Info, int DefaultInputLocalIndex, int DefaultOutputLocalIndex)> discoveredHostApis,
        int defaultHostApiIndex)
    {
        var selected = discoveredHostApis
            .Where(host => host.Index == defaultHostApiIndex)
            .ToList();
        if (selected.Count > 0)
        {
            return selected;
        }

        selected = discoveredHostApis
            .Where(host => host.Info.IsDefault)
            .ToList();
        if (selected.Count > 0)
        {
            return selected;
        }

        return [discoveredHostApis[0]];
    }

    private static void PromoteDefaultFirst(List<AudioDeviceInfo> devices, Func<AudioDeviceInfo, bool> isDefault)
    {
        if (devices.Count <= 1)
        {
            return;
        }

        var defaultIndex = -1;
        for (var i = 0; i < devices.Count; i++)
        {
            if (isDefault(devices[i]))
            {
                defaultIndex = i;
                break;
            }
        }

        if (defaultIndex <= 0)
        {
            return;
        }

        var defaultDevice = devices[defaultIndex];
        devices.RemoveAt(defaultIndex);
        devices.Insert(0, defaultDevice);
    }

    private static string? NormalizePreferredHostApi(string? preferredHostApi)
    {
        if (string.IsNullOrWhiteSpace(preferredHostApi))
        {
            return null;
        }

        var normalized = preferredHostApi.Trim();
        return IsPulseAlias(normalized) ? "alsa" : normalized;
    }

    private static bool IsPulseAlias(string? hostApi)
    {
        return !string.IsNullOrWhiteSpace(hostApi) &&
               (string.Equals(hostApi, "pulse", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hostApi, "pulseaudio", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToHostApiId(PaHostApiTypeId type)
    {
        return type switch
        {
            PaHostApiTypeId.paALSA => "alsa",
            PaHostApiTypeId.paJACK => "jack",
            PaHostApiTypeId.paWASAPI => "wasapi",
            PaHostApiTypeId.paMME => "mme",
            PaHostApiTypeId.paDirectSound => "directsound",
            PaHostApiTypeId.paCoreAudio => "coreaudio",
            PaHostApiTypeId.paASIO => "asio",
            PaHostApiTypeId.paWDMKS => "wdmks",
            PaHostApiTypeId.paOSS => "oss",
            _ => type.ToString(),
        };
    }
}

