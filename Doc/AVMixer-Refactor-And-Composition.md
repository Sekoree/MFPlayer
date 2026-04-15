# AVMixer Refactor & Composition Timeline — Design Plan

> **Date:** 2026-04-15  
> **Status:** Draft — Design Phase (no implementation yet)  
> **Goal:** Decouple AVMixer from output lifetimes, enable runtime output
> hot-swap, and introduce a *Composition* layer for scheduling multiple
> media items on a shared timeline.

---

## Table of Contents

1. [Motivation](#1-motivation)
2. [Current Architecture & Pain Points](#2-current-architecture--pain-points)
3. [Design Goals](#3-design-goals)
4. [Proposal A — Decoupled Outputs](#4-proposal-a--decoupled-outputs)
5. [Proposal B — Composition (Timeline)](#5-proposal-b--composition-timeline)
6. [Proposal C — Revised MediaPlayer](#6-proposal-c--revised-mediaplayer)
7. [Clock Strategy](#7-clock-strategy)
8. [Migration Path](#8-migration-path)
9. [API Sketches](#9-api-sketches)
10. [Open Questions](#10-open-questions)
11. [Implementation Order](#11-implementation-order)
12. [Appendix — Use-Case Walk-Throughs](#appendix--use-case-walk-throughs)

---

## 1. Motivation

The library currently serves two broad use-case families:

| Use case | Example | Current approach |
|---|---|---|
| **Single-item playback** | Play `movie.mp4` to a window + speakers | `MediaPlayer` creates an `AVMixer` per `OpenAsync` call |
| **Live monitoring** | NDI receive → display + speakers | `NDIAutoPlayer` manually creates `AVMixer` + channels |

Both share the same bottleneck: **the output must exist before the mixer is
created, and cannot be changed afterwards**.  This blocks several planned
scenarios:

1. **Hot-swap outputs** — switch from headphones to speakers, or from
   SDL3 to Avalonia, without rebuilding the entire pipeline.
2. **Sequential / playlist playback** — play item 1, then item 2,
   without audible glitches or clock resets between items.
3. **Delayed output attachment** — create the mix graph first (e.g. while
   scanning a media library), attach the output later when the user clicks
   "Play".
4. **Audio-only → A/V upgrade** — start with audio playback, add a video
   window later when the user opens the video panel.
5. **Multi-output with independent lifecycles** — one output drops
   (e.g. HDMI disconnected), the rest continue.

Additionally, there is no first-class concept of **time-based scheduling of
inputs**. The current model is "add channel, it plays immediately". For a media
player with a playlist, or a broadcast playout system, we need a way to say:
"this input starts at timeline position T₁ and ends at T₂; the next input starts
at T₂".

---

## 2. Current Architecture & Pain Points

### 2.1 Object Graph

```
                       ┌──────────────────┐
                       │   Application    │
                       └─────┬────────────┘
                             │
                       ┌─────▼────────┐
                       │   AVMixer    │  ← facade
                       │ (owns Audio  │
                       │  + Video     │
                       │  Mixer)      │
                       └──┬───────┬───┘
                          │       │
              ┌───────────▼─┐   ┌─▼───────────┐
              │ AudioMixer  │   │  VideoMixer  │
              │(LeaderFmt)  │   │ (OutputFmt)  │
              └──────┬──────┘   └──────┬───────┘
                     │                 │
           ┌─────────▼──┐        ┌─────▼─────────┐
           │IAudioOutput │        │ IVideoOutput  │
           │(owns clock) │        │ (owns clock)  │
           └─────────────┘        └───────────────┘
```

### 2.2 Pain Points

| # | Issue | Root cause |
|---|---|---|
| **P-1** | AVMixer requires both mixers at construction | `internal AVMixer(IAudioMixer, IVideoMixer)` — no null allowed |
| **P-2** | AudioMixer needs `LeaderFormat` (sample rate + ch) at construction | Used to size scratch buffers; can't be changed later |
| **P-3** | VideoMixer needs `OutputFormat` at construction | Used for drop-lag threshold and format metadata |
| **P-4** | Outputs can't be detached | `AttachAudioOutput` calls `output.OverrideRtMixer(_audio)` but there's no reverse operation |
| **P-5** | No `DetachAudioOutput` / `DetachVideoOutput` | Outputs that are disposed while still attached leave a dangling mixer reference |
| **P-6** | Clock ownership is in the output | Audio clock = `Pa_GetStreamTime`, video clock = `VideoPtsClock`. Removing the output removes the clock, breaking all clock-dependent logic |
| **P-7** | `MediaPlayer` creates a new AVMixer per `OpenAsync` | This means channel IDs, sink registrations, and routing all reset when switching tracks |
| **P-8** | No input scheduling | Channels are "always on" from the moment they're added; there's no concept of start/end times |
| **P-9** | No gap handling | Between removing item 1's channels and adding item 2's, the mixer outputs silence (or stale video) — no explicit gap/black policy |

### 2.3 What Already Works Well

These aspects should be **preserved**:

- **Composition pattern**: AVMixer wraps AudioMixer + VideoMixer rather than
  inheriting — clean separation.
- **Copy-on-write channel/sink arrays**: Lock-free RT reads with management-thread
  writes — excellent for the audio hot path.
- **Sink/endpoint registration**: Per-sink routing with explicit channel maps is
  flexible and well-designed.
- **Fan-out architecture**: Leader output + N sinks, each with independent routing.
- **LiveMode**: Opt-in bypass of PTS scheduling for live sources.

---

## 3. Design Goals

### Must-Have

1. **Outputs optional at AVMixer creation** — construct with just format info.
2. **Runtime output attach/detach** — swap or remove outputs without disposing
   the mixer.
3. **Composition scheduling** — register inputs with start/end times on a
   timeline.
4. **Gapless item transitions** — item 2 starts seamlessly when item 1 ends.
5. **Backward-compatible** — existing `AVMixer` constructors and `MediaPlayer`
   continue to work.

### Nice-to-Have

6. **Crossfade between items** — overlap region where both items play, with
   configurable fade curves.
7. **Clock abstraction independent of output** — a "session clock" that can be
   backed by any output or by a software stopwatch.
8. **Format negotiation on output attach** — if the new output has a different
   sample rate, resamplers are inserted automatically.

### Non-Goals (for this phase)

- Multi-layer video compositing (picture-in-picture, overlays).
- Non-linear editing (random access to arbitrary timeline positions with frame
  accuracy).
- Plugin/effect chain architecture.

---

## 4. Proposal A — Decoupled Outputs

### 4.1 Core Idea

Separate "mixer existence" from "output existence" by making the mixer's format
the **session format** (chosen by the application), and letting outputs be
attached/detached at any time. A **session clock** replaces the output-owned
clock as the authoritative timeline.

### 4.2 Session Clock

Introduce `ISessionClock` (or reuse `IMediaClock`):

```
IMediaClock
   ├── HardwareClock      (backed by Pa_GetStreamTime)
   ├── StopwatchClock      (software-only)
   ├── VideoPtsClock       (PTS-driven)
   ├── NDIClock            (NDI timestamp-driven)
   └── **SessionClock**    (NEW — delegates to best available source)
```

`SessionClock` behaviour:

| State | Clock source |
|---|---|
| No output attached | Internal `StopwatchClock` (ticks when playing) |
| Audio output attached | Delegates to `audioOutput.Clock` (`HardwareClock`) |
| Audio output detached, video remains | Falls back to `StopwatchClock` (or `VideoPtsClock`) |
| Multiple audio outputs | Uses the **leader** output's clock |

This ensures the mixer always has a valid clock, regardless of which outputs are
connected. The `Position` property is always available; `Tick` events always
fire.

### 4.3 AVMixer Changes

```csharp
// NEW: Format-only construction (no outputs required)
public AVMixer(AudioFormat audioFormat, VideoFormat videoFormat, AVMixerOptions? options = null);
public AVMixer(AudioFormat audioFormat, AVMixerOptions? options = null);   // audio-only
public AVMixer(VideoFormat videoFormat, AVMixerOptions? options = null);   // video-only

// NEW: Session clock exposed
public IMediaClock SessionClock { get; }

// EXISTING (unchanged)
public void AttachAudioOutput(IAudioOutput output);
public void AttachVideoOutput(IVideoOutput output);

// NEW: Detach
public void DetachAudioOutput(IAudioOutput output);
public void DetachVideoOutput(IVideoOutput output);

// NEW: Query
public IAudioOutput? AudioOutput { get; }
public IVideoOutput? VideoOutput { get; }
```

### 4.4 Detach Semantics

When `DetachAudioOutput` is called:

1. The output's `OverrideRtMixer` is called with a **silent no-op mixer** stub
   (or `null` if the output supports it), so the output's RT callback no longer
   calls into our AudioMixer.
2. The `SessionClock` switches its backing source from `HardwareClock` to
   `StopwatchClock`, seamlessly continuing from the current position.
3. The AudioMixer stays alive — channels, sinks, and routing are preserved.
4. The output can be safely disposed or reused elsewhere.

When `AttachAudioOutput` is called with a new output:

1. The new output's `OverrideRtMixer` is called with our AudioMixer.
2. The `SessionClock` switches to the new output's `HardwareClock`.
3. If the new output's sample rate differs from the session's `AudioFormat`,
   the mixer's internal resamplers are reconfigured (or an error is thrown if
   the mismatch is not auto-resolvable).

### 4.5 Format Mismatch Strategy

| Scenario | Strategy |
|---|---|
| Same sample rate, same channels | Direct attach — zero overhead |
| Same sample rate, different channels | Channel routing handles this already |
| Different sample rate | **Option A:** Require session rate = output rate (throw) |
| | **Option B:** Insert a transparent output-side resampler |

**Recommendation:** Start with Option A (throw on mismatch) for simplicity. The
application is responsible for creating the AVMixer with a format that matches
(or will match) its output. Add Option B later if hot-swap between different
hardware rates becomes a real use case.

### 4.6 No-Op Mixer Stub

A small internal class used when no output is attached:

```csharp
internal sealed class SilentAudioMixer : IAudioMixer
{
    // FillOutputBuffer writes silence (or no-ops).
    // All other methods throw or no-op.
}
```

This prevents the output from crashing if its RT callback fires between detach
and disposal.

---

## 5. Proposal B — Composition (Timeline)

### 5.1 Naming

After considering several names:

| Name | Connotation | Verdict |
|---|---|---|
| Timeline | Video editing, NLE | Too complex — implies arbitrary clips, layers, effects |
| Playlist | Music player | Too simple — implies one-after-another only |
| Sequence | Broadcast playout | Close, but sounds like a fixed linear order |
| **Composition** | DAW / media framework | ✅ Right level: ordered items with start/end times, no NLE complexity |

**Winner: `Composition`** (with individual entries called **`CompositionItem`**).

### 5.2 Core Concept

A `Composition` is an ordered list of `CompositionItem` entries, each with:

- A **media source** (anything that produces `IAudioChannel` + `IVideoChannel`)
- A **start time** on the composition timeline
- An optional **end time** (or "play until source ends")
- An **enabled** flag (for skip / disable without removing)

The `Composition` manages the lifecycle of channels within the mixer: it
automatically adds/removes channels at the right times as the session clock
advances.

```
Composition Timeline
├─ Item 0: song1.mp3    [0:00 ────── 3:45]
├─ Item 1: song2.mp3          [3:45 ────── 7:20]
├─ Item 2: video.mp4                 [7:20 ────── 12:00]
└─ Item 3: ndi://SOURCE              [12:00 ────── ∞]
```

### 5.3 CompositionItem

```csharp
public sealed class CompositionItem
{
    /// <summary>Unique identifier for this item.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Human-readable label.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Factory that creates the audio/video channels for this item.
    /// Called lazily when the item is about to become active.
    /// The composition disposes the channels when the item ends.
    /// </summary>
    public required Func<CompositionItemContext, Task<CompositionChannels>> ChannelFactory { get; init; }

    /// <summary>Start position on the composition timeline.</summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// End position on the composition timeline.
    /// <see langword="null"/> = play until the source signals EndOfStream.
    /// </summary>
    public TimeSpan? EndTime { get; set; }

    /// <summary>Whether this item participates in playback.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Transition behaviour when this item starts.
    /// </summary>
    public CompositionTransition? InTransition { get; init; }
}

/// <summary>Channels produced by a composition item's factory.</summary>
public sealed class CompositionChannels : IDisposable
{
    public IAudioChannel? Audio { get; init; }
    public IVideoChannel? Video { get; init; }

    /// <summary>
    /// Optional decoder/source that should be started when the item
    /// becomes active and disposed when the item ends.
    /// </summary>
    public IDisposable? Source { get; init; }

    public void Dispose() { /* disposes Source, Audio, Video */ }
}

/// <summary>Context passed to the channel factory.</summary>
public sealed record CompositionItemContext(
    AudioFormat SessionAudioFormat,
    VideoFormat? SessionVideoFormat,
    CancellationToken CancellationToken);
```

### 5.4 Composition

```csharp
public sealed class Composition
{
    /// <summary>Ordered items. Items may overlap (for crossfades) or be gapped.</summary>
    public IReadOnlyList<CompositionItem> Items { get; }

    // Mutators
    public void Add(CompositionItem item);
    public void Insert(int index, CompositionItem item);
    public void Remove(Guid itemId);
    public void Move(int fromIndex, int toIndex);
    public void Clear();

    /// <summary>
    /// Total computed duration. If the last item has no EndTime, returns null
    /// (open-ended / live).
    /// </summary>
    public TimeSpan? Duration { get; }

    // Events
    public event EventHandler<CompositionItemEventArgs>? ItemStarted;
    public event EventHandler<CompositionItemEventArgs>? ItemEnded;
    public event EventHandler? CompositionEnded;
}
```

### 5.5 CompositionPlayer (the bridge)

The `CompositionPlayer` binds a `Composition` to an `AVMixer` and drives it
from the `SessionClock`:

```csharp
public sealed class CompositionPlayer : IDisposable
{
    public CompositionPlayer(IAVMixer mixer, Composition composition);

    /// <summary>Current position on the composition timeline.</summary>
    public TimeSpan Position { get; }

    /// <summary>Active item(s) at the current position.</summary>
    public IReadOnlyList<CompositionItem> ActiveItems { get; }

    // Transport
    public Task PlayAsync(CancellationToken ct = default);
    public Task PauseAsync(CancellationToken ct = default);
    public Task StopAsync(CancellationToken ct = default);
    public void Seek(TimeSpan position);

    /// <summary>
    /// Skip to the next enabled item. If already playing, the
    /// current item is ended and the next one starts immediately.
    /// </summary>
    public void SkipToNext();

    /// <summary>Skip to the previous item.</summary>
    public void SkipToPrevious();
}
```

### 5.6 Item Lifecycle

```
                ┌──────────┐
                │  Pending  │   (item exists in composition, not yet active)
                └─────┬─────┘
                      │  clock reaches StartTime
                      ▼
                ┌──────────┐
                │ Loading   │   (ChannelFactory called, decoder opening)
                └─────┬─────┘
                      │  channels ready
                      ▼
                ┌──────────┐
                │  Active   │   (channels registered in mixer, producing frames)
                └─────┬─────┘
                      │  clock reaches EndTime / source EOS / Skip
                      ▼
                ┌──────────┐
                │  Ending   │   (drain grace period, fade-out if configured)
                └─────┬─────┘
                      │  drain complete
                      ▼
                ┌──────────┐
                │ Disposed  │   (channels removed from mixer, disposed)
                └───────────┘
```

### 5.7 Sequential Playback (MediaPlayer Pattern)

For the common "play item 1, then item 2" use case, the `Composition` supports
an **auto-chain mode** where each item's `StartTime` is automatically set to the
previous item's end:

```csharp
var comp = new Composition { AutoChain = true };
comp.Add(new CompositionItem
{
    Label = "Track 1",
    ChannelFactory = async ctx =>
    {
        var decoder = FFmpegDecoder.Open("track1.mp3");
        decoder.Start();
        return new CompositionChannels
        {
            Audio = decoder.FirstAudioChannel,
            Source = decoder
        };
    }
});
comp.Add(new CompositionItem
{
    Label = "Track 2",
    ChannelFactory = async ctx =>
    {
        var decoder = FFmpegDecoder.Open("track2.mp3");
        decoder.Start();
        return new CompositionChannels
        {
            Audio = decoder.FirstAudioChannel,
            Source = decoder
        };
    }
});
```

When `AutoChain = true`:
- Item 0's `StartTime` is `TimeSpan.Zero` (implicit).
- Item 0's `EndTime` is left null (play until EOS).
- When Item 0's source signals `EndOfStream`, the `CompositionPlayer` sets
  Item 0's effective end time to the current clock position and activates
  Item 1 at that position.
- **The session clock does NOT reset.** It keeps advancing. Item 1's channels
  see time offsets via the existing `SetChannelTimeOffset` mechanism so their
  PTS starts from zero relative to the item start.

### 5.8 MediaPlayer Pattern: Position Reset Between Items

For a media-player UX where the position display resets to `0:00` for each
track:

```csharp
// The CompositionPlayer exposes both:
player.CompositionPosition   // → 3:45 + 1:20 = 5:05 (total timeline)
player.ItemPosition          // → 1:20 (within current item)
player.ItemDuration          // → 4:30 (current item's known duration)
player.ItemNormalizedPosition // → 1:20 / 4:30 ≈ 0.296
```

The UI binds to `ItemPosition` for the track scrubber and `CompositionPosition`
for a total-time display. No clock reset is needed — it's a display-level
concern.

### 5.9 Disabled Items / Skip

```csharp
// Disable item 1 (skip it in playback)
comp.Items[1].Enabled = false;

// Or use the player to skip at runtime:
player.SkipToNext();  // ends current item, starts next enabled item
```

When an item is disabled:
- `AutoChain` skips it entirely.
- Its `ChannelFactory` is never called.
- It still occupies its position in the item list (can be re-enabled).

### 5.10 Transitions

```csharp
public abstract class CompositionTransition
{
    /// <summary>Duration of the transition overlap.</summary>
    public TimeSpan Duration { get; init; }
}

public sealed class CrossfadeTransition : CompositionTransition
{
    /// <summary>Fade curve. Default: linear.</summary>
    public FadeCurve Curve { get; init; } = FadeCurve.Linear;
}

public sealed class CutTransition : CompositionTransition
{
    // Instant cut — Duration is zero.
}
```

For a crossfade, the `CompositionPlayer` temporarily has **two active items**:
the outgoing item (fading out) and the incoming item (fading in). Both items'
channels are registered in the mixer simultaneously, with time-varying volume
applied via the existing `IAudioChannel.Volume` property.

**Phase 1 will only implement `CutTransition` (instant switch).**
Crossfades require overlapping channels and volume automation, which can be
added later.

---

## 6. Proposal C — Revised MediaPlayer

`MediaPlayer` is rebuilt on top of `Composition` + `CompositionPlayer`:

```csharp
public sealed class MediaPlayer : IDisposable
{
    // Construction: outputs are optional, can be attached later
    public MediaPlayer(IAudioOutput? audioOutput = null, IVideoOutput? videoOutput = null);

    // NEW: The mixer persists across OpenAsync calls
    public IAVMixer Mixer { get; }

    // NEW: Composition-backed
    public Composition Composition { get; }

    // Output management (NEW — replaces constructor-only injection)
    public void AttachAudioOutput(IAudioOutput output);
    public void DetachAudioOutput();
    public void AttachVideoOutput(IVideoOutput output);
    public void DetachVideoOutput();

    // Transport (existing API, now backed by CompositionPlayer)
    public Task OpenAsync(string path, ...);
    public Task PlayAsync(...);
    public Task PauseAsync(...);
    public Task StopAsync(...);
    public void Seek(TimeSpan position);

    // Position (now item-relative for UX, like before)
    public TimeSpan Position { get; }
    public TimeSpan? Duration { get; }

    // Playlist-style convenience (NEW)
    public Task EnqueueAsync(string path, ...);
    public void SkipToNext();
    public void SkipToPrevious();

    // Events (existing + new)
    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;
    public event EventHandler<PlaybackCompletedEventArgs>? PlaybackCompleted;
    public event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;
    public event EventHandler<TrackChangedEventArgs>? TrackChanged;  // NEW
}
```

### 6.1 Key Behavioural Changes

| Aspect | Current | Proposed |
|---|---|---|
| Mixer lifetime | Per-`OpenAsync` call | Per-`MediaPlayer` instance |
| Channel IDs | Reset on each Open | Stable within a session |
| Sink registrations | Lost on Open | Preserved across Opens |
| Output requirement | At least one at construction | Optional; can attach later |
| Sequential playback | Manual (listen to PlaybackEnded, call OpenAsync again) | Built-in via `EnqueueAsync` |
| Position on track change | Continues from previous clock | Resets to 0:00 (item-relative) |

### 6.2 Backward Compatibility

The existing constructor `MediaPlayer(audioOutput, videoOutput)` continues to
work. Internally it calls `AttachAudioOutput` / `AttachVideoOutput` and creates
a single-item composition per `OpenAsync` call.

---

## 7. Clock Strategy

### 7.1 The Problem

Currently, clocks are owned by outputs:
- `PortAudioOutput` owns a `HardwareClock` (backed by `Pa_GetStreamTime`).
- `SDL3VideoOutput` owns a `VideoPtsClock`.
- `VirtualAudioOutput` owns a `StopwatchClock`.

When we detach an output, its clock becomes invalid. But the mixer and
composition still need a running clock.

### 7.2 SessionClock Design

```csharp
public sealed class SessionClock : MediaClockBase
{
    private volatile IMediaClock? _backing;
    private readonly StopwatchClock _fallback;

    // Seamless offset tracking for source switches
    private TimeSpan _offsetAtSwitch;

    public SessionClock(double sampleRate)
        : base(TimeSpan.FromMilliseconds(10))
    {
        _fallback = new StopwatchClock(sampleRate);
    }

    public override TimeSpan Position
    {
        get
        {
            var backing = _backing;
            return backing != null
                ? _offsetAtSwitch + backing.Position
                : _offsetAtSwitch + _fallback.Position;
        }
    }

    /// <summary>
    /// Switches the backing clock source. The position is preserved
    /// seamlessly — the offset is adjusted so Position doesn't jump.
    /// </summary>
    internal void SwitchBacking(IMediaClock? newBacking)
    {
        var currentPos = Position;
        _offsetAtSwitch = currentPos;

        if (newBacking != null)
        {
            // New backing starts from its current position;
            // we subtract it so our Position continues from currentPos.
            _offsetAtSwitch = currentPos - newBacking.Position;
        }
        else
        {
            _fallback.Reset();
            if (IsRunning) _fallback.Start();
        }
        _backing = newBacking;
    }
}
```

### 7.3 Clock Hierarchy

```
SessionClock (authoritative timeline)
    │
    ├── HardwareClock (from PortAudioOutput)     ← preferred when attached
    ├── StopwatchClock (internal fallback)        ← used when no output
    └── NDIClock / VideoPtsClock                  ← for NDI / video-only cases
```

### 7.4 Who Uses the Clock?

| Consumer | Clock source |
|---|---|
| AudioMixer (RT fill) | Called by output's callback — uses output's own timing |
| VideoMixer (render loop) | Reads `SessionClock.Position` (or overridden presentation clock) |
| CompositionPlayer | Reads `SessionClock.Position` to decide when to activate/deactivate items |
| Application (UI) | Reads `SessionClock.Position` (or `CompositionPlayer.ItemPosition`) |

---

## 8. Migration Path

### Phase 1: Decoupled Outputs (non-breaking)

**Changes:**

1. Add `SessionClock` class to `S.Media.Core/Clock/`.
2. Add `DetachAudioOutput(IAudioOutput)` and `DetachVideoOutput(IVideoOutput)` to
   `IAVMixer` and `AVMixer`.
3. Add `SessionClock` property to `IAVMixer`.
4. AVMixer constructor creates a `SessionClock` internally.
5. `AttachAudioOutput` switches the `SessionClock` backing to the output's clock.
6. `DetachAudioOutput` switches the `SessionClock` backing to the internal
   fallback.
7. Add `SilentAudioMixer` stub for safe detach.
8. Make `IAudioOutput?` and `IVideoOutput?` nullable in `AVMixer` (allow
   construction without outputs — already partially supported).

**Backward compatibility:** All existing code continues to work. The new
`DetachXxxOutput` methods are additive. `SessionClock` is an additional property.

**Test plan:**
- Unit test: create AVMixer without output → `SessionClock` works via stopwatch.
- Unit test: attach output → `SessionClock` delegates to hardware clock.
- Unit test: detach output → `SessionClock` continues seamlessly.
- Unit test: re-attach different output → no position jump.
- Integration test: `SimplePlayer` works unchanged.
- Integration test: `NDIAutoPlayer` works unchanged.

### Phase 2: Composition (additive)

**Changes:**

1. Add `CompositionItem`, `CompositionChannels`, `CompositionItemContext` to
   `S.Media.Core/Composition/`.
2. Add `Composition` class.
3. Add `CompositionPlayer` class.
4. Add `CompositionTransition`, `CutTransition` (crossfade deferred).

**Backward compatibility:** Entirely additive — new types only.

**Test plan:**
- Unit test: single-item composition plays to completion.
- Unit test: two-item auto-chain transitions seamlessly.
- Unit test: `SkipToNext` / `SkipToPrevious` work.
- Unit test: disabled items are skipped.
- Unit test: seek within item adjusts channel time offset.
- Unit test: seek across item boundary activates correct item.
- Integration test: audio playlist playback with auto-chain.

### Phase 3: Revised MediaPlayer (mostly backward-compatible)

**Changes:**

1. Refactor `MediaPlayer` to use `Composition` + `CompositionPlayer` internally.
2. Add `EnqueueAsync`, `SkipToNext`, `SkipToPrevious`, `TrackChanged` event.
3. Add `AttachAudioOutput` / `DetachAudioOutput` to `MediaPlayer`.
4. Keep existing `OpenAsync` / `PlayAsync` / etc. working as before.
5. `Mixer` property now returns a persistent mixer (not recreated per Open).

**Backward compatibility:** The `OpenAsync` API keeps working. The only
breaking change is that `Mixer` is now non-null after construction (previously
null until `OpenAsync`). Code that checks `player.Mixer is { } mixer` will
still work. Code that checks `player.Mixer == null` to detect "no media open"
should check `player.State == PlaybackState.Idle` instead.

**Test plan:**
- Existing `MediaPlayer` test suite passes.
- New test: `EnqueueAsync` two tracks → gapless transition.
- New test: `OpenAsync` while playing → replaces composition.
- Integration test: `SimplePlayer` and `VideoPlayer` work unchanged.

### Phase 4: Crossfade Transitions (future)

**Changes:**

1. Implement `CrossfadeTransition`.
2. `CompositionPlayer` supports overlapping active items with volume automation.
3. Requires two channels registered simultaneously in the mixer.

---

## 9. API Sketches

### 9.1 Simple Audio Playback (current style, still works)

```csharp
using var output = new PortAudioOutput();
output.Open(device, format);

using var player = new MediaPlayer(audioOutput: output);
await player.OpenAsync("song.mp3");
await player.PlayAsync();
// ...
await player.StopAsync();
```

### 9.2 Delayed Output Attachment (new)

```csharp
// Create player with no output (e.g. during library scan)
using var player = new MediaPlayer();
await player.OpenAsync("song.mp3");

// Later, when user clicks Play:
using var output = new PortAudioOutput();
output.Open(device, format);
player.AttachAudioOutput(output);
await player.PlayAsync();
```

### 9.3 Output Hot-Swap (new)

```csharp
// Playing on speakers
player.AttachAudioOutput(speakerOutput);
await player.PlayAsync();

// User switches to headphones — no interruption
var headphoneOutput = new PortAudioOutput();
headphoneOutput.Open(headphoneDevice, format);
player.DetachAudioOutput();
// (playback continues with internal clock)
player.AttachAudioOutput(headphoneOutput);
// (playback continues with headphone clock)
```

### 9.4 Playlist (new)

```csharp
using var player = new MediaPlayer(audioOutput: output);

await player.OpenAsync("track1.mp3");
await player.EnqueueAsync("track2.mp3");
await player.EnqueueAsync("track3.mp3");

player.TrackChanged += (_, e) =>
    Console.WriteLine($"Now playing: {e.Label}");

await player.PlayAsync();
// Plays all three tracks sequentially.
```

### 9.5 Composition with Manual Scheduling (new, advanced)

```csharp
using var mixer = new AVMixer(audioFormat, videoFormat);
mixer.AttachAudioOutput(output);
mixer.AttachVideoOutput(videoOutput);

var comp = new Composition();
comp.Add(new CompositionItem
{
    Label = "Intro",
    StartTime = TimeSpan.Zero,
    EndTime = TimeSpan.FromSeconds(10),
    ChannelFactory = async ctx =>
    {
        var dec = FFmpegDecoder.Open("intro.mp4");
        dec.Start();
        return new CompositionChannels
        {
            Audio = dec.FirstAudioChannel,
            Video = dec.FirstVideoChannel,
            Source = dec
        };
    }
});
comp.Add(new CompositionItem
{
    Label = "Main Content",
    StartTime = TimeSpan.FromSeconds(10),
    ChannelFactory = async ctx =>
    {
        var src = await NDIAVChannel.OpenByNameAsync("NDI Source", ct: ctx.CancellationToken);
        src.Start();
        return new CompositionChannels
        {
            Audio = src.AudioChannel,
            Video = src.VideoChannel,
            Source = src
        };
    }
});

using var player = new CompositionPlayer(mixer, comp);
await player.PlayAsync();
```

### 9.6 NDI with Runtime Video Output (new)

```csharp
// Start NDI audio-only
using var mixer = new AVMixer(audioFormat);
mixer.AttachAudioOutput(output);

var ndi = await NDIAVChannel.OpenByNameAsync("Camera 1");
mixer.AddAudioChannel(ndi.AudioChannel);
await output.StartAsync();

// Later, user opens a video window:
using var videoOut = new SDL3VideoOutput();
videoOut.Open("Camera 1", 1920, 1080, videoFormat);
mixer.AttachVideoOutput(videoOut);
mixer.AddVideoChannel(ndi.VideoChannel!);
await videoOut.StartAsync();
```

---

## 10. Open Questions

| # | Question | Options | Leaning |
|---|---|---|---|
| **Q-1** | Should `SessionClock` be exposed on `IAVMixer` or only on the concrete `AVMixer`? | Interface (accessible to all consumers) vs. concrete (implementation detail) | Interface — consumers need it for UI updates |
| **Q-2** | Should `Composition` own the `AVMixer`, or should it be passed in? | Owned (simpler) vs. injected (more flexible) | Injected — keeps composition logic separate from output routing |
| **Q-3** | Should `CompositionPlayer` handle output Start/Stop, or just channel lifecycle? | Full transport (like MediaPlayer) vs. channel-only (mixer + outputs managed externally) | Channel-only for `CompositionPlayer`; `MediaPlayer` wraps it for full transport |
| **Q-4** | How should seek across item boundaries work? | (a) Seek activates the item at that position, disposes others. (b) Seek activates item, keeps previous loaded. | (a) — simpler, lower memory |
| **Q-5** | Should `CompositionItem.ChannelFactory` be sync or async? | Async (decoders may need I/O) vs. sync (simpler scheduling) | Async — opening a file or NDI source is inherently async |
| **Q-6** | What happens when an item's source has different audio format than the session? | Error vs. auto-resample | Auto-resample — AudioMixer already handles this via per-channel resamplers |
| **Q-7** | Should the composition pre-load the next item before the current one ends? | Yes (gapless) vs. no (simpler) | Yes — load N seconds before end for gapless transitions |
| **Q-8** | How does `VideoLiveMode` interact with composition? | Per-item setting vs. global mixer setting | Global mixer setting — it's an output concern, not a content concern |
| **Q-9** | Should `DetachAudioOutput` stop the output, or leave it running? | Stop (safer) vs. leave running (caller's responsibility) | Leave running — the caller owns the output and may reuse it |

---

## 11. Implementation Order

| Phase | Scope | Est. Effort | Dependencies |
|---|---|---|---|
| **1a** | `SessionClock` | 1–2 days | None |
| **1b** | `DetachAudioOutput` / `DetachVideoOutput` on AVMixer | 1–2 days | 1a |
| **1c** | `SilentAudioMixer` no-op stub | 0.5 day | None |
| **1d** | Unit tests for Phase 1 | 1 day | 1a–1c |
| **2a** | `CompositionItem` + `CompositionChannels` | 1 day | None |
| **2b** | `Composition` (item management, auto-chain) | 2 days | 2a |
| **2c** | `CompositionPlayer` (clock-driven activation) | 3–4 days | 1a, 2b |
| **2d** | `CutTransition` | 0.5 day | 2c |
| **2e** | Unit tests for Phase 2 | 2 days | 2a–2d |
| **3a** | Refactor `MediaPlayer` onto Composition | 2–3 days | 2c |
| **3b** | Add `EnqueueAsync` / `SkipToNext` / etc. | 1 day | 3a |
| **3c** | Integration tests + sample app update | 1–2 days | 3a–3b |
| **4** | `CrossfadeTransition` (future) | 3–5 days | 2c |

**Total estimated effort:** ~15–22 days for Phases 1–3.

---

## Appendix — Use-Case Walk-Throughs

### A.1 Media Player: Play Track 1, Then Track 2

```
Timeline:  0:00 ──────────── 3:45 ──────────── 7:20
           │ Track 1 (song1) │ Track 2 (song2) │
           │                 │                  │
Mixer:     │ [audioChA]      │ [audioChB]       │
           │ added @ 0:00    │ added @ 3:45     │
           │ removed @ 3:45  │ removed @ 7:20   │
           │                 │                  │
Clock:     │ SessionClock ─────────────────────→│
           │ (backed by HardwareClock)          │
           │                                    │
UI:        │ pos: 0:00→3:45  │ pos: 0:00→3:35  │
           │ (item-relative) │ (item-relative)  │
```

1. `CompositionPlayer` clock reaches 0:00 → calls Item 0's factory → gets
   `audioChA` → calls `mixer.AddAudioChannel(audioChA)`.
2. `audioChA` signals `EndOfStream` at clock ≈ 3:45.
3. `CompositionPlayer` records Item 0's effective end = 3:45.
4. `CompositionPlayer` calls Item 1's factory → gets `audioChB`.
5. `mixer.SetAudioChannelTimeOffset(audioChB.Id, TimeSpan.FromMinutes(3) + ...)` so
   audioChB's PTS 0:00 maps to composition time 3:45.
6. `mixer.AddAudioChannel(audioChB)` → `mixer.RemoveAudioChannel(audioChA.Id)`.
7. `audioChA` is disposed. Playback of Track 2 begins seamlessly.
8. UI shows `ItemPosition = clock - 3:45 = 0:00 → 3:35`.

### A.2 NDI Monitor: Audio Starts, Video Window Opens Later

```
Time: ──── t₀ ───── t₁ ───── t₂ ─────
           │ Audio  │ Audio  │ Audio+Video
           │ only   │ only   │
           │        │        │
Mixer:     │ [ndiAudio]      │ [ndiAudio] + [ndiVideo]
Outputs:   │ [PA]            │ [PA] + [SDL3]
Clock:     │ SessionClock ───│──────────→
           │ (HW via PA)     │ (HW via PA)
```

1. At t₀: `mixer = new AVMixer(audioFormat)`, `mixer.AttachAudioOutput(pa)`.
2. At t₀: `mixer.AddAudioChannel(ndi.AudioChannel)`, start PA.
3. At t₁: User opens video window.
4. At t₁: `videoOut.Open(...)`, `mixer.AttachVideoOutput(videoOut)`.
5. At t₁: `mixer.AddVideoChannel(ndi.VideoChannel)`, start SDL3.
6. Audio continues without interruption. Video starts showing frames.

### A.3 Output Hot-Swap: Speakers → Headphones

```
Time: ──── t₀ ───── t₁ ───── t₂ ─────
           │ Playing│ Swap  │ Playing
           │ (spkr) │       │ (hdph)
Clock:     │ HW(spk)│ SW    │ HW(hdph)
           │ 5.0s   │ 5.0s  │ 5.0s
           │        │  ↑    │
           │        │  seamless
```

1. At t₁: `mixer.DetachAudioOutput(speakerOutput)`.
   - `SessionClock` saves position (5.0s), switches to `StopwatchClock`.
   - `speakerOutput.OverrideRtMixer(silentStub)`.
2. Application disposes `speakerOutput`, opens `headphoneOutput`.
3. At t₂: `mixer.AttachAudioOutput(headphoneOutput)`.
   - `SessionClock` switches to `headphoneOutput.Clock`, adjusting offset so
     position continues from 5.0s.
   - `headphoneOutput.OverrideRtMixer(audioMixer)`.
4. Audio resumes from exactly where it left off.

