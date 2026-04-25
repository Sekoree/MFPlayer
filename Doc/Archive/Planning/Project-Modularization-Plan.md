# Project Modularization Plan (Tier 5+)

Last updated: **2026-04-25**

## Goal

Define a safe project split that supports Tier 5 growth (timeline/cues/soundboard)
without overloading `S.Media.Core` or `S.Media.FFmpeg`.

Primary question answered: **yes, moving `MediaPlayer` into its own project makes sense**.

---

## Framework vs App/Helper Boundary

For the current phase, keep the framework focused on reusable runtime capability:

- Framework: routing, playback primitives, transport orchestration primitives, optional protocol adapters.
- App/helper layer: product workflows, persistence formats, load/save/migration, UI interaction contracts.
- Composition host objects (for example app-level `ShowSession`) belong outside the core framework.

When in doubt, if a feature is mostly about operator workflow or screen/state persistence, keep it out of framework for now.

---

## De-scope Candidates (Current Branch)

If we trim to a basic playback/routing baseline, these are the first candidates to move out:

- `Media/S.Media.Composition/Cues/*`
- `Media/S.Media.Composition/Soundboard/*`
- `OSC/S.Media.Composition.OSC/*`
- `MIDI/S.Media.Composition.MIDI/*`
- `Test/S.Media.Composition.Tests/*`
- `Test/S.Media.Composition.Protocol.Tests/*`

Keep in baseline:

- `Media/S.Media.Core/*`
- `Media/S.Media.Playback/*`
- `NDI/S.Media.NDI.Playback/*` (optional glue package)

---

## Why Split Now

Current state is functionally solid, but layering is becoming crowded:

- `S.Media.FFmpeg` currently mixes:
  - FFmpeg decode/runtime internals (`FFmpegDecoder`, channels, demux, resampler, loader)
  - app-facing playback facade (`MediaPlayer`, `MediaPlayerBuilder`, playback events/state)
- Tier 5 introduces orchestration domains (timeline/actions/cues/soundboard) that
  should not live in decoder/native-interop projects.
- `S.Media.NDI` currently includes builder extensions (`MediaPlayerBuilderExtensions`) that
  are app-layer glue and should be optional for users who only need endpoint/channel features.

Splitting now reduces future churn and keeps dependency boundaries clear.

---

## Recommended Target Project Layout

## Keep (unchanged role)

- `Media/S.Media.Core`
  - routing, clocks, media abstractions, mixer, shared utility primitives
- `Media/S.Media.FFmpeg`
  - FFmpeg decode/runtime implementation only
- `Audio/S.Media.PortAudio`, `Video/S.Media.SDL3`, `Video/S.Media.Avalonia`, `NDI/S.Media.NDI`
  - endpoint/source integrations

## Add (new)

- `Media/S.Media.Playback`
  - move `MediaPlayer` facade here
  - move `MediaPlayerBuilder` here
  - move playback events/state models (`PlaybackState`, `PlaybackCompletedReason`, etc.) here
  - move `AvDriftCorrectionOptions` here
  - references: `S.Media.Core`, `S.Media.FFmpeg`

- `Media/S.Media.Composition` (Tier 5 core)
  - timeline/action/cue/soundboard runtime domain
  - references: `S.Media.Core`, `S.Media.Playback`
  - no dependency on endpoint-specific projects
  - session persistence intentionally deferred to app/helper layer for now

- `OSC/S.Media.Composition.OSC` (adapter)
  - action handlers/adapters on top of `OSCLib`
  - references: `S.Media.Composition`, `OSCLib`

- `MIDI/S.Media.Composition.MIDI` (adapter)
  - action handlers/adapters on top of `PMLib`
  - references: `S.Media.Composition`, `PMLib`

- `NDI/S.Media.NDI.Playback` (optional glue)
  - move `MediaPlayerBuilderExtensions` out of `S.Media.NDI`
  - references: `S.Media.NDI`, `S.Media.Playback`

This keeps protocol and NDI playback glue optional.

---

## Dependency Rules (Hard)

- `S.Media.Core` references nothing in `Media`, `NDI`, `OSC`, `MIDI`, `Test`.
- `S.Media.FFmpeg` must not reference `S.Media.Playback` or `S.Media.Composition`.
- `S.Media.Playback` can reference `S.Media.FFmpeg`, but not vice versa.
- `S.Media.Composition` depends on playback/core abstractions, not concrete endpoints.
- Adapter projects (`*.OSC`, `*.MIDI`, `*.NDI.Playback`) are leaf integrations.
- Test apps should reference the highest-level project they use (prefer playback/composition, not internals).

