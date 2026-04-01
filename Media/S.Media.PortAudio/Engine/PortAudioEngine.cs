using PALib;
using PALib.Runtime;
using PALib.Types.Core;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.PortAudio.Input;
using S.Media.PortAudio.Output;

namespace S.Media.PortAudio.Engine;

public sealed class PortAudioEngine : IAudioEngine
{
    // Validation bounds (6.4)
    private const int MaxSampleRate = 384_000;
    private const int MaxOutputChannelCount = 64;
    private const int MaxFramesPerBuffer = 32_768;

    private readonly Lock _gate = new();
    private readonly List<AudioDeviceInfo> _outputDevices;
    private readonly List<AudioDeviceInfo> _inputDevices;
    private readonly List<AudioHostApiInfo> _hostApis;
    private readonly List<IAudioOutput> _outputs = [];
    private readonly List<IAudioInput> _inputs = [];
    private AudioDeviceInfo? _defaultOutputDevice;
    private AudioDeviceInfo? _defaultInputDevice;
    private bool _nativeInitialized;
    private bool _disposed;

    public PortAudioEngine()
    {
        // (6.6) Phantom devices are flagged as IsFallback = true so callers can distinguish
        // them from real hardware devices discovered after native initialization.
        _outputDevices =
        [
            new AudioDeviceInfo(new AudioDeviceId("default-output"), "Default Output",
                HostApi: "fallback", IsDefaultOutput: true, IsFallback: true),
            new AudioDeviceInfo(new AudioDeviceId("monitor-output"), "Monitor Output",
                HostApi: "fallback", IsFallback: true),
        ];

        _inputDevices =
        [
            new AudioDeviceInfo(new AudioDeviceId("default-input"), "Default Input",
                HostApi: "fallback", IsDefaultInput: true, IsFallback: true),
        ];

        _hostApis =
        [
            new AudioHostApiInfo("fallback", "Fallback", IsDefault: true,
                DeviceCount: _outputDevices.Count + _inputDevices.Count),
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

            // (7.2) Guard against re-initialization without a prior Terminate().
            if (State == AudioEngineState.Initialized || State == AudioEngineState.Running)
            {
                return (int)MediaErrorCode.PortAudioAlreadyInitialized;
            }

            // Lower-bound validation
            if (config.SampleRate <= 0 || config.OutputChannelCount <= 0 || config.FramesPerBuffer <= 0)
            {
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            }

            // Upper-bound validation (6.4)
            if (config.SampleRate > MaxSampleRate ||
                config.OutputChannelCount > MaxOutputChannelCount ||
                config.FramesPerBuffer > MaxFramesPerBuffer)
            {
                return (int)MediaErrorCode.PortAudioInvalidConfig;
            }

            Config = config;
            var (discoveryOk, nativeFailed) = TryInitializeNativeRuntimeAndRefreshDevices();
            if (!discoveryOk)
            {
                // (7.7) Distinguish between a native load failure and a bad config (wrong API name).
                return nativeFailed
                    ? (int)MediaErrorCode.PortAudioInitializeFailed
                    : (int)MediaErrorCode.PortAudioInvalidConfig;
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

    /// <summary>
    /// Stops all active audio outputs and inputs, then transitions the engine to
    /// <see cref="AudioEngineState.Initialized"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>All tracked outputs and inputs are stopped immediately.</b>
    /// Callers holding <see cref="IAudioOutput"/> or <see cref="IAudioInput"/> references will
    /// observe their state change to <c>Stopped</c>. Subsequent <c>PushFrame</c> calls on
    /// stopped outputs return <see cref="MediaErrorCode.PortAudioStreamStartFailed"/>.
    /// <para>
    /// No per-output notification event is fired — callers must poll <c>output.State</c> or
    /// re-create outputs after calling <see cref="Start"/> to resume audio processing.
    /// </para>
    /// </remarks>
    // (7.4) Stop() stops all active outputs in addition to transitioning state.
    public int Stop()
    {
        lock (_gate)
        {
            if (_disposed) return MediaResult.Success;
            if (State == AudioEngineState.Running)
            {
                foreach (var output in _outputs) output.Stop();
                foreach (var input  in _inputs)  input.Stop();
                TransitionTo(AudioEngineState.Initialized);
            }
            return MediaResult.Success;
        }
    }

    public int Terminate()
    {
        lock (_gate)
        {
            if (_disposed) return MediaResult.Success;

            var outputs = _outputs.ToArray();
            _outputs.Clear();
            foreach (var output in outputs) { output.Stop(); output.Dispose(); }

            var inputs = _inputs.ToArray();
            _inputs.Clear();
            foreach (var input in inputs) { input.Stop(); input.Dispose(); }

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
            // (6.6/1.1) Return empty before Initialize so phantom/fallback entries are never
            // exposed to callers who haven't started the engine.  After Initialize() the real
            // (or fallback-with-IsFallback=true) device list is returned.
            if (State == AudioEngineState.Uninitialized)
                return Array.Empty<AudioDeviceInfo>();
            return _outputDevices.ToArray();
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        lock (_gate)
        {
            if (State == AudioEngineState.Uninitialized)
                return Array.Empty<AudioDeviceInfo>();
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

            output = CreateTrackedOutput(device.Value);
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

                output = CreateTrackedOutput(_outputDevices[i]);
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

                output = CreateTrackedOutput(defaultOutput.Value);
                return MediaResult.Success;
            }

            if (deviceIndex < 0 || deviceIndex >= _outputDevices.Count)
            {
                return (int)MediaErrorCode.PortAudioDeviceNotFound;
            }

            output = CreateTrackedOutput(_outputDevices[deviceIndex]);
            return MediaResult.Success;
        }
    }

    // (7.5) Remove a specific output from the engine's tracked list and dispose it.
    public int RemoveOutput(IAudioOutput output)
    {
        lock (_gate)
        {
            if (!_outputs.Remove(output))
            {
                // D.1 — "not in our list" is a bad argument, not a missing device.
                return (int)MediaErrorCode.MediaInvalidArgument;
            }
        }

        output.Dispose();
        return MediaResult.Success;
    }

    // ── Input factory (Issue 5.1) ─────────────────────────────────────────────

    public IReadOnlyList<IAudioInput> Inputs
    {
        get { lock (_gate) { return _inputs.ToArray(); } }
    }

    public int CreateInput(AudioDeviceId deviceId, out IAudioInput? input)
    {
        input = null;
        lock (_gate)
        {
            if (_disposed || (State != AudioEngineState.Initialized && State != AudioEngineState.Running))
                return (int)MediaErrorCode.PortAudioNotInitialized;
            var device = TryFindInputDevice(deviceId);
            if (!device.HasValue) return (int)MediaErrorCode.PortAudioDeviceNotFound;
            input = CreateTrackedInput(device.Value);
            return MediaResult.Success;
        }
    }

    public int CreateInputByName(string deviceName, out IAudioInput? input)
    {
        input = null;
        if (string.IsNullOrWhiteSpace(deviceName)) return (int)MediaErrorCode.MediaInvalidArgument;
        lock (_gate)
        {
            if (_disposed || (State != AudioEngineState.Initialized && State != AudioEngineState.Running))
                return (int)MediaErrorCode.PortAudioNotInitialized;
            for (var i = 0; i < _inputDevices.Count; i++)
            {
                if (!string.Equals(_inputDevices[i].Name, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                input = CreateTrackedInput(_inputDevices[i]);
                return MediaResult.Success;
            }
            return (int)MediaErrorCode.PortAudioDeviceNotFound;
        }
    }

    public int CreateInputByIndex(int deviceIndex, out IAudioInput? input)
    {
        input = null;
        lock (_gate)
        {
            if (_disposed || (State != AudioEngineState.Initialized && State != AudioEngineState.Running))
                return (int)MediaErrorCode.PortAudioNotInitialized;
            if (deviceIndex == -1)
            {
                var def = _defaultInputDevice;
                if (!def.HasValue) return (int)MediaErrorCode.PortAudioDeviceNotFound;
                input = CreateTrackedInput(def.Value);
                return MediaResult.Success;
            }
            if (deviceIndex < 0 || deviceIndex >= _inputDevices.Count)
                return (int)MediaErrorCode.PortAudioDeviceNotFound;
            input = CreateTrackedInput(_inputDevices[deviceIndex]);
            return MediaResult.Success;
        }
    }

    public int RemoveInput(IAudioInput input)
    {
        lock (_gate)
        {
            if (!_inputs.Remove(input)) return (int)MediaErrorCode.MediaInvalidArgument;
        }
        input.Dispose();
        return MediaResult.Success;
    }

    // (7.6) Re-enumerate devices without tearing down active streams.
    public int RefreshDevices()
    {
        lock (_gate)
        {
            if (_disposed || !IsInitialized)
            {
                return (int)MediaErrorCode.PortAudioNotInitialized;
            }

            if (!_nativeInitialized)
            {
                // D.3 — engine IS initialized (fallback mode) but native runtime is unavailable.
                return (int)MediaErrorCode.PortAudioNativeUnavailable;
            }

            return RefreshNativeDevices()
                ? MediaResult.Success
                : (int)MediaErrorCode.PortAudioInvalidConfig;
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

            _disposed = true;
            StateChanged = null;

            // (7.1) Same cleanup as Terminate() but inline — calling Terminate() while holding
            // _gate would be safe (System.Threading.Lock is re-entrant) but more fragile.
            // Clear the list first so per-output onDisposed callbacks find an empty list.
            var dOutputs = _outputs.ToArray();
            _outputs.Clear();
            var dInputs = _inputs.ToArray();
            _inputs.Clear();

            foreach (var output in dOutputs) { output.Stop(); output.Dispose(); }
            foreach (var input  in dInputs)  { input.Stop();  input.Dispose();  }

            if (_nativeInitialized)
            {
                try
                {
                    _ = Native.Pa_Terminate();
                }
                catch
                {
                    // Best-effort — Dispose must not throw.
                }
                finally
                {
                    _nativeInitialized = false;
                }
            }

            if (State != AudioEngineState.Terminated)
            {
                TransitionTo(AudioEngineState.Terminated);
            }
        }
    }

    // ── Logging (Issue 6.8) ───────────────────────────────────────────────────
    private static ILogger? _logger;

    /// <summary>
    /// Configures a shared logger for all <see cref="PortAudioEngine"/>,
    /// <see cref="PortAudioOutput"/>, and <see cref="PortAudioInput"/> instances.
    /// Call once at application startup before creating any engine.
    /// </summary>
    public static void ConfigureLogging(ILogger logger)
    {
        _logger = logger;
    }

    internal static ILogger? Logger => _logger;

    private AudioDeviceInfo? TryFindOutputDevice(AudioDeviceId deviceId)
    {
        for (var i = 0; i < _outputDevices.Count; i++)
        {
            if (_outputDevices[i].Id == deviceId) return _outputDevices[i];
        }
        return null;
    }

    private AudioDeviceInfo? TryFindInputDevice(AudioDeviceId deviceId)
    {
        for (var i = 0; i < _inputDevices.Count; i++)
        {
            if (_inputDevices[i].Id == deviceId) return _inputDevices[i];
        }
        return null;
    }

    // (7.5) Centralised factory that wires up the cleanup callback.
    private PortAudioOutput CreateTrackedOutput(AudioDeviceInfo device)
    {
        var output = new PortAudioOutput(
            device,
            deviceProvider: () => _outputDevices,
            config: Config,
            defaultOutputProvider: () => _defaultOutputDevice,
            onDisposed: o => { lock (_gate) { _outputs.Remove(o); } });
        _outputs.Add(output);
        return output;
    }

    private PortAudioInput CreateTrackedInput(AudioDeviceInfo device)
    {
        var input = new PortAudioInput(
            deviceProvider: () => _inputDevices,
            defaultInputProvider: () => _defaultInputDevice,
            onDisposed: i => { lock (_gate) { _inputs.Remove(i); } },
            initialDevice: device);
        _inputs.Add(input);
        return input;
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

    // (7.7) Returns (ok, nativeFailed) so Initialize() can emit the right error code.
    private (bool ok, bool nativeFailed) TryInitializeNativeRuntimeAndRefreshDevices()
    {
        try
        {
            PortAudioLibraryResolver.Install();
            var init = Native.Pa_Initialize();
            if (init != PaError.paNoError)
            {
                _nativeInitialized = false;
                // D.2 — Pa_Initialize failed even though the DLL loaded. This is always a real
                // error, regardless of PreferredHostApi. Only DllNotFoundException / EntryPoint /
                // TypeInitialization (below) should fall through to the phantom-device fallback.
                return (ok: false, nativeFailed: true);
            }

            _nativeInitialized = true;
            _logger?.LogDebug("PortAudio native runtime initialized; refreshing devices.");
            return (RefreshNativeDevices(), nativeFailed: false);
        }
        catch (DllNotFoundException)
        {
            _nativeInitialized = false;
            _logger?.LogWarning("PortAudio native library not found (DllNotFoundException); falling back to phantom devices.");
            return (string.IsNullOrWhiteSpace(Config.PreferredHostApi), nativeFailed: true);
        }
        catch (EntryPointNotFoundException)
        {
            _nativeInitialized = false;
            _logger?.LogWarning("PortAudio entry point not found; falling back to phantom devices.");
            return (string.IsNullOrWhiteSpace(Config.PreferredHostApi), nativeFailed: true);
        }
        catch (TypeInitializationException)
        {
            _nativeInitialized = false;
            _logger?.LogWarning("PortAudio type initialization failed; falling back to phantom devices.");
            return (string.IsNullOrWhiteSpace(Config.PreferredHostApi), nativeFailed: true);
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
                    discoveredOutputs.Add(new AudioDeviceInfo(id, name,
                        HostApi: host.Info.Id,
                        IsDefaultOutput: isDefaultOutput,
                        MaxInputChannels: 0,
                        MaxOutputChannels: info.Value.maxOutputChannels,
                        DefaultSampleRate: info.Value.defaultSampleRate > 0 ? info.Value.defaultSampleRate : null));
                }

                if (info.Value.maxInputChannels > 0)
                {
                    discoveredInputs.Add(new AudioDeviceInfo(id, name,
                        HostApi: host.Info.Id,
                        IsDefaultInput: isDefaultInput,
                        MaxInputChannels: info.Value.maxInputChannels,
                        MaxOutputChannels: 0,
                        DefaultSampleRate: info.Value.defaultSampleRate > 0 ? info.Value.defaultSampleRate : null));
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

        // (7.3) If PreferredOutputDevice is set, override the default with the preferred device.
        if (Config.PreferredOutputDevice.HasValue)
        {
            var preferred = _outputDevices.FirstOrDefault(d => d.Id == Config.PreferredOutputDevice.Value);
            if (preferred.Id.Value is not null)
            {
                _defaultOutputDevice = preferred;
            }
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
        if (IsPulseAlias(normalized))
        {
            // PulseAudio is a Linux sound server; PortAudio's ALSA backend handles it.
            // On non-Linux platforms, "pulse" is meaningless — fall back to system default.
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            return "alsa";
        }

        return normalized;
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
