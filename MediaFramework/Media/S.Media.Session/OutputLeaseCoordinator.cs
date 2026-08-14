using S.Media.Core.Audio;
using S.Media.Core.Buses;
using S.Media.Core.Diagnostics;
using S.Media.Routing;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace S.Media.Session;

// A clip's attached audio output plus its ownership. The session disposes it on clip replace only when
// DisposeOnRelease (a backend-created device it owns); a host lease (e.g. an NDI carrier's audio) is
// BORROWED - never disposed, only its Release hook is invoked so the host can drop its reference.
internal sealed record AudioEffectControl(string InstanceId, IAutomatableAudioBusEffect Effect);
internal readonly record struct ClipAudioOutput(
    IAudioOutput Output,
    bool DisposeOnRelease,
    Action? Release,
    IReadOnlyList<AudioEffectControl>? EffectControls = null);
internal sealed record AudioRouteTarget(string OutputId, float TargetGain, ShowClipAudioRoute? Route = null);


/// <summary>
/// The session's acquired-audio-output lifecycle (review F-11): resolve a route's device to a sink
/// (host lease first, else a backend-owned output), stage the effect inserts, attach with per-route
/// error isolation, and retire per ownership. One owner, because three partials
/// (fire path, hot rebuild, voice teardown) share exactly this lifecycle and each grew its own copy
/// of half of it before the seam existed.
/// </summary>
/// <remarks>
/// Owned by <see cref="ShowSession"/> and driven from its serial dispatcher only - the coordinator
/// adds no locking of its own. Ownership rule (the whole point of <see cref="ClipAudioOutput"/>):
/// a backend-created sink is disposed on release; a host lease is BORROWED - never disposed, only
/// its release hook runs so the host can drop its reference.
/// </remarks>
internal sealed class OutputLeaseCoordinator(
    AudioOutputDeviceCache deviceCache,
    IAudioBackend? audioBackend,
    Func<string, AudioFormat, ClipAudioOutputLease?>? audioOutputFactory,
    IBusRegistry? effectRegistry)
{
    /// <summary>The route-less fallback output device (backend default, else first), resolved through the
    /// 5 s device cache at every point of use - never frozen at construction, so a device plugged in after
    /// app start becomes the fallback on the next cache refresh. Null without a backend or devices.</summary>
    public string? ResolveFallbackOutputDeviceId()
    {
        var devices = deviceCache.EnumerateOutputDevices();
        return (devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault())?.Id;
    }

    /// <summary>Resolves a route's device to a sink: the host audio factory first (a borrowed lease it owns),
    /// else the session's <see cref="IAudioBackend"/> creates one it owns. Called only when a backend exists.</summary>
    public ClipAudioOutput ResolveAudioOutput(string? deviceId, AudioFormat format)
    {
        if (deviceId is { } id && audioOutputFactory?.Invoke(id, format) is { } lease)
            return new ClipAudioOutput(lease.Output, lease.DisposeOutputOnRuntimeDispose, lease.Release);
        return new ClipAudioOutput(audioBackend!.CreateOutput(deviceId, format), DisposeOnRelease: true, Release: null);
    }

    /// <summary>Teardown for one attached audio output: run the host's release hook (if any), then dispose the
    /// sink only when the session owns it.</summary>
    public static void Release(ClipAudioOutput o)
    {
        o.Release?.Invoke();
        if (o.DisposeOnRelease)
            (o.Output as IDisposable)?.Dispose();
    }

    private ClipAudioOutput ApplyAudioEffects(
        ClipAudioOutput output,
        IReadOnlyList<ShowAudioEffectInstance>? instances)
    {
        if (instances is not { Count: > 0 })
            return output;
        var effects = new List<IAudioBusEffect>(instances.Count);
        var controls = new List<AudioEffectControl>();
        foreach (var instance in instances)
        {
            if (!instance.Enabled || string.IsNullOrWhiteSpace(instance.EffectTypeId))
                continue;
            var config = EffectConfig(instance.ConfigJson, instance.Parameters);
            IAudioBusEffect? effect = null;
            if (effectRegistry?.TryCreateAudioEffect(instance.EffectTypeId, config, out var registered) == true)
                effect = registered;
            else if (instance.EffectTypeId == GainAudioEffect.EffectId)
                effect = GainAudioEffect.FromJson(config);
            if (effect is null)
                continue;
            effects.Add(effect);
            if (effect is IAutomatableAudioBusEffect automatable)
                controls.Add(new AudioEffectControl(instance.InstanceId, automatable));
        }
        if (effects.Count == 0)
            return output;
        try
        {
            var wrapped = AudioEffectOutput.Wrap(output.Output, effects, disposeInner: output.DisposeOnRelease);
            return new ClipAudioOutput(wrapped, DisposeOnRelease: true, output.Release, controls);
        }
        catch
        {
            foreach (var effect in effects)
                effect.Dispose();
            throw;
        }
    }

    private static string? EffectConfig(
        string? configJson,
        IReadOnlyList<ShowEffectParameterValue> parameters)
    {
        var authored = parameters.Where(parameter =>
            !string.IsNullOrWhiteSpace(parameter.ParameterId) && double.IsFinite(parameter.Value)).ToArray();
        if (authored.Length == 0)
            return configJson;
        var overwritten = authored.Select(parameter => parameter.ParameterId).ToHashSet(StringComparer.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                using var document = JsonDocument.Parse(configJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var property in document.RootElement.EnumerateObject())
                        if (!overwritten.Contains(property.Name))
                            property.WriteTo(writer);
            }
            catch (JsonException)
            {
                // Scalar authoring remains usable even when a plugin's optional blob is malformed.
            }
        }
        foreach (var parameter in authored)
            writer.WriteNumber(parameter.ParameterId, parameter.Value);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    /// <summary>Resolves + attaches ONE audio route's output with per-route error isolation: a device that
    /// cannot be opened (fixed-rate JACK graph rejecting the clip's mix rate, unplugged hardware) or attached is
    /// logged and skipped so the clip still plays on its remaining routes - instead of one bad device faulting
    /// the whole cue fire or (worse) a mid-play rebuild that has already detached every output. On success the
    /// output is appended to <paramref name="outputs"/> (the caller's ownership-tracked set).</summary>
    public bool TryAttachRouteOutput(
        S.Media.Players.MediaPlayer player,
        string outputId,
        string? deviceId,
        ChannelMap? channelMap,
        int rate,
        float gain,
        List<ClipAudioOutput> outputs,
        ShowClipAudioRoute? route = null,
        IReadOnlyList<ShowAudioEffectInstance>? audioEffects = null)
    {
        ClipAudioOutput o;
        try
        {
            var channels = route is { HasGainMatrix: true }
                ? route.MatrixOutputChannels ?? route.MatrixCells!.Max(c => c.OutputChannel) + 1
                : channelMap?.OutputChannels ?? 2;
            var resolved = ResolveAudioOutput(
                deviceId ?? ResolveFallbackOutputDeviceId(), new AudioFormat(rate, channels));
            try
            {
                o = ApplyAudioEffects(resolved, audioEffects);
            }
            catch
            {
                // ApplyAudioEffects owns any effects it managed to construct, but ownership of the
                // terminal output is transferred only after the wrapper exists. Release the raw output
                // when wrapper construction/configuration fails so an invalid insert cannot leak a device.
                Release(resolved);
                throw;
            }
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning(
                "ShowSession: audio route '{0}' → device '{1}' could not open ({2}); the clip plays without it.",
                outputId, deviceId ?? "(default)", ex.Message);
            return false;
        }

        try
        {
            if (route is { HasGainMatrix: true }
                && player.AudioRouter is { } router
                && player.AudioSourceId is { } sourceId)
            {
                router.AddOutput(o.Output, outputId);
                try
                {
                    router.ApplyMatrix(sourceId, outputId, route.ToGainMatrix(gain));
                }
                catch
                {
                    router.RemoveOutput(outputId);
                    throw;
                }
            }
            else
            {
                player.AttachAudioOutput(o.Output, outputId, map: channelMap, gain: gain);
            }
        }
        catch (Exception ex)
        {
            Release(o);
            MediaDiagnostics.LogWarning(
                "ShowSession: audio route '{0}' → device '{1}' could not attach ({2}); the clip plays without it.",
                outputId, deviceId ?? "(default)", ex.Message);
            return false;
        }

        outputs.Add(o);
        return true;
    }

    /// <summary>Acquires + attaches a voice's PROGRAM input (HaCue logical sends): one V-wide lease from the
    /// program-audio target on the clip's router, carrying the cue's N×V send matrix - realized as a synthetic
    /// matrix route so every existing level path (<c>ApplyAudioScale</c>: fades, envelope, master trim) rides
    /// the logical sends without touching a real device. Same error isolation as a device route: a target that
    /// rejects the voice (foreign rate with no bridge) is logged and the clip plays without audio rather than
    /// faulting the fire. Sends naming a logical channel the target does not have are logged and skipped (the
    /// preflight validator owns authoring errors); declared-but-empty resolvable sends attach nothing
    /// (explicitly silent). The lease is released through the voice's normal output teardown.</summary>
    public bool TryAttachProgramInput(
        S.Media.Players.MediaPlayer player,
        IShowProgramAudioTarget target,
        IReadOnlyList<ShowClipLogicalSend> sends,
        int rate,
        float attachLevel,
        List<ClipAudioOutput> outputs,
        List<AudioRouteTarget> routeTargets,
        string cueId,
        IReadOnlyList<ShowAudioEffectInstance>? audioEffects = null)
    {
        const string outputId = "_program";
        if (player.AudioRouter is not { } router || player.AudioSourceId is not { } sourceId)
            return false;

        var channelIds = target.LogicalChannelIds;
        var cells = new List<ShowAudioMatrixCell>(sends.Count);
        foreach (var send in sends)
        {
            var busChannel = -1;
            for (var i = 0; i < channelIds.Count; i++)
            {
                if (string.Equals(channelIds[i], send.LogicalChannelId, StringComparison.Ordinal))
                {
                    busChannel = i;
                    break;
                }
            }

            if (busChannel < 0 || send.SourceChannel < 0)
            {
                MediaDiagnostics.LogWarning(
                    "ShowSession: clip '{0}' sends source channel {1} to unknown logical channel '{2}'; the send is skipped.",
                    cueId, send.SourceChannel, send.LogicalChannelId);
                continue;
            }

            cells.Add(new ShowAudioMatrixCell(send.SourceChannel, busChannel, send.Gain));
        }

        if (cells.Count == 0)
            return false; // silent by authoring (empty/unresolvable sends) - nothing to attach

        var route = new ShowClipAudioRoute { MatrixCells = cells, MatrixOutputChannels = channelIds.Count };
        ProgramAudioInputLease lease;
        try
        {
            lease = target.AcquireInput(cueId, new AudioFormat(rate, channelIds.Count));
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning(
                "ShowSession: clip '{0}' could not acquire a program input ({1}); the clip plays without audio.",
                cueId, ex.Message);
            return false;
        }

        var output = new ClipAudioOutput(lease.Output, DisposeOnRelease: false, Release: lease.Dispose);
        try
        {
            output = ApplyAudioEffects(output, audioEffects);
            router.AddOutput(output.Output, outputId);
            try
            {
                router.ApplyMatrix(sourceId, outputId, route.ToGainMatrix(attachLevel));
            }
            catch
            {
                router.RemoveOutput(outputId);
                throw;
            }
        }
        catch (Exception ex)
        {
            Release(output);
            MediaDiagnostics.LogWarning(
                "ShowSession: clip '{0}' could not attach its program input ({1}); the clip plays without audio.",
                cueId, ex.Message);
            return false;
        }

        // BORROWED like a host audio lease: the voice's teardown runs the release hook, never a dispose.
        outputs.Add(output);
        routeTargets.Add(new AudioRouteTarget(outputId, 1f, route));
        return true;
    }
}