---

## Migration Plan

### Phase 0: Pre-split safety net

- [ ] P0.1 Add architecture doc links in `Doc/README.md`.
- [ ] P0.2 Add dependency validation script (simple `ProjectReference` graph checks).
- [ ] P0.3 Add baseline build/test snapshot (all current projects).

### Phase 1: Extract `MediaPlayer` to `S.Media.Playback`

- [ ] P1.1 Create `Media/S.Media.Playback/S.Media.Playback.csproj`.
- [ ] P1.2 Move:
  - `Media/S.Media.FFmpeg/MediaPlayer.cs`
  - `Media/S.Media.FFmpeg/MediaPlayerBuilder.cs`
  - `Media/S.Media.FFmpeg/AvDriftCorrectionOptions.cs`
- [ ] P1.3 Namespace migration:
  - from `S.Media.FFmpeg` -> `S.Media.Playback`
- [ ] P1.4 Update references in:
  - test apps using `MediaPlayer` builder flow
  - NDI builder-extension project (Phase 2)
- [ ] P1.5 Compatibility bridge in `S.Media.FFmpeg`:
  - add `[TypeForwardedTo]` for moved public types (or temporary obsolete wrappers)
  - keep one release cycle bridge if soft migration desired
- [ ] P1.6 Build/test pass + API audit.

### Phase 2: Move NDI builder glue out of `S.Media.NDI`

- [ ] P2.1 Create `NDI/S.Media.NDI.Playback/S.Media.NDI.Playback.csproj`.
- [ ] P2.2 Move `NDI/S.Media.NDI/MediaPlayerBuilderExtensions.cs` into that project.
- [ ] P2.3 Remove `S.Media.Playback` dependency pressure from base `S.Media.NDI`.
- [ ] P2.4 Update test apps to reference `S.Media.NDI.Playback` where needed.

### Phase 3: Introduce composition core (`S.Media.Composition`)

- [ ] P3.1 Scaffold composition domain folders:
  - `Timeline`, `Actions`, `Cues`, `Soundboard`, `Diagnostics`
- [ ] P3.2 Implement Tier 5 base contracts in composition project.
- [ ] P3.3 Keep adapter-free core (no OSC/MIDI references here).
- [ ] P3.4 Add focused unit tests for determinism and replay ordering.

### Phase 4: Protocol adapter projects

- [ ] P4.1 Create `OSC/S.Media.Composition.OSC` and add action handlers.
- [ ] P4.2 Create `MIDI/S.Media.Composition.MIDI` and add action handlers.
- [ ] P4.3 Add integration tests for scheduled dispatch timing.

### Phase 5: App adoption and cleanup

- [ ] P5.1 Migrate player-oriented test apps to `S.Media.Playback`.
- [ ] P5.2 Add a composition-driven sample app (cue/playlist/soundboard scenario).
- [ ] P5.3 Remove compatibility shims after agreed deprecation window.

---

## `MediaPlayer` Move Impact Assessment

## Benefits

- Clear separation: decode internals vs app-facing transport facade.
- Faster onboarding: users can choose playback without digging into FFmpeg internals.
- Cleaner foundation for non-FFmpeg future playback frontends.

## Costs / Risks

- Namespace/reference churn in apps and tests.
- Temporary dual-location confusion if compatibility wrappers stay too long.
- NDI builder extension must move to avoid base NDI -> playback coupling.

## Mitigations

- Use type forwarders for one transition window.
- Enforce dependency rules in CI after Phase 1.
- Keep migration small and mechanical before adding new Tier 5 features.

---

## Suggested Rollout Order (Practical)

1. Phase 1 (`S.Media.Playback` extraction)
2. Phase 2 (`S.Media.NDI.Playback` split)
3. Phase 3 (`S.Media.Composition` scaffold + Tier 5 core contracts)
4. Phase 4 (OSC/MIDI adapters)
5. Phase 5 (app migration + shim removal)

This order minimizes breakage and keeps each step testable.

---

## Tracking Checklist

| Phase | Done | Planned | Progress |
|---|---:|---:|---:|
| Phase 0 | 0 | 3 | 0% |
| Phase 1 | 0 | 6 | 0% |
| Phase 2 | 0 | 4 | 0% |
| Phase 3 | 0 | 4 | 0% |
| Phase 4 | 0 | 3 | 0% |
| Phase 5 | 0 | 3 | 0% |

**Total planned items:** 23  
**Total completed:** 0
