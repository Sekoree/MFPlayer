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
/// an injected clock - fires on MIDI note / OSC address / hotkey match, the always-on
/// <c>OnControlInput</c> entry point (direction filtering + MIDI/OSC routing), the ControlChange
/// rising-edge latch, the TriggersArmed + edit-mode gates, the 250 ms per-binding retrigger guard,
/// wildcard device/channel matching, disabled bindings staying inert, sequential firing when one
/// message matches several cues, MIDI learn capture, the hotkey edit-time veto, and the
/// ControlMonitorRecord mappers.</summary>
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

    /// <summary>Two media cues inside a group, both carrying the SAME binding - what Ctrl+D
    /// (duplicate) produces, since a duplicate clones the original's Triggers. Grouped media fires
    /// through <c>FireGroupedMediaIndependentlyAsync</c>, which AWAITS its executor, so the gate
    /// below makes "did the second fire start before the first finished?" observable without any
    /// wall-clock sleeping.</summary>
    private sealed record GroupedHarness(
        CuePlayerViewModel Vm,
        CueTriggerService Service,
        TestClock Clock,
        Guid FirstCueId,
        Guid SecondCueId,
        ConcurrentQueue<Guid> Started,
        ConcurrentQueue<Guid> Completed,
        SemaphoreSlim CompletedSignal,
        TaskCompletionSource FirstCueGate);

    private static GroupedHarness BuildGroupedHarness(CueTriggerBinding binding)
    {
        var first = new MediaCueNode
        {
            Number = "1.1",
            Label = "Copy A",
            Source = new FilePlaylistItem("/tmp/a.wav"),
            Triggers = [binding],
        };
        var second = new MediaCueNode
        {
            Number = "1.2",
            Label = "Copy B",
            Source = new FilePlaylistItem("/tmp/b.wav"),
            Triggers = [binding],
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists(
        [
            new CueList
            {
                Nodes = [new CueGroupNode { Number = "1", Label = "Set", Children = [first, second] }],
            },
        ]);
        vm.IsCueEditMode = false;

        var started = new ConcurrentQueue<Guid>();
        var completed = new ConcurrentQueue<Guid>();
        var signal = new SemaphoreSlim(0);
        var gate = new TaskCompletionSource();
        vm.MediaCueIndependentExecutor = async (m, _) =>
        {
            started.Enqueue(m.Id);
            if (m.Id == first.Id)
                await gate.Task;
            vm.OnCueStarted(m.Id);
            completed.Enqueue(m.Id);
            signal.Release();
            return null;
        };

        var clock = new TestClock();
        var service = new CueTriggerService(vm, () => clock.Now);
        vm.TriggersArmed = true;
        return new GroupedHarness(
            vm, service, clock, first.Id, second.Id, started, completed, signal, gate);
    }

    private static async Task<Guid> NextFiredAsync(Harness h)
    {
        Assert.True(await h.FireSignal.WaitAsync(TimeSpan.FromSeconds(5)), "timed out waiting for a trigger fire");
        Assert.True(h.Fired.TryDequeue(out var id));
        return id;
    }

    /// <summary>Deterministic negative assertion. The input entry points report SYNCHRONOUSLY
    /// whether the sweep decided to fire (the sweep runs inline; only the cue's media execution is
    /// dispatched), so there is nothing to wait for - the old <c>Task.Delay(250)</c> per negative
    /// case added ~2 s of pure sleep per run and went flaky whenever the box was loaded.</summary>
    private static void AssertNoFire(Harness h, bool fired)
    {
        Assert.False(fired, "the trigger sweep fired when it should have stayed silent");
        Assert.Empty(h.Fired);
    }

    private static CueTriggerBinding MidiNoteBinding(int note = 60) => new()
    {
        Kind = CueTriggerKind.Midi,
        MidiMessageType = CueTriggerMidiMessageType.NoteOn,
        MidiNumber = note,
    };

    private static CueTriggerBinding MidiCcBinding(int controller = 16, int valueMin = 0) => new()
    {
        Kind = CueTriggerKind.Midi,
        MidiMessageType = CueTriggerMidiMessageType.ControlChange,
        MidiNumber = controller,
        MidiValueMin = valueMin,
    };

    private static CueMidiTriggerInput NoteOn(
        int note = 60, int velocity = 100, int channel = 1, string? device = "APC mini") =>
        new(device, null, CueTriggerMidiMessageType.NoteOn, channel, note, velocity);

    private static CueMidiTriggerInput ControlChange(
        int value, int controller = 16, int channel = 1, string? device = "X-Touch MINI") =>
        new(device, null, CueTriggerMidiMessageType.ControlChange, channel, controller, value);

    private static ControlMonitorRecord MidiRecord(int note = 60, int velocity = 100) => new()
    {
        Direction = ControlMonitorDirection.Input,
        Protocol = ControlMonitorProtocol.MIDI,
        Endpoint = "APC mini",
        DeviceKey = "apc",
        MIDIChannel = 1,
        MIDIMessageType = ControlMIDIMessageType.NoteOn,
        MIDINote = note,
        MIDIValue = velocity,
    };

    // ---- Service runtime: MIDI ----

    [Fact]
    public async Task Midi_NoteOn_FiresMatchingCue()
    {
        var h = BuildHarness([MidiNoteBinding()]);
        Assert.True(h.Service.OnMidiInput(NoteOn()));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Wrong note number stays silent.
        h.Clock.Now += TimeSpan.FromSeconds(1);
        AssertNoFire(h, h.Service.OnMidiInput(NoteOn(note: 61)));
    }

    [Fact]
    public async Task Midi_RespectsArmedGate_AndEditMode()
    {
        var h = BuildHarness([MidiNoteBinding()], arm: false);
        AssertNoFire(h, h.Service.OnMidiInput(NoteOn())); // never armed

        h.Vm.TriggersArmed = true;
        h.Vm.IsCueEditMode = true;
        AssertNoFire(h, h.Service.OnMidiInput(NoteOn())); // edit mode suppresses firing

        h.Vm.IsCueEditMode = false;
        Assert.True(h.Service.OnMidiInput(NoteOn()));
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Midi_RetriggerWindow_SuppressesChatter()
    {
        var h = BuildHarness([MidiNoteBinding()]);
        Assert.True(h.Service.OnMidiInput(NoteOn()));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // 100 ms later: chatter, suppressed.
        h.Clock.Now += TimeSpan.FromMilliseconds(100);
        AssertNoFire(h, h.Service.OnMidiInput(NoteOn()));

        // Past the 250 ms window: a deliberate re-fire. NoteOn is a discrete event - unlike a
        // control change it never latches, so every fresh press counts.
        h.Clock.Now += TimeSpan.FromMilliseconds(200);
        Assert.True(h.Service.OnMidiInput(NoteOn()));
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Midi_DisabledBinding_IsInert()
    {
        var binding = MidiNoteBinding();
        binding.Enabled = false;
        var h = BuildHarness([binding]);
        AssertNoFire(h, h.Service.OnMidiInput(NoteOn()));

        // Re-enabling through the drawer row makes it live again.
        h.CueVm.Triggers[0].Enabled = true;
        Assert.True(h.Service.OnMidiInput(NoteOn()));
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Midi_DeviceAndChannelWildcards_AndSpecificMismatchesStaySilent()
    {
        // Null device + null channel = any: fires for arbitrary source/channel.
        var h = BuildHarness([MidiNoteBinding()]);
        Assert.True(h.Service.OnMidiInput(NoteOn(channel: 7, device: "Some Random Port")));
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
        AssertNoFire(h2, h2.Service.OnMidiInput(NoteOn(channel: 4, device: "APC mini")));
        h2.Clock.Now += TimeSpan.FromSeconds(1);
        AssertNoFire(h2, h2.Service.OnMidiInput(NoteOn(channel: 3, device: "Launchpad")));
        h2.Clock.Now += TimeSpan.FromSeconds(1);
        // Case-insensitive substring.
        Assert.True(h2.Service.OnMidiInput(NoteOn(channel: 3, device: "APC mini")));
        Assert.Equal(h2.CueId, await NextFiredAsync(h2));
    }

    [Fact]
    public async Task Midi_ValueThreshold_AndVelocityZeroNoteOn()
    {
        var binding = MidiNoteBinding();
        binding.MidiValueMin = 64;
        var h = BuildHarness([binding]);

        AssertNoFire(h, h.Service.OnMidiInput(NoteOn(velocity: 10))); // below threshold
        // Velocity-0 NoteOn is a NoteOff on the wire - never fires.
        AssertNoFire(h, h.Service.OnMidiInput(NoteOn(velocity: 0)));

        Assert.True(h.Service.OnMidiInput(NoteOn(velocity: 100)));
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    // ---- Service runtime: ControlChange rising edge ----

    [Fact]
    public async Task Midi_ControlChangeButton_FiresOnPressOnly_AndReArmsOnRelease()
    {
        // A CC button sends 127 on press and 0 on release. With the default Min of 0 the press must
        // fire and the release must NOT: the pre-fix matcher skipped the value check entirely when
        // Min was 0, so every button held longer than the 250 ms guard fired a SECOND time on its
        // release.
        var h = BuildHarness([MidiCcBinding()]);
        Assert.True(h.Service.OnMidiInput(ControlChange(127)));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        h.Clock.Now += TimeSpan.FromMilliseconds(400); // held well past the retrigger guard
        AssertNoFire(h, h.Service.OnMidiInput(ControlChange(0))); // release

        // The release re-armed the latch, so the next press fires again.
        h.Clock.Now += TimeSpan.FromMilliseconds(400);
        Assert.True(h.Service.OnMidiInput(ControlChange(127)));
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Midi_ControlChangeSweepAboveThreshold_FiresExactlyOnce()
    {
        // Min 64, the operator sweeps a fader 70 → 110 over two seconds: ONE fire on the crossing.
        // Every step here sits well past the 250 ms chatter guard, so only the rising-edge latch can
        // suppress them (the guard alone let this sweep fire ~8 times).
        var h = BuildHarness([MidiCcBinding(valueMin: 64)]);
        AssertNoFire(h, h.Service.OnMidiInput(ControlChange(60))); // below the threshold
        Assert.True(h.Service.OnMidiInput(ControlChange(70)));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        foreach (var value in new[] { 80, 90, 100, 110 })
        {
            h.Clock.Now += TimeSpan.FromMilliseconds(500);
            AssertNoFire(h, h.Service.OnMidiInput(ControlChange(value)));
        }

        // Falling back below the threshold re-arms; the next crossing fires again.
        h.Clock.Now += TimeSpan.FromMilliseconds(500);
        AssertNoFire(h, h.Service.OnMidiInput(ControlChange(10)));
        h.Clock.Now += TimeSpan.FromMilliseconds(500);
        Assert.True(h.Service.OnMidiInput(ControlChange(100)));
        Assert.Equal(h.CueId, await NextFiredAsync(h));
    }

    [Fact]
    public async Task Midi_ProgramChange_StaysDiscrete_AndIgnoresTheValueThreshold()
    {
        // A program change carries no value at all (the program IS the number), so it must never
        // latch and must ignore Min - every message is its own intent.
        var binding = new CueTriggerBinding
        {
            Kind = CueTriggerKind.Midi,
            MidiMessageType = CueTriggerMidiMessageType.ProgramChange,
            MidiNumber = 7,
            MidiValueMin = 64,
        };
        var h = BuildHarness([binding]);
        var program = new CueMidiTriggerInput(
            "APC mini", null, CueTriggerMidiMessageType.ProgramChange, 1, 7, 0);

        Assert.True(h.Service.OnMidiInput(program));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        h.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(h.Service.OnMidiInput(program));
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
        AssertNoFire(h, h.Service.OnOscInput(new CueOscTriggerInput("/haplay/other", null)));
        Assert.True(h.Service.OnOscInput(new CueOscTriggerInput("/haplay/cue/1", null)));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        var h2 = BuildHarness(
        [
            new CueTriggerBinding { Kind = CueTriggerKind.Osc, OscAddress = "/go", OscArgument = "1" },
        ]);
        // Button-up payload.
        AssertNoFire(h2, h2.Service.OnOscInput(new CueOscTriggerInput("/go", "0")));
        h2.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(h2.Service.OnOscInput(new CueOscTriggerInput("/go", "1")));
        Assert.Equal(h2.CueId, await NextFiredAsync(h2));
    }

    // ---- Service runtime: the always-on device input entry point ----

    [Fact]
    public async Task OnControlInput_AcceptsInputAndDropped_RejectsOutputEchoes()
    {
        // The production entry point: ControlInputSession → ControlWorkspaceViewModel →
        // MainViewModel → here, for every configured/enabled device, armed or not.
        var h = BuildHarness([MidiNoteBinding()]);

        Assert.True(h.Service.OnControlInput(MidiRecord()));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // "Dropped" only means no Control-workspace DEVICE mapping matched - the message still
        // physically arrived, which is all a cue trigger needs.
        h.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(h.Service.OnControlInput(
            MidiRecord() with { Direction = ControlMonitorDirection.Dropped }));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        // Output is HaPlay's own echo of what it sent; Internal/Error are not device traffic.
        h.Clock.Now += TimeSpan.FromSeconds(1);
        AssertNoFire(h, h.Service.OnControlInput(
            MidiRecord() with { Direction = ControlMonitorDirection.Output }));
        AssertNoFire(h, h.Service.OnControlInput(
            MidiRecord() with { Direction = ControlMonitorDirection.Internal }));

        // Unmodeled MIDI kinds and non-MIDI/OSC protocols never map to a trigger input.
        AssertNoFire(h, h.Service.OnControlInput(
            MidiRecord() with { MIDIMessageType = ControlMIDIMessageType.PitchBend }));
        AssertNoFire(h, h.Service.OnControlInput(
            MidiRecord() with { Protocol = ControlMonitorProtocol.Script }));
    }

    [Fact]
    public async Task OnControlInput_RoutesOscRecords_ByAddressAndFirstArgument()
    {
        var h = BuildHarness(
        [
            new CueTriggerBinding { Kind = CueTriggerKind.Osc, OscAddress = "/haplay/cue/1", OscArgument = "1" },
        ]);
        var record = new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.OSC,
            Address = "/haplay/cue/1",
            OSCArguments = [new ControlMonitorOSCArgumentRecord { Kind = "Int32", IntegerValue = 0 }],
        };

        AssertNoFire(h, h.Service.OnControlInput(record)); // button-up payload

        var down = record with
        {
            OSCArguments = [new ControlMonitorOSCArgumentRecord { Kind = "Int32", IntegerValue = 1 }],
        };
        Assert.True(h.Service.OnControlInput(down));
        Assert.Equal(h.CueId, await NextFiredAsync(h));

        h.Clock.Now += TimeSpan.FromSeconds(1);
        AssertNoFire(h, h.Service.OnControlInput(down with { Address = "/haplay/other" }));
        AssertNoFire(h, h.Service.OnControlInput(
            down with { Direction = ControlMonitorDirection.Output }));
    }

    [Fact]
    public void OnControlInput_RespectsTheArmedGate()
    {
        var h = BuildHarness([MidiNoteBinding()], arm: false);
        AssertNoFire(h, h.Service.OnControlInput(MidiRecord()));
    }

    // ---- Service runtime: one message matching several cues ----

    [Fact]
    public async Task Trigger_MatchingSeveralCues_FiresThemSequentially()
    {
        // Ctrl+D clones a cue's Triggers, so both copies answer the same note. They must fire ONE
        // AT A TIME (the operator's own multi-selection GO semantics): the pre-fix loop launched
        // every match fire-and-forget, and each fire's GoCore opened with CancelTransportRun(),
        // killing the previous match's plan (pre-waits, group members, auto-follow).
        var h = BuildGroupedHarness(MidiNoteBinding());

        Assert.True(h.Service.OnMidiInput(NoteOn()));

        // The first fire is still inside its executor, so the second has NOT been started yet.
        Assert.Equal([h.FirstCueId], h.Started.ToArray());

        h.FirstCueGate.SetResult();
        Assert.True(await h.CompletedSignal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await h.CompletedSignal.WaitAsync(TimeSpan.FromSeconds(5)));

        // Both cues ran to completion, in tree order - neither cancelled the other.
        Assert.Equal([h.FirstCueId, h.SecondCueId], h.Completed.ToArray());
        Assert.Equal([h.FirstCueId, h.SecondCueId], h.Started.ToArray());
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
        AssertNoFire(h, h.Service.TryHandleHotkey(key));
    }

    [Fact]
    public void LegacyPerCueHotkey_HonorsTheSameArmedGate_AsAHotkeyBinding()
    {
        // The drawer's legacy per-cue Hotkey field and a Kind=Hotkey binding are the same feature
        // with two editors, so the master Triggers toggle must gate both. It used to fire on
        // "not edit mode" alone, which made arming look like it only disabled half the keyboard.
        var h = BuildHarness([], arm: false);
        h.CueVm.HotkeyGesture = "Ctrl+F5";
        var key = new KeyEventArgs { Key = Key.F5, KeyModifiers = KeyModifiers.Control };

        Assert.False(h.Vm.TryFireCueHotkey(key)); // disarmed
        Assert.Empty(h.Fired);

        h.Vm.TriggersArmed = true;
        h.Vm.IsCueEditMode = true;
        Assert.False(h.Vm.TryFireCueHotkey(key)); // edit mode
        Assert.Empty(h.Fired);

        h.Vm.IsCueEditMode = false;
        Assert.True(h.Vm.TryFireCueHotkey(key));
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

        // The view ALSO hard-claims Ctrl+P (toggle preview) and Ctrl+F (focus search) before it ever
        // reaches the trigger probe, so authoring those would create a binding that can never fire.
        // Both handlers test KeyModifiers.HasFlag(Control), so the extra-modifier forms are taken too.
        row.HotkeyGestureText = "Ctrl+P";
        Assert.Null(row.HotkeyGesture);
        Assert.Contains("Ctrl+P", vm.StatusMessage);
        row.HotkeyGestureText = "Ctrl+F";
        Assert.Null(row.HotkeyGesture);
        row.HotkeyGestureText = "Ctrl+Shift+P";
        Assert.Null(row.HotkeyGesture);

        row.HotkeyGestureText = "Ctrl+F5"; // free gesture - accepted
        Assert.Equal("Ctrl+F5", row.HotkeyGesture);

        row.HotkeyGestureText = "Alt+P"; // no Control - not one of the view's claims
        Assert.Equal("Alt+P", row.HotkeyGesture);

        row.HotkeyGestureText = "not a key"; // unparseable - last valid retained
        Assert.Equal("Alt+P", row.HotkeyGesture);
    }

    // ---- Unresolvable targets ----

    [Fact]
    public void Trigger_OnACueThatResolvesToNothing_Fails_WithoutFiringTheStandbyCue()
    {
        // An empty group answering the note used to be DROPPED by the fire path's resolution, which
        // then fell through to StandbyCueNode - the operator's note started a completely different
        // cue (and POST /api/v1/cues/{ref}/go answered 200 while it did).
        var empty = new CueGroupNode
        {
            Number = "1",
            Label = "Empty group",
            Children = [],
            Triggers = [MidiNoteBinding()],
        };
        var somebodyElse = new MediaCueNode
        {
            Number = "2",
            Label = "Not what you asked for",
            Source = new FilePlaylistItem("/tmp/other.wav"),
        };
        var vm = new CuePlayerViewModel();
        vm.ApplyCueLists([new CueList { Nodes = [empty, somebodyElse] }]);
        vm.IsCueEditMode = false;
        var fired = new ConcurrentQueue<Guid>();
        vm.MediaCueExecutor = (m, _) => { fired.Enqueue(m.Id); return Task.FromResult<string?>(null); };
        var clock = new TestClock();
        var service = new CueTriggerService(vm, () => clock.Now);
        vm.TriggersArmed = true;
        vm.StandbyCueNode = vm.SelectedCueList!.Nodes[1];

        Assert.True(service.OnMidiInput(NoteOn())); // the binding matched and was handled...

        Assert.Empty(fired); // ...but nothing played, least of all the standby cue
        Assert.Null(vm.CurrentCueNode);
        Assert.Same(vm.SelectedCueList!.Nodes[1], vm.StandbyCueNode); // standby is not consumed
        Assert.Contains("Empty group", vm.StatusMessage);
        Assert.Contains("Cannot fire", vm.StatusMessage);
    }

    // ---- MIDI learn ----

    [Fact]
    public void MidiLearn_CapturesNextMessage_WithoutFiring()
    {
        var h = BuildHarness([MidiNoteBinding()]);
        var row = h.CueVm.Triggers[0];
        h.Vm.MidiLearnTarget = row;
        Assert.True(row.IsMidiLearning);

        // Learn consumes the message - it never fires anything.
        AssertNoFire(h, h.Service.OnMidiInput(new CueMidiTriggerInput(
            "X-Touch MINI", null, CueTriggerMidiMessageType.ControlChange, 5, 16, 90)));

        Assert.Equal(CueTriggerMidiMessageType.ControlChange, row.MidiMessageType);
        Assert.Equal(16, row.MidiNumber);
        Assert.Equal(5, row.MidiChannel);
        Assert.Equal("X-Touch MINI", row.MidiDeviceName);
        Assert.Null(h.Vm.MidiLearnTarget); // one-shot capture
        Assert.False(row.IsMidiLearning);
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
