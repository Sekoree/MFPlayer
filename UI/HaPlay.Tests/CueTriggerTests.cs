using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Input;
using HaPlay.Services;
using HaPlay.ViewModels;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Per-cue MIDI/OSC/hotkey triggers (Ideas/CuePlayer-Enhancements.md §6): binding
/// persistence (incl. the STJ init-property gotcha - a minimal <c>{}</c> binding must keep
/// Enabled true), the VM round-trip on the CueNode base, and the CueTriggerService runtime with
/// an injected clock - fires on MIDI note / OSC address / hotkey match, the TriggersArmed +
/// edit-mode gates, the 250 ms per-binding retrigger guard, wildcard device/channel matching,
/// disabled bindings staying inert, MIDI learn capture, the transport-hotkey edit-time veto, and
/// the ControlMonitorRecord mappers.</summary>
public sealed class CueTriggerTests
{
    // ---- Persistence ----

    [Fact]
    public void Triggers_RoundTrip_ThroughViewModelAndJson()
    {
        var node = new MediaCueNode
        {
            Number = "1",
            Label = "Opener",
            Source = new FilePlaylistItem("/tmp/opener.wav"),
            Triggers =
            [
                new CueTriggerBinding
                {
                    Kind = CueTriggerKind.Midi,
                    MidiDeviceName = "APC mini",
                    MidiMessageType = CueTriggerMidiMessageType.NoteOn,
                    MidiChannel = 3,
                    MidiNumber = 60,
                    MidiValueMin = 10,
                    Enabled = false, // retained-while-disabled must round-trip too
                },
                new CueTriggerBinding
                {
                    Kind = CueTriggerKind.Osc,
                    OscAddress = "/haplay/cue/1",
                    OscArgument = "1",
                },
                new CueTriggerBinding { Kind = CueTriggerKind.Hotkey, HotkeyGesture = "Ctrl+F5" },
            ],
        };

        // VM round-trip preserves the payload (Triggers live on the CueNode base like Schedule).
        var vm = CueNodeViewModel.FromModel(node);
        Assert.True(vm.HasTriggers);
        Assert.Equal(3, vm.Triggers.Count);
        Assert.False(vm.Triggers[0].Enabled);
        Assert.Equal(3, vm.Triggers[0].MidiChannel);
        Assert.Equal(60, vm.Triggers[0].MidiNumber);
        Assert.Equal("/haplay/cue/1", vm.Triggers[1].OscAddress);
        Assert.Equal("Ctrl+F5", vm.Triggers[2].HotkeyGesture);
        var back = Assert.IsType<MediaCueNode>(vm.ToModel());
        Assert.Equal(node.Triggers, back.Triggers);

        // JSON round-trip through the cue-list contract.
        var list = new CueList { Nodes = [node] };
        var json = JsonSerializer.Serialize(list, CueListJsonContext.Default.CueList);
        var loaded = JsonSerializer.Deserialize(json, CueListJsonContext.Default.CueList)!;
        var reloaded = Assert.IsType<MediaCueNode>(Assert.Single(loaded.Nodes));
        Assert.Equal(node.Triggers, reloaded.Triggers);
    }

    [Fact]
    public void Triggers_LegacyAbsence_LoadsNull_AndEmptyBindingKeepsDefaults()
    {
        // Old files carry no "triggers" field at all - must load unchanged (null list).
        var legacy = """{"nodes":[{"kind":"media","label":"Old"}]}""";
        var loaded = JsonSerializer.Deserialize(legacy, CueListJsonContext.Default.CueList)!;
        Assert.Null(Assert.IsType<MediaCueNode>(loaded.Nodes[0]).Triggers);

        // The STJ source-gen gotcha: a minimal "triggers":[{}] must keep the C# property
        // initializers (set, not init - see the CueTriggerBinding doc note), NOT CLR defaults.
        var minimal = """{"nodes":[{"kind":"media","label":"New","triggers":[{}]}]}""";
        var minimalLoaded = JsonSerializer.Deserialize(minimal, CueListJsonContext.Default.CueList)!;
        var binding = Assert.Single(Assert.IsType<MediaCueNode>(minimalLoaded.Nodes[0]).Triggers!);
        Assert.True(binding.Enabled);
        Assert.Equal(CueTriggerKind.Midi, binding.Kind);
        Assert.Equal(CueTriggerMidiMessageType.NoteOn, binding.MidiMessageType);
        Assert.Null(binding.MidiDeviceName);
        Assert.Null(binding.MidiChannel);
        Assert.Null(binding.MidiValueMin);
        Assert.Null(binding.OscAddress);
        Assert.Null(binding.HotkeyGesture);
    }

