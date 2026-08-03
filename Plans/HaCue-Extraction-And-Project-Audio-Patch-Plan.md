# HaCue2 extraction and project audio patch plan

Status: active plan — reviewed against the code 2026-07-30 (four architecture decisions resolved);
updated 2026-08-01 twice: first to agree with the approved rev-3 UI design ("Design decisions from the
2026-08-01 UI pass"), then to absorb the framework audit ("Framework audit decisions D1–D11"). **Both
registers supersede conflicting statements elsewhere in this document**, and
`Plans/HaCue2-Framework-Gap-Analysis.md` supersedes this document's "Current-state findings" wherever
they disagree — it was written against the code, this section partly was not.  
Date: 2026-07-30 · updated 2026-08-01  
Scope: split the Cue Player out of HaPlay into a dedicated **HaCue2** application and replace
per-cue direct-to-device audio routing with a persistent project-level logical-output patch.
Companion documents: `Plans/MockUps/HaCue2/HaCue2 — UI design, all screens.html` (the approved
rev-3 UI spec — authoritative for every screen), `Plans/HaCue2-Framework-Gap-Analysis.md` (the
framework audit — authoritative for what the code currently does), `Plans/HaCue-Feature-Ideas.md`
(enhancement backlog, not first-release scope).

Naming note: the product is **HaCue2** (`UI/HaCue2/…`, `HaCue2.Core`, `.hacue2proj`,
`hacue2 --check`). Where this document says "HaCue" below, read HaCue2; the UI word for a
`LogicalAudioChannel` is **logical output** and the model word never appears on screen.

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

## Design decisions from the 2026-08-01 UI pass (owner-approved, supersedes conflicts below)

The rev-3 mockup review settled the product surface screen by screen. Every item here is an owner
decision; where older text in this document disagrees, this register wins.

**Shell and modes**

1. One window with four views — **CUES · AUDIO · VIDEO · TARGETS** — plus a **Settings view**
   (application + project scopes) and a **Diagnostics window** (remembers its screen). This
   replaces the earlier Cues/I-O/Targets/Project surface list.
2. **The app launches into editing mode.** Show mode survives only as an opt-in **Lock** latch
   (one title-bar chip): read-only document, destructive commands disabled, GO untouched. It is
   never the launch state. This supersedes "show mode as the launch state" everywhere below.
3. **No arming gates tied to edit mode.** GO, preview and audition always work while editing. The
   three HaPlay arms (triggers/schedules/chase) collapse into one **External input** master toggle
   in the transport covering MIDI/OSC/hotkey triggers, wall-clock schedules and MTC chase;
   per-source enables live in the Targets view; default off when a project opens (project
   setting); never gates GO.
4. **No permanent output strip.** Program meters, line chips and bay counters live in an
   **Output info** drawer (status-bar toggle / F9, hidden by default, overlays the active panel,
   poppable to its own window) plus a one-token status-bar summary. Diagnostics holds the deep
   counters. One Diagnostics button, title bar only.
5. **Multi-list transport is v1.** Every cue list keeps its own standby/playhead in model *and*
   UI: the transport acts on the selected list, each list remembers its position, and per-list GO
   is addressable by hotkeys, triggers and the remote API (`POST /lists/{id}/go`). This supersedes
   the earlier single-playhead-with-model-insurance resolution.
6. Cue-row click behaviour is a per-project setting (default: single click view-selects;
   double-click or explicit Stby commands move standby).
7. Right panel of the Cues view carries bottom-edge tabs **Cue properties | Lists & groups**;
   selecting a cue never auto-flips the panel, and Cue properties reopens in the no-selection
   state. Scoping the tree to a list/group happens only via Lists & groups; scope is machine-local
   view state, never a transport boundary, and never hides sounding cues.

**Audio model**

8. UI name: **logical outputs**. `LogicalAudioChannel` stays the model type.
9. **Named Output Groups are v1.** A group ("Main", "Fold") links its members for *editing*:
   changing one member's patch-cell gain/mute applies the same delta to the other members'
   corresponding cells, and cue send grids show grouped columns with the same linked-delta
   behaviour. A stereo pair is a two-member group. Grouping affects editing and display only —
   the mix math stays per-channel (provisional decision 3 stands).
10. **No per-logical-output trim.** Patch-cell gains are the only project-side gain stage; the
    gain-composition chain in "Gain composition" is unchanged.
11. **Deleting a logical output cleans up automatically**: its sends are stripped from every cue
    as one undoable journaled command. This supersedes the explicit
    cancel/remove/rebind choice in the model invariants.
12. Patch cues ship with both snapshot recall **and** the inline `PatchLevelChange` list.
13. **Solo-to-audition is always allowed** on a patch line — it plays through the audition
    monitoring path, never interrupts the program mix, and never appears in the Active list.
    Patch-cell interaction: click toggles at unity, drag adjusts gain, right-click mutes.
14. Audio lines are **project-owned** (travel with the show, go absent elsewhere, relink on
    arrival). Mix-rate changes with a show loaded take an explicit "Apply & restart audio".
    Per-line effect inserts are deferred (visualizer feeds tap the program bus upstream of
    inserts, so deferral costs nothing).
15. **Audition is one rig**: an audio device plus a video surface (window / dedicated screen /
    none), configured in an Audition pane duplicated in the Audio and Video views. It enters the
    bay as a monitoring input. Every cue's context menu gets "Preview on audition outputs".

**Cues and editing**

16. **Fade curves**: every curve picker shows drawn thumbnails; the last option is **custom** — a
    point-drag editor with smooth/hold segments, a **dB ⇄ linear** scale toggle, and curves
    saveable as **project presets**. Same control everywhere a curve is picked (fades,
    crossfades, patch cues).
17. Playlist groups get **crossfade presets** (cut / 0.5 s / 2 s / 4 s / custom) with a curve, and
    **per-transition overrides** set on the child cue.
18. **Effect lanes replace `VolumeEnvelope`** as the one envelope concept: volume, opacity and
    outbound OSC/MIDI ramps are lanes *added* per cue or per group (hidden until added), edited in
    both the inspector and the timeline against the same data. A child's effect overrides the
    group's same-kind effect, with a warning badge on the child. The timeline is a bottom sheet by
    default and **undockable** to its own window (mode remembered per machine); groups collapse to
    one span-clip, drag as a whole, and accept group-level effect lanes; disabled children render
    hatched with struck labels.
19. **Note** (singular) replaces Notes/Script: one tab on every cue kind; a comment cue is just
    its note. Notes stay writable under Lock.
20. Auto-renumber on insert defaults **on**, seeded from an application-scope **New project
    defaults** group (which also seeds Main L/R patched to the machine's default device, mix rate
    and default fades) and overridable per project.

**Video**

21. **Compositions carry no visualizer flag** — the visualizer is purely a cue type and its
    settings live on the cue; its canvas presence is an ordinary placement. A composition owns
    exactly: size, frame rate, idle image. (`CueComposition.VisualizerEnabled`/`…PresetDirectory`
    do not migrate into the HaCue2 model.)
22. **Mapping (splitting + mesh warp) is per output binding**, toggleable on/clean per output —
    the same composition renders warped to a projector and clean to a TV/NDI. Video-view tab
    order: Compositions · Mapping · Outputs. Mesh warp first, corner-pin stays reserved. A
    per-output test pattern ships in v1. Mapping editing supports mouse drag, numeric x/y/w/h
    entry, and arrow-key nudging of the last-clicked point.
23. **Idle images exist at both levels**: per output as a fallback, with the composition's idle
    image taking precedence when set.

**Targets, status, settings**

24. Targets view splits into tabs: **Action endpoints · Trigger inputs · Remote API**, each with a
    direction-filtered wire monitor; the Remote API tab lists its actual endpoints with call
    counts. **Continuous-controller bindings to parameters** (e.g. cc → master trim) are v1, not
    just note→cue. Endpoint test messages are configurable per endpoint.
25. **Preflight is renamed "Project status"** — a collector of errors and warnings with a fix
    action per row, plus relink and consolidate. Headless form: `hacue2 --check` (exit 1 while
    errors remain). Severity: unpatched-but-fed logical output = error; patched-but-unfed and
    absent devices = warnings — except outputs flagged **required**, whose absence is an error.
26. Settings split hard into application scope (machine, `app-settings.json`, not journaled) and
    project scope (journaled, undoable), with a per-project **override ledger** (overridable set
    frozen for now: panic fade, remote API, hotkeys — a project override always wins and is always
    visible in both scopes). Standing principle: the more user-changeable defaults, the better.
    Media may live outside the project's media root: adding such a file warns and offers
    move/copy, with a project setting choosing the default action (keep in place / move / copy —
    keep is default).
27. Diagnostics' event panel is a level-filtered live tail of the **`Microsoft.Extensions.Logging`
    pipeline** — the same sink as the file log; no second event-collection system.
28. **One undo journal across all views** (cues, patch, compositions, project settings); the undo
    toast names the domain ("undid: patch — Fold L → Out 3 gain").
29. **No built-in HaPlay importer.** HaCue2 is a clean start; `.haplayproj`/`.haplaycues`
    migration becomes a separate companion converter later, with no priority. This supersedes
    provisional decision 2 and the "HaPlay project import" section (kept below as the future
    converter's reference algorithm).
30. Recording file patterns support insert tokens (`{date} {time} {project} {list} {n}`) with an
    insert dropdown and help popover. The launcher (recents, inline recovery, machine device
    checks) opens projects in editing mode.

## Framework audit decisions (2026-08-01) — D1–D11

A six-part audit of `MediaFramework/` against the rev-3 UI and the register above produced
`Plans/HaCue2-Framework-Gap-Analysis.md`. **That document is authoritative for current-state facts**
about the framework; where the "Current-state findings" section below disagrees with it, the gap
analysis wins — its §8 lists 21 specific statements in this plan that the code contradicts, with
`file:line` evidence. Read it before starting any phase.

The audit raised eleven decisions, all owner-answered:

| # | Decision | Effect on this plan |
|---|---|---|
| D1 | **HaPlay keeps evolving alongside HaCue2** | Forking `ShowSession` is off the table. The shared engine needs a *real* cue/engine seam, and document version tolerance becomes mandatory (two live apps + the C ABI cannot share a hard `Version ==`). See gap analysis §10. |
| D2 | **Build a clock-master watchdog** with pre-emptive detach | New Phase 3 item, not present in the recovered work. A wedged pacing master currently faults and permanently kills the router. |
| D3 | **Outbound OSC/MIDI ramps are v1** | The automation-lane work is three features (internal audio, internal video, outbound sender), not two. Settle the three contract points in gap analysis §3.2 before fixing the lane record's shape. |
| D4 | **Control-surface feedback extracts with `HaControl.Input`** | Phase 2 grows: the LED/scribble/motor-fader half leaves HaPlay's Control workspace too. Scope = shared sender + HaCue2-owned feedback mapping, *not* a port of HaPlay's mixer-surface logic. |
| D5 | **Multiple visualizer cues per composition must work** | Turns register item 21 from "delete dead fields" into real framework work: key visualizer slots by cue id, one surface per source, and a documented cap (N visualizers = N renderers on one composition GL thread). |
| D6 | **Disabled-cue auto-follow is a project setting** | Skip-onward vs stop-the-chain. The framework implements only *stop* today, and **both** the framework chain and the host's `ClipNaturallyEnded` path must honour the setting. |
| D7 | **`MaxReleasingVoices`: bounded N, default 3** (delegated) | Per transport group, so the real ceiling is N × active lists — check that against the 8-voice budget. Fade the oldest tail when the bound is hit rather than hard-cutting. |
| D8 | **Audition channel count follows the configured audition output** | **Settles open question 17** with a third answer: the audition rig is an output like any other and takes that output's width. Never hardcoded stereo. |
| D9 | **Headless status CLI verb is draft** | `--check` / `--preflight` naming is unfixed; treat every occurrence as a placeholder. The headless form itself still ships. |
| D10 | **`UI/**/*.Tests` are exempt from arch rules** | As `Test/` already is. One line in the new UI scope — which must be *added*, since the arch tests cannot see `UI/` at all today. |
| D11 | **CI covers the extraction branch** | **Done**: `.github/workflows/build.yml` now triggers on `[master, 'next-*', 'cue-*']`. |

**The single most important audit finding is not a decision but a discovery:** most of the Phase 3
framework work already exists, tested, on an unreferenced commit now preserved as tag
**`hacue-archive-2026-08-01`** (`bdf27ffd`) — `AudioPatchBay`, `ProgramBusSource`, `AudibleClientClock`,
`ShowProgramAudio`, the `ShowSession` integration, the preview monitoring seam, the wide-matrix
benchmarks and ~1100 lines of tests, with near-zero drift from today's HEAD. **Push that tag.** Phase 3
is a recover/review/rebase job, not greenfield.

**The largest uncosted gap** is also not in this plan: `ShowSession.LoadDocumentCoreAsync` unconditionally
tears down every transport group, destroying every list's GO cursor *and stopping every playing voice in
every list* — while the app reloads on a 300 ms debounce after any edit. Register item 5 (multi-list
transport) and item 3 ("editing never blocks playback") are both blocked on making that load
non-destructive. See gap analysis §2.1.

## Provisional product decisions

These recommendations let implementation proceed. The questions that still need an owner decision
are collected at the end.

1. HaCue owns one open project at a time and writes a new HaCue project format.
2. ~~Existing `.haplayproj`, `.haplaycues`, and `.haplaycuelists` files are import formats~~
   **Superseded (register item 29):** HaCue2 has no built-in importer — clean start; a companion
   converter may exist later. The old formats are never jointly edited either way.
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
    the **Lock latch** (register item 2 — the app launches into *editing* mode; Lock is the opt-in
    read-only state that replaced "show mode as the launch state"), **patch snapshots and patch
    cues** (only meaningful once a persistent patch exists), and **Project status /
    relink / consolidate** (a project-level concern; "preflight" throughout this document reads as
    Project status per register item 25). Each has its own section below. The wider enhancement
    backlog lives in `Plans/HaCue-Feature-Ideas.md` and is explicitly *not* first-release scope.

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

### Two dead half-built versions already exist in the session layer

`S.Media.Session/ClipAudioOutputRuntime.cs` (310 lines) is documented as "owns one `AudioRouter`
feeding one physical `IAudioOutput`, and lets multiple cue clips add/remove routed sources" — the
per-output half of what `AudioPatchBay` needs. It is referenced by nothing outside itself.

**Settled by the audit: delete it, do not seed from it.** Its topology is precisely the `P×R` form this
plan replaces — one router per *terminal* at the **output's** rate, every clip source routed directly to
that terminal per cell, with no lease, no matrix reconciliation, no monitoring and no shared mix rate.
The only reusable ideas are its base-gain bookkeeping and a fade helper that duplicates what
`RouteGainSlot`'s per-chunk ramp already does better. The recovered archive already deletes it.

**A second decoy was found in the same folder:** `SoundboardGrid.cs` (267 lines) has **zero external
references** anywhere in `MediaFramework/` or `UI/` — only its static `SoundboardQuantization` helper is
live, consumed by `SoundboardWorkspaceViewModel`. Delete the rest with it. Together the two are 577 lines
shaped like responsibilities the session layer was once expected to own, and leaving them in place is how
the next reviewer concludes the framework owns soundboard concepts.

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

`HaControl.Input` — also missed.

> **CORRECTED 2026-08-01 by the framework audit.** This section originally claimed MIDI/OSC input
> **device** ownership lives in `ControlWorkspaceViewModel`. That is **wrong**. The device layer is
> already framework-side and Avalonia-free in `S.Control`: `ControlInputSession` provides ref-counted
> device open/close, an `InputObserved` monitor-record stream and dispatcher/monitor leases, alongside
> `ControlMIDIPortCatalogProvider`, `ControlMIDIDeviceResolver`, `ControlSystemMIDIDeviceSessions`,
> `ControlOSCListenerManager`, `UdpControlOSCSender`, `ControlSystemConfig`/`IO` and
> `ControlDeviceHealthRegistry`. The extraction is therefore smaller and lower-risk than this plan
> assumed. See `Plans/HaCue2-Framework-Gap-Analysis.md` §5.1.

What is actually stuck in HaPlay and must move:

- **session lifecycle glue** — `ControlWorkspaceViewModel.MidiFallback.cs:165-280`: the session factory,
  config-signature diffing, retry-on-failed-open, the sync gate and teardown ordering (~120 lines of
  carefully-commented lifetime code every host needs, reachable today only through an Avalonia ViewModel);
- **the learn flow, which exists twice** — a control-workspace implementation for script triggers and a
  separate, simpler one for cue triggers — to be unified into one record→binding capture service (the
  pure parts are already `internal static` and Avalonia-free, so they lift cleanly);
- **the cue trigger matcher** (`CueTriggerService`'s MIDI/OSC match, map, latch and retrigger guard),
  Avalonia-free except its hotkey path and localized strings;
- `ActionEndpoint` outbound targets plus `ActionEndpointProbe` / `EndpointHealthMonitor` (the latter
  needs its `DispatcherTimer` swapped for a timer abstraction);
- the host fan-out in `MainViewModel.cs:107,215-231` — the I/O-thread pre-filter → chase → accept →
  UI-thread post chain, 15 lines that encode the whole performance contract;
- **the control-surface feedback half (D4)** — see the Phase 2 bullet.

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
- Deleting a referenced logical channel strips its sends from every cue automatically as **one
  undoable journaled command** (register item 11 — supersedes the earlier explicit
  cancel/remove/rebind choice).

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

> **Scope widened 2026-08-01 (D1).** With HaPlay confirmed as a continuing product, `ShowSession` now has
> **two first-class customers**, and the audit found the fault line is not HaPlay-vs-HaCue2 but
> **engine-vs-cue-semantics**. Evidence: HaPlay already contains *two* mappers targeting `ShowDocument` —
> the cue mapper and `MediaPlayerShowMapper`, where the **deck** wraps a single playlist item in a
> one-clip document and invents a synthetic cue named `"player"` just to reach the engine. Only **one of
> `ShowDocument`'s six members** (`Cues`) is a cue concept, and `ShowDocument.cs` defines zero cue types.
> Roughly **3,900 of `ShowSession`'s 4,150 partial lines are app-neutral**; the cue-shaped surface is
> `CueGraph` (343), `CueFireOrchestrator` (216), `GoAsync` + cursor (~35) and one `AddCue` loop.
>
> **Forking is therefore rejected** — it would duplicate ~10,000 lines of concurrency- and clock-critical
> code (the claim/supersession protocol, `CommitClipAsync`, composition lifetime, device acquisition) that
> is not cue-specific and would never legitimately diverge. The target is a shared engine with a liftable
> cue layer:
>
> - **`ShowSession` core, cue-free** — load, clip commit, transport-by-clip-id, seek/pause/stop/level,
>   compositions, live edits, taps, queries. Rename the join key `CueId` → `ClipId`; the deck already
>   treats it as an opaque string, so this is a rename, not a redesign.
> - **Cue layer on top** — `CueGraph` + `CueFireOrchestrator` + `GoAsync`/cursor + the `AddCue` loop become
>   a cue runner that *drives* the core (~600 lines, along seams that already exist).
> - **Split the `ShowDocument` record, keep one serialization envelope** so the sidecar and the C ABI do
>   not fork.
> - **Version tolerance is mandatory**, not optional (see Phase 4): two live apps plus an external ABI
>   consumer cannot share a hard `Version ==` check without lockstep releases.
>
> Full reasoning and evidence: `Plans/HaCue2-Framework-Gap-Analysis.md` §10.

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
- Preview uses an injected monitoring output provider, **at the audition output's own channel count**
  (D8) rather than the current hardcoded stereo.
- Soundboard voice APIs should leave the cue session boundary — and the audit shows this is cheaper than
  assumed: the soundboard ViewModel **never references `ShowSession`**, the voice half of `VoicePlayer`
  (~430 lines) touches no document, cue, group or composition, and the stop-claim protocol it shares is
  already an extracted type. Give it `(dispatcher, standby, soundingRegistry, deviceCache)` and the
  session's eight voice methods become removable one-line delegations. **This is the recommended first
  concrete cut of the whole extraction.**

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

The shell is settled by the rev-3 mockups (authoritative:
`Plans/MockUps/HaCue2/HaCue2 — UI design, all screens.html`): one window with **CUES · AUDIO ·
VIDEO · TARGETS** views, a **Settings view** (application + project scope with an override
ledger), and a **Diagnostics window**. The Audio view carries Logical outputs / Patch / Devices /
Audition panes; the Video view carries Compositions / Mapping / Outputs / Audition. There is no
permanent output strip — an **Output info** drawer (status-bar toggle / F9, poppable to a window)
plus a one-token status summary replace it. The launcher (recents, inline recovery, machine device
checks) opens projects in editing mode.

### The Lock latch (supersedes "show mode is a state")

Today "am I editing or running a show" is `CuePlayerViewModel.IsCueEditMode`, a flag inside a
six-workspace shell that schedules and triggers are gated on (`CuePlayerViewModel.cs:348,448`).
The rev-3 decision inverts the earlier plan: **the app launches into editing mode**, and the old
show mode survives as an opt-in **Lock** latch (register item 2):

- Lock is one title-bar chip: engaging it makes the document read-only and disables destructive
  commands. GO, transport, preview and audition are untouched.
- **No arming is tied to edit state.** The edit-mode gate on schedules/triggers is deleted;
  instead one **External input** master toggle in the transport covers MIDI/OSC/hotkey triggers,
  schedules and MTC chase, with per-source enables in Targets and "off at project open" as a
  project setting (register item 3).
- A project may opt to open locked (Show behaviour · "Open in"), but editing is the default.
- Notes stay writable under Lock.

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

The Audio view's panes are **Logical outputs · Patch · Devices · Audition** (rev-3 screens 06–08).
Named **Output Groups** (register item 9) appear as a column in the logical-outputs table and as
linked-delta editing in both matrices. Provide two related panes:

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

### HaPlay project import — deferred to a companion converter

**Superseded as first-release scope (register item 29): HaCue2 ships without an importer.** The
material below is retained as the reference algorithm for the later stand-alone converter, because
the route→logical-send conversion rules were carefully worked out and should not be re-derived.

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
- **Push the archive tag `hacue-archive-2026-08-01` before anything else** — it is the only copy of the
  recovered framework work (gap analysis §0) and it exists locally only.
- **Recover and read the archived framework subset** onto a working branch. It settles several Phase 0
  questions outright and changes what Phase 3 is.
- `ClipAudioOutputRuntime`'s fate is **settled: delete it.** It is confirmed dead (zero external
  references) and is explicitly *not* a usable seed — its topology is the `P×R` form this plan replaces.
  The archived work already deletes it. **Also delete `SoundboardGrid.cs`** — a second dead file in the
  same folder, 267 lines, zero external references, keeping only the live `SoundboardQuantization` helper.
- The wide-matrix benchmark this phase calls for **already exists** in the archive
  (`WideMatrixBenchmarks.cs`: an 8/16/32/64 sweep plus a program-sum-at-maximums case). Recover and run
  it rather than writing it; set the cell-op budgets from those numbers.
- Define latency and matrix-size budgets before the new audio stage is implemented.
- **Decide the clock-master watchdog's trigger policy** (D2) — this is a design decision that belongs
  here, before Phase 3 implements it in the router's fault path.

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
- Split `CueShowSessionCoordinator` into cue transport and soundboard hosting. **The audit makes this
  cheaper than it looks**: the soundboard ViewModel never references `ShowSession` at all — it exposes a
  `PlaySoundCallback` and the coordinator wires it onto the cue session, so the coupling is a wiring
  decision, not a design one (gap analysis §10.1).
- **Extract `VoicePlayer`'s soundboard half** (~430 of its 647 lines) into a standalone voice engine
  taking `(dispatcher, standby, soundingRegistry, deviceCache)` instead of `ShowSession`. It touches no
  document, no cue, no transport group and no composition; the stop-claim protocol it needs is *already*
  a shared type. The session's eight voice methods (`ShowSession.cs:947-987`) are one-line delegations
  that then fall away. This is the cheapest correct cut in the whole plan and it removes the
  "soundboard depends on the cue player" liability outright.
- Replace its concrete `OutputManagementViewModel` dependency with a focused runtime-catalog
  interface. Note `IOutputRuntimeCatalog` does not exist today and has **six** concrete-type consumers to
  invert (gap analysis §7.1).
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
- Extract `HaControl.Input`. **Correction from the audit: the device layer is already framework-side**
  (`S.Control`'s `ControlInputSession` owns ref-counted open/close, the `InputObserved` monitor stream and
  the dispatcher/monitor leases), so this plan's earlier premise that device ownership lives in
  `ControlWorkspaceViewModel` is wrong. What actually moves is ~120 lines of session lifecycle glue
  (`ControlWorkspaceViewModel.MidiFallback.cs:165-280`), **two duplicate learn implementations** that
  should be unified, the cue trigger matcher, and the `ActionEndpoint`/probe/health types. It is
  lift-and-rehome, not a rewrite (gap analysis §5.1).
- **Extract the control-surface feedback half too (D4)** — LED, scribble strip, motor faders. The senders
  already exist framework-side (`IControlMIDISender`, `UdpControlOSCSender`) and `ControlFeedbackMode`
  already models echo suppression; what moves is the mapping layer and the throttle. First consumer is
  standby/active state, which is the "which cue is standing by" gap.
- Keep current HaPlay I/O UI as an adapter over the service.
- Preserve the acquire/hold/detach/release ordering documented by the cue video path.
- Add service-level tests for PortAudio shared clients, NDI carrier sides, armed encode sinks,
  output replacement, failure isolation, and lease/raw-terminal mutual exclusion.
- **Add a `UI/` scope to the architecture tests first** — they currently walk only `MediaFramework/`, so
  `UI/` is under *zero* layering enforcement and "register the new libraries" is impossible until the
  scope exists. `UI/**/*.Tests` are exempt (D10); the allow-map must permit out-of-tree names for the
  `External/Classic.Avalonia` references.

Exit: HaPlay and a minimal test host can both use the same output runtime service without sharing a
root ViewModel, and nothing HaCue needs from the cue domain still lives behind a HaPlay-only owner.

### Phase 3 — recover, review and rebase the project audio patch

**Retitled after the audit.** The bullets below were written as greenfield work; **most of them already
exist**, tested, at tag `hacue-archive-2026-08-01`. Treat each as a review checklist against the recovered
implementation rather than a build list.

Already built and tested in the archive (gap analysis §0):

- the router's V-wide program-sum stage and the `AudioPatchBay` owning its terminals outright;
- the injected resampler factory, with a clock master at a foreign rate rejected as a *named* validation
  failure;
- client clock/latency extracted from `SharedAudioOutput` into `AudibleClientClock`, reusing the
  epoch/lead machinery rather than re-deriving it;
- terminal quarantine + hot-swap, including the fix that moves deferred pump disposal **off the run-loop
  thread** (inline, it stalled every other terminal for up to ~3 s);
- patch validation, the `ShowSession` program-audio target, the monitoring seam with `VoicePlayer`'s
  preview borrowing a lease instead of opening its own device;
- logical clip sends (`ShowClipLogicalSend`) retaining the v1 direct-route adapter, plus
  `ApplyActiveLogicalSendsAsync` for live send edits.

Genuinely new work in this phase:

- **The clock-master watchdog (D2)** — detect a stalling master from pump pressure/in-flight depth and
  hand the clock off via `RetargetSlaveClock` (the only running-safe path) *before* `RunLoop` faults,
  falling back to `NullClockedAudioOutput` when no other terminal qualifies. The watchdog must not run on
  the router thread, false positives must be inaudible, and "report and stop" remains the fallback when
  the handoff itself fails.
- **The non-destructive document load** — the largest uncosted item in the plan. `LoadDocumentCoreAsync`
  currently calls `DisposeGroupsAsync()` unconditionally, wiping every group's GO cursor and stopping
  every playing voice in every list, on a path the app takes after every structural edit. Both
  multi-list transport (register item 5) and "editing never blocks playback" (item 3) depend on fixing
  it — and under D1 this is **shared work, not HaCue2 tax**, because HaPlay wants editing-while-playing
  too. Fold it in here: it is a `ShowSession` change and this is when that file is open.
- **Per-logical-output metering** — a peak/RMS tap on the program bus. Nothing in the framework measures
  audio level today, and three rev-3 screens need it.
- Raise `MaxReleasingVoices` to a bounded N (D7) and make the loop-crossfade curve data rather than
  hardcoded `EqualPower`.

Exit: unchanged from the original — several simultaneous/crossfading voices through named logical
channels to several fake outputs, hot-changing the physical patch, quarantining a wedged terminal and
keeping the voices running, with preview auditioning through a bay-owned line without double-opening it —
**plus** a wedged *master* surviving via handoff, and a document reload that leaves untouched lists
playing.

> **Sequencing warning.** This phase now carries the bay recovery, the load-path change and a fault-path
> watchdog — three structural changes to the same files. The plan's own risk register warns against
> stacking structural work; if any phase needs splitting, it is this one.

### Phase 4 — create HaCue2 project/core

- Add `HaCue2.Core` models, validation, source-generated serialization, project hashing, atomic
  I/O, and compiler to ShowDocument. (Migration/import is out of scope — companion converter
  later, register item 29.)
- Use stable logical channel IDs and the two-matrix model, plus named Output Groups (editing-only
  linkage, register item 9), per-list standby state for the multi-list transport (register
  item 5), custom fade curves + project curve presets (register item 16), effect lanes replacing
  `VolumeEnvelope` (register item 18), the required-output flag (register item 25), and the
  composition model without visualizer fields (register item 21).
- **Effect lanes are three actuators under one editor (D3)**, and the lane record's shape depends on
  settling the outbound contract first: an outbound lane is **not** undone when its cue stops (the
  opposite rule from internal lanes, because it owns a value in another system); Panic's behaviour toward
  an in-flight ramp must be explicit; and the runner lives beside the action-endpoint sender, never in
  `ShowSession`. Internal lanes must keep multiplying into the documented composition chain rather than
  becoming a second gain authority — and note a **layer-opacity lane cannot ride
  `UpdateActivePlacementAsync`**, which does not refresh `BaseLayerOpacities` and would fight fades;
  video needs a multiplicative automation component mirroring `SoundingLevel`, which it does not have
  today.
- **Custom fade curves reuse an evaluator that already exists.** `VolumeEnvelopes.Sample` is already
  piecewise interpolation over a point list with a per-segment curve — structurally identical to a
  normalized custom curve. The gap is only that a *fade* takes the `FadeCurve` enum by value. Widen the
  type behind `FadeCurves.Shape` (one switch) and thread it through ~15 signatures; add the curve as a
  **nullable companion field, not a new enum member** (see the enum caveat above).
- **Build the edit-command journal in from the start** (see "Editing model: an undo journal, decided
  now"): commands over `HaCueProject` with `Apply`/`Revert`, coalescing groups, composite multi-edits,
  and the dirty/hash/autosave commit point hanging off it. Every editing surface added later in Phase 4
  and Phase 5 goes through it — this is the phase where that is still cheap.
- Add `PatchSnapshot` and the `PatchCueNode` kind to the model, validator, and compiler, including
  broken-reference reporting for snapshot cells.
- Add a `Disabled` flag to `CueNode` — skipped by GO, auto-follow and compilation, still visible. **The
  framework half already exists** (`CueDefinition.Enabled`, and GO already filters on it); the work is the
  app model plus mapping it through. Add the D6 project setting for whether a disabled cue **skips
  onward or stops** an auto-follow chain, and make both the framework chain and the host's
  `ClipNaturallyEnded` path honour it.
- Add uniform reference counting and a reverse-reference ("what targets this?") query over cues,
  logical channels, snapshots, compositions, and endpoints — the delete-safety machinery the plan
  already requires for logical channels, generalized.
- Add the project-status validator as a pure, headless-runnable pass over a loaded project, plus relink
  and consolidate as journaled commands. **Two audit notes**: a real document validator already exists
  (`ShowDocumentValidator`) and covers every dead-reference class, so this is the *environment-aware*
  second pass beside it (missing media, absent devices, clock master) — and every probe it needs already
  exists, including the device-matching logic buried in `PortAudioOutputRuntime`'s open path. Upgrade the
  return shape from bare strings to records carrying severity, subject id and a navigation target, which
  rev-3 needs per row.
- **Adopt additive-nullable document evolution as a rule** (D1): HaCue2-only members are nullable and do
  not bump `ShowDocument.Version`, following the archived `LogicalSends` precedent. **Enum extension is
  the exception** — enums round-trip numerically and the sidecar's real reader is the C ABI host, so a new
  `FadeCurve` variant would be silently mis-read; use the nullable `CustomCurve` companion instead. Move
  `SupportedVersion` from a hard `==` to `MinSupported`/`Current` with tolerant loading **before**
  divergence starts.
- Add the logical-channel and physical-patch ViewModels plus pure matrix editing operations.
- Port cue-specific tests to `HaCue2.Tests` — and copy the headless harness **wholesale**, including
  `HeadlessSessionBootstrap.cs`, whose custom xunit framework warms the Avalonia session before any test
  runs. A test assembly that copies the csproj but not that file reproduces a whole-run hang whose
  occurrence depends on test order.

Exit: a HaCue2 project round-trips, its status pass runs headlessly, and every document addition is
additive-tolerant.

### Phase 5 — create and prove the HaCue2 application

- Add `HaCue2`, `HaCue2.Desktop`, and launch-smoke wiring.
- Move cue views/ViewModels/services/resources rather than copying them, reshaping to the rev-3
  screens. Route every editing command through the Phase 4 journal as each surface lands — a
  surface that mutates directly is a surface whose undo silently does nothing.
- Add the rev-3 shell: CUES/AUDIO/VIDEO/TARGETS views, Settings view, Diagnostics window,
  launcher, Output info drawer — **launching into editing mode**, with the Lock latch and the
  single External-input toggle (register items 1–4).
- Add the operator surfaces: multi-list transport with per-list standby (register item 5),
  active-cues panel, per-cue disable, the Note tab, the curve editor, effect lanes and the
  undockable timeline, and the Audition rig with per-cue "Preview on audition outputs".
- Add the patch snapshots pane and the patch-cue editor.
- Add the Project status UI over the Phase 4 validator, plus the relink and consolidate flows,
  and expose it headlessly (exit code + machine-readable report) so it can gate a fixture project in CI.
  The CLI verb itself is **draft** (D9).
- Wire the existing media modules required by cue sources and outputs.
- **Allow N simultaneous visualizer cues per composition (D5).** Today the visualizer slot dictionary is
  keyed by composition and `Attach` *replaces* the existing one, so only one can run per canvas. Re-key by
  cue id and give each source its own surface, honouring the hard constraint that `CreateLayerSurface()`
  may be called at most once per source (projectM crashes otherwise) with layer 0 owning disposal. Note
  the ceiling is real: N visualizers are N renderers on that composition's single GL thread, so this needs
  a documented, validator-enforced cap rather than being discovered at a get-in. The z-order limitation
  (surface layers always render above frame layers) is **separate work** and out of scope unless called.
- **Close the video shortfalls rev-3 assumes exist**: a per-**output** test pattern and Identify flash
  (today's pattern is composition-wide and would light up every bound output while you calibrate one),
  composition-owned idle images with a per-output fallback (today's per-output idle is suppressed the
  entire time a cue list holds the line), and the audition **video** surface.
- Port project recovery, preview, schedules/triggers, remote API (including
  `POST /lists/{id}/go` — which depends on the per-list standby from Phase 4, not merely on a new route),
  composition mappings, and projectM behavior.
- **Add the diagnostics package**: a non-summing terminal+lease telemetry query (the current session query
  discards all but enqueued/dropped), a clock epoch/advancing/latency snapshot (values exist on live
  objects but nothing snapshots them), an in-memory ring `ILoggerProvider` with a volatile level switch
  for the log tail, and a report serializer for "Copy report".
- Run HaPlay and HaCue2 side by side against equivalent fixture shows and compare compiled
  documents/effective routes.
- Add NativeAOT publish and Linux/Windows launch gates mirroring HaPlay's current policy.

Exit: HaCue2 is independently buildable, publishable, smoke-tested, and usable for a show without
loading HaPlay — launching into editing mode, passing Project status checks on a transported
project, and with undo working on every editing surface it ships.

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
- (moved to the companion converter, register item 29: HaPlay fixture migration and dry-run report);
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
- the app launches into editing mode; Lock makes the document read-only without touching GO,
  transport or audition; the External-input toggle gates triggers/schedules/chase as one switch
  and per-source enables compose with it;
- per-list standby survives list switching, GO acts on the selected list, and
  `POST /lists/{id}/go` fires the right list;
- undo/redo works from every editing surface the app ships (cue list, inspector, multi-edit,
  timeline, both matrices, snapshots, compositions, project settings) — a per-surface test, not
  one generic one — and the undo toast names the domain;
- Output Group linked-delta edits change every member's cells and coalesce into one undo step;
- deleting a logical output strips its sends everywhere as one undoable command;
- a patch cue fired mid-show changes the live patch without reloading the document or interrupting a
  playing cue;
- Project status UI and `hacue2 --check` agree on the same project, including the required-output
  severity rules;
- audition (cue preview and patch-line solo) never joins the Active list and never disturbs
  program playback;
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
   **2026-08-01 · answered:** atomic channels, with named **Output Groups** in v1 providing
   linked-delta *editing* over members (register item 9). Routing stays per-channel.

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
   **2026-08-01 · answered:** explicit — the Devices pane's "Clock master" picker (rev-3
   screen 08), with the native-rate constraint stated inline.

9. May logical-channel add/remove be blocked while program cues are active in the first release?  
   **Recommendation:** yes. Patch gain/mute/attachment remains live; changing channel width waits for
   a safe transport boundary.

10. Do you need delay, polarity inversion, solo, or per-logical-channel effects in the first audio
    patch?  
    **Recommendation:** per-cell gain/mute only. Add delay/polarity/effects after the topology and
    clock are stable.  
    **2026-08-01:** solo landed in v1 as **solo-to-audition** (register item 13) — a
    non-persisted listen through the audition monitoring path, not a program mute of other
    channels. Delay/polarity/inserts stay deferred.

11. Should output-device definitions remain inside each HaCue project, or should projects bind to
    separate machine profiles?  
    **Recommendation:** keep them in the project for the first release, with explicit rebind for
    missing devices. A portable machine-profile layer can be added without changing logical
    channel IDs.

12. When an imported old project has no direct route for a cue, should that cue remain deliberately
    silent?  
    **Recommendation:** yes. Preserve the current HaPlay no-hidden-default behavior.  
    **2026-08-01:** deferred with the importer itself (register item 29) — applies to the
    companion converter when it exists.

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
    ~~**Recommendation:** audition into the project's logical channels through a monitoring-only send~~  
    **ANSWERED (D8, 2026-08-01):** neither stereo-forever nor audition-into-logical-channels. **The
    audition rig is an output like any other and takes that output's own channel count** — read from the
    configured audition device's format, never hardcoded. This is the only one of the three candidate
    answers that survives auditioning through a multichannel interface. Rev-3 gives the rig its own
    Audition pane (audio device + video surface) in both the Audio and Video views.

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
- Every document edit is undoable and redoable, and no editing surface bypasses the journal.
- The app launches into editing mode; Lock is available and read-only; External input is one
  master toggle that never gates GO.
- Every cue list keeps its own standby; the transport and the remote API can drive lists
  independently.
- A project passes Project status checks, relinks after a move, and consolidates into a
  transportable folder. (Fixture import parity moves to the companion converter's definition of
  done.)
- Patch snapshots recall partially, idempotently, and without interrupting a running cue.
- **A document reload leaves untouched cue lists playing** — editing one list never stops another, which
  is what makes both multi-list transport and edit-while-playing real (D1: HaPlay benefits equally).
- **A wedged clock master is survived, not fatal** — the watchdog hands the clock off before the router
  faults, and "report and stop" is only the fallback when the handoff itself fails (D2).
- **Program levels are measured, not inferred** — per-logical-output meters read real peak/RMS from the
  program bus.
- **HaCue2 does not depend on the soundboard**, and HaPlay's soundboard does not depend on the cue
  engine.
- Full solution tests, routing/clock performance gates, NativeAOT publish, and Linux/Windows launch
  smokes pass — **including a HaCue2 AOT-publish gate**, which has no equivalent today and without which
  an AOT regression ships silently.

---

## Phase 5 progress — 2026-08-03

Four slices, on branch `cue-separation`. `Plans/HaCue2-Framework-Gap-Analysis.md` carries the
register-level detail; this is the phase-level view. Verified: solution 0/0, `HaCue2.Core.Tests` 235,
`HaCue2.Tests` 37, arch 27, session 347, core 781.

### Slice 1 — the transport, and the bugs under it

The shell was a well-built editor over an engine that could only play media cues. Closed:

- **Control-flow cues execute.** `ShowHost` resolves every kind app-side, as this plan says the
  transport layer must: group fire modes (all-together / playlist / timeline-by-offset), jump
  (random pick, cross-list targets, fire-on-arrival), patch (snapshot recall + inline levels, ramped
  in dB by `PatchRamp`), fade (session stop to silence, live send-gain rewrite to a level), action
  (OSC; a MIDI endpoint is **refused out loud** rather than reported as sent). Pre/post waits and
  auto-continue are honoured for every kind, with a 64-deep chain bound so an authored jump loop is
  one reported line instead of a hang.
- **Every cue kind is now emitted into the document** with a `CueDefinition` and, for the control-flow
  kinds, no clip. Not cosmetic: `SetStandbyCueAsync` refuses an id the document does not carry, so a
  jump absent from it could never be made standby and GO stepped straight over it.
- **The patch reaches a running show** — `ProjectPatchBay.Apply` → `AudioPatchBay.UpdatePatch`.
  Adding or reordering logical outputs while live is refused with a message; the bus width is fixed
  when the bay opens.
- **Telemetry is measured** — `BayPresentation` over `SnapshotDiagnostics()` replaced invented rows on
  the one screen an operator opens to ask why there is no sound.
- Transport semantics per the round-3 decisions: bare STOP stops the selected active cue (stop-all in
  a split menu), PAUSE toggles, PANIC holds ~400 ms.

### Slice 2 — the machine store

`HaCue2.Machine.StoragePaths` / `AppSettings` / `RecoveryStore`. This closes the plan's
"per-app settings and recovery roots, one shared media cache": HaCue2 has its own resolver because the
architecture rules correctly forbid it referencing HaPlay's. Autosave is written **beside** the show and
never over it — recovery is an offer made at the next launch, not something that happened to the
operator's file while they were not looking. The launcher now opens the recent that was clicked.

### Slice 3 — a fixture out of real media

`UI/HaCue2.Seed` + `LibrarySeeder`/`LibraryScan`. A generator rather than a committed file: a fixture
holding absolute paths into one person's home works on one machine and goes stale immediately. It emits
every cue kind against files that exist, and **one deliberate error** (an output fed but unpatched — the
condition register item 25 singles out), because a fixture that passes every check teaches nothing about
the screen that reports them.

### Slice 4 — video outputs

`ProjectVideoOutputs` opens one `SDL3GLVideoOutput` per local-screen output, sized to its composition;
`OutputMapping` converts the document's destination fractions to output pixels and warp offsets to
absolute mesh points. Leases declare `DisposeOutputOnRuntimeDispose:false` — the host owns the windows,
and a reload must not close the operator's projector on a keystroke. `ForgetDetachedScreens` re-attaches
after a reload rebuilds a composition, which `preserveMatchingCompositions` does on any size or
frame-rate change.

**Composition idle images are NOT done and were deliberately backed out**: nothing in this repo decodes
a still image to a `VideoFrame`, so `SetCompositionIdleFrameAsync` has nothing to hand. Register item 23
needs that loader first.

### Against the Phase 5 exit criteria

Met: independently buildable; launches into editing mode; the Lock latch and the single External-input
toggle exist as UI; multi-list transport with per-list standby; per-cue disable; the Note tab; the curve
editor; project status with relink and consolidate, plus its headless twin; undo on every editing
surface that ships.

Not met: usable for a show without HaPlay (no preview/audition, no external input, no remote API);
N-visualizers-per-composition; the video shortfalls other than mapping; side-by-side comparison against
HaPlay fixtures; NativeAOT publish and launch gates.

### Testing

`UI/HaCue2.Tests` now exists, with `HeadlessSessionBootstrap.cs` copied **wholesale** as this plan
requires — a test assembly that takes the csproj but not that file reproduces a whole-run hang whose
occurrence depends on test order. It covers the cue view's scope navigator and transport, the inspector
fields, the audio view's project edits, both settings scopes, and the launcher. The engine's own logic
is in `HaCue2.Core.Tests`, which now also reaches `HaCue2.Engine`'s pure parts.

### Open questions this raised

- **Open question 20 is now answerable and still open.** The `Ideas/*.md` documents cited by cue code
  are still cited; the Phase 5 file move has not happened, so the re-point has not either.
- **§7.1's eight `HaOutput` couplings became a decision rather than a task.** HaCue2 needed video
  outputs before they could be inverted, so it opens its own. Whether the two apps end up sharing one
  output engine is now a Phase 6 question, when HaPlay's side is being dismantled anyway.


### Phase 5 progress — 2026-08-03 (continued)

**Audition rig.** `AuditionRig` on the project: the audio side names a project LINE rather than a raw
device, which is D8 taken literally — the rig takes that line's channel count, travels with the show,
goes absent elsewhere, relinks on arrival. Null means the bay's default monitor terminal, which is why
audition works before anybody configures it. `ShowHost.PreviewAsync` goes through the framework's
monitoring seam, so a preview never reaches the program mix and never appears in the Active list. The
video surface opens lazily on first use. Ctrl+P, the cue context menu, a transport readout, and a new
"Audition rig" status check (a warning, never an error — preview falls back).

**External input.** Built on **`S.Control`**, not `HaControl.Input`, which is not on this branch (see
the gap analysis correction). `TriggerInputs` owns a `ControlInputSession` and the master gate;
`TriggerMatching` is the pure half — patterns written the way the wire monitor prints them, so an
operator can copy a line they can see into a binding. Register item 3 holds: one toggle, off when a
project opens unless the show says otherwise, and it never gates GO. Devices are opened only while the
toggle is on rather than opened and filtered — holding a MIDI port the app is ignoring takes it from
somebody else's rig.

Still open on external input: the parameter registry (cc → master trim is refused out loud rather than
guessed), wall-clock schedules, MTC chase, and a Learn button.

> **Superseded 2026-08-03.** The parameter registry (`ShowParameters` over `ParameterRegistry`) and
> Learn both shipped in later slices; only wall-clock schedules and MTC chase remain. See
> **Open items — the canonical list** at the end of this document, which supersedes every
> "still open" note above it.

**Trigger authoring and Learn (same day).** External input ran but a `TriggerBinding` could not be
constructed anywhere in the app — the runtime worked and the authoring surface did not, the same shape
of gap the control-flow inspector panes had. Closed: the Targets view can now LEARN (catches the FIRST
message and stops, because a fader sends a stream), choose what it fires (any cue, or a transport
verb), report a conflict BEFORE the bind rather than after, replace a clashing binding as one undo
step, and remove one. New bindings carry a repeat filter by default, because a hardware button bounces.

The wire monitor is live off `TriggerInputs.Observed`, and Learn watches the same stream — so what an
operator sees printed is literally the text that gets bound. `SampleShow.TriggerMonitor` and the
invented `LastSeen` are retired; `SampleShow` is down to 61 lines and four members (override ledger,
remote API routes, log tail, curve preview leftovers).

**Effect lanes — the authoring half (same day).** Register item 18's model, the compile path and the
journal's own `EffectLaneTarget` had all existed for some time; `EffectLaneTarget` had never been
constructed. So lanes reached the engine only when the fixture generator or the timeline's duck helper
wrote one — there was no way to add a lane by hand. Closed: add per kind (one lane per kind, because a
cue with two volume lanes has no defined level and the compiler takes the first), remove, and edit
through the SAME curve editor a fade uses, which is what the register asks for and what
`EffectLaneTarget` was written for.

A new lane opens on two points at unity: an editor over an empty list has no handles, and a flat lane
at unity changes nothing until dragged, so adding one is safe mid-show. Each row says what the lane
will actually DO — "needs at least two points", "no endpoint, so nothing is sent" — rather than a point
count that implies it reaches the engine when it does not.

**The log tail (same day).** Register item 27's premise held but the app had **no logging wired at
all** — not merely a sample list on the panel, but a `MediaDiagnostics.LoggerFactory` still set to the
null factory, so the framework's own logs went nowhere. `Session/AppLogging` now installs one
`LoggerFactory` carrying a `LogRingProvider` and hands it to `MediaDiagnostics` at startup, before
anything logs; that hand-off is what makes the session, the router and the patch bay appear in the
panel, which is the interesting half — a wedged output pump reports itself from inside the routing
layer.

The ring captures at Debug regardless of what the panel shows, and the FILTER is applied on read. So
turning the level down reveals what was already captured rather than only what arrives afterwards,
which is what a fault that reproduces once needs. The panel reports the ring's drop count, because a
tail that silently lost records invites the wrong conclusion from what is left. `SampleShow.LogTail`
is retired; the file is down to 51 lines and two members (the override ledger and the remote API
route table).

**Remote API (same day).** A third "the register says LANDED, but it landed elsewhere": the route
table, self-documentation and counters are real, and they are in **`UI/HaPlay/Remote`** — app code an
app may not reference, whose dispatcher targets HaPlay's view-models regardless. HaCue2 has its own:
`RemoteApiRoutes` (enforced shapes, so an unknown path is a 404 and a known path with the wrong verb
is a 405 — collapsing those sends people looking in the wrong place), `RemoteApiServer` over
`HttpListener`, and source-generated payload records.

**`POST /api/v1/lists/{list}/go` ships.** The register had it PARTIAL, blocked on per-list standby;
the session owns that now, so a bare go against a list finally means something. Lists resolve by id or
by NAME, because a show-control system is configured by a human typing into a macro editor.

Local-only unless the project allows the LAN, and a machine-scope token — never in the project, since
a token travelling in the show file is a token published to everyone the show is sent to — is required
either way. Every route goes through the same transport verbs the buttons use, the rule external input
already follows.

The AOT gate added earlier earned its place immediately: the first draft used anonymous JSON payloads
and the build refused them. They are named records with a source-generated context now, which an API
contract deserved anyway.

`SampleShow` is down to **34 lines and one member** — the project override ledger.

**Known gap to close later — remote API dispatch is untested.** `RemoteApiRoutes` is covered
exhaustively; `RemoteApiServer.HandleAsync` is not. It takes a concrete `ShowHost`, and a host needs
devices, so auth, the 404/405 split and every transport call are currently exercised only by hand.
Closing it means a seam over the transport verbs — an interface `ShowHost` implements, or a fake — so
a request can be driven end to end without hardware. This is the one surface where a defect fires cues
from off the machine, so it should be closed before a show-control system is pointed at it.

**Parameter bindings (same day) — register item 24 completed.** External input could fire cues and
transport verbs but REFUSED parameter bindings out loud, which was honest and incomplete. Closed over
the framework's own `ParameterRegistry` / `ContinuousBinding`, which had landed and had no consumer.

`ShowParameters` is HaCue2's answer to "which values does a cue player offer": master trim and the
audition level, both in decibels over the same −60..+12 range the app's own level fields accept, so a
fader and a typed value cannot disagree about what the ends mean. Deliberately few — exposing every
number in the document would produce a list nobody could search and invite binding a surface to what
are really authoring decisions.

The registry holds **delegates, not values**, so a parameter always reads what is true now. That is
what makes soft takeover work: a control latches against the live value rather than a cached one it
had already moved past. Soft takeover is on by default, so a fader that is out of position is ignored
until it catches up instead of jumping the level audibly mid-show — and the binding object is kept per
parameter, because the latch is state that would never form if it were rebuilt per message.

A parameter binding carries the target's own range and **no repeat filter**: a fader sends a stream,
and the 250 ms filter that stops a button bouncing would make one lurch.

**The override ledger (same day) — and the end of the sample data.** Register item 26's ledger needed
a model change first: `ProjectSettings.PanicFadeMs` was a plain int with a default, which cannot
express "this project does not care", so every project silently pinned whatever value it was created
with and the ledger had nothing real to show. It is `int?` now, with the machine default on
`AppSettings` and the engine taking it from the shell — a session that read machine preferences itself
would make the two scopes one.

Every row is derived from the two scopes, so the ledger cannot claim an override the document does not
hold. Both values are shown side by side, because a project override always wins and must always be
visible: somebody reading the application pane has to be able to see that the number in front of them
is not the one in force. `revert ×` clears it, journaled — removing an override changes what the show
DOES.

With that, **`SampleShow` is deleted.** Every value the shell displays now comes from the document,
the machine, or a running session. `Sample/` holds only `SampleProject` (a demo document, reachable
via `HACUE2_START=main`) and `SampleRuntime` (guarded so any project but the demo gets an idle
runtime).

**The cue-execution seam (same day) — and what it found immediately.** Coverage in this assembly had
split by CONSTRUCTABILITY rather than importance: every pure helper was tested and every device-holding
class was not, purely because it could not be built without hardware. The code that decides what every
cue does was on the wrong side of that line.

`ICueExecutionHost` is the seam — every effect firing a cue can have, and nothing about a session, a
bay or a socket. `CueExecutor` holds the decisions (fire modes, jump chains, auto-continue, the loop
bound, patch recall, fade targeting); `ShowHost` stays the device half and implements the interface
over its session and bay. Behaviour-neutral: the full suite passed unchanged after the move.

**33 tests, and the first run found a real defect.** `FadeAsync` iterated `host.Sounding` while
stopping targets, and stopping one calls `Forget`, which removes it from that list — a
collection-modified throw. It had never bitten only because `ShowHost` happens to return a copy; the
interface never promised one, and a "fade everything sounding" cue crashing mid-show is not a bug to
leave to luck. Fixed by snapshotting in the executor, where the contract lives.

That is the argument for the seam, made by the seam: three defects have now been found in this logic
(the depth-first `Flatten` double-fire, the `TrimOutMs` lane drop, and this), and this is the first one
a test found rather than a reading.

Still uncovered and worth the same treatment: `RemoteApiServer.HandleAsync` (see the known gap above —
the seam it needs is the transport-verb half, not this one) and the device classes themselves.

### Recording and streaming (2026-08-03)

**Register item 30 is implemented, and the model it needed did not exist.** A Record output and a
FileRecord line carried a name and a hint and nothing else — no folder, no pattern, no format. The
runtime matched: `ProjectVideoOutputs` reported record and stream outputs as "not implemented yet", and
`ProjectPatchBay.Open` ignored `line.Kind` entirely, so a record line was opened as a PORTAUDIO DEVICE
named `show-{date}.flac` and then reported as a missing interface. That last one is fixed as a
by-product; it was never going to be found by reading the devices list, which said exactly what a
genuinely absent interface says.

`RecordTarget` (folder, pattern, URL, arm-with-show, continuous) hangs off both an audio line and a
video output, because "record" means the same thing on both sides.

**The extension is the format**, matching how the mockup drew patterns (`show-{date}.flac`) — whole
filenames, no separate container picker to contradict the name the operator typed. Split in two on
purpose: `RecordFormatNames` (Core) holds which extensions are legal, so **project status can validate
a pattern with no encoder, no session and no machine** — a bad extension is findable at the get-in
rather than at arm time, when the operator has least room to fix it. `RecordFormats` (Engine) maps each
to its container and codecs. A test holds the two lists to each other.

**The mockup's `.flac` is not achievable and is refused rather than approximated.** The encode library
muxes five containers and none is a raw FLAC or WAV stream; lossless audio is FLAC inside Matroska,
`.mka`. Every extension somebody would plausibly type is mapped to the closest one that works, and the
suggestion respects what is being recorded — offering `.mka` to a VIDEO output would answer one refusal
with another.

**What was built.** `ProjectRecorders` owns one encode session per armed target. Audio recorders join
the bay as ORDINARY TERMINALS with the line's own patch cells, so what lands in the file is what the
operator patched to it — "record the foldback mix" needs no feature, only a different patch — and a
patch edit made mid-recording reaches the recording. `AudioPatchBay.AddTerminal` is running-safe and
fades in, so arming mid-show neither interrupts the program nor starts the file with a click. Video
recorders sit behind a `RecordVideoOutput` the compositor has been rendering into since the show
loaded; arming swaps a session in behind it, so **pressing record never restarts the clips on that
composition**. Continuous mode (the archive/reel choice the mockup drew) is wired to
`ContinuousEncodeCarrier`; streams are always continuous whatever the document says, because an ingest
drops a connection that stops sending.

**The UI half was the fifth dead authoring surface on this branch.** The record pane was drawn
complete — Directory, Pattern, an insert-token dropdown, a `?` popover, a Mode segment, a Format
line — with every value a literal in the markup and every button inert. `RecordEditor` is the real one,
shared by the Audio and Video views rather than written twice, and the preview renders through the
recorder's own expander so it cannot promise a name the recorder would not write.

**Tested, including against a real encoder.** 47 new tests. The end-to-end ones write real files and
assert what came back: `ffprobe` confirms 25 frames at 25 fps produce exactly 1.000 s of H.264 in
Matroska. That mattered — the first version of that test asserted only "the file is over 1 kB" and
passed while the bounded encode queue silently dropped two thirds of the frames. The queue dropping
under a burst is correct (a recording must never stall the show it is recording), so the honest fix was
to pace the producer, test the drop path separately, and **surface the drop count to the operator** —
the mockup's own diagnostics row says "12 dropped", and it is the only warning before a file gaps.

Two defects the tests found rather than confirmed: the audio-only suggestion handed to video outputs,
and a pattern of pure separators cleaning to `---` instead of falling back to a readable name.

**Deliberate refusals and gaps.**
- Filename expansion is a security boundary: a pattern becomes a path, and both it and the values
  substituted in are operator text. Separators and the characters Windows refuses are stripped from
  every substituted value AND the result, so a project named `../../../etc/passwd` yields an odd
  filename in the right folder rather than a write outside it.
- A stream URL's last segment is a credential; it is redacted everywhere it is displayed or logged.
- NDI outputs remain reported as not-implemented. They are the same wiring — an `IVideoOutput` and an
  `IAudioOutput` behind the same arm/disarm — and are the obvious next one.
- The rest of the Video view's output pane (composition, screen, mode, idle fallback, mapping toggle,
  IDENTIFY) is still drawn against literals. Pre-existing and out of this pass's scope, but it is the
  same dead-surface shape and should be treated as such.

### NDI, and the video output pane (2026-08-03)

**Both gaps named at the end of the recording pass are closed.**

**NDI outputs and lines open.** `S.Media.NDI`'s `NDIOutput` exposes an `IVideoOutput` and an
`IAudioOutput`, so a sender attaches exactly like a screen or an interface — which is what made this the
obvious one to do next. Video NDI joins the compositor's lease list; audio NDI joins the bay as an
ordinary terminal. Two things are deliberately NOT like a recorder: an NDI feed is never ARMED (it is
live, and receivers connect when they choose, so an arm switch would have nothing behind it), and an NDI
line is never the CLOCK MASTER, because it paces on the network's terms rather than the rig's. The same
latent bug recording had was here too: the bay ignored `line.Kind`, so an NDI line was opened as a
PortAudio device named `HACUE-PROG` and then reported as a missing interface. Tested against the
machine's real NDI runtime, headless. With every kind now opening, the "not implemented yet" branch and
its `Describe` helper are gone rather than left as misleading dead code.

**The video output and composition panes were the sixth dead authoring surface.** Composition pinned to
`SelectedIndex="0"`, a screen picker over three invented resolutions that matched no rig it would ever
open on, fullscreen/windowed inert, idle fallback a read-only sentence, size a literal `1920×1080`. An
operator could not point an output at a composition — the first thing anybody does. All of it now edits
the document through the journal, plus `Required` (register item 25), which the model had and no pane
offered. The screen list comes from `TopLevel.Screens`, and `ProjectVideoOutputs` applies the chosen
display and fullscreen through SDL3's `ApplyWindowPlacement`, so the settings do something rather than
merely persist.

**One model addition, for a destructive control.** The mapping segment offered on/clean over
`IsMapped => Mapping.Count > 0`, so "clean" could only have meant deleting the sections. Those are
different questions and the difference is an hour of somebody's evening — "show this output clean
tonight" is ordinary, and a warp should not have to be authored again to get it back. `MappingEnabled`
separates them, `IsMapped` is `MappingEnabled && Mapping.Count > 0`, and `OutputMapping.Spec` honours it
so clean is bypassed at the engine rather than only relabelled in the pane.

**A defect the tests found:** a composition size was two undo steps, so undoing reverted the height and
left the width — a canvas nobody authored, and every placement in the show is a fraction of it. Now one
`Composite`. The comment claiming this was already true was written before the test that disproved it.

18 new tests (374 core + 143 app). AOT publish still clean with the NDI interop added.

**Still open**, and now the honest list for this area: the Targets view's endpoint/trigger panes have not
been audited for the same dead-surface shape; the visualizer runtime, wall-clock schedules and MTC chase
remain unimplemented; and Phase 6 (the HaPlay cutover) should still wait until HaCue2 has run a real show.

> **Partly superseded 2026-08-03.** The Targets audit happened in the stub sweep below and found exactly
> the predicted shape: the endpoint pane's host and port were literals. See the canonical list at the end.

### The full stub sweep, and a guard so it is the last one (2026-08-03)

Six dead authoring surfaces had been found one at a time, each by asking "can you actually make one?" of
whatever pane was in front of us. That is not a method, so this pass swept every markup file in the app
and then turned the result into a test.

**What the sweep found and this pass fixed.**

- **Settings — eleven literals**, including the fixture's own name (`PROJECT · MIDSUMMER-2026`) as the
  nav heading on every project anybody opened. Peak hold, app stop/panic fade, new-project mix rate and
  fades, project stop fade, project panic-fade override, media root: all now read and write. `AppSettings`
  gained the five fields that had no home; the rest already existed and were simply never bound.
- **`CacheInUse` was a constant string** — "waveforms 1.2 GB · probes 44 MB · thumbnails 180 MB" read
  identically on an empty cache and a full disk, which is the exact situation somebody opens that pane
  to find out about. `MediaCache` measures it, and the two CLEAR buttons and OPEN LOGS FOLDER now work.
- **Action endpoints could not be edited at all.** Host and port were `10.0.1.20` and `8000` in the
  markup, so an action cue could not be pointed at a desk. Worse, `TestMessage` was loaded once from the
  FIRST endpoint and never written back: the box showed one desk's payload while SEND TEST sent
  another's — it proved nothing and looked like it had.
- **The remote token** was `••••••••  · rotate` in two places, with no rotate. It is now masked from the
  stored value with a real ROTATE, because a laptop that left the building is a reason to invalidate it.
- **Audio patch readout** showed a fixture's rig (`18i20 · Out 3`, `Fold L @ −3.0`) over every project —
  on the pane that answers "why can I not hear Lobby". Meter-in-summary now writes its model field.
- **PREVIEW ON AUDITION OUTPUTS** had a handler already written and was never attached to the button.

**The guard.** `MarkupBindingGuardTests` scans every `.axaml` and fails when an input control — text
box, selector, checkbox, slider — renders a value no binding can change, or when a button is drawn with
no handler. Two documented exception lists carry the genuinely unimplemented controls WITH the reason
each is a gap rather than a bug, and separate tests keep those lists short, reasoned, and free of
entries whose control has since been fixed. The guard was verified by reintroducing a literal and
watching it fail. `ListBox` is in the scanned set because this app's segmented controls are list
boxes — omitting it would have exempted most of the selectors from a guard aimed at selectors.

**Still unimplemented, now listed in the guard rather than rediscovered:** add-stereo-pair, reorder
logical outputs, solo-to-audition (register item 13 — needs a bay monitor lease the audition rig does
not take), IDENTIFY (needs a compositor overlay no cue path asks for), and the five timeline transport
buttons. Beyond markup: MIDI output (honestly reported at the cue), the visualizer runtime, wall-clock
schedules and MTC chase.

**A process note worth keeping.** An incremental build reported `0 Errors` while the Avalonia XAML
compiler had actually failed, and 133 tests broke with an unrelated-looking "no precompiled XAML"
message. `--no-incremental` is what tells the truth after a markup change.

379 core + 162 app + 27 arch. AOT publish clean.

---

## Open items — the canonical list (2026-08-03)

Five separate "still open" notes had accumulated in this document, written at the end of successive
sessions, and two of them had gone stale — one listed a parameter registry and a Learn button that had
both shipped, the other predicted a Targets audit that has since happened. A reader looking for "what is
left" got contradicting answers depending on which note they found first.

**This section supersedes every earlier "still open" note.** Add to it here rather than appending a new
one; the older notes are kept, annotated, as a record of what was true when each slice was written.

### Drawn but not implemented (enforced by `MarkupBindingGuardTests`)

These are in the guard's exception lists with the same reasons, so they cannot be silently forgotten and
cannot silently grow.

| Control | What it needs |
|---|---|
| `+ ADD STEREO PAIR` | a one-step add of two linked logical outputs; the group model exists |
| `REORDER` (logical outputs) | bus order is positional, so reordering needs a re-open path |
| `SOLO THIS LINE TO AUDITION` | register item 13 — a bay monitor lease on one line, which the audition rig does not take yet |
| `IDENTIFY` | a compositor overlay on one output, which no cue path currently asks for |
| Timeline transport (5 buttons) | the timeline row is drawn in full and unimplemented in full |
| Curve picker selection | choosing a curve is not yet wired to a document field |

### Subsystems

**All four landed 2026-08-03.** What each one turned out to be:

- **MIDI output** — `MidiOut` over S.Control's device layer. An endpoint's Host field is a device NAME
  HINT, matched the way an audio line's is; ports open on FIRST SEND, so the app never holds a port it
  is not using. The message is parsed from the cue's two boxes read as one token stream (`cc 1 7` +
  `100` ≡ `cc 1 7 100`), by `MidiActions` in **HaCue2.Core** — so the status pass refuses an
  unparseable message on a laptop with no interface in it, rather than at the moment the cue fires.
- **Visualizer runtime** — `ProjectVisualizers` attaches a projectM renderer per composition (not per
  placement: the framework creates a source's layer surface at most once, and one-per-section crashed
  projectM in HaPlay). Fires and stops like any cue, counts as sounding, retires when its cue is
  deleted, and reports a machine without the native library instead of leaving a canvas black.
- **Wall-clock schedules** and **MTC chase** — both are `TriggerInputKind`s with ordinary bindings, so
  every existing rule (target cue/transport, per-source enable, the one External-input master gate)
  applies unchanged. `TriggerClocks` fires on CROSSINGS, not equalities; a relocate re-anchors via the
  chase clock's generation counter rather than sweeping every target behind it; a stall freezes; a
  machine that was suspended does not fire what it missed.

Still open on this line: nothing FIRES from a `TimecodeChase` reading other than through a Timecode
source's bindings — there is no per-cue "start at this timecode" field, and none is planned.

### Tested-around, not tested

- **`RemoteApiServer.HandleAsync`** — auth, 404/405 and every transport verb are untested because the
  method takes a concrete `ShowHost`. The `ICueExecutionHost` seam covers the cue half; the transport
  half still needs its own. Marked `KNOWN GAP` in the source.

### Sequencing

- **Phase 6 (the HaPlay cutover) should not start until HaCue2 has run a real show.** Nothing above is
  a blocker for that run; they are what the run will make it obvious to prioritise.
