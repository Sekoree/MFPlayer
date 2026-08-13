# HaCue2 vs HaPlay's cue player - parity audit

> **STATUS SUPERSEDED (2026-08-11).** Its headline gap - "video placement, 6 model fields HaCue2
> cannot express" - is closed: all six now exist on `LayerPlacement`, and `LayerFit` carries all six
> fit modes. The remaining rows were not re-verified individually and should be treated as stale. See
> `Plans/Open-Items-Survey-2026-08-11.md`.


**2026-08-04.** Asked for after a first real run of HaCue2 on `~/Documents/HaCueProj/TestCue.hacue2proj`
turned up four defects in ten minutes. The question behind it: *is the standalone app at least as
capable as the cue player it was extracted from, and where is it not?*

Method: compared the two cue MODELS field by field (`UI/HaPlay/Models/CueList.cs` against
`UI/HaCue2.Core/Model/Cues.cs`), then the authoring surfaces over them
(`CuePlayerView.axaml` + its view-models against `CuesView`/`InspectorPane`/`VideoView`). The model
comparison is the reliable half - a field that does not exist cannot be authored, whatever a screen
looks like. Where HaCue2 is AHEAD it says so; this is not a list of things to copy.

---

## Summary

HaCue2 is **ahead** on show portability, output management, diagnostics and undo, and **behind** on
per-cue authoring detail - most sharply on **video placement**, where HaPlay has six capabilities
HaCue2's model cannot express at all.

| Area | Verdict |
| --- | --- |
| Audio routing / patch | **HaCue2 ahead** - logical outputs are a real venue-swap seam |
| Outputs, mapping, recording | **HaCue2 ahead** - per-output mapping, raster, NDI, recorders |
| Undo, validation, diagnostics | **HaCue2 ahead** - journal, status pass, log ring, remote API |
| Cue kinds | **HaCue2 ahead** (patch cues); behind on media SOURCES |
| Video placement | **HaPlay well ahead** - 6 model fields missing |
| Playlist / group behaviour | **HaPlay ahead** - 3 fields missing |
| Per-cue triggers and schedules | **Different design**; HaCue2's is centralised, and thinner per cue |
| Now Playing / Active panel | **HaPlay ahead** - group aggregation, upcoming chain |
| Cue-list row detail | **HaPlay ahead** - colour tags, richer columns |

---

## 1. Video placement - the biggest gap

`HaPlay.CueVideoPlacement` against `HaCue2.LayerPlacement`:

| HaPlay | HaCue2 | Gap |
| --- | --- | --- |
| `CompositionId`, `LayerIndex`, `Opacity` | same | - |
| `DestX/Y/Width/Height` | `X/Y/Width/Height` | - |
| `Position`: Cover · Letterbox · Center · FillWidth · FillHeight · Stretch | `Fit`: Contain · Cover · Stretch | **3 fit modes missing** |
| `CropLeft/Top/Right/Bottom` | - | **missing** - no source crop at all |
| `RotationDegrees` | - | **missing** |
| `VideoFx` + `VideoFxEnabled` (a full `CueOutputMapping` per placement) | - | **missing** - per-layer warp/section mapping |
| `ChromaKey` + `ChromaKeyEnabled` | - | **missing** |
| `ColorAdjust` (brightness/contrast) + enabled | - | **missing** |

The authoring surface is correspondingly thinner. HaPlay also has quick-layout commands
(`ApplyPlacementLayout`: fit · full · left · right · top · bottom · four quadrants) and crop presets
(`ApplyCropPreset`: none · centre-50 %-wide · centre-50 %-tall · centre box), plus `SourceFitRect`,
which lands a new layer at the SOURCE's own size and aspect rather than stretched full-frame.

**Recommendation.** Port the model fields first - they are additive and every one is a thing an
operator can see is missing. `ChromaKey`/`ColorAdjust` are the exception worth deferring: the framework
side is the layer-effects plugin system, which is a bigger piece of work than the placement fields.

