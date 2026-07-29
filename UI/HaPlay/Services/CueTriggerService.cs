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
/// OFF. Scope is EVERY loaded list (the cross-list merged session, the scheduler's reasoning): a
/// binding in a non-selected list fires into the same session without moving the visible
/// transport.</para>
/// <para><b>Rising edge (ControlChange)</b>: a CC binding latches when the value reaches its
/// effective threshold and only re-arms once the value falls back below, so a held button fires
/// once on press (and re-arms on release) and a pot sweep fires once as it crosses the threshold -
/// no matter how long the hold or how many messages the sweep emits. The effective threshold is
/// <c>max(1, MidiValueMin)</c>, so an unset Min still treats 0 as "off". NoteOn (velocity-0 is
/// rejected as the wire NoteOff) and ProgramChange are discrete events and never latch.</para>
/// <para><b>Retrigger guard</b>: repeats of the SAME binding within
/// <see cref="RetriggerWindow"/> are ignored (MIDI pot/NoteOn chatter, OS key repeat). Distinct
/// bindings are independent.</para>
/// <para><b>Multi-match</b>: one message can match several cues (duplicating a cue clones its
/// bindings). Matches are collected first and then fired SEQUENTIALLY, awaited in order - the
/// operator's own multi-select GO semantics. Firing them concurrently would have each fire's
/// <c>GoCore</c> cancel the previous one's transport run (pre-waits, group members, auto-follow).</para>
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

    /// <summary>Idle per-binding state older than this is pruned (memory bound for long sessions).</summary>
    private static readonly TimeSpan StateRetention = TimeSpan.FromSeconds(10);

    /// <summary>Below this many tracked bindings pruning isn't worth the live-set scan.</summary>
    private const int StatePruneThreshold = 64;

    /// <summary>How one incoming message relates to ONE binding.</summary>
    private enum TriggerMatch
    {
        /// <summary>Not addressed to this binding at all - leave its latch untouched.</summary>
        None,

        /// <summary>Discrete event (NoteOn / ProgramChange / OSC / hotkey): fire, no latch.</summary>
        Fire,

        /// <summary>Continuous controller at/above the threshold: fire only on the rising edge,
        /// then latch until it falls back below.</summary>
        FireLatching,

        /// <summary>Continuous controller below the threshold: re-arm the latch, fire nothing.</summary>
        Rearm,
    }

    /// <summary>Per-binding runtime state: the retrigger guard's last fire plus the ControlChange
    /// rising-edge latch. Keyed by the live binding row, and pruned against the live cue graph so a
    /// cue-list rebuild (which replaces every row VM) cannot strand entries.</summary>
    private sealed class BindingState
    {
        public DateTimeOffset LastFire = DateTimeOffset.MinValue;

        /// <summary>ControlChange only: the last observed value was at/above the effective
        /// threshold, so the binding is spent until it falls back below.</summary>
        public bool Latched;
    }

    private readonly CuePlayerViewModel _cuePlayer;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<CueTriggerBindingViewModel, BindingState> _state = new();
    private bool _disposed;

    public CueTriggerService(CuePlayerViewModel cuePlayer, Func<DateTimeOffset>? now = null)
    {
        _cuePlayer = cuePlayer ?? throw new ArgumentNullException(nameof(cuePlayer));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    private bool GateOpen => !_disposed && _cuePlayer.TriggersArmed && !_cuePlayer.IsCueEditMode;

    /// <summary>Entry point for the always-on device input event. Accepts Input AND Dropped
    /// records: "dropped" only means no Control-workspace DEVICE mapping matched - the message
    /// still physically arrived on a configured port/listener, which is all a cue trigger needs.
    /// Output (echo of what WE sent) is never a trigger. Returns true when at least one binding
    /// fired.</summary>
    public bool OnControlInput(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_disposed)
            return false;
        if (record.Direction is not (ControlMonitorDirection.Input or ControlMonitorDirection.Dropped))
            return false;
        if (record.Protocol == ControlMonitorProtocol.MIDI && TryMapMidiRecord(record, out var midi))
            return OnMidiInput(midi);
        if (record.Protocol == ControlMonitorProtocol.OSC && TryMapOscRecord(record, out var osc))
            return OnOscInput(osc);
        return false;
    }

    /// <summary>Handles one normalized incoming MIDI message: MIDI-learn capture first (ungated),
    /// then the armed-gated binding sweep. Returns true when at least one binding fired.</summary>
    public bool OnMidiInput(in CueMidiTriggerInput input)
    {
        if (_disposed)
            return false;

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
            return false;
        }

        // A velocity-0 NoteOn is the wire encoding of NoteOff - never a trigger.
        if (input.Type == CueTriggerMidiMessageType.NoteOn && input.Value <= 0)
            return false;

        var local = input; // capture for the lambda (in-parameter cannot be closed over)
        return Sweep(binding => MidiMatches(binding, local), nameof(Strings.CueMidiTriggerFiredStatusFormat));
    }

    /// <summary>Handles one normalized incoming OSC message (armed-gated binding sweep). Returns
    /// true when at least one binding fired.</summary>
    public bool OnOscInput(in CueOscTriggerInput input)
    {
        if (_disposed)
            return false;
        var local = input;
        return Sweep(binding => OscMatches(binding, local), nameof(Strings.CueOscTriggerFiredStatusFormat));
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
                       && CueHotkeyGesture.Matches(binding.HotkeyGesture, e)
                ? TriggerMatch.Fire
                : TriggerMatch.None,
            nameof(Strings.CueHotkeyFiredStatusFormat));
    }

    /// <summary>One armed-gated pass over every binding in every loaded list: collect each enabled
    /// match (rising-edge latched and retrigger-guarded per binding), then fire the matched cues
    /// sequentially through the operator-selected GO path. Returns true when at least one cue was
    /// queued to fire - the hotkey path's "handled" answer.</summary>
    private bool Sweep(Func<CueTriggerBindingViewModel, TriggerMatch> matches, string statusFormatKey)
    {
        if (!GateOpen)
            return false;

        var now = _now();
        // Fires are collected and dispatched AFTER the sweep: firing inside the loop let each
        // fire's GoCore cancel the previous one's transport run (review defect 3).
        var pending = new List<CueNodeViewModel>();
        // Only scanned (and only allocated) once the state map is worth pruning.
        var live = _state.Count >= StatePruneThreshold
            ? new HashSet<CueTriggerBindingViewModel>()
            : null;

        foreach (var cue in _cuePlayer.EnumerateTriggeredCueNodes().ToList())
        {
            foreach (var binding in cue.Triggers.ToArray())
            {
                live?.Add(binding);
                if (!binding.Enabled)
                    continue;

                var match = matches(binding);
                if (match == TriggerMatch.None)
                    continue;
                if (match == TriggerMatch.Rearm)
                {
                    // The controller fell back below the threshold - the next rise fires again.
                    if (_state.TryGetValue(binding, out var armed))
                        armed.Latched = false;
                    continue;
                }

                if (!_state.TryGetValue(binding, out var state))
                    _state[binding] = state = new BindingState();
                if (match == TriggerMatch.FireLatching)
                {
                    if (state.Latched)
                        continue; // still above the threshold - not a new rising edge
                    state.Latched = true;
                }

                if (now - state.LastFire < RetriggerWindow)
                    continue;
                state.LastFire = now;
                pending.Add(cue);
            }
        }

        PruneState(now, live);
        if (pending.Count == 0)
            return false;
        _ = FirePendingAsync(pending, statusFormatKey);
        return true;
    }

    /// <summary>Fires the sweep's matches one at a time, awaited in order - the exact sequencing of
    /// the operator's multi-selection GO (<c>FireSelectedCueNow</c>). Each fire swallows its own
    /// failures (<c>FireTriggeredCueSafeAsync</c>), so one bad cue never strands the rest.</summary>
    private async Task FirePendingAsync(List<CueNodeViewModel> cues, string statusFormatKey)
    {
        foreach (var cue in cues)
            await _cuePlayer.FireTriggeredCueSafeAsync(cue, statusFormatKey);
    }

    private static TriggerMatch MidiMatches(CueTriggerBindingViewModel binding, in CueMidiTriggerInput input)
    {
        if (binding.Kind != CueTriggerKind.Midi || binding.MidiMessageType != input.Type)
            return TriggerMatch.None;
        if (binding.MidiChannel is >= 1 and <= 16
            && (input.Channel is not { } channel || channel != binding.MidiChannel))
            return TriggerMatch.None;
        if (binding.MidiNumber != input.Number)
            return TriggerMatch.None;
        if (!DeviceMatches(binding.MidiDeviceName, input.DeviceName)
            && !DeviceMatches(binding.MidiDeviceName, input.DeviceKey))
            return TriggerMatch.None;

        // A CC is a continuous stream, not an event: a button holds 127 until release and a fader
        // emits a value per step. Only the RISING edge through the effective threshold fires; the
        // fall back below re-arms. The effective threshold is at least 1, so an unset Min still
        // reads a button's 0-release as "off" instead of firing a second time on it.
        if (input.Type == CueTriggerMidiMessageType.ControlChange)
        {
            return input.Value >= Math.Max(1, binding.MidiValueMin)
                ? TriggerMatch.FireLatching
                : TriggerMatch.Rearm;
        }

        // NoteOn / ProgramChange are discrete events - each message is its own intent.
        if (input.Type != CueTriggerMidiMessageType.ProgramChange
            && binding.MidiValueMin >= 1 && input.Value < binding.MidiValueMin)
            return TriggerMatch.None;
        return TriggerMatch.Fire;
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

    private static TriggerMatch OscMatches(CueTriggerBindingViewModel binding, in CueOscTriggerInput input)
    {
        if (binding.Kind != CueTriggerKind.Osc || string.IsNullOrWhiteSpace(binding.OscAddress))
            return TriggerMatch.None;
        if (!string.Equals(binding.OscAddress.Trim(), input.Address, StringComparison.Ordinal))
            return TriggerMatch.None;
        // An OSC message is a discrete event; the optional argument match is the caller's own
        // button-down/up filter, so a non-matching argument is simply "not addressed to us".
        return string.IsNullOrWhiteSpace(binding.OscArgument)
               || string.Equals(binding.OscArgument.Trim(), input.FirstArgumentText, StringComparison.Ordinal)
            ? TriggerMatch.Fire
            : TriggerMatch.None;
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

    /// <summary>Bounds the per-binding state map. <paramref name="live"/> is the set of binding rows
    /// this sweep actually walked (every loaded list's cue graph); anything else is stranded by a
    /// cue-list rebuild - every row VM is recreated, so the old keys can never match again - or by a
    /// list being closed. Live-but-idle entries also expire, except a latched one (dropping it would
    /// re-arm a controller that is still physically held).</summary>
    private void PruneState(DateTimeOffset now, HashSet<CueTriggerBindingViewModel>? live)
    {
        if (live is null)
            return;
        var cutoff = now - StateRetention;
        var stale = _state
            .Where(kv => !live.Contains(kv.Key) || (!kv.Value.Latched && kv.Value.LastFire < cutoff))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var binding in stale)
            _state.Remove(binding);
    }

    public void Dispose()
    {
        _disposed = true;
        _state.Clear();
    }
}
