using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using PALib;
using PALib.Types.Core;

namespace S.Media.PortAudio;

/// <summary>
/// Enumerates PortAudio host APIs and devices, and manages Pa_Initialize / Pa_Terminate.
/// One instance per application; call <see cref="Initialize"/> before creating any output.
/// <para>
/// <b>§3.28a / P7 — single-instance contract:</b> PortAudio's native
/// <c>Pa_Initialize</c> / <c>Pa_Terminate</c> pair keeps an internal refcount,
/// but this wrapper does <em>not</em> replicate it. Create exactly one
/// <see cref="PortAudioEngine"/> for the lifetime of the process, and let the
/// owning DI container / <c>using</c> scope drive its disposal. Creating two
/// engines and disposing one of them will tear PortAudio down for the other —
/// a latent footgun. If you need a multi-engine story (tests, plugin hosts),
/// wrap access behind a reference-counted façade.
/// </para>
/// </summary>
public sealed class PortAudioEngine : IAudioEngine
{
    private static readonly ILogger Log = PortAudioLogging.GetLogger(nameof(PortAudioEngine));

    private bool _initialized;
    private bool _disposed;

    public bool IsInitialized => _initialized;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;

        Log.LogInformation("Initializing PortAudio");
        var err = Native.Pa_Initialize();
        if (err != PaError.paNoError)
            throw new InvalidOperationException(
                $"Pa_Initialize failed: {Native.Pa_GetErrorText(err)} ({err})");
        _initialized = true;
        Log.LogDebug("PortAudio initialized: deviceCount={DeviceCount}, hostApiCount={HostApiCount}",
            Native.Pa_GetDeviceCount(), Native.Pa_GetHostApiCount());
    }

    public void Terminate()
    {
        if (!_initialized) return;
        Log.LogInformation("Terminating PortAudio");
        Native.Pa_Terminate();
        _initialized = false;
    }

    public IReadOnlyList<Core.Audio.AudioHostApiInfo> GetHostApis()
    {
        EnsureInitialized();
        int count = Native.Pa_GetHostApiCount();
        var result = new List<Core.Audio.AudioHostApiInfo>(count);
        for (int i = 0; i < count; i++)
        {
            var info = Native.Pa_GetHostApiInfo(i);
            if (info == null) continue;
            result.Add(new Core.Audio.AudioHostApiInfo(
                Index:                    i,
                Name:                     info.Value.Name ?? string.Empty,
                Type:                     MapHostApiType(info.Value.type),
                DeviceCount:              info.Value.deviceCount,
                DefaultInputDeviceIndex:  info.Value.defaultInputDevice,
                DefaultOutputDeviceIndex: info.Value.defaultOutputDevice));
        }
        return result;
    }

    public IReadOnlyList<Core.Audio.AudioDeviceInfo> GetDevices()
    {
        EnsureInitialized();
        int count = Native.Pa_GetDeviceCount();
        var result = new List<Core.Audio.AudioDeviceInfo>(count);
        for (int i = 0; i < count; i++)
        {
            var info = Native.Pa_GetDeviceInfo(i);
            if (info == null) continue;
            result.Add(new Core.Audio.AudioDeviceInfo(
                Index:                   i,
                Name:                    info.Value.Name ?? string.Empty,
                HostApiIndex:            info.Value.hostApi,
                MaxInputChannels:        info.Value.maxInputChannels,
                MaxOutputChannels:       info.Value.maxOutputChannels,
                DefaultSampleRate:       info.Value.defaultSampleRate,
                DefaultLowOutputLatency: info.Value.defaultLowOutputLatency,
                DefaultHighOutputLatency:info.Value.defaultHighOutputLatency));
        }
        return result;
    }

    public Core.Audio.AudioDeviceInfo? GetDefaultOutputDevice()
    {
        EnsureInitialized();
        int idx = Native.Pa_GetDefaultOutputDevice();
        if (idx < 0) return null;
        return GetDeviceByIndex(idx);
    }

    public Core.Audio.AudioDeviceInfo? GetDefaultInputDevice()
    {
        EnsureInitialized();
        int idx = Native.Pa_GetDefaultInputDevice();
        if (idx < 0) return null;
        return GetDeviceByIndex(idx);
    }

    private Core.Audio.AudioDeviceInfo? GetDeviceByIndex(int idx)
    {
        var info = Native.Pa_GetDeviceInfo(idx);
        if (info == null) return null;
        return new Core.Audio.AudioDeviceInfo(
            Index:                   idx,
            Name:                    info.Value.Name ?? string.Empty,
            HostApiIndex:            info.Value.hostApi,
            MaxInputChannels:        info.Value.maxInputChannels,
            MaxOutputChannels:       info.Value.maxOutputChannels,
            DefaultSampleRate:       info.Value.defaultSampleRate,
            DefaultLowOutputLatency: info.Value.defaultLowOutputLatency,
            DefaultHighOutputLatency:info.Value.defaultHighOutputLatency);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Call Initialize() first.");
    }

    private static Core.Audio.HostApiType MapHostApiType(PaHostApiTypeId id) => id switch
    {
        PaHostApiTypeId.paDirectSound  => Core.Audio.HostApiType.DirectSound,
        PaHostApiTypeId.paMME          => Core.Audio.HostApiType.Mme,
        PaHostApiTypeId.paASIO         => Core.Audio.HostApiType.Asio,
        PaHostApiTypeId.paCoreAudio    => Core.Audio.HostApiType.CoreAudio,
        PaHostApiTypeId.paOSS          => Core.Audio.HostApiType.Oss,
        PaHostApiTypeId.paALSA         => Core.Audio.HostApiType.Alsa,
        PaHostApiTypeId.paJACK         => Core.Audio.HostApiType.Jack,
        PaHostApiTypeId.paWASAPI       => Core.Audio.HostApiType.Wasapi,
        _                              => Core.Audio.HostApiType.Unknown
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.LogInformation("Disposing PortAudioEngine");
        Terminate();
        Log.LogDebug("PortAudioEngine disposed");
    }
}

