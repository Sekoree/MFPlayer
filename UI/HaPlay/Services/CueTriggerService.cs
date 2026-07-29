using System.Globalization;
using Avalonia.Input;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.ViewModels;
using S.Control;

namespace HaPlay.Services;

/// <summary>Normalized incoming MIDI message for cue trigger matching (only the kinds
/// <see cref="CueTriggerMidiMessageType"/> models). <paramref name="Number"/> is the note,
/// controller or program; <paramref name="Value"/> is the velocity / CC value (0 for
/// ProgramChange); <paramref name="Channel"/> is 1..16.</summary>
public readonly record struct CueMidiTriggerInput(
    string? DeviceName,
    string? DeviceKey,
    CueTriggerMidiMessageType Type,
    int? Channel,
    int Number,
    int Value);

/// <summary>Normalized incoming OSC message for cue trigger matching. Only the FIRST argument is
/// carried (as invariant text) - the binding's optional argument match is a plain text compare.</summary>
public readonly record struct CueOscTriggerInput(string Address, string? FirstArgumentText);

/// <summary>
/// Per-cue MIDI/OSC/hotkey trigger runtime (Ideas/CuePlayer-Enhancements.md §6) - the
/// <see cref="CueSchedulerService"/> sibling. Incoming control I/O reaches it from S.Control's
/// always-on device input session (<c>ControlInputSession.InputObserved</c> →
/// <c>ControlWorkspaceViewModel.InputObserved</c> → MainViewModel marshals to the UI thread →
/// <see cref="OnControlInput"/>), so MIDI/OSC triggers flow whenever the device/listener is
/// configured and enabled - the Control mapping engine does NOT have to be armed. Hotkey bindings
/// arrive via <see cref="TryHandleHotkey"/> from the cue view's transport-key handler (which
/// checks the configurable transport keys and the legacy per-cue hotkey first - they win any
/// clash).
/// <para><b>Armed gate</b>: bindings fire only while the master
/// <see cref="CuePlayerViewModel.TriggersArmed"/> toggle is ON (session-scoped, defaults OFF -
/// deliberately separate from <see cref="CuePlayerViewModel.SchedulesArmed"/> so arming one
/// surface never silently opens the other) and <see cref="CuePlayerViewModel.IsCueEditMode"/> is
/// OFF. Scope is the SELECTED list (fires ride its transport, the scheduler's reasoning).</para>
/// <para><b>Retrigger guard</b>: repeats of the SAME binding within
/// <see cref="RetriggerWindow"/> are ignored (MIDI pot/NoteOn chatter, OS key repeat). Distinct
/// bindings are independent.</para>
/// <para><b>MIDI learn</b>: while <see cref="CuePlayerViewModel.MidiLearnTarget"/> is set, the
/// next incoming MIDI message fills that row instead of firing anything - deliberately NOT gated
/// on the armed toggle (learn is an edit-mode affordance).</para>
/// <para>Fires run through <see cref="CuePlayerViewModel.FireTriggeredCueSafeAsync"/> - the exact
/// operator-selected GO path. UI thread only (the scheduler contract); the clock is injectable
/// for tests.</para>
/// </summary>
public sealed class CueTriggerService : IDisposable
{
    /// <summary>Repeats of one binding inside this window are chatter, not intent.</summary>
    public static readonly TimeSpan RetriggerWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>Last-fire entries older than this are pruned (memory bound for long sessions).</summary>
    private static readonly TimeSpan LastFireRetention = TimeSpan.FromSeconds(10);

    private readonly CuePlayerViewModel _cuePlayer;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<CueTriggerBindingViewModel, DateTimeOffset> _lastFire = new();
    private bool _disposed;

