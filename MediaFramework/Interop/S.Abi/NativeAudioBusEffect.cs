using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Buses;
using S.Media.Core.Effects;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpAudioEffectFactoryVTable</c> to the managed bus registry: each factory
/// registers under its `kind` and builds <see cref="IAudioBusEffect"/> instances from the host's opaque
/// per-insert JSON config - indistinguishable from a built-in effect once inserted.
///
/// <para><strong>Real-time boundary:</strong> <c>Process</c> forwards straight through the function
/// pointer with the chunk pinned - no allocation, no marshaling copies. The RT rules the header imposes
/// on the plugin (bounded work, no host reentry) are the same ones <see cref="IAudioBusEffect"/> imposes
/// on managed effects, so nothing extra is needed at this seam. The M5 retire-on-processing-thread
/// contract in the hosts is what makes hot-swapping a NATIVE effect safe: it is never destroyed while a
/// Process could still be executing.</para>
/// </summary>
public sealed unsafe class NativeAudioEffectFactory : IDisposable
{
    private readonly MfpAudioEffectFactoryVTable* _vt;
    private readonly void* _self;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeAudioEffectFactory(nint vtable, nint self, AbiPluginLease lease)
    {
        _vt = (MfpAudioEffectFactoryVTable*)vtable;
        _self = (void*)self;
        _lease = lease;
        try
        {
            Parameters = ReadParameters();
        }
        catch
        {
            // Construction did not hand this lease to a usable adapter. The registered factory itself
            // remains plugin-owned and is destroyed once by AbiLoadedPlugin during eventual unload.
            _lease.Dispose();
            throw;
        }
    }

    /// <summary>The factory-level authoring catalog. Empty for an older/native runtime-only effect.</summary>
    public IReadOnlyList<EffectParameterDescriptor> Parameters { get; }

    /// <summary>The plugin's human-readable name for insertion menus, or empty when it publishes none
    /// (including every pre-extension plugin, whose struct simply ends before this field). Callers then
    /// fall back to the registered kind.</summary>
    public string DisplayName => Utf8(_vt->DisplayName);

    /// <summary>Creates one effect instance (throws when the plugin returns NULL - the registry's
    /// factory contract; the host surfaces the plugin's last-error detail).</summary>
    public IAudioBusEffect Create(string? configJson)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[]? configUtf8 = null;
        if (configJson is not null)
        {
            var len = Encoding.UTF8.GetByteCount(configJson);
            configUtf8 = new byte[len + 1];
            Encoding.UTF8.GetBytes(configJson, configUtf8);
        }

        AbiPluginHost.ClearLastError();
        void* instance;
        fixed (byte* cfg = configUtf8)
            instance = _vt->Create(_self, cfg);
        if (instance == null)
            throw AbiPluginHost.StatusException("audio-effect create", (int)MfpStatus.ErrInternal);

        return new NativeAudioBusEffect(
            _vt->EffectVTable, instance, Parameters, _lease.AcquireDependent());
    }

    private IReadOnlyList<EffectParameterDescriptor> ReadParameters()
    {
        if (_vt->GetParameterCount == null || _vt->GetParameterDescriptor == null)
            return [];
        AbiPluginHost.ClearLastError();
        var count = 0;
        var rc = _vt->GetParameterCount(_self, &count);
        if (rc != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("audio-effect parameter count", rc);
        if (count is < 0 or > 4096)
            throw new InvalidOperationException($"audio-effect factory returned invalid parameter count {count}.");

        var result = new List<EffectParameterDescriptor>(count);
        for (var index = 0; index < count; index++)
        {
            var native = new MfpEffectParameterDescriptor
            {
                AbiVersion = AbiPluginHost.AbiVersion,
                StructSize = (uint)sizeof(MfpEffectParameterDescriptor),
            };
            AbiPluginHost.ClearLastError();
            rc = _vt->GetParameterDescriptor(_self, index, &native);
            if (rc != (int)MfpStatus.Ok)
                throw AbiPluginHost.StatusException($"audio-effect parameter descriptor {index}", rc);
            var id = Utf8(native.Id);
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"audio-effect parameter {index} has an empty id.");
            if (!float.IsFinite(native.Minimum) || !float.IsFinite(native.Maximum)
                || !float.IsFinite(native.DefaultValue) || native.Minimum > native.Maximum
                || native.DefaultValue < native.Minimum || native.DefaultValue > native.Maximum)
                throw new InvalidOperationException($"audio-effect parameter '{id}' has invalid bounds/default.");
            var scale = native.Scale switch
            {
                MfpEffectParameterScale.Decibels => EffectParameterScale.Decibels,
                MfpEffectParameterScale.Percentage => EffectParameterScale.Percentage,
                _ => EffectParameterScale.Linear,
            };
            result.Add(new EffectParameterDescriptor(
                id,
                string.IsNullOrWhiteSpace(Utf8(native.DisplayName)) ? id : Utf8(native.DisplayName),
                native.Minimum,
                native.Maximum,
                native.DefaultValue,
                Utf8(native.Unit),
                scale,
                native.Flags.HasFlag(MfpEffectParameterFlags.Automatable)
                && _vt->EffectVTable->SetParameter != null));
        }

        if (result.GroupBy(parameter => parameter.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("audio-effect factory returned duplicate parameter ids.");
        return result;
    }

    private static string Utf8(byte* value) => value == null
        ? ""
        : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)value) ?? "";

    /// <summary>Releases this adapter's lease. It deliberately does NOT destroy the native factory: the
    /// registered capability is plugin-owned and <c>AbiLoadedPlugin</c> destroys it exactly once during
    /// unload (the rule the constructor's failure path already states). Destroying here as well was a
    /// double-destroy waiting for the first caller to dispose a factory - and, because nothing disposed
    /// them, it also meant a plugin that registered any effect could never be unloaded at all.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lease.Dispose();
    }
}

