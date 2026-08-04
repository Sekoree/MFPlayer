# HaCue2 vs HaPlay's cue player — parity audit

**2026-08-04.** Asked for after a first real run of HaCue2 on `~/Documents/HaCueProj/TestCue.hacue2proj`
turned up four defects in ten minutes. The question behind it: *is the standalone app at least as
capable as the cue player it was extracted from, and where is it not?*

Method: compared the two cue MODELS field by field (`UI/HaPlay/Models/CueList.cs` against
`UI/HaCue2.Core/Model/Cues.cs`), then the authoring surfaces over them
(`CuePlayerView.axaml` + its view-models against `CuesView`/`InspectorPane`/`VideoView`). The model
comparison is the reliable half — a field that does not exist cannot be authored, whatever a screen
looks like. Where HaCue2 is AHEAD it says so; this is not a list of things to copy.

---

## Summary

HaCue2 is **ahead** on show portability, output management, diagnostics and undo, and **behind** on
per-cue authoring detail — most sharply on **video placement**, where HaPlay has six capabilities
HaCue2's model cannot express at all.

| Area | Verdict |
| --- | --- |
| Audio routing / patch | **HaCue2 ahead** — logical outputs are a real venue-swap seam |
| Outputs, mapping, recording | **HaCue2 ahead** — per-output mapping, raster, NDI, recorders |
| Undo, validation, diagnostics | **HaCue2 ahead** — journal, status pass, log ring, remote API |
| Cue kinds | **HaCue2 ahead** (patch cues); behind on media SOURCES |
| Video placement | **HaPlay well ahead** — 6 model fields missing |
| Playlist / group behaviour | **HaPlay ahead** — 3 fields missing |
| Per-cue triggers and schedules | **Different design**; HaCue2's is centralised, and thinner per cue |
| Now Playing / Active panel | **HaPlay ahead** — group aggregation, upcoming chain |
| Cue-list row detail | **HaPlay ahead** — colour tags, richer columns |

---

## 1. Video placement — the biggest gap

`HaPlay.CueVideoPlacement` against `HaCue2.LayerPlacement`:

| HaPlay | HaCue2 | Gap |
| --- | --- | --- |
| `CompositionId`, `LayerIndex`, `Opacity` | same | — |
| `DestX/Y/Width/Height` | `X/Y/Width/Height` | — |
| `Position`: Cover · Letterbox · Center · FillWidth · FillHeight · Stretch | `Fit`: Contain · Cover · Stretch | **3 fit modes missing** |
| `CropLeft/Top/Right/Bottom` | — | **missing** — no source crop at all |
| `RotationDegrees` | — | **missing** |
| `VideoFx` + `VideoFxEnabled` (a full `CueOutputMapping` per placement) | — | **missing** — per-layer warp/section mapping |
| `ChromaKey` + `ChromaKeyEnabled` | — | **missing** |
| `ColorAdjust` (brightness/contrast) + enabled | — | **missing** |

The authoring surface is correspondingly thinner. HaPlay also has quick-layout commands
(`ApplyPlacementLayout`: fit · full · left · right · top · bottom · four quadrants) and crop presets
(`ApplyCropPreset`: none · centre-50 %-wide · centre-50 %-tall · centre box), plus `SourceFitRect`,
which lands a new layer at the SOURCE's own size and aspect rather than stretched full-frame.

**Recommendation.** Port the model fields first — they are additive and every one is a thing an
operator can see is missing. `ChromaKey`/`ColorAdjust` are the exception worth deferring: the framework
side is the layer-effects plugin system, which is a bigger piece of work than the placement fields.

Note the pattern HaPlay uses throughout and HaCue2 should copy: an effect is stored as
`X` + `XEnabled`, so **turning it off keeps the geometry**. HaCue2 already applies this rule to output
mapping (`MappingEnabled`) and mapping sections (`Enabled`); placements should follow it.

---

## 2. Playlist and group behaviour

| HaPlay `CuePlaylistOptions` | HaCue2 `GroupCueNode` | Gap |
| --- | --- | --- |
| `Shuffle`, `ReshuffleEachPass`, `CrossfadeMs` | same | — |
| `AvoidImmediateRepeat` | — | **missing** — a shuffle that can repeat across a pass boundary |
| `LoopCount` (0 = infinite) | — | **missing** — HaCue2 has only `AtEnd` |
| `PlayCount` | — | **missing** |
| `EndBehavior` | `AtEnd` | equivalent |

`AvoidImmediateRepeat` is the one an operator notices: without it a shuffled bed can play the same
track twice across a pass boundary, which sounds like a bug in front of an audience.

---

## 3. Per-cue fields