    public CueTriggerService(CuePlayerViewModel cuePlayer, Func<DateTimeOffset>? now = null)
    {
        _cuePlayer = cuePlayer ?? throw new ArgumentNullException(nameof(cuePlayer));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    private bool GateOpen => !_disposed && _cuePlayer.TriggersArmed && !_cuePlayer.IsCueEditMode;

    /// <summary>Entry point for the always-on device input event. Accepts Input AND Dropped
    /// records: "dropped" only means no Control-workspace DEVICE mapping matched - the message
    /// still physically arrived on a configured port/listener, which is all a cue trigger needs.</summary>
    public void OnControlInput(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_disposed)
            return;
        if (record.Direction is not (ControlMonitorDirection.Input or ControlMonitorDirection.Dropped))
            return;
        if (record.Protocol == ControlMonitorProtocol.MIDI && TryMapMidiRecord(record, out var midi))
            OnMidiInput(midi);
        else if (record.Protocol == ControlMonitorProtocol.OSC && TryMapOscRecord(record, out var osc))
            OnOscInput(osc);
    }

    /// <summary>Handles one normalized incoming MIDI message: MIDI-learn capture first (ungated),
    /// then the armed-gated binding sweep.</summary>
    public void OnMidiInput(in CueMidiTriggerInput input)
    {
        if (_disposed)
            return;

        if (_cuePlayer.MidiLearnTarget is { } learnRow)
        {
            learnRow.Kind = CueTriggerKind.Midi;
            learnRow.MidiDeviceName = input.DeviceName ?? input.DeviceKey ?? string.Empty;
            learnRow.MidiMessageType = input.Type;
            learnRow.MidiChannel = input.Channel ?? 0;
            learnRow.MidiNumber = Math.Clamp(input.Number, 0, 127);
            _cuePlayer.MidiLearnTarget = null;
            _cuePlayer.StatusMessage = Strings.Format(
                nameof(Strings.CueTriggerLearnCapturedStatusFormat),
                $"{input.Type} {learnRow.MidiNumber}"
                + (input.Channel is { } ch ? $" ch{ch}" : string.Empty));
            return;
        }

        // A velocity-0 NoteOn is the wire encoding of NoteOff - never a trigger.
        if (input.Type == CueTriggerMidiMessageType.NoteOn && input.Value <= 0)
            return;

        var local = input; // capture for the lambda (in-parameter cannot be closed over)
        Sweep(binding => MidiMatches(binding, local), nameof(Strings.CueMidiTriggerFiredStatusFormat));
    }

    /// <summary>Handles one normalized incoming OSC message (armed-gated binding sweep).</summary>
    public void OnOscInput(in CueOscTriggerInput input)
    {
        if (_disposed)
            return;
        var local = input;
        Sweep(binding => OscMatches(binding, local), nameof(Strings.CueOscTriggerFiredStatusFormat));
    }

    /// <summary>Hotkey-binding dispatch, called LAST by the cue view's transport-key handler (the
    /// transport keys and the legacy per-cue hotkey always win a clash). Returns true when at
    /// least one binding fired.</summary>
    public bool TryHandleHotkey(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Sweep(
            binding => binding.Kind == CueTriggerKind.Hotkey
                       && !string.IsNullOrWhiteSpace(binding.HotkeyGesture)
                       && CueHotkeyGesture.Matches(binding.HotkeyGesture, e),
            nameof(Strings.CueHotkeyFiredStatusFormat));
    }

    /// <summary>One armed-gated pass over every binding in the selected list: fire each enabled
    /// match through the operator-selected GO path, retrigger-guarded per binding.</summary>
    private bool Sweep(Func<CueTriggerBindingViewModel, bool> matches, string statusFormatKey)
    {
        if (!GateOpen)
            return false;

        var now = _now();
        var fired = false;
        foreach (var cue in _cuePlayer.EnumerateTriggeredCueNodes().ToList())
        {
            foreach (var binding in cue.Triggers.ToArray())
            {
                if (!binding.Enabled || !matches(binding))
                    continue;
                if (_lastFire.TryGetValue(binding, out var last) && now - last < RetriggerWindow)
                    continue;
                _lastFire[binding] = now;
                fired = true;
                _ = _cuePlayer.FireTriggeredCueSafeAsync(cue, statusFormatKey);
            }
        }

        PruneLastFire(now);
        return fired;
    }

