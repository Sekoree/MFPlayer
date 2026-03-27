# Advanced Routing & Cleanup Plan — 2026-03-27 (Part 2)

> Follows the completed refactor plan in `REFACTOR-2026-03-27.md`.
> Goal: re-establish a clear separation between the simple `MediaPlayer` surface
> and the advanced `AudioVideoMixer` capabilities, and clean up leftover dead code.

---

## Problem Statement

After the simplification refactor, `IMediaPlayer : IAudioVideoMixer` inherited
**everything** from the mixer interface. There is no differentiation:
a `MediaPlayer` consumer sees `AddAudioSource(source, offset)`,
`SetAudioSourceStartOffset`, `StartPlayback`, etc. — mixer internals that a
simple "play a file" consumer shouldn't need to think about.

More importantly, the old architecture had a concept of **per-source / per-channel
routing to specific outputs** that was lost. The `AudioVideoMixerConfig.RouteMap`
is a single global `int[]` that maps output channels → source channels, but it
applies identically to *every* output. There is no way to say:

- "Send source A's left channel to output 1, and source B's right channel to output 2"
- "Route source A to output 1 only, source B to output 2 only"
- "Send a specific video source to a specific video output"

---

## Proposed Changes

### 1. `ISupportsAdvancedRouting` interface

A new interface implemented by `AudioVideoMixer` (but **not** by `MediaPlayer`).
Consumers who need fine-grained control cast/check for this interface or use the
mixer directly.

```csharp
namespace S.Media.Core.Mixing;

/// <summary>
/// Implemented by mixers that support per-source, per-channel routing rules.
/// The MediaPlayer does not implement this — use the AudioVideoMixer directly
/// for advanced routing scenarios.
/// </summary>
public interface ISupportsAdvancedRouting
{
    /// <summary>Current set of active routing rules.</summary>
    IReadOnlyList<AudioRoutingRule> AudioRoutingRules { get; }

    /// <summary>Current set of active video routing rules.</summary>
    IReadOnlyList<VideoRoutingRule> VideoRoutingRules { get; }

    int AddAudioRoutingRule(AudioRoutingRule rule);
    int RemoveAudioRoutingRule(AudioRoutingRule rule);
    int ClearAudioRoutingRules();

    int AddVideoRoutingRule(VideoRoutingRule rule);
    int RemoveVideoRoutingRule(VideoRoutingRule rule);
    int ClearVideoRoutingRules();
}
```

### 2. Routing rule types

```csharp
/// <summary>
/// Routes a specific source channel to a specific output channel.
/// SourceId + SourceChannel identify the input signal.
/// OutputId + OutputChannel identify the destination.
/// Gain allows per-route volume control (1.0 = unity).
/// </summary>
public readonly record struct AudioRoutingRule(
    Guid SourceId,
    int SourceChannel,
    Guid OutputId,        // needs IAudioOutput to expose an Id — see consideration 1
    int OutputChannel,
    float Gain = 1.0f);

/// <summary>
/// Routes a specific video source to a specific video output.
/// </summary>
public readonly record struct VideoRoutingRule(
    Guid SourceId,
    Guid OutputId);
```

### 3. Mixer behaviour when routing rules exist

- **Audio pump**: When `AudioRoutingRules` is non-empty, the pump uses the rules
  instead of the global `RouteMap` from `AudioVideoMixerConfig`. Each rule tells
  the pump which source channel to read from and which output channel to write to,
  with per-rule gain. Sources/channels not covered by any rule produce silence on
  that output channel. When no rules exist, the current "mix all → push all with
  global RouteMap" behaviour is preserved (backwards compatible).
  
- **Video**: When `VideoRoutingRules` is non-empty, each rule maps a video source
  to a specific video output. Sources not covered by a rule are not pushed to any
  output. When no rules exist, the current "active video source → all outputs"
  behaviour is preserved.

### 4. Remove `MixerKind` enum and inline `MixerClockTypeRules`

`MixerKind` has a single member `AudioVideo = 0`. `MixerClockTypeRules.Validate`
ignores the parameter entirely. Plan:

- Delete `MixerKind.cs`
- Change `MixerClockTypeRules.Validate(MixerKind, ClockType)` → `ValidateClockType(ClockType)` (drop the `MixerKind` param)
- Update all call-sites (AudioVideoMixer, FakeMixer in tests)
- Update `MixerClockTypeRulesTests` to drop `MixerKind` parameter

### 5. Move `RouteMap` off `AudioVideoMixerConfig`

The global `RouteMap` in `AudioVideoMixerConfig` is a leftover from the
single-source-single-output era. With advanced routing, it becomes the
**fallback** route map used when no `AudioRoutingRule`s are defined.