    [Fact]
    public void CueWithoutTriggers_WritesNoTriggersField()
    {
        var vm = CueNodeViewModel.FromModel(new MediaCueNode { Label = "Plain" });
        Assert.False(vm.HasTriggers);
        var back = Assert.IsType<MediaCueNode>(vm.ToModel());
        Assert.Null(back.Triggers);

        var json = JsonSerializer.Serialize(
            new CueList { Nodes = [back] }, CueListJsonContext.Default.CueList);
        Assert.DoesNotContain("triggers", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Harness (the CueScheduleTests shape) ----

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 29, 12, 0, 0, TimeSpan.FromHours(2));
    }

    private sealed record Harness(
        CuePlayerViewModel Vm,
        CueTriggerService Service,
        TestClock Clock,
        CueNodeViewModel CueVm,
        Guid CueId,
        ConcurrentQueue<Guid> Fired,
        SemaphoreSlim FireSignal);

    private static Harness BuildHarness(List<CueTriggerBinding> triggers, bool arm = true)
    {
        var cue = new MediaCueNode
        {
            Number = "1",
            Label = "Triggered song",
            Source = new FilePlaylistItem("/tmp/song.wav"),
            Triggers = triggers,
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [cue] }]);
        vm.IsCueEditMode = false; // the ctor default is edit mode ON - a show runs with it off

        var fired = new ConcurrentQueue<Guid>();
        var signal = new SemaphoreSlim(0);
        vm.MediaCueExecutor = (m, _) =>
        {
            vm.OnCueStarted(m.Id);
            fired.Enqueue(m.Id);
            signal.Release();
            return Task.FromResult<string?>(null);
        };

        var clock = new TestClock();
        var service = new CueTriggerService(vm, () => clock.Now);
        if (arm)
            vm.TriggersArmed = true;

        return new Harness(vm, service, clock, vm.SelectedCueList!.Nodes[0], cue.Id, fired, signal);
    }

    private static async Task<Guid> NextFiredAsync(Harness h)
    {
        Assert.True(await h.FireSignal.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting for a trigger fire");
        Assert.True(h.Fired.TryDequeue(out var id));
        return id;
    }

    private static async Task AssertNoFiresAsync(Harness h, int graceMs = 250)
    {
        await Task.Delay(graceMs);
        Assert.Empty(h.Fired);
    }

    private static CueTriggerBinding MidiNoteBinding(int note = 60) => new()
    {
        Kind = CueTriggerKind.Midi,
        MidiMessageType = CueTriggerMidiMessageType.NoteOn,
        MidiNumber = note,
    };

    private static CueMidiTriggerInput NoteOn(
        int note = 60, int velocity = 100, int channel = 1, string? device = "APC mini") =>
        new(device, null, CueTriggerMidiMessageType.NoteOn, channel, note, velocity);

    // ---- Service runtime: MIDI ----

    [Fact]
    public async Task Midi_NoteOn_FiresMatchingCue()
    {
        var h = BuildHarness([MidiNoteBinding()]);
        h.Service.OnMidiInput(NoteOn());
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Wrong note number stays silent.
        h.Clock.Now += TimeSpan.FromSeconds(1);
        h.Service.OnMidiInput(NoteOn(note: 61));
        await AssertNoFiresAsync(h);
    }

    [Fact]
    public async Task Midi_RespectsArmedGate_AndEditMode()
    {
        var h = BuildHarness([MidiNoteBinding()], arm: false);
        h.Service.OnMidiInput(NoteOn());
        await AssertNoFiresAsync(h); // never armed

        h.Vm.TriggersArmed = true;
        h.Vm.IsCueEditMode = true;
        h.Service.OnMidiInput(NoteOn());
        await AssertNoFiresAsync(h); // edit mode suppresses firing

        h.Vm.IsCueEditMode = false;
        h.Service.OnMidiInput(NoteOn());
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Midi_RetriggerWindow_SuppressesChatter()
    {
        var h = BuildHarness([MidiNoteBinding()]);
        h.Service.OnMidiInput(NoteOn());
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // 100 ms later: chatter, suppressed.
        h.Clock.Now += TimeSpan.FromMilliseconds(100);
        h.Service.OnMidiInput(NoteOn());
        await AssertNoFiresAsync(h);

        // Past the 250 ms window: a deliberate re-fire.
        h.Clock.Now += TimeSpan.FromMilliseconds(200);
        h.Service.OnMidiInput(NoteOn());
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Midi_DisabledBinding_IsInert()
    {
        var binding = MidiNoteBinding();
        binding.Enabled = false;
        var h = BuildHarness([binding]);
        h.Service.OnMidiInput(NoteOn());
        await AssertNoFiresAsync(h);

        // Re-enabling through the drawer row makes it live again.
        h.CueVm.Triggers[0].Enabled = true;
        h.Service.OnMidiInput(NoteOn());
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Midi_DeviceAndChannelWildcards_AndSpecificMismatchesStaySilent()
    {
        // Null device + null channel = any: fires for arbitrary source/channel.
        var h = BuildHarness([MidiNoteBinding()]);
        h.Service.OnMidiInput(NoteOn(channel: 7, device: "Some Random Port"));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Specific channel + device substring: wrong channel or wrong device stays silent.
        var specific = new CueTriggerBinding
        {
            Kind = CueTriggerKind.Midi,
            MidiMessageType = CueTriggerMidiMessageType.NoteOn,
            MidiNumber = 60,
            MidiChannel = 3,
            MidiDeviceName = "apc",
        };
        var h2 = BuildHarness([specific]);
        h2.Service.OnMidiInput(NoteOn(channel: 4, device: "APC mini"));
        await AssertNoFiresAsync(h2);
        h2.Clock.Now += TimeSpan.FromSeconds(1);
        h2.Service.OnMidiInput(NoteOn(channel: 3, device: "Launchpad"));
        await AssertNoFiresAsync(h2);
        h2.Clock.Now += TimeSpan.FromSeconds(1);
        h2.Service.OnMidiInput(NoteOn(channel: 3, device: "APC mini")); // case-insensitive substring
        Assert.Equal(h2.CueId, await NextFiredAsync(h2));
    }

    [Fact]
    public async Task Midi_ValueThreshold_AndVelocityZeroNoteOn()
    {
        var binding = MidiNoteBinding();
        binding.MidiValueMin = 64;
        var h = BuildHarness([binding]);

        h.Service.OnMidiInput(NoteOn(velocity: 10));
        await AssertNoFiresAsync(h); // below threshold

        h.Service.OnMidiInput(NoteOn(velocity: 0));
        await AssertNoFiresAsync(h); // velocity-0 NoteOn is a NoteOff on the wire - never fires

        h.Service.OnMidiInput(NoteOn(velocity: 100));
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    // ---- Service runtime: OSC ----

    [Fact]
    public async Task Osc_AddressMatch_Fires_AndArgumentMatchFilters()
    {
        var h = BuildHarness(
        [
            new CueTriggerBinding { Kind = CueTriggerKind.Osc, OscAddress = "/haplay/cue/1" },
        ]);
        h.Service.OnOscInput(new CueOscTriggerInput("/haplay/other", null));
        await AssertNoFiresAsync(h);
        h.Service.OnOscInput(new CueOscTriggerInput("/haplay/cue/1", null));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        var h2 = BuildHarness(
        [
            new CueTriggerBinding { Kind = CueTriggerKind.Osc, OscAddress = "/go", OscArgument = "1" },
        ]);
        h2.Service.OnOscInput(new CueOscTriggerInput("/go", "0")); // button-up payload
        await AssertNoFiresAsync(h2);
        h2.Clock.Now += TimeSpan.FromSeconds(1);
        h2.Service.OnOscInput(new CueOscTriggerInput("/go", "1"));
        Assert.Equal(h2.CueId, await NextFiredAsync(h2));
    }

    // ---- Service runtime: hotkey bindings ----

    [Fact]
    public async Task Hotkey_Binding_FiresThroughService_AndRespectsGate()
    {
        var h = BuildHarness(
        [
            new CueTriggerBinding { Kind = CueTriggerKind.Hotkey, HotkeyGesture = "Ctrl+F5" },
        ]);
        var key = new KeyEventArgs { Key = Key.F5, KeyModifiers = KeyModifiers.Control };

        Assert.True(h.Service.TryHandleHotkey(key));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Non-matching gesture is not claimed.
        Assert.False(h.Service.TryHandleHotkey(new KeyEventArgs { Key = Key.F6, KeyModifiers = KeyModifiers.None }));

        // Disarming closes the gate: the gesture is no longer claimed (nor fired).
        h.Vm.TriggersArmed = false;
        h.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.False(h.Service.TryHandleHotkey(key));
        await AssertNoFiresAsync(h);
    }

    [Fact]
    public void HotkeyBinding_TransportProfileClash_RejectedAtEditTime()
    {
        var vm = new CuePlayerViewModel(); // default profile: GO = Space, Panic = Ctrl+Esc, ...
        var row = new CueTriggerBindingViewModel
        {
            Kind = CueTriggerKind.Hotkey,
            HotkeyConflictProbe = vm.ProbeTriggerHotkeyConflict,
        };

        row.HotkeyGestureText = "Space"; // the GO key - vetoed
        Assert.Null(row.HotkeyGesture);
        Assert.Contains("Space", vm.StatusMessage);

        row.HotkeyGestureText = "Ctrl+Esc"; // the panic key - vetoed
        Assert.Null(row.HotkeyGesture);

        row.HotkeyGestureText = "Ctrl+F5"; // free gesture - accepted
        Assert.Equal("Ctrl+F5", row.HotkeyGesture);

        row.HotkeyGestureText = "not a key"; // unparseable - last valid retained
        Assert.Equal("Ctrl+F5", row.HotkeyGesture);
    }

    // ---- MIDI learn ----

    [Fact]
    public async Task MidiLearn_CapturesNextMessage_WithoutFiring()
    {
        var h = BuildHarness([MidiNoteBinding()]);
        var row = h.CueVm.Triggers[0];
        h.Vm.MidiLearnTarget = row;
        Assert.True(row.IsMidiLearning);

        h.Service.OnMidiInput(new CueMidiTriggerInput(
            "X-Touch MINI", null, CueTriggerMidiMessageType.ControlChange, 5, 16, 90));

        Assert.Equal(CueTriggerMidiMessageType.ControlChange, row.MidiMessageType);
        Assert.Equal(16, row.MidiNumber);
        Assert.Equal(5, row.MidiChannel);
        Assert.Equal("X-Touch MINI", row.MidiDeviceName);
        Assert.Null(h.Vm.MidiLearnTarget); // one-shot capture
        Assert.False(row.IsMidiLearning);
        await AssertNoFiresAsync(h); // learn consumes the message - it never fires anything
    }

    // ---- ControlMonitorRecord mappers ----

    [Fact]
    public void TryMapMidiRecord_MapsModeledKinds_AndRejectsOthers()
    {
        var noteOn = new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.MIDI,
            Endpoint = "APC mini",
            DeviceKey = "apc",
            MIDIChannel = 2,
            MIDIMessageType = ControlMIDIMessageType.NoteOn,
            MIDINote = 60,
            MIDIValue = 100,
        };
        Assert.True(CueTriggerService.TryMapMidiRecord(noteOn, out var mappedNote));
        Assert.Equal(new CueMidiTriggerInput("APC mini", "apc", CueTriggerMidiMessageType.NoteOn, 2, 60, 100), mappedNote);

        var cc = noteOn with
        {
            MIDIMessageType = ControlMIDIMessageType.ControlChange,
            MIDINote = null,
            MIDIController = 16,
            MIDIValue = 42,
        };
        Assert.True(CueTriggerService.TryMapMidiRecord(cc, out var mappedCc));
        Assert.Equal(CueTriggerMidiMessageType.ControlChange, mappedCc.Type);
        Assert.Equal(16, mappedCc.Number);
        Assert.Equal(42, mappedCc.Value);

        // ProgramChange carries the program in MIDIValue (the payload's Value field).
        var pc = noteOn with
        {
            MIDIMessageType = ControlMIDIMessageType.ProgramChange,
            MIDINote = null,
            MIDIValue = 7,
        };
        Assert.True(CueTriggerService.TryMapMidiRecord(pc, out var mappedPc));
        Assert.Equal(CueTriggerMidiMessageType.ProgramChange, mappedPc.Type);
        Assert.Equal(7, mappedPc.Number);
        Assert.Equal(0, mappedPc.Value);

        // Unmodeled kinds (pitch bend, clock, ...) never map.
        Assert.False(CueTriggerService.TryMapMidiRecord(
            noteOn with { MIDIMessageType = ControlMIDIMessageType.PitchBend }, out _));
    }

    [Fact]
    public void TryMapOscRecord_CarriesAddressAndFirstArgumentText()
    {
        var record = new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.OSC,
            Address = "/haplay/cue/1",
            OSCArguments =
            [
                new ControlMonitorOSCArgumentRecord { Kind = "Int32", IntegerValue = 1 },
                new ControlMonitorOSCArgumentRecord { Kind = "String", StringValue = "ignored-second" },
            ],
        };
        Assert.True(CueTriggerService.TryMapOscRecord(record, out var mapped));
        Assert.Equal("/haplay/cue/1", mapped.Address);
        Assert.Equal("1", mapped.FirstArgumentText);

        var floatArg = record with
        {
            OSCArguments = [new ControlMonitorOSCArgumentRecord { Kind = "Float32", FloatValue = 1 }],
        };
        Assert.True(CueTriggerService.TryMapOscRecord(floatArg, out var mappedFloat));
        Assert.Equal("1", mappedFloat.FirstArgumentText);

        var noArgs = record with { OSCArguments = [] };
        Assert.True(CueTriggerService.TryMapOscRecord(noArgs, out var mappedBare));
        Assert.Null(mappedBare.FirstArgumentText);

        Assert.False(CueTriggerService.TryMapOscRecord(record with { Address = null }, out _));
    }
}
