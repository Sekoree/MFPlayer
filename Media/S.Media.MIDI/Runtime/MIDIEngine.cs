using PMLib;
using PMLib.Runtime;
using PMLib.Types;
using S.Media.Core.Errors;
using S.Media.Core.Runtime;
using S.Media.MIDI.Config;
using S.Media.MIDI.Input;
using S.Media.MIDI.Output;
using S.Media.MIDI.Types;

namespace S.Media.MIDI.Runtime;

public sealed class MIDIEngine : IMediaEngine
{
    private readonly Lock _gate = new();
    private readonly List<MIDIInput> _inputs = [];
    private readonly List<MIDIOutput> _outputs = [];
    private readonly List<MIDIDeviceInfo> _inputCatalog = [];
    private readonly List<MIDIDeviceInfo> _outputCatalog = [];
    private MIDIReconnectOptions _defaultReconnectOptions = new();
    private bool _disposed;

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// <see langword="true"/> if the native PortMidi backend was loaded successfully during
    /// <see cref="Initialize"/>. When <see langword="false"/>, the engine falls back to
    /// synthetic (no-op) devices — <see cref="IsInitialized"/> is still <see langword="true"/>
    /// so callers can use the MIDI API without crashing, but no real hardware is available.
    /// </summary>
    public bool NativeBackendAvailable { get; private set; }

    /// <summary>
    /// Initializes the MIDI engine. If the native PortMidi runtime is not available
    /// (DLL missing or CPU unsupported), the engine initializes successfully with
    /// synthetic fallback devices (see <see cref="NativeBackendAvailable"/>).
    /// </summary>
    public int Initialize(MIDIReconnectOptions? reconnectOptions = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (int)MediaErrorCode.MIDIInitializeFailed_V2;
            }

            if (IsInitialized)
            {
                return MediaResult.Success;
            }

            var nativeAvailable = TryInitializeNativeRuntime();
            NativeBackendAvailable = nativeAvailable;

            // A.3: Propagate unified logging to the native PMLib layer.
            PMLibLogging.Configure(MediaLogging.Factory);

            _defaultReconnectOptions = (reconnectOptions ?? new MIDIReconnectOptions()).Normalize();
            RefreshCatalog(nativeAvailable);
            IsInitialized = true;
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

            foreach (var input in _inputs)
            {
                input.Close();
                input.Dispose();
            }

            foreach (var output in _outputs)
            {
                output.Close();
                output.Dispose();
            }

            _inputs.Clear();
            _outputs.Clear();

            if (IsInitialized)
            {
                TryTerminateNativeRuntime();
                IsInitialized = false;
            }

            return MediaResult.Success;
        }
    }

    public IReadOnlyList<MIDIDeviceInfo> GetInputs()
    {
        lock (_gate)
        {
            return _inputCatalog.ToArray();
        }
    }

    public IReadOnlyList<MIDIDeviceInfo> GetOutputs()
    {
        lock (_gate)
        {
            return _outputCatalog.ToArray();
        }
    }

    public MIDIDeviceInfo? GetDefaultInput()
    {
        lock (_gate)
        {
            return _inputCatalog.FirstOrDefault();
        }
    }

    public MIDIDeviceInfo? GetDefaultOutput()
    {
        lock (_gate)
        {
            return _outputCatalog.FirstOrDefault();
        }
    }

    public int CreateInput(MIDIDeviceInfo device, out MIDIInput? input)
    {
        input = null;

        lock (_gate)
        {
            if (_disposed || !IsInitialized)
            {
                return (int)MediaErrorCode.MIDINotInitialized_V2;
            }

            if (!device.IsInput)
            {
                return (int)MediaErrorCode.MIDIInvalidConfig_V2;
            }

            var resolvedDevice = ResolveCatalogDevice(device.DeviceId, _inputCatalog);
            if (!resolvedDevice.HasValue)
            {
                return (int)MediaErrorCode.MIDIDeviceNotFound_V2;
            }

            input = new MIDIInput(resolvedDevice.Value, _defaultReconnectOptions);
            _inputs.Add(input);
            return MediaResult.Success;
        }
    }

    public int CreateOutput(MIDIDeviceInfo device, out MIDIOutput? output)
    {
        output = null;

        lock (_gate)
        {
            if (_disposed || !IsInitialized)
            {
                return (int)MediaErrorCode.MIDINotInitialized_V2;
            }

            if (!device.IsOutput)
            {
                return (int)MediaErrorCode.MIDIInvalidConfig_V2;
            }

            var resolvedDevice = ResolveCatalogDevice(device.DeviceId, _outputCatalog);
            if (!resolvedDevice.HasValue)
            {
                return (int)MediaErrorCode.MIDIDeviceNotFound_V2;
            }

            output = new MIDIOutput(resolvedDevice.Value, _defaultReconnectOptions);
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

            // P2.2: Set _disposed immediately so concurrent callers see it
            // before we release the lock to call Terminate().
            _disposed = true;
        }

        _ = Terminate();
    }

    private static bool TryInitializeNativeRuntime()
    {
        try
        {
            var code = PMUtil.Initialize();
            return code == PmError.NoError;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (TypeInitializationException)
        {
            return false;
        }
    }

    private static void TryTerminateNativeRuntime()
    {
        try
        {
            _ = PMUtil.Terminate();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (TypeInitializationException)
        {
        }
    }

    private void RefreshCatalog(bool nativeAvailable)
    {
        _inputCatalog.Clear();
        _outputCatalog.Clear();

        if (!nativeAvailable)
        {
            _inputCatalog.Add(new MIDIDeviceInfo(-1, "Synthetic MIDI Input", IsInput: true, IsOutput: false, IsNative: false));
            _outputCatalog.Add(new MIDIDeviceInfo(-2, "Synthetic MIDI Output", IsInput: false, IsOutput: true, IsNative: false));
            return;
        }

        foreach (var entry in PMUtil.GetAllDevices())
        {
            var name = string.IsNullOrWhiteSpace(entry.Name)
                ? $"MIDI Device {entry.Id}"
                : entry.Name!;

            var device = new MIDIDeviceInfo(
                DeviceId: entry.Id,
                Name: name,
                IsInput: entry.IsInput,
                IsOutput: entry.IsOutput,
                IsNative: true);

            if (device.IsInput)
            {
                _inputCatalog.Add(device);
            }

            if (device.IsOutput)
            {
                _outputCatalog.Add(device);
            }
        }

        if (_inputCatalog.Count == 0)
        {
            _inputCatalog.Add(new MIDIDeviceInfo(-1, "Synthetic MIDI Input", IsInput: true, IsOutput: false, IsNative: false));
        }

        if (_outputCatalog.Count == 0)
        {
            _outputCatalog.Add(new MIDIDeviceInfo(-2, "Synthetic MIDI Output", IsInput: false, IsOutput: true, IsNative: false));
        }
    }

    private static MIDIDeviceInfo? ResolveCatalogDevice(int deviceId, List<MIDIDeviceInfo> catalog)
    {
        foreach (var candidate in catalog)
        {
            if (candidate.DeviceId == deviceId)
            {
                return candidate;
            }
        }

        return null;
    }
}
