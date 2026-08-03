using System.Diagnostics;
using HaCue2.Core.Model;
using S.Control;

namespace HaCue2.Engine;

/// <summary>What a matched binding asks the show to do.</summary>
/// <param name="Value">For a parameter binding, the scaled value; null otherwise.</param>
public readonly record struct TriggerAction(
    TriggerTarget Target,
    Guid? CueId,
    string ParameterId,
    double? Value,
    string Describe);

/// <summary>
/// External input: the MIDI ports and OSC listeners a show is driven from (register item 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>One master gate, and it never touches GO.</b> The whole point of collapsing HaPlay's three arms
/// into one toggle is that an operator has a single answer to "is this desk live?". Gating the app's
/// own transport behind it would make GO stop working for reasons nobody could see.
/// </para>
/// <para>
/// <b>The device layer is S.Control's.</b> Discovery, name matching across driver versions, hot-plug
/// and the input monitor are solved there and were not worth re-deriving. What lives here is the part
/// that is HaCue2's own: turning the project's trigger definitions into a device config, and turning
/// what arrives into cue fires.
/// </para>
/// <para>
/// <b>Nothing here fires anything itself.</b> It raises <see cref="Triggered"/> and the host decides,
/// so the one place that knows what firing a cue means stays the one place that does it.
/// </para>
/// </remarks>
public sealed class TriggerInputs : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, long> _lastFired = [];
    private ControlInputSession? _session;
    private HaCueProject _project;
    private bool _enabled;

    public TriggerInputs(HaCueProject project) => _project = project;

    /// <summary>Raised when an inbound message matched a binding. Off the I/O thread's back — the
    /// handler marshals; this must not block a UDP receive or a PortMIDI poll.</summary>
    public event Action<TriggerAction>? Triggered;

    /// <summary>Raised for everything that arrived, matched or not — the Targets wire monitor.</summary>
    public event Action<TriggerSignal>? Observed;

    /// <summary>Raised when a source could not be opened, or a binding could not be resolved.</summary>
    public event Action<string>? Problem;

    /// <summary>Whether external input is live. False when a project opens (register item 3).</summary>
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
                return _enabled;
        }
    }

    /// <summary>
    /// Opens or closes the configured sources.
    /// </summary>
    /// <remarks>
    /// Devices are opened only while enabled, rather than opened once and filtered: an app holding a
    /// MIDI port it is deliberately ignoring is an app another program cannot use that port from,
    /// which is a rude thing to do to somebody's rig during a get-in.
    /// </remarks>
    public async Task SetEnabledAsync(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _enabled)
                return;

            _enabled = enabled;
        }

        if (enabled)
            await OpenAsync().ConfigureAwait(false);
        else
            await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>Adopts an edited document. Reopens the sources only when the CONFIG actually changed.</summary>
    /// <remarks>
    /// Compared rather than reopened blindly, because a reload happens on every edit and closing a MIDI
    /// port to reopen the identical one would drop whatever arrived in between — during a show, that is
    /// a missed GO.
    /// </remarks>
    public async Task ReloadAsync(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var before = Describe(_project);
        _project = project;

        if (IsEnabled && before != Describe(project))
        {
            await CloseAsync().ConfigureAwait(false);
            await OpenAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The source config as a string, so "did the devices change" is one comparison.</summary>
    private static string Describe(HaCueProject project) =>
        string.Join(
            "|",
            project.TriggerInputs
                .Where(input => input.Enabled)
                .OrderBy(input => input.Id)
                .Select(input => $"{input.Kind}:{input.DeviceHint}:{input.Port}"));

    private async Task OpenAsync()
    {
        var sources = _project.TriggerInputs.Where(input => input.Enabled).ToList();

        if (sources.Count == 0)
            return;

        var config = new ControlSystemConfig
        {
            IsArmed = true,
            OSCListeners =
            [
                .. sources
                    .Where(input => input.Kind == TriggerInputKind.OscIn)
                    .Select(input => new ControlOSCListenerConfig
                    {
                        Id = input.Id,
                        Name = input.Name,
                        IsEnabled = true,
                        LocalPort = input.Port > 0 ? input.Port : 10_020,
                    }),
            ],
            Devices =
            [
                .. sources
                    .Where(input => input.Kind == TriggerInputKind.MidiIn)
                    .Select(input => new ControlDeviceInstanceConfig
                    {
                        Id = input.Id,
                        Name = input.Name,
                        Protocol = ControlDeviceProtocol.MIDI,
                        IsEnabled = true,
                        Binding = new ControlDeviceBindingConfig
                        {
                            // A HINT, matched by name the way an audio line's is — device ids are not
                            // stable across reboots, let alone across machines.
                            MIDIInputDeviceName = input.DeviceHint.Length > 0 ? input.DeviceHint : null,
                        },
                    }),
            ],
        };

        try
        {
            var session = new ControlInputSession(config);
            session.InputObserved += OnObserved;

            await session.StartAsync().ConfigureAwait(false);

            lock (_gate)
                _session = session;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A port another application already holds, or a UDP port in use. Reported and survived:
            // external input is an addition to a show that already works without it.
            Problem?.Invoke($"external input could not be opened — {failure.Message}");
        }
    }

    private async Task CloseAsync()
    {
        ControlInputSession? session;

        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is null)
            return;

        session.InputObserved -= OnObserved;

        try
        {
            await session.StopAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Closing must not throw out of a toggle.
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Matches one arrived message against every enabled source's bindings.
    /// </summary>
    /// <remarks>
    /// Runs on the I/O thread. It does no work beyond matching and raising, because a PortMIDI poll
    /// that is blocked is a poll that is dropping messages.
    /// </remarks>
    private void OnObserved(ControlMonitorRecord record)
    {
        var sourceId = record.DeviceInstanceId ?? record.ListenerId ?? Guid.Empty;

        if (TriggerMatching.Read(record, sourceId) is not { } signal)
            return;

        Observed?.Invoke(signal);

        if (!IsEnabled)
            return;

        foreach (var input in _project.TriggerInputs)
        {
            // A record whose source we cannot identify is matched against EVERY enabled source rather
            // than dropped: an OSC listener that did not stamp its id is still a message that arrived,
            // and refusing to act on it would make triggers mysteriously stop working.
            if (!input.Enabled || (sourceId != Guid.Empty && input.Id != sourceId))
                continue;

            foreach (var binding in input.Bindings)
            {
                if (!TriggerMatching.Matches(signal, binding.Input) || !Admit(binding))
                    continue;

                if (Resolve(binding, signal) is { } action)
                    Triggered?.Invoke(action);
            }
        }
    }

    /// <summary>
    /// The no-repeat filter.
    /// </summary>
    /// <remarks>
    /// A hardware button bounces and a fader sends a stream; without this, one press fires a cue
    /// several times. Keyed per BINDING rather than per message, so two bindings on the same note both
    /// still fire — they are two things the operator asked for.
    /// </remarks>
    private bool Admit(TriggerBinding binding)
    {
        if (binding.NoRepeatMs <= 0)
            return true;

        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (_lastFired.TryGetValue(binding.Id, out var last)
                && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(binding.NoRepeatMs))
                return false;

            _lastFired[binding.Id] = now;
            return true;
        }
    }

    /// <summary>What a matched binding means, or null when it cannot mean anything.</summary>
    private TriggerAction? Resolve(TriggerBinding binding, TriggerSignal signal)
    {
        switch (binding.Target)
        {
            case TriggerTarget.Cue when binding.TargetCueId is { } cueId:
                if (_project.FindCue(cueId) is not { } cue)
                {
                    Problem?.Invoke($"“{binding.Input}” targets a cue that is no longer in this show");
                    return null;
                }

                return new TriggerAction(TriggerTarget.Cue, cueId, "", null, $"{binding.Input} → {cue.Label}");

            case TriggerTarget.Parameter when binding.ParameterId.Length > 0:
                // A note-on carries no continuous value, so binding one to a fader-shaped parameter is
                // refused rather than writing an arbitrary number into a master trim.
                if (TriggerMatching.Scale(signal, binding.RangeMin, binding.RangeMax) is not { } value)
                    return null;

                return new TriggerAction(
                    TriggerTarget.Parameter, null, binding.ParameterId, value,
                    $"{binding.Input} → {binding.ParameterId}");

            case TriggerTarget.Transport:
                return new TriggerAction(
                    TriggerTarget.Transport, null, binding.ParameterId, null,
                    $"{binding.Input} → {binding.ParameterId}");

            default:
                return null;
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