/// <summary>One created native effect instance behind <see cref="IAudioBusEffect"/>.</summary>
internal sealed unsafe class NativeAudioBusEffect : IAutomatableAudioBusEffect
{
    private readonly MfpAudioEffectVTable* _vt;
    private readonly void* _effect;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeAudioBusEffect(
        MfpAudioEffectVTable* vt,
        void* effect,
        IReadOnlyList<EffectParameterDescriptor> parameters,
        AbiPluginLease lease)
    {
        _vt = vt;
        _effect = effect;
        Parameters = parameters;
        _lease = lease;
        // Resolved ONCE. TrySetParameter is called per automation tick per routed copy, and doing the
        // lookup there meant a LINQ closure plus a string concat plus a fresh byte[] on every write -
        // pure garbage on the control thread, where the managed gain effect allocates nothing at all.
        _writable = parameters
            .Where(parameter => parameter.SupportsAutomation)
            .ToDictionary(
                parameter => parameter.Id,
                parameter => (parameter, Encoding.UTF8.GetBytes(parameter.Id + '\0')),
                StringComparer.Ordinal);
    }

    /// <summary>Automatable parameters by id, with their NUL-terminated UTF-8 names pre-encoded.</summary>
    private readonly Dictionary<string, (EffectParameterDescriptor Descriptor, byte[] Utf8Id)> _writable;

    public IReadOnlyList<EffectParameterDescriptor> Parameters { get; }

    public void Configure(AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->Configure == null)
            return;
        var native = new MfpAudioFormat
        {
            SampleRate = (uint)format.SampleRate,
            Channels = (uint)format.Channels,
            SampleFormat = 0, // f32 interleaved - the only bus format
        };
        AbiPluginHost.ClearLastError();
        var rc = _vt->Configure(_effect, &native);
        if (rc != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("audio-effect configure", rc);
    }

    public void Process(Span<float> interleaved, long samplePosition)
    {
        // RT path: no throw, no allocation - a faulted plugin call surfaces as unprocessed audio, and
        // the plugin's set_last_error is visible in the host log at the next non-RT touchpoint.
        if (_disposed || _vt->Process == null || interleaved.IsEmpty)
            return;
        fixed (float* samples = interleaved)
            _vt->Process(_effect, samples, interleaved.Length, samplePosition);
    }

    public bool TrySetParameter(string parameterId, float value, TimeSpan smoothing)
    {
        if (_disposed || _vt->SetParameter == null || string.IsNullOrWhiteSpace(parameterId))
            return false;
        if (!_writable.TryGetValue(parameterId, out var writable))
            return false;
        var ticks = Math.Max(0, smoothing.Ticks);
        AbiPluginHost.ClearLastError();
        fixed (byte* nativeId = writable.Utf8Id)
            return _vt->SetParameter(_effect, nativeId, writable.Descriptor.Clamp(value), ticks)
                   == (int)MfpStatus.Ok;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_effect);
        _lease.Dispose();
    }
}
