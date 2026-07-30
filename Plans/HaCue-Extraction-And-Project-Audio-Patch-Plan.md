# HaCue extraction and project audio patch plan

Status: proposed plan — reviewed against the code 2026-07-30; four architecture decisions resolved,
six new questions open (see "Still open after the 2026-07-30 review"), four enhancements added to
first-release scope (provisional decision 11)  
Date: 2026-07-30  
Scope: split the Cue Player out of HaPlay into a dedicated HaCue application and replace per-cue
direct-to-device audio routing with a persistent project-level logical-channel patch. Companion
document: `Plans/HaCue-Feature-Ideas.md` (enhancement backlog, not first-release scope).

## Executive recommendation

HaCue should become a separate application with its own project format, application shell, tests,
recovery files, settings, and release artifact. It should consume the media framework directly; it
must not reference the HaPlay application assembly.

The split should not be implemented as a large file copy. First make the cue domain and the output
runtime real services while HaPlay still hosts them. Then add the project audio patch to the
framework/session boundary. Only after those seams are running in HaPlay should the cue files move
to HaCue and the new desktop head be introduced.

The audio redesign should use two explicit matrices:

```text
media stream channels ── cue send matrix ──► named logical project channels
named logical channels ─ project patch ────► real output-line channels
```

This is the important behavioral change. A cue no longer knows that “source L goes to PortAudio
line X, channel 3.” It knows that “source L goes to logical channel Main L.” The project patch
decides which physical, NDI, recording, or streaming channels receive Main L. Both matrices support
many-to-many routing and per-cell gain/mute.

The framework already contains most of the DSP building blocks:

- `AudioRouter.ApplyMatrix` supports a full source-to-output gain matrix and atomically reconciles
  cells.
- `ChannelMap` and the router mix paths support duplication, omission, summing, and wide channel
  layouts.
- `SharedAudioOutput` provides safe multi-client fan-in and an audible device-derived client clock.
- `AudioBus` provides a buffered output/source bridge, although stacking it naïvely would add too
  much latency.

The missing component is a persistent topology owner—called `AudioPatchBay` in this plan—that
generalizes the useful ownership/clocking behavior of `SharedAudioOutput` to several real outputs
and several playback clients. This is a topology/orchestration rewrite, not a new sample-mixing
algorithm.

## Resolved decisions (2026-07-30 review)

These four were open in the first draft and are now settled. Each is expanded in the section named.

1. **Mix topology: program sum, not per-pair matrices** (see `AudioPatchBay` responsibilities).
   Producers sum into one V-wide logical program bus inside the router's chunk pass, then one dense
   V×R pass runs per terminal — `P + R` matrix passes, not `P × R`. The accumulator is per-chunk
   scratch, not a queue, so it adds no latency. This is what makes the stated performance maximums
   reachable at all; the per-pair form is ~5× over the whole chunk deadline at those sizes.