| HaPlay `MediaCueNode` | HaCue2 | Gap |
| --- | --- | --- |
| `StartOffsetMs`/`EndOffsetMs` | `TrimInMs`/`TrimOutMs` | — |
| `FadeInMs`/`FadeOutMs` + curves | same | — |
| `LevelDb`, `Loop` | same | — |
| `AudioTrackIndex`/`Signature`, `VideoTrackIndex`/`Signature` | same | — |
| `Subtitles` | same | — |
| `VolumeEnvelope` | `EffectLanes` | **HaCue2 ahead** — lanes cover volume and more |
| `LoopCrossfadeMs` | — | **missing** — a seamless loop needs it |
| `EndBehavior`: Stop · FreezeLastFrame · Loop · FadeOutAndStop | — | **missing** per cue |
| `DisablePreRoll` | — | **missing** (HaCue2 has no pre-roll at all — see §7) |
| `ColorTag` | — | **missing** — cue-list colour coding |
| `Schedule` (per cue) | central `TriggerInputs` | **different design** |
| `Triggers` / `HotkeyGesture` (per cue) | central `TriggerBinding` list | **different design** |
| `VideoIsAttachedPicture` | — | **missing** — see §8 |

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
two worth having early — a holding slate and a caption are ordinary cue-list contents.

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
canvas produced a cue that would not fire at all — see §9.

Worth adding: the probe already knows (`AttachedPictureVideoSource` exists in the framework), and the
inspector should say "album art" rather than offering it as a video track.

---

## 9. Fixed on 2026-08-04 while auditing

Four of the reported defects, with their causes:

1. **Placing album art killed the cue.** `VideoRouterOptions` was never passed to any `VideoRouter`, so
   the router's can-convert probe answered *false* for every pixel-format pair and any fan-out branch
   needing a conversion was rejected and rolled back — taking the audio with it. A composition layer is
   always a branch (the player's primary output is the discard sink it negotiates against), so this hit
   any clip whose native format was not the compositor's BGRA. **Framework-level; HaPlay was exposed to
   the same bug.** Fixed by wiring the factory and a new registry probe through `MediaPlayer`.
2. **A local output opened no window.** Outputs are created unbound now, and `Sync` skipped anything
   with no composition. A local screen now opens and is painted black the moment it exists; assigning a
   composition later retargets it (which also never worked — the open record kept its original canvas).
3. **The Active panel's clock was wall time since the fire**, so it was wrong after any pause, seek,
   trim or loop, and there was no seek at all. Now reads the transport's own playhead, shows
   elapsed / remaining / length, and the bar is draggable. The ✕ was a `TextBlock`.
4. **Default fades were 100 ms in / 2 s out.** Now zero: a cue fades because somebody asked it to.

---

## Suggested order

1. **Pre-roll** (§7) — the only item here that changes how the show FEELS.
2. **Placement fields** (§1) — crop, rotation, the three missing fit modes, per-placement FX.
3. **Now Playing group aggregation + upcoming chain** (§5).
4. **Playlist `LoopCount` / `AvoidImmediateRepeat`** (§2), **per-cue `EndBehavior` and
   `LoopCrossfadeMs`** (§3).
5. **Colour tags** (§6), **attached-picture flag** (§8).
6. **Image and text cues** (§4).
7. Chroma key / colour adjust (§1) — behind the layer-effects system.

---

## 10. Closed on 2026-08-04 (the same day)

The owner asked for all of it, so items 1–7 above were built in one pass. What each turned out to be:

| Item | What it was |
| --- | --- |
| **Pre-roll** | `ShowSession.WarmUpcomingAsync` already existed and nothing called it. `ShowHost` now warms after every GO, after a standby move, and once at start-up; `ProjectSettings.PreRollCount` (default 2, 0 = off) is the depth. Fire-and-forget by contract — pre-roll must never delay the verb that triggered it. |
| **Placement** | Every field was already on the framework's `ShowVideoPlacement` and never filled. `LayerPlacement` gained crop ×4, rotation, `VideoFx` + enabled, `ChromaKey` + enabled, `ColorAdjust` + enabled; `LayerFit` went 3 → 6. Inspector gained numeric dest/crop fields, nine quick layouts, four crop presets, and both effect panes. |
| **Group aggregation** | `ActiveGroupRow` with the WHOLE group's remaining/total, "item 3/12", a per-group ✕, and the rest of the chain with countdowns. **Expanded by default** — a group that hid its children would show less than the flat list it replaced. The expander survives the 4 Hz rebuild; everything else on the row is a measurement and is meant to be replaced. |
| **Playlist** | `AvoidImmediateRepeat` (head-swap rather than reshuffle-until-different — one swap always terminates) and `LoopCount` passes, counted on `PlaylistRun`. |
| **End behaviour** | `CueEndBehavior` (stop / freeze / loop / fade out) and `LoopCrossfadeMs`, both already honoured by the session and unreachable from the document. The old `Loop` flag still loops, so older shows are unchanged. |
| **Colour tags** | `CueNode.ColorTag`, an index into a nine-entry palette resolved in `CueColors` so it restyles with the theme and a travelling show carries no hex codes. |
| **Trimmed LEN** | The cue list showed the FILE's duration, so a ten-second sting cut from a four-minute track read as four minutes. |
| **Image cues** | Did not need a new cue kind: FFmpeg decodes a still as a one-frame container, and `FreezeLastFrame` is what makes it a title card. An imported image now gets that end behaviour automatically. |

**Still open after this pass:** TEXT cues (§4) — they need a text renderer, which is a genuinely
different piece of work from everything above. The per-cue `DisablePreRoll` opt-out (§3) is also not
there: the session's warm picks the next N itself and has no per-cue veto.