Note the pattern HaPlay uses throughout and HaCue2 should copy: an effect is stored as
`X` + `XEnabled`, so **turning it off keeps the geometry**. HaCue2 already applies this rule to output
mapping (`MappingEnabled`) and mapping sections (`Enabled`); placements should follow it.

---

## 2. Playlist and group behaviour

| HaPlay `CuePlaylistOptions` | HaCue2 `GroupCueNode` | Gap |
| --- | --- | --- |
| `Shuffle`, `ReshuffleEachPass`, `CrossfadeMs` | same | - |
| `AvoidImmediateRepeat` | - | **missing** - a shuffle that can repeat across a pass boundary |
| `LoopCount` (0 = infinite) | - | **missing** - HaCue2 has only `AtEnd` |
| `PlayCount` | - | **missing** |
| `EndBehavior` | `AtEnd` | equivalent |

`AvoidImmediateRepeat` is the one an operator notices: without it a shuffled bed can play the same
track twice across a pass boundary, which sounds like a bug in front of an audience.

---

## 3. Per-cue fields

| HaPlay `MediaCueNode` | HaCue2 | Gap |
| --- | --- | --- |
| `StartOffsetMs`/`EndOffsetMs` | `TrimInMs`/`TrimOutMs` | - |
| `FadeInMs`/`FadeOutMs` + curves | same | - |
| `LevelDb`, `Loop` | same | - |
| `AudioTrackIndex`/`Signature`, `VideoTrackIndex`/`Signature` | same | - |
| `Subtitles` | same | - |
| `VolumeEnvelope` | `EffectLanes` | **HaCue2 ahead** - lanes cover volume and more |
| `LoopCrossfadeMs` | - | **missing** - a seamless loop needs it |
| `EndBehavior`: Stop · FreezeLastFrame · Loop · FadeOutAndStop | - | **missing** per cue |
| `DisablePreRoll` | - | **missing** (HaCue2 has no pre-roll at all - see §7) |
| `ColorTag` | - | **missing** - cue-list colour coding |
| `Schedule` (per cue) | central `TriggerInputs` | **different design** |
| `Triggers` / `HotkeyGesture` (per cue) | central `TriggerBinding` list | **different design** |
| `VideoIsAttachedPicture` | - | **missing** - see §8 |

On triggers the two designs genuinely differ, and HaCue2's is defensible: bindings live on the INPUT
so one screen answers "what can fire this show", and a cue carries no device names. What HaCue2 loses
is the ability to ask "what fires THIS cue" from the cue itself. A reverse lookup in the inspector
would close that without moving the data.

---

## 4. Media sources

HaPlay's Add menu offers **image**, **text**, **subtitle**, **NDI input**, **PortAudio input**,
**YouTube** and **MMD** cues. HaCue2 has one `MediaCueNode` over a file path.

This is the widest *count* of missing features and the least urgent: they are all "what can a cue play",
and the framework provides them through the same registry HaCue2 already uses. Image and text are the
two worth having early - a holding slate and a caption are ordinary cue-list contents.

---

## 5. Now Playing vs the Active panel

HaPlay's Now Playing (fixed here in part on 2026-08-04, see §9):

| HaPlay | HaCue2 |
| --- | --- |
| Per-cue progress bar, **seekable** | now seekable ✔ |
| **Seek LOCK toggle** so a show cannot be scrubbed by accident | **missing** |
| Position / near-end colouring | ✔ (plus remaining and length) |
| Per-cue ✕ | now a real button ✔ |
| **Group aggregate rows**, expandable, with children nested | **missing** |
| **Playlist run status** ("item 3/12 · pass 1/2") | **missing** |
| **Upcoming chain with time-until-start countdowns** | **missing** |
| Stop-all button in the panel | in the transport bar |

The group aggregate is the significant one. HaCue2 lists every sounding cue flat, so a playlist group
of twelve fills the panel with twelve rows and no indication that they are one thing.