    private static bool MidiMatches(CueTriggerBindingViewModel binding, in CueMidiTriggerInput input)
    {
        if (binding.Kind != CueTriggerKind.Midi || binding.MidiMessageType != input.Type)
            return false;
        if (binding.MidiChannel is >= 1 and <= 16
            && (input.Channel is not { } channel || channel != binding.MidiChannel))
            return false;
        if (binding.MidiNumber != input.Number)
            return false;
        if (input.Type != CueTriggerMidiMessageType.ProgramChange
            && binding.MidiValueMin >= 1 && input.Value < binding.MidiValueMin)
            return false;
        return DeviceMatches(binding.MidiDeviceName, input.DeviceName)
               || DeviceMatches(binding.MidiDeviceName, input.DeviceKey);
    }

    /// <summary>Null/empty configured name = any device; otherwise a case-insensitive substring
    /// match against the port name / device alias (tolerant of the monitor's decorated formats).</summary>
    private static bool DeviceMatches(string? configured, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return true;
        return candidate is not null
               && candidate.Contains(configured.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool OscMatches(CueTriggerBindingViewModel binding, in CueOscTriggerInput input)
    {
        if (binding.Kind != CueTriggerKind.Osc || string.IsNullOrWhiteSpace(binding.OscAddress))
            return false;
        if (!string.Equals(binding.OscAddress.Trim(), input.Address, StringComparison.Ordinal))
            return false;
        return string.IsNullOrWhiteSpace(binding.OscArgument)
               || string.Equals(binding.OscArgument.Trim(), input.FirstArgumentText, StringComparison.Ordinal);
    }

    /// <summary>Maps a monitor MIDI input record to a trigger input. Only NoteOn / ControlChange /
    /// ProgramChange map (the modeled kinds); everything else returns false.</summary>
    public static bool TryMapMidiRecord(ControlMonitorRecord record, out CueMidiTriggerInput input)
    {
        input = default;
        CueTriggerMidiMessageType type;
        int? number;
        switch (record.MIDIMessageType)
        {
            case ControlMIDIMessageType.NoteOn:
                type = CueTriggerMidiMessageType.NoteOn;
                number = record.MIDINote;
                break;
            case ControlMIDIMessageType.ControlChange:
                type = CueTriggerMidiMessageType.ControlChange;
                number = record.MIDIController;
                break;
            case ControlMIDIMessageType.ProgramChange:
                type = CueTriggerMidiMessageType.ProgramChange;
                number = record.MIDIValue; // the payload stores the program in Value
                break;
            default:
                return false;
        }

        if (number is not { } resolvedNumber)
            return false;
        input = new CueMidiTriggerInput(
            record.Endpoint,
            record.DeviceKey,
            type,
            record.MIDIChannel,
            resolvedNumber,
            type == CueTriggerMidiMessageType.ProgramChange ? 0 : record.MIDIValue ?? 0);
        return true;
    }

    /// <summary>Maps a monitor OSC input record to a trigger input (address + first argument as
    /// invariant text).</summary>
    public static bool TryMapOscRecord(ControlMonitorRecord record, out CueOscTriggerInput input)
    {
        input = default;
        if (string.IsNullOrEmpty(record.Address))
            return false;
        string? firstArg = null;
        if (record.OSCArguments.Count > 0)
        {
            var arg = record.OSCArguments[0];
            firstArg = arg.StringValue
                       ?? arg.IntegerValue?.ToString(CultureInfo.InvariantCulture)
                       ?? arg.FloatValue?.ToString(CultureInfo.InvariantCulture)
                       ?? (arg.BoolValue is { } b ? (b ? "true" : "false") : null);
        }

        input = new CueOscTriggerInput(record.Address, firstArg);
        return true;
    }

    private void PruneLastFire(DateTimeOffset now)
    {
        if (_lastFire.Count < 64)
            return;
        var cutoff = now - LastFireRetention;
        foreach (var stale in _lastFire.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            _lastFire.Remove(stale);
    }

    public void Dispose()
    {
        _disposed = true;
        _lastFire.Clear();
    }
}