2. **NDI cue-audio routing is fixed in Phase 1, before characterization** (see "NDI cue audio never
   reaches the NDI carrier"). Characterization exists to protect real behavior, and routing an NDI
   cue to the default speakers is not behavior worth protecting.
3. **The bay owns its audio lines exclusively** (see `AudioPatchBay` responsibilities). For lines
   HaCue patches, `AudioPatchBay` replaces `SharedAudioOutput` and owns the terminal directly;
   program audio and preview both enter through the bay. `HaOutput` therefore needs a per-line
   raw-terminal acquisition mode alongside the client-lease mode HaPlay keeps using.
4. **Three shared app-support libraries, not one** (see "What becomes shared app support"):
   `HaOutput`, a shared media-source model/dialog library, and a shared control-input library.

## Provisional product decisions

These recommendations let implementation proceed. The questions that still need an owner decision
are collected at the end.

1. HaCue owns one open project at a time and writes a new HaCue project format.
2. Existing `.haplayproj`, `.haplaycues`, and `.haplaycuelists` files are import formats, not files
   jointly edited by HaPlay and HaCue.
3. A logical audio channel is an atomic mono channel with a stable ID and a user-visible name.
   Stereo pairs or surround groups are optional presentation metadata, not routing primitives.
4. The first audio implementation routes the channels of the one selected audio stream. Decoding
   several container audio streams/stems simultaneously is a separate feature.
5. All audio-capable output lines may be patch destinations: PortAudio, NDI, file recording, and
   live streaming. Video-only lines remain outside the audio patch.
6. New projects start with `Main L` and `Main R` logical channels and a 48 kHz project mix rate.
   The operator can add, rename, reorder, or remove logical channels.
7. One real output line is selected as the program clock master. Secondary clocked outputs use the
   framework's existing adaptive-rate policy. A project with no real audio output gets a headless
   clocked discard output so video-only cues still advance correctly.
8. Preview/audition remains a monitoring path. It bypasses the program master fader and project
   patch, matching the current `SoundingSourceRegistry` policy.
9. HaCue includes cue action targets, MIDI/OSC/hotkey triggers, scheduling, remote control,
   projectM cue layers, timeline editing, composition/output mapping, and cue preview. HaPlay's
   deck workspace and soundboard are not part of HaCue.
10. Physical output effects stay after the project patch. Logical-bus insert effects are a later
    feature; they should not be smuggled into the first patch implementation.
11. Four enhancements are first-release scope because they are far cheaper to build into the new app
    than to retrofit onto it: an edit-command **undo journal** (constrains the project format),
    **show mode** as the launch state (an app-shell decision), **patch snapshots and patch cues**
    (only meaningful once a persistent patch exists), and **preflight/relink/consolidate** (a
    project-level concern). Each has its own section below. The wider enhancement backlog lives in
    `Plans/HaCue-Feature-Ideas.md` and is explicitly *not* first-release scope.

## Current-state findings

### HaPlay is currently the composition root for several products

`UI/HaPlay/HaPlay.csproj` references almost every media, source, presentation, encoding, control,
MIDI, and OSC module. `App.axaml.cs` builds one static `MediaRuntime`, and `MainViewModel` wires
players, cues, soundboard, control, outputs, project persistence, recovery, remote control,
scheduling, triggers, and endpoint health.

The cue surface is large enough to be its own product:

- `CuePlayerViewModel` is 8,198 lines across its root and 14 partials.
- `CueShowSessionCoordinator` is 1,963 lines.
- `CuePlayerView.axaml` is 1,674 lines.
- 91 HaPlay `.cs` files and 10 `.axaml` files reference the cue domain, plus 50 HaPlay test files.
  (The first draft said 69/40; phase sizing should use these numbers.)

The partial-file splits improved navigation, but they did not create an application or ownership
boundary.

### The cue runtime is coupled to non-cue HaPlay services

`CueShowSessionCoordinator` currently owns or coordinates all of the following:

- the cue `ShowSession`;
- cue composition-to-output video leases;
- output reconfiguration ordering;
- projectM source lifetime;
- merged-list document reloads;
- cue progress and natural-end handling;
- per-cue audio output acquisition;
- soundboard voice transport and polling.

Its constructor takes concrete `CuePlayerViewModel`, `SoundboardWorkspaceViewModel`, and
`OutputManagementViewModel` instances. The soundboard dependency is especially useful evidence:
the first extraction step should split cue-session hosting from soundboard-session hosting rather
than carry the soundboard into HaCue accidentally.

### Output management mixes reusable runtime ownership with HaPlay UI

`OutputManagementViewModel` is 1,869 lines and is simultaneously:

- an output-definition collection and editor;
- a persistent PortAudio/NDI/local-video/encode runtime owner;
- an audio/video lease service;
- an effect-wrapper factory;
- a reconfiguration event bus;
- an output health aggregator;
- a recording/streaming command surface.

Both HaPlay and HaCue need output runtime ownership, but they should not share a giant application
ViewModel. The runtime catalog and its immutable health snapshots should be extracted from the
ViewModel. Each app may then provide an appropriately scoped I/O UI over the same service.

### Cue persistence has two source-of-truth layers

`HaPlayProject` is the editable app document. It includes outputs, players, cue lists, soundboards,
action endpoints, and control configuration. Per-list `ShowDocument` files are generated sidecars
through `HaPlayShowMapper`; they are execution artifacts, not complete editable cue documents.

That distinction should remain:

- `HaCueProject` is the editable source of truth.
- `ShowDocument` remains a framework/runtime document compiled from it.
- HaCue view state stays in a machine-local sidecar.

Making `ShowDocument` the HaCue editor document would lose action cues, jump cues, visualizer cues,
playlist/group behavior, schedules, triggers, and other host-level behavior that does not currently
map into `ShowDocument`.

### Audio routing is direct-to-physical today

The current persisted cue route is:

```text
CueAudioRoute
  SourceChannel
  OutputLineId
  OutputChannel
  GainDb
  Muted
```

`HaPlayShowMapper.MapAudioRoutes` groups these routes per output line and emits
`ShowClipAudioRoute` records containing a device token, sample rate, channel map or gain matrix.
When a clip commits, `ShowSession` opens/acquires each real output and attaches it directly to that
clip's `MediaPlayer.AudioRouter`.

Consequences:

- every cue contains physical topology;
- changing the house patch requires editing/recompiling cue routes;
- a logical program feed cannot be named once and fanned to several devices;
- output acquisition, device format, matrix realization, fade level composition, and transport
  commit all meet inside `ShowSession.CommitClipAsync`;
- the project's real-output topology is recreated independently for every active voice.

### NDI cue audio never reaches the NDI carrier (existing defect)

The cue route editor offers NDI audio-capable lines
(`CuePlayerViewModel.cs:191-197` adds any `NDIOutputDefinition` whose `StreamMode` is not
`VideoOnly`), but the mapper cannot emit a device token for one:

- `HaPlayShowMapper.MapAudioRoutes`'s `deviceId` switch has no NDI case
  (`HaPlayShowMapper.cs:381-390`, falls through to `_ => null`);
- its `minChannels` switch has no NDI case either (`:365-375`), so the line's declared width is lost;
- `ShowSession.TryAttachRouteOutput` then resolves `deviceId ?? ResolveFallbackOutputDeviceId()`
  (`ShowSession.cs:222`);
- `CueShowSessionCoordinator.BuildCueAudioLease` only parses `portaudio-line:` and `file-audio:`
  (`:51-68`).

Net effect: a cue routed to an NDI line plays out of the **default hardware device**. The plumbing
already exists — `OutputAudioRouteDeviceIds.Ndi()` is defined and the media-player deck uses it
(`MediaPlayerViewModel.ShowSession.cs:940`) — it was simply never wired for cues.

This is fixed in Phase 1, before the golden fixtures are recorded (resolved decision 2). Otherwise
Phase 0 characterization pins the defect as required behavior and every later "equivalent effective
source-to-real matrix" assertion has to carve out an exception for it.

### A dead half-built version of this feature already exists

`S.Media.Session/ClipAudioOutputRuntime.cs` (310 lines) is documented as "owns one `AudioRouter`
feeding one physical `IAudioOutput`, and lets multiple cue clips add/remove routed sources" — the
per-output half of what `AudioPatchBay` needs. It is referenced by nothing outside itself.

Phase 0 must either delete it or declare it the seed of the bay. Leaving it in place while building
a second thing with the same responsibility is how the next reviewer loses a day.

### The router cannot host an output at a rate other than its own

`AudioRouter.AddOutput` throws when `output.Format.SampleRate != _sampleRate`
(`AudioRouter.cs:366-368`), and the router explicitly never resamples outputs. A fixed project mix
rate therefore requires every non-matching terminal to be wrapped — `ResamplingAudioOutput.Wrap`
does preserve `IClockedOutput`/`IPlaybackClock`/`IAudioOutputLatency` and forwards `EpochId`, so the
wrapper is viable, but it lives in `S.Media.Decode.FFmpeg`, not `S.Media.Routing`.

Two consequences for the design:

- a neutral `AudioPatchBay` needs an injected resampler factory (the same shape as
  `AudioRouter.ResamplerFactory`, wired from the media registry) rather than a hard dependency;
- `ResamplingAudioOutput.SubmitToOutputLatency` returns `AudioOutputLatency.Of(Inner)` (`:66`) and
  does **not** add the swresample internal delay, so a resampled terminal under-reports its own
  latency. That directly conflicts with the clock requirement that every added queue be accounted
  for, which is why the clock master must run natively at the project rate (see "Clock policy").

### Preview opens its own device and would collide with a bay-owned terminal

`VoicePlayer.PreviewCueAsync` calls
`_audioBackend.CreateOutput(previewDeviceId ?? _resolveFallbackDeviceId(), new AudioFormat(rate, 2))`
(`:182`) — it bypasses the host `audioOutputFactory` entirely, is hardcoded to two channels, and
falls back to the default device. Today that survives because program audio reaches a line through
a `SharedAudioOutput` client lease, so two OS nodes on one device are merely wasteful.

Once the bay owns a terminal exclusively (resolved decision 3), previewing to the same physical
device double-opens it. The monitoring-input seam is therefore a required Phase 3 item, not a
polish item, and `HaCueProject.PreviewAudioEndpointId` must be an **output-line ID** rather than a
backend device id.

### An older virtual-channel field exists, but it is intentionally dead

`HaPlayProject.VirtualAudioChannels` and `VirtualAudioChannelAssignment` still deserialize, but the
field is documented as legacy, ignored during load, and written empty. It maps a real output channel
to an integer `VOut N`; it does not provide stable IDs, names, cue-to-logical sends, per-cell gain,
validation, or a runtime topology owner.

The new design should not reactivate that field. It may use non-empty legacy assignments as an
import hint, but it needs a new explicit schema.

### ShowSession is source-split but still conceptually broad

`ShowSession` was recently split into transport, stops, completion, levels, live edits, queries,
taps, and transport-voice partials. That source split is worth preserving. The remaining issue is
responsibility: the session still performs document reconciliation, clip opening, transport,
composition lifetime, real audio device acquisition, audio matrix realization, preview,
soundboard voices, visualizer/tap hosting, and diagnostics.

The next change should therefore extract collaborators behind the existing `ShowSession` facade,
not merely make more partial files or immediately replace the public API.

## Target application boundaries

Recommended project layout:

```text
UI/
  HaOutput/
    output definitions, runtime catalog, leases + raw-terminal acquisition,
    reconfiguration, health snapshots

  HaSource/            (shared media-source model - resolved decision 4)
    PlaylistItem hierarchy, media probe/URI mapping, the add-media and
    media-properties/subtitle-selection dialogs both apps present

  HaControl.Input/     (shared control input - resolved decision 4)
    MIDI/OSC device open + monitor + learn records; the trigger-input
    configuration cues bind against

  HaCue.Core/
    HaCue project/cue models, validation, migration, mapper/compiler, non-UI cue services

  HaCue/
    Avalonia views, view models, dialogs, app-specific host adapters and resources

  HaCue.Desktop/
    desktop executable, logging, NativeAOT/publish policy, composition root

  HaCue.Tests/
    core, view-model, headless UI, migration and app-wiring tests

  HaPlay/
    players, soundboard, control workspace and HaPlay-specific project/UI

  HaPlay.Desktop/
    existing desktop executable
```

The names can change; the dependency rules should not:

- `HaCue.Core` has no Avalonia dependency. Neither does the model half of `HaSource` — its dialogs
  are a separate Avalonia assembly so `HaCue.Core` can reference the models without pulling in UI.
- HaCue does not reference HaPlay.
- HaPlay and HaCue may both reference the focused `HaOutput`, `HaSource` and `HaControl.Input`
  support libraries.
- No app-support library is referenced from `S.Media.*`. Add each one to
  `S.Media.Arch.Tests/ArchitectureTests.cs`'s reference rules so
  `ProjectReferencesAreDownwardAndAllowed` enforces this instead of a code review having to.
- `S.Media.Session` consumes neutral routing/session contracts and never consumes HaCue project
  models.
- Each desktop app owns its media-module selection and `MediaHost` lifetime. Do not replace one
  static `MediaRuntime` with a suite-wide global.

### What moves to HaCue

- Cue list/project models and cue project I/O.
- Cue list editor, node/binding/now-playing ViewModels, and all `CuePlayerViewModel` partials.
- Cue playback/session host after its soundboard responsibilities are removed.
- Cue scheduling, timecode chase, external triggers, cue preview, cue project recovery, and cue
  remote API commands.
- Cue composition, timeline, waveform, mapping, media-property, action-cue, and route/patch UI.
- Cue-specific tests.
- Only the strings, icons, styles, and dialogs actually used by those surfaces.

### What remains in HaPlay

- Media-player decks and playlists.
- Soundboard and quantized-launch UI.
- Full control/mixer workspace unless explicitly chosen for HaCue too.
- HaPlay's own project/recovery/remote commands.
- HaPlay-specific shell, workspace switching, and player output matrix.

### What becomes shared app support

`HaOutput`:

- Output definitions and stable output IDs.
- Persistent output runtime ownership, audio/video lease acquisition, **and the raw-terminal
  acquisition mode a HaCue bay needs** — mutually exclusive with client-lease mode per line.
- Transactional output reconfiguration callbacks.
- Output effects and health snapshots.
- Small neutral contracts such as `IOutputRuntimeCatalog`.

`HaSource` — the first draft missed this. `MediaCueNode.Source` is a `PlaylistItem`
(`UI/HaPlay/Models/PlaylistItem.cs:22`), the same abstract record the deck's `PlaylistConfig` uses,
with nine subtypes (file, image, subtitle, text, MMD, YouTube, NDI input, PortAudio input). It is
also consumed by `RemoteApiDispatcher`, `HaPlayShowMapper`, `TextSourceSpecMapper` and six dialogs
(`AddYouTube`, `AddMMD`, `AddNDIInput`, `AddPortAudioInput`, `MediaProperties`,
`SubtitleSelection`). Both apps need all of it. Duplicating it would fork the media-source schema
between two applications that must read each other's cue files, so it is shared:

- the `PlaylistItem` hierarchy and its JSON contract;
- source → registry path/URI mapping and probe helpers;
- the add-media / media-properties / subtitle-selection dialogs.

`HaControl.Input` — also missed. MIDI/OSC input **device** ownership lives in
`ControlWorkspaceViewModel` (3,825 lines across partials), which the plan leaves in HaPlay, while
`MainViewModel` forwards `ControlMonitorRecord`s into `CueTriggerService`
(`MainViewModel.cs:106,230`). Cue triggers cannot work in HaCue without a device layer, so the
open/monitor/learn half is extracted:

- MIDI/OSC device enumeration, open, and monitor-record stream;
- the learn flow and the trigger-input configuration cues bind against;
- `ActionEndpoint` outbound targets plus `ActionEndpointProbe` / `EndpointHealthMonitor`.

HaPlay keeps the full Control workspace — graphs, scripting, mixer layers, device profiles — on top
of this shared input layer.

Views should be shared only where they are genuinely the same product surface. The runtime service
is the important shared seam; avoiding duplicated PortAudio/NDI/encode lifetime code matters more
than forcing both apps to have an identical I/O screen. The `HaSource` dialogs are the exception
that proves the rule: they are the same product surface, verbatim, in both apps.

## Target HaCue project model

The following names are illustrative. Stable IDs and relationships are the contract.

```csharp
public sealed record HaCueProject
{
    public int SchemaVersion { get; init; } = 1;
    public ProjectAudioPatch AudioPatch { get; init; } = ProjectAudioPatch.Stereo48K();
    public List<PatchSnapshot> PatchSnapshots { get; init; } = [];
    public List<OutputDefinition> Outputs { get; init; } = [];
    public List<ActionEndpoint> ActionEndpoints { get; init; } = [];
    public List<CueList> CueLists { get; init; } = [];
    public ControlTriggerConfiguration TriggerInputs { get; init; } = new();
    public string? PreviewAudioEndpointId { get; init; }
    public bool AutoSaveEnabled { get; init; }
}

public sealed record ProjectAudioPatch
{
    public int MixSampleRate { get; init; } = 48_000;
    public Guid? ClockMasterOutputLineId { get; init; }
    public List<LogicalAudioChannel> LogicalChannels { get; init; } = [];
    public List<PhysicalAudioPatchCell> OutputCells { get; init; } = [];
}

/// A named, recallable V×R state. Cells listed here OVERRIDE the live patch's gain/mute for the
/// channels they name and leave every other cell alone, so a snapshot is a partial recall ("Act 2
/// reverb sends") rather than a whole-console reset.
public sealed record PatchSnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public List<PatchSnapshotCell> Cells { get; init; } = [];
}

public sealed record PatchSnapshotCell
{
    public Guid LogicalChannelId { get; init; }
    public Guid OutputLineId { get; init; }
    public int OutputChannel { get; init; }
    public double GainDb { get; init; }
    public bool Muted { get; init; }
}

public sealed record LogicalAudioChannel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int SortOrder { get; init; }
    public Guid? DisplayGroupId { get; init; } // optional stereo/surround UI grouping
}

public sealed record PhysicalAudioPatchCell
{
    public Guid LogicalChannelId { get; init; }
    public Guid OutputLineId { get; init; }
    public int OutputChannel { get; init; } // zero-based in the model/runtime
    public double GainDb { get; init; }
    public bool Muted { get; init; }
}
```

`MediaCueNode.AudioRoutes` should be replaced in the new schema with logical sends:

```csharp
public sealed record CueAudioSend
{
    public int SourceChannel { get; init; }
    public Guid LogicalChannelId { get; init; }
    public double GainDb { get; init; }
    public bool Muted { get; init; }
}
```

The persistent patch also earns a new cue kind. `CueActionKind` is currently only `OSCOut` and
`MIDIOut` (`UI/HaPlay/Models/CueList.cs:963-967`) — an action cue can talk to other systems but
cannot change HaCue's own state. With a named house patch that persists across cue fires, the
obviously missing cue is one that recalls part of it:

```csharp
public sealed record PatchCueNode : CueNode        // discriminator "patch"
{
    public Guid? SnapshotId { get; set; }          // set, not init - see the FadeCueNode gotcha
    public List<PatchLevelChange> Levels { get; set; } = [];
    public int FadeMs { get; set; }
    public CueFadeCurve FadeCurve { get; set; }
}

/// One inline level move, for the common case that does not deserve a stored snapshot.
public sealed record PatchLevelChange
{
    public Guid LogicalChannelId { get; set; }
    public Guid? OutputLineId { get; set; }        // null = every cell fed by this logical channel
    public int? OutputChannel { get; set; }        // null with a line = every cell on that line
    public double GainDb { get; set; }
    public bool Muted { get; set; }
}
```

Rules that keep this from becoming a second, competing gain authority:

- A patch cue writes the **project patch cell gains** only — the same values the operator edits in the
  patch pane. It never touches a cue's sends, a voice's fade level, or the program master trim, so the
  gain-composition chain in "Gain composition" is unchanged and still composes exactly once.
- A fade is a ramp on those cells, driven the same way the V×R live-update path already is; it must not
  reload the document, rebuild a matrix from scratch per step, or interrupt a playing cue.
- The recall is a **partial** one: only the cells a snapshot or level list names are written. Anything
  unnamed keeps its current value, so two patch cues can own disjoint parts of the house.
- Recall is idempotent and re-firable — firing the same patch cue twice lands on the same state.
- A snapshot cell naming a deleted logical channel or a missing line is a **broken binding**, reported
  like any other, never silently dropped and never applied to a neighbouring cell.
- Patch cues persist their effect: they are not undone when the firing cue stops. Returning to a prior
  state is another patch cue, which is what a board operator expects.

The interaction worth deciding before implementation is what a patch cue means with cues already
playing — the recommendation is that it applies immediately to the live patch (that is the point of a
persistent patch), and that "Live changes" already guarantees a V×R gain/mute edit cannot interrupt a
running voice.

Important invariants:

- IDs, not names or list positions, bind cues to logical channels.
- Names are non-empty and unique case-insensitively within a project for operator clarity.
- Reordering a logical channel never retargets a cue.
- A real output cell must target an existing audio-capable line and a channel inside its declared
  width.
- Several logical channels may sum into one real channel.
- One logical channel may feed any number of real channels or output lines.
- Several source channels may sum into one logical channel.
- One source channel may feed any number of logical channels.
- Gains must be finite. The initial UI range should match existing cue controls
  (`-60 dB` silence floor through `+12 dB`), while the runtime remains safe for validated values.
- No implicit normalization is applied when several cells sum. Meters should make clipping visible;
  a limiter is not silently inserted.
- Missing physical endpoints retain their cells and report an unresolved binding; they do not
  redirect to an OS default device.
- Deleting a referenced logical channel requires an explicit choice: cancel, remove the affected
  sends, or rebind them.

### Editing model: an undo journal, decided now

HaCue has no undo. The only thing resembling it today is a one-shot "Undo" button on the toast raised
after removing cues (`CuePlayerViewModel.CueAuthoring.cs:714`, `RestoreRemovedCues`). Every other edit —
drag-reorder, multi-select property edits, timeline moves, matrix cells, composition/placement edits — is
an immediate mutation of observable view models with no record of what changed.

This has to be decided **with** the new project format rather than after it, because it is the one
enhancement that constrains the document design. Retrofitting undo onto mutable VM graphs is how this
kind of app ends up with a permanently half-working undo that operators learn not to trust.

The model:

- Every mutation is a **command** with `Apply`/`Revert` against `HaCueProject`, not against a view model.
  View models rebuild (or refresh in place) from the resulting document.
- The journal holds commands, not document snapshots. Whole-document snapshots are simple but make the
  memory cost of an undo depth proportional to project size, and a large cue project with waveform and
  probe caches is not cheap to clone.
- Commands are **coalescing** where the UI generates streams of them: a slider drag, a numeric spinner,
  a timeline drag, and a matrix cell scrub each collapse into one undo step, keyed by (target, property)
  with an idle/blur boundary closing the group.
- Multi-edit is a **composite** command — one undo step reverts the whole multi-selection edit, matching
  what the operator did rather than what the code did.
- The journal is cleared on load and is **not persisted**; it is an editing aid, not show state.
- Firing cues, transport, arming, and patch-cue recalls are **not** journaled. Undo means "un-edit my
  document", never "un-play my show" — the distinction has to be explicit or the first stressed operator
  will try to undo a GO.
- Autosave, the project hash, and the dirty flag hang off the same commit point, so "is this project
  modified" stops being inferred separately from what actually changed.

Two things fall out for free once commands exist, and both are worth having:

- an **edit log** the operator can read ("what changed since I last saved") — cheap, and exactly the
  paperwork question people ask after a rehearsal;
- reliable **reference integrity on delete**. The plan already requires an explicit choice when deleting a
  referenced logical channel; the same machinery answers the equivalent cue-reference question, which is
  currently unanswered — `JumpCueNode.TargetCueIds` and `FadeCueNode.TargetCueIds`
  (`CueList.cs:774,802`) have no reverse-reference view, so deleting a cue silently orphans every jump and
  fade pointing at it. Reference counts and a "what targets this?" inspector should cover cues, logical
  channels, snapshots, compositions, and endpoints uniformly.

Scope boundary for the first release: journal document edits (cues, lists, patch, snapshots, outputs,
endpoints). Machine-local view state (column widths, expansion, zoom) stays outside the journal.

## Target audio runtime

### Audio flow

For each sounding transport voice:

```text
decoded source (N channels)
       │
       │ cue N×V send matrix
       ▼
voice producer lease (V logical channels at the project mix rate)
       │
       │ persistent project V×R patch
       ├────────► PortAudio line A (R₁ channels)
       ├────────► NDI line B       (R₂ channels)
       ├────────► armed recorder C (R₃ combined track channels)
       └────────► live stream D    (R₄ combined track channels)
```

There should be one persistent project patch per open HaCue project. Crossfade voices, simultaneous
timeline lanes, and cues from different lists acquire independent producer leases into it. Real
devices remain attached while cues start and stop.

### `AudioPatchBay` responsibilities

Add a neutral routing component under `S.Media.Routing`:

- owns one dynamic `AudioRouter` at the project mix rate;
- owns its real terminal outputs **exclusively** — for a line HaCue patches, the bay replaces
  `SharedAudioOutput` rather than layering on top of it (resolved decision 3);
- is the only producer that submits to those terminals;
- provides an isolated, bounded producer lease for each program voice;
- registers every program lease as a V-channel source;
- **sums every program source into one V-wide logical program bus, then applies the persisted V×R
  patch once per real output** — `P + R` matrix passes rather than `P × R` (resolved decision 1).
  The program bus is per-chunk scratch inside the router's mix pass, not a queue: it changes the
  cost model without adding a buffer, a ring, or a millisecond of latency;
- atomically reconciles output matrices on live patch changes;
- chooses and exposes one primary audible playback clock;
- adaptive-rate-wraps secondary clocked outputs through existing registry hooks;
- isolates output failures so one bad line does not silence other lines;
- reports per-input and per-output queue/drop/latency health;
- can provide a direct monitoring input to a selected endpoint without sending that input through
  the program patch.

This should be implemented as a generalization/extraction of `SharedAudioOutput`'s useful client
input, clock, latency, and ownership machinery. Do not put an 80 ms `AudioBus` between two complete
routers and then put that stack inside the existing 120 ms shared-output client. Such a composition
would be functionally correct but could add a large and hard-to-explain monitoring delay.

The target topology has one bounded client ring per producer and one output pump per terminal. Any
additional buffer must be justified by a measured scheduling requirement and included in the
reported audible latency. The V-wide program sum is explicitly *not* such a buffer: it is chunk
scratch reused every pass, and it must stay that way.

Several of the listed responsibilities are wire-ups of shipped code, not new work. Say so in the
implementation so nobody rebuilds them:

- **Failure isolation between terminals** already exists: `OutputPump.Commit(applyBackpressure:)`
  paces only the pacing primary and drops the oldest chunk on every other output
  (`AudioRouter.OutputPump.cs:140-186`), with `PumpPressure` / `OutputErrored` /
  `OutputPumpStats` for health.
- **Per-producer epochs and terminal-reanchor recovery** already exist in
  `SharedAudioOutput.ClientInput` (`ReanchorPlaybackEpoch`/`RecoverFromTerminalReanchor`), including
  the low-passed DAC-lead subtraction and the monotonic high-water clamp.
- **The headless clocked discard output** already exists as `NullClockedAudioOutput` — real-time
  paced consumption exposed as an `IPlaybackClock`, with the same master-promotion path as hardware.

One failure mode does need new handling. A pump whose native `Submit` wedges past the join cap is
leaked and the router is marked non-restartable, with the host told to rebuild
(`AudioRouter.OutputPump.cs:340-351`). That is proportionate for a per-clip router about to be torn
down anyway; for **one persistent bay per project** it means rebuilding the program bus mid-show.
The bay must be able to quarantine and hot-swap a wedged terminal without rebuilding itself or
interrupting the other lines.

Because the bay owns terminals exclusively, `HaOutput` needs two acquisition modes per line —
client-lease (HaPlay decks, soundboard, anything that fans in) and raw-terminal (a HaCue bay) —
enforced so the two can never be granted on the same line at the same time.

Terminals whose native rate differs from the project mix rate are wrapped through an injected
resampler factory (see "The router cannot host an output at a rate other than its own"). The clock
master is never wrapped.

### Clock policy

The clock contract is part of the feature, not a later polish item:

- The selected master is an output line, not an individual channel.
- The master must run **natively at the project mix rate**: it is the one terminal that is never
  wrapped in a resampler, because the wrapper does not report its own internal delay
  (`ResamplingAudioOutput.cs:66`) and a clock read through it would be silently skewed. If the
  chosen line cannot open at the project rate, that is a validation failure with a named cause, not
  a silent wrap.
- A producer lease implements `IClockedOutput`, `IPlaybackClock`, and `IAudioOutputLatency`.
- Its clock is the master terminal's audible clock, rebased to that producer and reduced by every
  queue between that producer and the speaker.
- A producer patched to several lines has a *different* real latency to each one; the clock follows
  the master only. `SharedAudioOutput.DownstreamLatencyTicks` is single-terminal today
  (`:173-182`), so its generalization must be explicit about which terminal it measures. Per-line
  offsets relative to the master are reported for the operator (and are the input to any later
  per-line delay feature) — they are never folded into the program clock.
- Flush/seek establishes a new epoch without causing a backwards read.
- A terminal epoch change is propagated coherently.
- If the master is unavailable at project arm time, HaCue either uses an explicitly reported
  fallback master or the headless clock; it never silently opens the OS default device.
- First implementation may stop/fault visibly when the live master dies. Automatic master failover
  is acceptable only after its epoch and A/V behavior has dedicated tests.

### Gain composition

The effective contribution of one source-to-real cell is:

```text
cue send gain
× cue master level
× envelope
× fade/crossfade/stop level
× show program master trim
× project patch-cell gain
× physical output effect chain
```

Preview/audition is classified as monitoring and excludes the show program master trim, as it does
today.

Static matrix gains should not be rebuilt from scratch for every 25 ms fade step if this becomes a
hot-path cost. Prefer either:

- matrix cells with a shared voice-level gain slot; or
- the current atomic matrix reconciliation with a benchmark proving the supported matrix sizes and
  overlap counts remain comfortably inside the audio chunk deadline.

Do not add an optimization without the benchmark; the existing matrix implementation may already
be sufficient for realistic cue counts.

The program-sum topology makes this easier than the first draft assumed: a fade rides the **voice's
N×V send gains** (one small matrix per active voice), never the V×R patch, so the wide matrix is
touched only by operator patch edits at control-plane rate. Note that `ApplyMatrix` allocates one
`ChannelMap` — an `int[dstChannels]` — per non-zero cell (`AudioRouter.Matrix.cs:159`), so a wide
patch edit is a real allocation burst even though it is off the audio thread. If patch edits become
frequent (a fader-driven surface), that is the thing to fix, not the mix.

### Live changes

The following operations must not reload `ShowDocument`, reopen decoders, or interrupt an active
cue:

- rename/reorder a logical channel;
- change a project patch gain or mute;
- attach/detach a logical channel from a real output channel;
- add an already-running real output line;
- remove a real output line after its patch routes are detached.

Changing the number of logical channels changes the producer format. Treat that as a topology
transaction:

1. validate and stage the new topology;
2. acquire/stage replacement endpoints;
3. either adapt existing voice inputs or defer the width change until no program voices are active;
4. commit once;
5. dispose the old topology after no producer can submit to it.

For the first release, deferring logical-channel add/remove while cues are active is safer than a
glitchy in-place width change. Rename, reorder, and V×R patch edits can remain live.

## ShowSession redesign

Keep `ShowSession` as the compatibility facade while moving audio responsibilities into a
collaborator. A possible neutral interface is:

```csharp
public interface IShowProgramAudioTarget
{
    int SampleRate { get; }
    IReadOnlyList<string> LogicalChannelIds { get; }
    ProgramAudioInputLease AcquireInput(string voiceId);
}
```

The exact type names are less important than the ownership split:

- `ShowSession`/`TransportVoice` still owns clip transport, fades, envelopes, crossfades, stops, and
  the lifetime of a voice's producer lease.
- The program-audio target owns real outputs, the V×R patch, device clocks, and output health.
- The session attaches one V-channel output to a clip's `AudioRouter` and applies that cue's N×V
  send matrix.
- Fade/master/envelope updates scale the voice's logical send routes. They never rebuild or directly
  address real output devices.
- Preview uses an injected monitoring output provider.
- Soundboard voice APIs should leave the cue session boundary. They may keep using `VoicePlayer` in
  HaPlay or gain their own session/service, but HaCue's cue host must not depend on the soundboard.

Maintain a legacy direct-output adapter while existing tests/tools and ShowDocument v1 still use
`ShowClipAudioRoute`. New HaCue documents must use logical sends. Delete the legacy adapter only
after repository callers and compatibility fixtures prove it is no longer needed.

### ShowDocument evolution

Use an additive versioned framework shape:

- logical channel definitions with stable string/Guid IDs;
- per-clip logical sends;
- output endpoint definitions and V×R patch cells where a self-contained/headless document needs
  them;
- an opaque host endpoint ID for app-owned NDI/encode/local runtimes;
- the current direct `AudioRoutes` retained only for v1 compatibility.

Before changing the sidecar format, audit the current “directly runnable headless or through the C
ABI” claim. Existing HaPlay device tokens such as output-line/encode IDs already need host-side
factories. The v2 contract should state precisely which endpoints a generic registry can open and
which require a HaCue host adapter.

Validation and staging must happen before the running graph is torn down, preserving the current
transactional `ShowSession.LoadDocumentAsync` rule.

## HaCue UI

HaCue should have a focused shell rather than reproducing HaPlay's six-workspace sidebar.

Recommended top-level surfaces:

- Cues: existing cue-list/editor/transport surface.
- I/O: compositions/video outputs, logical audio channels, physical patch, output health.
- Targets: MIDI/OSC action targets and trigger inputs.
- Project: file/recovery/remote API/appearance settings.

### Show mode is a state, not a checkbox

Today "am I editing or running a show" is `CuePlayerViewModel.IsCueEditMode`, a flag inside a
six-workspace shell that schedules and triggers are gated on (`CuePlayerViewModel.cs:348,448`). That
gating logic is correct and should be kept; what should change is its status in the UI. A dedicated
application can make **show mode the default state of the program**, which is most of the operator-facing
value of the split and costs almost nothing structurally.

Show mode:

- is the state the app launches into with a project loaded, and the state it returns to after a save;
- presents the transport, the cue list with standby/next, now-playing with elapsed and remaining, the
  logical-channel meters, and output health — and nothing that mutates the document;
- leaves edit affordances *visible but inert* rather than hidden, so an operator can see what a cue does
  without a mode change and without being one stray click from changing it;
- is exited by an explicit, deliberate unlock (not a tab click), which is what makes the existing
  schedule/trigger gate trustworthy rather than incidental;
- cannot be left while a cue is running, or leaving it disarms nothing — pick one and state it. The
  recommendation is that entering edit mode is allowed but disarms triggers and schedules exactly as the
  current gate already does, with that consequence shown before the switch, not after.

Two related operator-surface gaps worth closing at the same time, since they are the same screen:

- **Per-cue enable/disable.** `ArmedList` group mode exists and the remote API has `arm`/`disarm`
  (`RemoteApiDispatcher.cs:343-347`), but an individual cue has no disabled flag — skipping one cue for
  one performance means deleting it. A `Disabled` bool on `CueNode` that keeps the cue visible, skipped by
  GO and by auto-follow, and excluded from compilation.
- **A script/notes view.** `CueNode.Notes` exists on every cue with no surface that reads it as a
  continuous document. In show mode this is the column the operator actually follows.

### Preflight, relink, and consolidate

There is no show-validation pass in the codebase — no missing-media sweep, no report, no relink. This is
the first thing an operator does on a venue machine, and it belongs to HaCue because it is a
project-level question:

- **Preflight** walks the loaded project and reports, without playing anything: media files that do not
  resolve, cues with no audio send and no video placement (deliberately silent is legal but should be
  *stated*), sends targeting deleted logical channels, patch cells targeting missing or non-audio-capable
  lines, snapshot cells with dead references, jump/fade cues with no live target, an unresolvable clock
  master, endpoints that fail their probe, and schedules already in the past. Every row links to the thing
  that caused it.
- **Relink** takes a moved or re-rooted media tree and rebinds by a chosen strategy — same filename, same
  relative subpath under a new root, or operator-picked — as a single journaled command so it is one undo
  step and one reviewable diff.
- **Consolidate** copies every referenced media file into a folder beside the project and rewrites the
  references, so a show transports as one directory. It must report what it could not copy rather than
  silently producing a project that half-works at the venue.

Preflight should also be reachable headlessly (exit code plus a machine-readable report), which makes it
usable as a pre-show check in a script and as a CI gate over committed fixture projects.

### Project audio patch editor

Provide two related panes:

1. Logical channels:
   add, rename, reorder, group for display, show reference count, and show live meter.
2. Physical patch:
   rows for real output-line channels, columns for logical channels, with cell enable, gain, and
   mute.

The row label should always include both the stable output alias and the real channel number, for
example `Main Interface · Out 3`.

Useful presets:

- stereo identity to one line;
- stereo fan-out to selected lines;
- mono sum to one channel;
- clear selected rows/columns;
- copy/paste a line patch.

A third pane once patch cues exist: **snapshots**. Store the current patch (whole, or just the selected
cells) as a named `PatchSnapshot`, recall one to preview it, show which patch cues reference it, and show
per-cell whether a snapshot's stored value differs from the live patch — the "is the console where I left
it" question. Recall from this pane is an operator action on the live patch and is not journaled; editing
a snapshot's contents is a document edit and is.

### Per-cue audio editor

Replace the current output-line/channel picker with an N×V matrix:

- source channels from the selected/probed media stream;
- logical channel names from the project;
- per-cell gain and mute;
- presets for identity stereo, swap, mono-from-left/right, and clear.

Show an “effective route” inspector for the selected cell:

```text
Source R → Main R (-3 dB) → Interface Out 2 (0 dB), NDI Out 2 (-6 dB)
```

This makes a silent or unexpectedly duplicated route diagnosable without mentally multiplying two
matrices.

Unknown/stale logical IDs and unavailable real outputs stay visible as broken bindings with a
rebind action. They must not disappear from the editor merely because their runtime is unavailable.

## Persistence and migration

### New format

Use a new extension such as `.hacueproj` and a new source-generated JSON context. HaCue should
write atomically and retain the existing project-hash, recovery-copy, autosave, and unsaved-change
behavior.

Do not let both applications edit the same `.haplayproj` in place. That creates ambiguous ownership
and makes it easy for a cue-unaware HaPlay build to erase HaCue-only fields.

### HaPlay project import

Import only the relevant sections:

- outputs;
- action endpoints;
- cue lists;
- cue preview choice;
- the minimal trigger/control device settings HaCue supports.

Players, soundboards, and the full control workspace remain in the source HaPlay project and are
reported as intentionally not imported.

For each current direct `CueAudioRoute`, preserve audible behavior with this deterministic
conversion:

1. Collect every valid referenced `(OutputLineId, OutputChannel)` pair. The legacy cue field is
   operator-facing and one-based; reject/report values below 1.
2. Create one logical channel per pair, named from the output alias and legacy channel number.
3. Add an identity project patch cell from that logical channel back to the same real channel,
   converting the legacy number to the new zero-based index (`OutputChannel - 1`).
4. Replace every cue's source-to-real route with a source-to-logical send carrying the same
   gain/mute.

This may create more logical channels than an operator ultimately wants, but it preserves the old
show exactly and never guesses that two independently routed physical channels were intended to be
one program bus. HaCue can offer a later merge/rebind workflow.

If a legacy project contains non-empty `VirtualAudioChannels`, the importer may use common virtual
numbers as a grouping hint only after golden fixtures establish the old semantics. Never prefer a
clever grouping over exact preservation.

### Standalone cue-list import

`.haplaycues` and `.haplaycuelists` contain output IDs but not the corresponding project output
definitions. Import therefore needs one of:

- import into an already-open HaCue project and rebind against its outputs;
- select a companion `.haplayproj`; or
- retain unresolved output IDs and let the operator bind them later.

The importer must not map unresolved IDs to similarly named/default devices automatically.

### Compatibility policy

- Keep old-reader failure closed: a newer explicit schema version is rejected with a clear message.
- Keep migration pure and separately testable; do not mutate while deserializing.
- Store imported legacy identifiers in an optional migration report, not as runtime routing state.
- Preserve a timestamped backup of the source file; import never overwrites it.
- A legacy export may flatten N×V and V×R into direct source-to-real cells, but it is lossy because
  logical names/grouping disappear. It is not required for the first HaCue release.

## Implementation phases

Each phase should land with the solution building and the old HaPlay cue path working until the
HaCue cutover phase.

### Phase 0 — decisions, fixtures, and characterization

- Resolve the blocking product questions at the end of this document.
- Add representative committed fixtures:
  HaPlay project schema 1/2/3, cue-list v1/v2/v3, direct stereo, summed routes, multi-output,
  missing endpoints, NDI/encode routing, timeline groups, actions, and triggers.
- Characterize current playback for those fixtures: routed matrix, gain composition, preview
  policy, output acquisition count, and generated ShowDocument.
- Record a clean build/test/AOT-smoke baseline.
- Decide `ClipAudioOutputRuntime`'s fate — delete it or declare it the bay's seed. It is dead code
  with the bay's job description and must not survive as a decoy.
- Benchmark the fused matrix kernel at the widths this design actually needs (up to 64×64, several
  terminals) and set the cell-op budgets from measurement, not from the 8×8 extrapolation in
  "Performance budgets".
- Define latency and matrix-size budgets before the new audio stage is implemented.

Exit: compatibility behavior is pinned without changing production behavior, and the mix-cost model
is measured rather than assumed.

Note the deliberate exception: the NDI cue-audio defect is fixed in Phase 1 **before** these
fixtures are treated as golden, so characterization does not enshrine it (resolved decision 2).
Record the pre-fix behavior as a migration note, not as an expected-output assertion.

### Phase 1 — make the cue boundary real inside HaPlay

- Fix NDI cue-audio routing: add the `NDIOutputDefinition` cases to `MapAudioRoutes`'s `deviceId`
  and `minChannels` switches, teach `BuildCueAudioLease` to parse `ndi-audio:` and borrow the NDI
  carrier the way the deck already does, and add a regression test that an NDI-routed cue never
  resolves a hardware fallback. This is a behavior change, deliberately taken before the fixtures
  are frozen.
- Split `CueShowSessionCoordinator` into cue transport and soundboard hosting.
- Replace its concrete `OutputManagementViewModel` dependency with a focused runtime-catalog
  interface.
- Move project-independent cue compilation/mapping logic behind a cue-domain service.
- Move scheduling, trigger, remote-command, and recovery dependencies behind explicit interfaces.
- Remove direct access from cue code to the root `MainViewModel`.
- Keep HaPlay as the only executable and prove behavior parity.

Exit: the cue subsystem can be constructed in a test without a HaPlay main shell or soundboard, and
every audio-capable output kind the cue editor offers actually reaches its own endpoint.

### Phase 2 — extract output runtime ownership

- Extract definitions, runtime ownership, leases, effect wrapping, transactional reconfiguration,
  and health snapshots from `OutputManagementViewModel` into `HaOutput`.
- Add the per-line raw-terminal acquisition mode next to the client-lease mode, with the two
  mutually exclusive per line and a clear error when a line is already held the other way.
- Extract `HaSource`: the `PlaylistItem` hierarchy and JSON contract, source→URI/probe helpers, and
  the shared add-media / media-properties / subtitle-selection dialogs. Keep the model assembly
  Avalonia-free.
- Extract `HaControl.Input`: MIDI/OSC device open/monitor/learn plus the outbound `ActionEndpoint`
  surface and its health probe. HaPlay's Control workspace becomes a consumer of it.
- Keep current HaPlay I/O UI as an adapter over the service.
- Preserve the acquire/hold/detach/release ordering documented by the cue video path.
- Add service-level tests for PortAudio shared clients, NDI carrier sides, armed encode sinks,
  output replacement, failure isolation, and lease/raw-terminal mutual exclusion.
- Register all three libraries in the architecture-test reference rules.

Exit: HaPlay and a minimal test host can both use the same output runtime service without sharing a
root ViewModel, and nothing HaCue needs from the cue domain still lives behind a HaPlay-only owner.

### Phase 3 — add the project audio patch to the framework/session

- Add the router's V-wide program-sum stage: sum registered program sources into one logical bus in
  the chunk pass, then run one dense V×R pass per terminal. Prove by benchmark that it is `P + R`
  and that it adds no queue (reported latency identical to the single-producer case).
- Implement and test the neutral `AudioPatchBay` in `S.Media.Routing`, owning its terminals
  outright.
- Give the bay an injected resampler factory for terminals off the project rate, and reject a clock
  master that cannot open natively at the project rate.
- Extract/generalize client clock and latency behavior from `SharedAudioOutput`; avoid a stacked
  high-latency bus. Reuse `ClientInput`'s epoch/lead machinery rather than re-deriving it.
- Add terminal quarantine + hot-swap so a wedged pump cannot force a bay rebuild mid-show.
- Add patch spec validation and immutable/reconciled snapshots.
- Add the `ShowSession` program-audio target collaborator and the monitoring-output seam — and
  change `VoicePlayer`'s preview path to use it instead of opening its own device.
- Add logical clip sends while retaining the v1 direct-route adapter.
- Move real device acquisition/matrix realization out of `CommitClipAsync`.
- Add live V×R patch updates and output health events.

Exit: a framework test can run several simultaneous/crossfading voices through named logical
channels to several real fake outputs, hot-change the physical patch, quarantine a wedged terminal,
and keep the voices running — with preview auditioning through the same bay-owned line as the
program without double-opening the device.

### Phase 4 — create HaCue project/core and migration

- Add `HaCue.Core` models, validation, source-generated serialization, project hashing, atomic I/O,
  migration, and compiler to ShowDocument.
- Use stable logical channel IDs and the two-matrix model.
- **Build the edit-command journal in from the start** (see "Editing model: an undo journal, decided
  now"): commands over `HaCueProject` with `Apply`/`Revert`, coalescing groups, composite multi-edits,
  and the dirty/hash/autosave commit point hanging off it. Every editing surface added later in Phase 4
  and Phase 5 goes through it — this is the phase where that is still cheap.
- Add `PatchSnapshot` and the `PatchCueNode` kind to the model, validator, and compiler, including
  broken-reference reporting for snapshot cells.
- Add a `Disabled` flag to `CueNode` — skipped by GO, auto-follow and compilation, still visible.
- Add uniform reference counting and a reverse-reference ("what targets this?") query over cues,
  logical channels, snapshots, compositions, and endpoints — the delete-safety machinery the plan
  already requires for logical channels, generalized.
- Add the preflight validator as a pure, headless-runnable pass over a loaded project, plus relink and
  consolidate as journaled commands.
- Add the logical-channel and physical-patch ViewModels plus pure matrix editing operations.
- Port cue-specific tests to `HaCue.Tests`; leave compatibility tests near the importer.

Exit: a HaCue project round-trips and old fixtures migrate to an equivalent effective
source-to-real matrix.

### Phase 5 — create and prove the HaCue application

- Add `HaCue`, `HaCue.Desktop`, and launch-smoke wiring.
- Move cue views/ViewModels/services/resources rather than copying them. Route every editing command
  through the Phase 4 journal as each surface lands — a surface that mutates directly is a surface whose
  undo silently does nothing.
- Add the focused Cues/I/O/Targets/Project shell, with **show mode as the launch state** and edit mode
  behind an explicit unlock (see "Show mode is a state, not a checkbox").
- Add the operator surfaces show mode implies: standby/next, elapsed and remaining, logical-channel
  meters, output health, per-cue disable, and the notes/script view.
- Add the patch snapshots pane and the patch-cue editor.
- Add the preflight report UI over the Phase 4 validator, plus the relink and consolidate flows, and
  expose preflight headlessly (exit code + machine-readable report) so it can gate a fixture project in
  CI.
- Wire the existing media modules required by cue sources and outputs.
- Port project recovery, preview, schedules/triggers, remote API, composition mappings, and
  projectM behavior.
- Run HaPlay and HaCue side by side against the same imported fixture and compare compiled
  documents/effective routes.
- Add NativeAOT publish and Linux/Windows launch gates mirroring HaPlay's current policy.

Exit: HaCue is independently buildable, publishable, smoke-tested, and usable for a show without
loading HaPlay — launching into show mode, passing preflight on a transported project, and with undo
working on every editing surface it ships.

### Phase 6 — cut over and simplify HaPlay

- Remove the Cue workspace and cue-only shell wiring from HaPlay.
- Remove HaPlay's dependency on HaCue UI/core, retaining only shared output support and any explicit
  compatibility DTO/import package.
- Decide whether HaPlay still reads old full projects. If it does, preserve cue sections opaquely
  on save or force Save As to a cue-less format; never silently discard them.
- Move soundboard session hosting to its own owner.
- Update README, docs, screenshots, CLI/environment variable names, release artifacts, packaging,
  SBOM labels, and workflow comments.
- Deprecate then remove the framework direct-route adapter only when no repository caller needs it.

Exit: HaPlay contains no cue UI/runtime composition, and HaCue contains no deck/soundboard/full
control workspace.

### Phase 7 — optional post-cutover cleanup

- Consider splitting the `ShowSession` facade further into document runtime, transport engine,
  composition host, program audio host, and audition service only where ownership is now proven.
- Add virtual-channel effect insert chains if required.
- Add automatic clock-master failover.
- Add simultaneous multi-audio-stream/stem decoding.
- Add portable machine output profiles that can be rebound to a project's stable logical patch.

These are not prerequisites for the first independent HaCue release.

## Test and verification plan

### Pure model/compiler tests

- unique IDs/names and dangling-reference validation;
- N×V and V×R effective matrix multiplication;
- many logical channels into one real channel;
- one logical channel into several lines/channels;
- mute, gain, and silence-floor behavior;
- reorder/rename stability;
- deletion/rebind policies;
- source-generated JSON round-trips and unknown/newer-version failure;
- HaPlay fixture migration and dry-run report;
- no-default-output behavior for unresolved bindings;
- every command's `Apply`/`Revert` round-trips the document to a byte-identical serialization;
- a coalescing group (slider drag, matrix scrub) is exactly one undo step, and a multi-select edit is
  exactly one composite step;
- undo depth does not retain document clones (journal holds commands, not snapshots);
- transport, arming and patch recalls are absent from the journal;
- a patch snapshot recall is partial, idempotent, and re-firable; unnamed cells keep their values;
- a snapshot or patch-level change naming a deleted channel/line reports a broken binding and applies
  nothing;
- reverse-reference queries find every jump/fade cue targeting a cue, and every cue/snapshot targeting
  a logical channel;
- a disabled cue is skipped by GO, by auto-follow, and by compilation, and still round-trips;
- preflight finds each seeded defect class (missing media, dead send, dead patch cell, dead snapshot
  cell, targetless jump, unresolvable clock master, past schedule) and is clean on a healthy project;
- relink and consolidate are single journaled commands and report what they could not resolve or copy.

### Routing tests

- two or more producers mix through the same logical channels;
- crossfade overlap does not corrupt or replace another producer;
- patch changes are atomic at an audio chunk boundary;
- removed cells stop contributing and added cells fade in click-free;
- secondary output blocking/failure does not stop the master or other outputs;
- add/remove/reconfigure terminal lifetime is race-safe;
- producer release removes only that producer;
- no sample bleed into unpatched/trailing encode channels;
- matrix sizes at the agreed supported maximum meet the chunk deadline;
- queue depth, underrun/overrun, and latency counters are truthful;
- the program sum runs `P + R` matrix passes, not `P × R`, at several producers and terminals;
- adding a producer does not change any terminal's reported latency (the sum is not a queue);
- a wedged terminal is quarantined and hot-swapped without rebuilding the bay or interrupting the
  other lines;
- a terminal off the project rate is resampler-wrapped transparently, and a clock master that cannot
  open at the project rate fails validation with a named cause;
- a line cannot be held as a client lease and as a raw terminal at the same time.

### Clock tests

- producer clock follows actually audible master progress;
- all in-flight queue latency is subtracted — including any resampler wrapper's internal delay;
- flush/seek creates a coherent new epoch;
- terminal epoch changes do not regress;
- headless/no-output projects advance (via `NullClockedAudioOutput`);
- paused/stopped master behavior is explicit;
- master loss produces the selected failure/fallback behavior;
- a producer patched to lines of differing latency reports the master's, and the per-line offsets
  surface separately;
- video scheduled from the producer clock remains aligned through a long run.

### Session tests

- one logical producer lease per transport voice;
- fades, cue level, envelope, master trim, and patch gain compose once;
- playlist and loop crossfades keep both voices routed;
- simultaneous timeline lanes sum correctly;
- live cue-send updates affect only the selected active cue;
- live project patch updates do not reload the document or decoder;
- Stop/Panic releases program leases while preview remains monitoring;
- preview auditions through the bay's monitoring input and never opens a device of its own — in
  particular, previewing to a bay-owned line does not double-open it;
- a bad real endpoint raises an operator-addressable alert without killing valid endpoints;
- an NDI, encode or live-stream endpoint that cannot be resolved stays silent and unresolved rather
  than falling back to hardware;
- legacy direct-route documents retain current behavior.

### HaCue app tests

- project new/open/save/save-as/recovery/dirty hash;
- show mode is the launch state, edit mode needs an explicit unlock, and the trigger/schedule
  consequence is shown before the switch;
- undo/redo works from every editing surface the app ships (cue list, drawer, multi-edit, timeline,
  both matrices, snapshots, compositions) — a per-surface test, not one generic one;
- a patch cue fired mid-show changes the live patch without reloading the document or interrupting a
  playing cue;
- preflight UI and its headless/exit-code form agree on the same project;
- import preview, backup, migration, and unresolved-output rebind;
- logical and physical matrix keyboard/pointer editing;
- route inspector text;
- output hot reconfiguration ordering;
- cue authoring, transport, schedules, triggers, remote API, visualizers, mappings, timeline, and
  preview parity;
- headless Avalonia ownership/lint tests;
- Linux and Windows NativeAOT launch smoke;
- full solution warning-free build.

## Performance budgets to set in Phase 0

Use explicit measured budgets rather than “seems fine.” Suggested starting targets:

- 48 kHz project rate, 480-sample chunks.
- 1–64 logical channels.
- At least 8 simultaneous program voices.
- At least 8 real output lines and 64 total real output channels.
- No allocation on the per-chunk steady-state routing path.
- Mix time below 25% of the 10 ms chunk deadline at the supported maximum on the CI reference CPU.
- No more than one additional 10 ms chunk of target buffering versus the current direct route,
  unless a measured backend scheduling requirement justifies more.
- Reported clock latency includes every added queue within a small test tolerance.

### Why the topology decision is load-bearing for these numbers

The router's fused matrix kernel costs `S × D` multiply-accumulates per frame **regardless of how
sparse the matrix is** (`AudioRouter.FusedMix.cs:9-21`; a sparse group below
`MinFusedMatrixCells` stays on the per-route path, which is worse still). The shipped benchmark
measures a dense 8×8 at 480 samples per chunk at roughly 12 µs
(`MatrixMixBenchmarks.cs:20-21`), i.e. about 2.6 G cell-ops/s on that machine. A 10 ms chunk
therefore affords ~26 M cell-ops in total, and the 25% budget affords ~6.5 M.

Against the maximums above:

```text
per-pair (P×R):  8 voices × 8 lines × 64 logical × 64 real × 480  ≈ 126 M cell-ops/chunk
                 ≈ 49 ms of mixing per 10 ms chunk                            ✗ ~5× over deadline

program sum (P+R):  8 × 64 × 480          (voice sends into the bus)
                  + 8 × 64 × 64 × 480     (bus → each terminal)
                  ≈ 16 M cell-ops/chunk   ≈ 1.6 ms per 10 ms chunk            ✓ ~16% of deadline
```

Realistic shows are far below either figure (4 voices, 3 lines, 8 logical, 8 real channels is
~0.14 ms, ~1.4% of deadline) — the per-pair form fails only at the maximums, which is precisely
where a documented, validator-enforced maximum has to hold.

Two rules follow:

- express every budget in **non-zero cells and cell-ops per chunk**, not in matrix dimensions;
  dimensions alone hide the multiplication that breaks the budget.
- re-measure the fused kernel at 64-wide before finalizing the numbers. The figures above are
  extrapolated from the shipped 8×8 datapoint and assume the kernel scales linearly in cells; wider
  matrices may lose SIMD efficiency or gain cache pressure. Phase 0 owns that measurement.

The exact supported maximums may be lowered or raised after representative benchmarks. They must be
documented and enforced consistently by the model validator and UI.

## Main risks and mitigations

| Risk | Mitigation |
|---|---|
| A “simple bus” adds multiple large rings and noticeable cue/preview latency | Generalize `SharedAudioOutput`; benchmark before integration; account for every queue in the clock |
| App split and routing rewrite fail together with no working baseline | Land boundary extraction and characterization first; keep HaPlay cue playback live through Phase 4 |
| Old direct routes are heuristically merged and change the show | Import one logical channel per physical target by default; offer manual merge later |
| Logical-channel reorder retargets cues | Bind by stable ID, never array index or name |
| Real output disappears and audio leaks to the OS default | Preserve unresolved cells, route silence, surface a blocking warning |
| ShowSession becomes a second monolith after adding a patch | Put physical topology in an injected collaborator; keep the facade for compatibility |
| Shared output code becomes a new suite-wide kitchen sink | Share runtime ownership/contracts, not root shell state or unrelated UI |
| Master-output loss causes an A/V time jump | Specify epochs/failure policy before implementation and add clock tests |
| Many matrix cells make fade updates miss deadlines | Benchmark current reconciliation; add a shared voice gain slot only if needed |
| HaPlay saves an old full project after cue UI removal and erases cues | Preserve cue payload opaquely or require a new cue-less Save As; never silently rewrite |
| Sidecars imply generic headless support but contain app-only endpoint tokens | Version and document host-resolved endpoints explicitly; test both generic and HaCue-hosted cases |
| Wide patches blow the chunk deadline because fused matrix cost is dimensional, not sparse | Program-sum topology (`P + R`); budgets stated in cell-ops; measured at 64-wide in Phase 0 |
| A wedged native `Submit` poisons the one persistent bay and takes the whole show down | Terminal quarantine + hot-swap without rebuilding the bay; health event drives it, not silence |
| A resampled clock master skews the program clock invisibly | Master never wrapped; a master that cannot open at the project rate is a named validation failure |
| Preview double-opens a bay-owned device | Preview becomes a bay monitoring input; `PreviewAudioEndpointId` is an output-line id, not a device id |
| `PlaylistItem` forks into two incompatible media-source schemas | Shared `HaSource` model assembly; both apps read one contract |
| Cue triggers ship without a device layer because Control stayed in HaPlay | Shared `HaControl.Input` extracted in Phase 2, before the app is created |

## Open questions for the owner

The recommended answer is in parentheses.

1. Should the Cue workspace be removed from HaPlay once HaCue reaches parity, or retained for a
   deprecation release?  
   **Recommendation:** one deprecation release at most; do not maintain two active cue UIs.

2. Should HaCue use a new `.hacueproj` project format, or continue writing `.haplayproj`?  
   **Recommendation:** new format plus one-way import. Two applications should not own one mutable
   document schema.

3. By “audio tracks from media files,” do you mean channels of the selected audio stream
   (L/R/5.1 channels), or should one cue decode several container audio streams/stems at once?  
   **Recommendation:** selected stream channels first; multi-stream decoding is a separate,
   larger playback feature.

4. Are logical audio channels always atomic mono channels, or should stereo/surround buses be
   indivisible routing units?  
   **Recommendation:** atomic channels with optional display groups. It preserves arbitrary
   many-to-many routing.

5. Which HaPlay areas belong in HaCue besides the Cue workspace: soundboard, full Control workspace,
   or only cue targets/triggers?  
   **Recommendation:** no soundboard and no full Control workspace; keep only cue action targets and
   the MIDI/OSC/hotkey input configuration needed by cues.

6. Should every audio-capable output kind be patchable, including NDI, file records, and live
   streams?  
   **Recommendation:** yes. A “program bus” that reaches only PortAudio would immediately recreate
   direct special cases for broadcast/recording outputs.  
   **Amended:** this is not a preservation requirement — NDI cue audio does not work today at all
   (see "NDI cue audio never reaches the NDI carrier"). Treat it as a Phase 1 bug fix plus a new
   feature, and do not let the Phase 0 fixtures assert the current behavior.

7. Is a fixed per-project 48 kHz mix rate acceptable, with resampling at source/output boundaries,
   or must the graph follow a selected device/source rate?  
   **Recommendation:** persist a project mix rate defaulting to 48 kHz. Fixed rate makes the logical
   patch stable and matches NDI/broadcast expectations.  
   **Amended:** add the constraint that the clock-master line must open natively at the project rate.
   `AudioRouter.AddOutput` rejects rate mismatches outright, and the resampling wrapper does not
   report its own internal delay — so wrapping the master would skew the program clock silently.

8. Should the operator explicitly choose the audio clock-master output, or should HaCue always pick
   the first viable output?  
   **Recommendation:** explicit choice with a clearly displayed automatic fallback when unset.

9. May logical-channel add/remove be blocked while program cues are active in the first release?  
   **Recommendation:** yes. Patch gain/mute/attachment remains live; changing channel width waits for
   a safe transport boundary.

10. Do you need delay, polarity inversion, solo, or per-logical-channel effects in the first audio
    patch?  
    **Recommendation:** per-cell gain/mute only. Add delay/polarity/effects after the topology and
    clock are stable. Solo can be a non-persisted editor audition function later.

11. Should output-device definitions remain inside each HaCue project, or should projects bind to
    separate machine profiles?  
    **Recommendation:** keep them in the project for the first release, with explicit rebind for
    missing devices. A portable machine-profile layer can be added without changing logical
    channel IDs.

12. When an imported old project has no direct route for a cue, should that cue remain deliberately
    silent?  
    **Recommendation:** yes. Preserve the current HaPlay no-hidden-default behavior.

13. Should HaCue retain HaPlay's generated `ShowDocument` sidecars, and must those sidecars be
    generically runnable without the HaCue host?  
    **Recommendation:** keep versioned sidecars, but distinguish registry-openable endpoints from
    host-resolved output-line endpoints honestly.

14. Are project-level logical channels intended to be shared across every cue list in the project?  
    **Recommendation:** yes. Per-list buses would weaken the purpose of a stable house patch and make
    cross-list schedules harder to reason about.

### Still open after the 2026-07-30 review

Smaller than the four resolved decisions, but each needs an answer before the phase that touches it.

15. What is a patch cell's identity? `PhysicalAudioPatchCell` has no ID, so it is keyed implicitly by
    `(LogicalChannelId, OutputLineId, OutputChannel)`.  
    **Recommendation:** make that key explicit and reject duplicates at validation. With "no implicit
    normalization when several cells sum," two cells on one triple would be an invisible +6 dB.

16. `Guid` or `string` for logical channel IDs at the framework boundary? The model sketch uses
    `Guid`; `IShowProgramAudioTarget.LogicalChannelIds` uses `IReadOnlyList<string>`.  
    **Recommendation:** `Guid` in `HaCue.Core`, opaque `string` in `S.Media.*`, with the conversion in
    the compiler. The framework must not learn HaCue's ID type.

17. How many channels does preview audition, now that it can target a bay-owned multichannel line?
    It is hardcoded stereo today (`VoicePlayer.cs:182`).  
    **Recommendation:** audition into the project's logical channels through a monitoring-only send
    and let the patch place it, rather than adding a second channel-count concept.

18. Do the two apps share one cache/settings root? HaPlay's is `HaPlayStoragePaths.LocalAppRoot`
    (`HAPLAY_CACHE_ROOT`, `HAPLAY_SETTINGS_PATH`), and the YouTube/MMD-bake caches sit under it.  
    **Recommendation:** per-app settings and recovery roots, **one shared media cache**. Two
    applications re-downloading the same video and re-baking the same physics is a worse outcome than
    a shared directory.

19. Does the fixed project mix rate change the standby/pre-roll contract deliberately?
    `BuildClipSpec` derives `TargetAudioSampleRate` from the route's device rate and folds it into the
    standby cache key (`ShowSession.cs:877-905`).  
    **Recommendation:** yes, and say so — a warmed clip becomes device-independent, which is an
    improvement, but the pre-roll tests assert the current keying and must be updated with intent.

20. What happens to the `Ideas/*.md` documents that cue code cites? They are deleted on this branch,
    while `CueList.cs`, `ShowDocument.cs` and others still reference
    `Ideas/CuePlayer-Enhancements.md §4/§6`, `Ideas/Dual-Voice-Crossfade-Design.md §3` and
    `Ideas/CuePlayer-Timeline-Editor.md`.  
    **Recommendation:** re-point them during the Phase 5 file move — it is the one time every one of
    those files is being edited anyway.

## Definition of done

The redesign is complete when:

- HaCue builds, publishes, launches, saves, recovers, and runs cues independently of HaPlay.
- HaCue has no project/assembly dependency on HaPlay.
- HaPlay has no Cue workspace or cue runtime host after the agreed deprecation window.
- A project owns named, stable logical audio channels.
- Cue media channels route only to logical channels in new HaCue documents.
- Real output channels attach many-to-many to logical channels with per-cell gain/mute.
- Changing the real-output patch does not reopen media or rebuild active cue transport.
- Missing devices never fall back silently — including NDI, encode and live-stream lines, and
  including the preview/audition path.
- crossfades, simultaneous groups, fades, envelopes, master trim, preview classification, NDI,
  recording, streaming, and output health retain their tested behavior.
- Old HaPlay/cue fixtures import with an equivalent effective source-to-real matrix.
- Every document edit is undoable and redoable, and no editing surface bypasses the journal.
- The app launches into show mode; editing requires an explicit unlock.
- A project passes preflight, relinks after a move, and consolidates into a transportable folder.
- Patch snapshots recall partially, idempotently, and without interrupting a running cue.
- Full solution tests, routing/clock performance gates, NativeAOT publish, and Linux/Windows launch
  smokes pass.
