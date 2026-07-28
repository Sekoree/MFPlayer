using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels;

/// <summary>One editable MIDI/OSC/hotkey trigger row in the drawer's Triggers section
/// (Ideas/CuePlayer-Enhancements.md §6). Mirrors <see cref="CueTriggerBinding"/> with the
/// retained-while-hidden semantics of the model: switching <see cref="Kind"/> never clears the
/// other kinds' fields. Numeric wildcard convention for the editor: channel 0 = any channel,
/// value-min 0 = any value (both map to null in the persisted model).</summary>
public sealed partial class CueTriggerBindingViewModel : ObservableObject
{
    public static IReadOnlyList<CueTriggerKind> Kinds { get; } = Enum.GetValues<CueTriggerKind>();

    public static IReadOnlyList<CueTriggerMidiMessageType> MidiMessageTypes { get; } =
        Enum.GetValues<CueTriggerMidiMessageType>();

    [ObservableProperty]
    private CueTriggerKind _kind;

    partial void OnKindChanged(CueTriggerKind value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsMidi));
        OnPropertyChanged(nameof(IsOsc));
        OnPropertyChanged(nameof(IsHotkey));
    }

    public bool IsMidi => Kind == CueTriggerKind.Midi;

    public bool IsOsc => Kind == CueTriggerKind.Osc;

    public bool IsHotkey => Kind == CueTriggerKind.Hotkey;

    /// <summary>Per-binding enable; the spec is retained while disabled (the VideoFx pattern).</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>True while this row is the cue player's MIDI-learn target (the Learn toggle is
    /// lit and the next incoming MIDI message fills the fields). Session-transient.</summary>
    [ObservableProperty]
    private bool _isMidiLearning;

    /// <summary>MIDI input port name to match; empty = any device.</summary>
    [ObservableProperty]
    private string _midiDeviceName = string.Empty;

    [ObservableProperty]
    private CueTriggerMidiMessageType _midiMessageType;

    /// <summary>MIDI channel 1..16; 0 = any channel (the editor's wildcard).</summary>
    [ObservableProperty]
    private int _midiChannel;

    /// <summary>Note / controller / program number 0..127.</summary>
    [ObservableProperty]
    private int _midiNumber;

    /// <summary>Minimum NoteOn velocity / CC value; 0 = any (velocity-0 NoteOn never fires - it is
    /// the wire encoding of NoteOff).</summary>
    [ObservableProperty]
    private int _midiValueMin;

    /// <summary>Exact OSC address to match (case-sensitive, per the OSC spec).</summary>
    [ObservableProperty]
    private string _oscAddress = string.Empty;

    /// <summary>Optional first-argument text match; empty = any arguments.</summary>
    [ObservableProperty]
    private string _oscArgument = string.Empty;

    /// <summary>Edit-time transport-profile clash veto, stamped by the cue player when the row is
    /// surfaced in the drawer: returns true when <c>gesture</c> is already a configured transport
    /// key (GO/Stop/…), in which case the assignment is REJECTED (the probe surfaces the warning).
    /// Null (tests / detached rows) = no veto.</summary>
    public Func<string, bool>? HotkeyConflictProbe { get; set; }

    private string? _hotkeyGesture;

    /// <summary>Validated hotkey gesture (<see cref="CueHotkeyGesture"/> text); null = not set.</summary>
    public string? HotkeyGesture
    {
        get => _hotkeyGesture;
        set
        {
            if (SetProperty(ref _hotkeyGesture, value))
                OnPropertyChanged(nameof(HotkeyGestureText));
        }
    }

    /// <summary>Hotkey field text, LostFocus-parsed like the schedule time field: empty clears,
    /// unparseable text (or a transport-profile clash, per <see cref="HotkeyConflictProbe"/>)
    /// restores the last valid value.</summary>
    public string HotkeyGestureText
    {
        get => HotkeyGesture ?? string.Empty;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
                HotkeyGesture = null;
            else if (CueHotkeyGesture.IsValid(trimmed) && HotkeyConflictProbe?.Invoke(trimmed) != true)
                HotkeyGesture = trimmed;
            else
                OnPropertyChanged(nameof(HotkeyGestureText)); // restore the last valid text
        }
    }

    public static CueTriggerBindingViewModel FromModel(CueTriggerBinding binding) => new()
    {
        Kind = binding.Kind,
        Enabled = binding.Enabled,
        MidiDeviceName = binding.MidiDeviceName ?? string.Empty,
        MidiMessageType = binding.MidiMessageType,
        MidiChannel = binding.MidiChannel is { } ch ? Math.Clamp(ch, 1, 16) : 0,
        MidiNumber = Math.Clamp(binding.MidiNumber, 0, 127),
        MidiValueMin = binding.MidiValueMin is { } min ? Math.Clamp(min, 0, 127) : 0,
        OscAddress = binding.OscAddress ?? string.Empty,
        OscArgument = binding.OscArgument ?? string.Empty,
        HotkeyGesture = string.IsNullOrWhiteSpace(binding.HotkeyGesture) ? null : binding.HotkeyGesture,
    };

    public CueTriggerBinding ToModel() => new()
    {
        Kind = Kind,
        Enabled = Enabled,
        MidiDeviceName = string.IsNullOrWhiteSpace(MidiDeviceName) ? null : MidiDeviceName.Trim(),
        MidiMessageType = MidiMessageType,
        MidiChannel = MidiChannel is >= 1 and <= 16 ? MidiChannel : null,
        MidiNumber = Math.Clamp(MidiNumber, 0, 127),
        MidiValueMin = MidiValueMin is >= 1 and <= 127 ? MidiValueMin : null,
        OscAddress = string.IsNullOrWhiteSpace(OscAddress) ? null : OscAddress.Trim(),
        OscArgument = string.IsNullOrWhiteSpace(OscArgument) ? null : OscArgument.Trim(),
        HotkeyGesture = HotkeyGesture,
    };
}