---

## 6. Cue-list row detail

HaCue2 shows: number · label · source/target · fade · length · dB · badges. That is close to HaPlay's,
with two absences: **colour tags** (`ColorTag`, a per-cue palette index) and any **per-cue trigger
indicator**. HaCue2's LEN column reads the probed file duration and ignores the trim; HaPlay shows the
played length.

---

## 7. Where HaCue2 is ahead

Worth stating, because the extraction was not a subset:

- **Logical outputs + the V×R patch.** A show names "Lobby" and every venue patches it. HaPlay's cues
  address output lines directly, so a show does not travel.
- **Patch cues and snapshots.** No equivalent in HaPlay.
- **Per-output mapping with its own raster**, section enable, independent mesh, and a splitter.
- **Undo.** One journal across cues, patch, video and settings. HaPlay has none.
- **Project status pass**, log ring, remote API, recovery autosaves, override ledger.
- **Effect lanes** as a general automation concept, against HaPlay's volume-only envelope.
- **Audition rig** as a first-class, travelling object.

HaPlay has one structural advantage HaCue2 has not replicated: **pre-roll** (`PreRollCount`,
`MaxPreparedDecoders`, `DisablePreRoll`), which opens decoders for upcoming cues so a GO is instant.
HaCue2 opens on fire. On a slow disk with 4K media that is a visible difference, and it is the item on
this page most likely to be felt on the night.

---

## 8. Attached pictures (album art)

HaPlay's `MediaCueNode` carries `VideoIsAttachedPicture`, so the app knows a file's "video" is a still
cover and can treat it as one. HaCue2 has no such flag, which is how placing a FLAC's cover art on a
canvas produced a cue that would not fire at all - see §9.

Worth adding: the probe already knows (`AttachedPictureVideoSource` exists in the framework), and the
inspector should say "album art" rather than offering it as a video track.

---

## 9. Fixed on 2026-08-04 while auditing

Four of the reported defects, with their causes:

1. **Placing album art killed the cue.** `VideoRouterOptions` was never passed to any `VideoRouter`, so
   the router's can-convert probe answered *false* for every pixel-format pair and any fan-out branch
   needing a conversion was rejected and rolled back - taking the audio with it. A composition layer is
   always a branch (the player's primary output is the discard sink it negotiates against), so this hit
   any clip whose native format was not the compositor's BGRA. **Framework-level; HaPlay was exposed to
   the same bug.** Fixed by wiring the factory and a new registry probe through `MediaPlayer`.
2. **A local output opened no window.** Outputs are created unbound now, and `Sync` skipped anything
   with no composition. A local screen now opens and is painted black the moment it exists; assigning a
   composition later retargets it (which also never worked - the open record kept its original canvas).
3. **The Active panel's clock was wall time since the fire**, so it was wrong after any pause, seek,
   trim or loop, and there was no seek at all. Now reads the transport's own playhead, shows
   elapsed / remaining / length, and the bar is draggable. The ✕ was a `TextBlock`.
4. **Default fades were 100 ms in / 2 s out.** Now zero: a cue fades because somebody asked it to.

---

## Suggested order

1. **Pre-roll** (§7) - the only item here that changes how the show FEELS.
2. **Placement fields** (§1) - crop, rotation, the three missing fit modes, per-placement FX.
3. **Now Playing group aggregation + upcoming chain** (§5).
4. **Playlist `LoopCount` / `AvoidImmediateRepeat`** (§2), **per-cue `EndBehavior` and
   `LoopCrossfadeMs`** (§3).
5. **Colour tags** (§6), **attached-picture flag** (§8).
6. **Image and text cues** (§4).
7. Chroma key / colour adjust (§1) - behind the layer-effects system.

---

## 10. Closed on 2026-08-04 (the same day)

The owner asked for all of it, so items 1–7 above were built in one pass. What each turned out to be:

| Item | What it was |
| --- | --- |
| **Pre-roll** | `ShowSession.WarmUpcomingAsync` already existed and nothing called it. `ShowHost` now warms after every GO, after a standby move, and once at start-up; `ProjectSettings.PreRollCount` (default 2, 0 = off) is the depth. Fire-and-forget by contract - pre-roll must never delay the verb that triggered it. |
| **Placement** | Every field was already on the framework's `ShowVideoPlacement` and never filled. `LayerPlacement` gained crop ×4, rotation, `VideoFx` + enabled, `ChromaKey` + enabled, `ColorAdjust` + enabled; `LayerFit` went 3 → 6. Inspector gained numeric dest/crop fields, nine quick layouts, four crop presets, and both effect panes. |
| **Group aggregation** | `ActiveGroupRow` with the WHOLE group's remaining/total, "item 3/12", a per-group ✕, and the rest of the chain with countdowns. **Expanded by default** - a group that hid its children would show less than the flat list it replaced. The expander survives the 4 Hz rebuild; everything else on the row is a measurement and is meant to be replaced. |
| **Playlist** | `AvoidImmediateRepeat` (head-swap rather than reshuffle-until-different - one swap always terminates) and `LoopCount` passes, counted on `PlaylistRun`. |
| **End behaviour** | `CueEndBehavior` (stop / freeze / loop / fade out) and `LoopCrossfadeMs`, both already honoured by the session and unreachable from the document. The old `Loop` flag still loops, so older shows are unchanged. |
| **Colour tags** | `CueNode.ColorTag`, an index into a nine-entry palette resolved in `CueColors` so it restyles with the theme and a travelling show carries no hex codes. |
| **Trimmed LEN** | The cue list showed the FILE's duration, so a ten-second sting cut from a four-minute track read as four minutes. |
| **Image cues** | Did not need a new cue kind: FFmpeg decodes a still as a one-frame container, and `FreezeLastFrame` is what makes it a title card. An imported image now gets that end behaviour automatically. |

### The last three, closed the same day

| Item | What it was |
| --- | --- |
| **Seek lock** (§5) | A latch above the Active panel, **locked by default** - the one default here chosen against convenience. The bars sit under the pointer for a whole show, a drag is instantly audible, and there is no undo for it. Enforced on the control AND in the command, so a seek arriving from anywhere meets the same latch. |
| **Per-cue `DisablePreRoll`** (§3) | `ShowClipBinding.DisablePreRoll`, honoured in `BuildUpcomingSpecs`. A clip that opts out is SKIPPED rather than counted and dropped - pre-roll's depth is "how many decoders may be held", and spending one on a clip that declined would shorten the warm for the cues that wanted it. Shown as the positive ("pre-roll this cue"), because a checkbox called "disable", ticked to mean "do not", is one an operator reads twice under pressure. |
| **Text cues** (§4) | A new `TextCueNode` kind. The document stores WORDS - portable, diffable, translatable - and the compiler turns them into a `text:` URI carrying the whole render spec, which `S.Media.Source.Text` draws. From there a card is an ordinary held-frame clip, so placement, mapping, fades, pre-roll and recording all work on it unchanged. |

**Text was built twice.** The first version rasterised cards app-side into the machine's media cache
and told the compiler where they landed. It worked, and it was then deleted: `S.Media.Source.Text`
already existed, unwired - a SkiaSharp renderer behind a `text:` URI that carries the entire spec,
holds the frame for a duration it is given, and draws an outline. Strictly better, because a card then
needs no file anywhere, no cache to invalidate and nothing from the app: the words travel with the
show and each machine draws them with the faces it has. The fourth time this session that the answer
was "the framework already provides it and nothing registered it".

**Harness note.** `HaCue2.Tests` now runs headless with Skia (`UseHeadlessDrawing: false`) rather than
the stub drawing backend. The stub answers layout and hit-testing and produces no pixels, which was
enough for every other view test and not enough for the one that checked a card actually drew.

---

## 11. Closed on 2026-08-04, part two - the other media sources

§4's remaining sources were the last of this audit still open. All three are now buildable as cues;
MMD was excluded on request (it was an experiment).

**One idea, and then everything that assumed a media path is a filename.** A live source is an
ORDINARY media cue whose `MediaPath` is a URI the registry knows how to open - `ndi://` a camera,
`padev://` a capture device, `youtube://` a prepared video. Level, sends, placements, fades, effects
and recording then mean exactly what they mean for a file, and a separate cue kind would have forked
every one of them to change one field. `HaCue2.Core/Media/SourceUri.cs` is the whole rule: what a
scheme is, what is LIVE, and how to build and read each URI's query grammar.

What had to change downstream, each of which was a real failure:

| Assumed | Now |
| --- | --- |
| `MediaPaths.Resolve` joins a relative path onto the media root | A URI returns verbatim. `Path.Combine("/shows", "ndi://CAM 1")` normalises to `/shows/ndi:/CAM 1`, and the registry would have been handed a directory instead of a camera. |
| `MediaPaths.ReferencesIn` lists every media file | URIs are absent. That one list drives probing, relink, consolidate and the status pass's "missing" report - a camera has no length to probe, no copy to make, and no filesystem that could say it is gone. Including them painted every live cue red on any machine not yet on the venue's network. |
| A duration is always a machine fact | `MediaCueNode.SourceDurationMs`, filled only by a source that can state its own length. A prepared YouTube video knows it from the manifest and no probe on this machine can rediscover it from a `youtube://` URI, so without it the cue read "-" for the rest of the show's life. |
| The clip editor can open any cue's media | Gated on file cues: it scans a waveform and draws a trim window, and neither means anything for a camera. |

**Pre-roll is off for a live cue.** Pre-roll opens the next few cues' media early so the next GO is
instant - which for a camera means claiming the network connection, and for a capture device means
claiming the device, minutes before anybody asked. `DisablePreRoll` (built earlier the same day for
exactly this case) is set when the cue is created. A prepared YouTube video is an ordinary local file
and stays in pre-roll.

**The dialogs.**

| Source | Shape |
| --- | --- |
| **NDI input** | A prompt. The sender name can be PICKED from a live scan or TYPED, and both are first-class: names are `STUDIO-PC (CAM 1)` and nobody types those from memory, but the camera is very often not on the network yet and a show authored in an office must be able to name one that does not exist. Audio/video halves, low bandwidth, jitter-buffer override. The scan runs on the click rather than in the view-model factory - it blocks for a second or two, and that is not a factory's decision. |
| **Local input** | A prompt. Driver narrows the device list AND is stored, unlike the output-line picker: the same interface appears under ALSA and under JACK with different names, and the capture provider resolves against one family. Width and rate follow the chosen device. A machine that can enumerate nothing still gets a free-text name. |
| **YouTube** | Its own window. Paste a link → FETCH resolves the manifest → pick the video, audio and caption tracks → DOWNLOAD & ADD prepares into the shared cache with a progress bar. It has to download: the registry's youtube provider plays only from the prepared asset and refuses to fetch on the fire path, which is the right refusal - a GO that started a network transfer would be a cue that begins four minutes late, on a machine that may have no network at the venue at all. |

**A bug found on the way.** The shell only adopted probe results at load and after a save, so a media
cue added at 19:50 read "-" for its length until the show was next saved. `OnJournalChanged` now
re-adopts and kicks the probe; both are cheap (a dictionary walk, and a scan that skips every path it
has already looked at).

**Registration.** `ShowHost` builds the registry with every source family, each optional: a module
that will not load on this machine is LOGGED and skipped, so a box without the NDI runtime still plays
files. YouTube registers over a shared `YouTubeRuntime.Preparer` - the dialog that downloads and the
provider that opens have to agree on the cache, and two instances would mean watching a download
finish and then a cue that says the video is not prepared.