**Option A**: Keep `RouteMap` on config as-is (backwards compatible default).
**Option B**: Move it to `ISupportsAdvancedRouting` as `SetDefaultAudioRouteMap(int[])`.

→ **Recommendation: Option A** — keep it on config for simplicity. The advanced
routing rules override it when present. This avoids a breaking change for existing
consumers (AVMixerTest, MediaPlayerTest, etc.).

---

## Considerations for discussion

### Consideration 1: `IAudioOutput` has no `Id` / `Guid`

`IVideoOutput` has `Guid Id`, but `IAudioOutput` does not. The `AudioRoutingRule`
needs an `OutputId` to target a specific audio output. Options:

- **(a)** Add `Guid Id { get; }` to `IAudioOutput` (breaking change for any
  external implementors, but consistent with `IVideoOutput`)
- **(b)** Use the output's list index instead of a Guid (fragile if outputs are
  added/removed dynamically)
- **(c)** Use a wrapper/key type that identifies outputs by reference equality
  (e.g. the `IAudioOutput` instance itself rather than a Guid)

→ **Recommendation: (a)** — add `Guid Id` to `IAudioOutput` for consistency.
`PortAudioOutput` already has `Device.Id` which is an `AudioDeviceId`, so
generating a `Guid` per output instance is trivial.

### Consideration 2: Should `IMediaPlayer` stop inheriting `IAudioVideoMixer`?

Currently `IMediaPlayer : IAudioVideoMixer` means the player exposes *everything*
the mixer does. The user noted something "got lost" that differentiated them.
Options:

- **(a)** Keep `IMediaPlayer : IAudioVideoMixer` — the player is a thin wrapper,
  advanced users can still access mixer methods through the player. The
  differentiation is that `ISupportsAdvancedRouting` is a separate interface that
  `MediaPlayer` does **not** implement, so consumers discover capabilities via
  interface checks.
- **(b)** Break the inheritance: `IMediaPlayer` gets its own slim surface
  (`Play`, `Stop`, `Pause`, `Resume`, `Seek`, `PositionSeconds`, events, output
  management) and holds a `IAudioVideoMixer Mixer { get; }` property for
  advanced access. This is a larger breaking change.

→ **Recommendation: (a)** — keep inheritance, use `ISupportsAdvancedRouting` as
the capability split. This is the least disruptive approach and aligns with the
"cast to discover capabilities" pattern.

### Consideration 3: Gain per routing rule vs. per source

The proposed `AudioRoutingRule` has a `Gain` field. An alternative is a simpler
rule (source channel → output channel only) and a separate `SetSourceGain(Guid sourceId, float gain)` on the mixer. The per-rule gain is more flexible (e.g.
different gain for left vs right channel of the same source) but adds complexity.

→ **Recommendation: per-rule gain** — it's one extra float per rule and avoids
needing a second API surface for volume control.

---

## Files to change

| File | Action |
|------|--------|
| `Mixing/MixerKind.cs` | **Delete** |
| `Mixing/MixerClockTypeRules.cs` | Remove `MixerKind` param → `ValidateClockType(ClockType)` |
| `Mixing/AudioVideoMixer.cs` | Update `Validate` calls; implement `ISupportsAdvancedRouting`; update audio pump to use rules when present |
| `Mixing/IAudioVideoMixer.cs` | No change (advanced routing is a separate interface) |
| `Mixing/ISupportsAdvancedRouting.cs` | **New** — interface definition |
| `Mixing/AudioRoutingRule.cs` | **New** — routing rule record struct |
| `Mixing/VideoRoutingRule.cs` | **New** — video routing rule record struct |
| `Audio/IAudioOutput.cs` | Add `Guid Id { get; }` (if consideration 1a approved) |
| `PortAudio/Output/PortAudioOutput.cs` | Add `Guid Id` property |
| `S.Media.Core.Tests/MixerClockTypeRulesTests.cs` | Drop `MixerKind` param |
| `S.Media.Core.Tests/AudioVideoMixerClockTypeTests.cs` | No change expected |
| `S.Media.Core.Tests/MediaPlayerCompositionTests.cs` | Update `FakeMixer` `SetClockType` call |
| `Playback/MediaPlayer.cs` | No change (does not implement `ISupportsAdvancedRouting`) |
| `Playback/IMediaPlayer.cs` | No change |

---

## Execution order

```
1. Delete MixerKind, simplify MixerClockTypeRules (safe, no new features)
2. Add Guid Id to IAudioOutput + PortAudioOutput
3. Create ISupportsAdvancedRouting, AudioRoutingRule, VideoRoutingRule
4. Implement ISupportsAdvancedRouting on AudioVideoMixer
5. Update audio pump to use routing rules when present
6. Update tests
```

