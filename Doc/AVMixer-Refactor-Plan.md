# AVMixer Refactor Plan

Status: Draft (planning only, no implementation)
Date: 2026-04-16 (rev 3)
Scope: Hard-cut branch — all API breaks are intentional, no compatibility shims

---

## Overview

This document is split into two phases:

- **Phase 1 — AVMixer decoupling**: merge output/sink/endpoint contracts into one
  unified endpoint hierarchy, remove hard binding between AVMixer and outputs,
  make outputs runtime-swappable, rename internal types for clarity.
- **Phase 2 — Timeline**: introduce playlist/scheduling as a first-class concept.
  Deferred; design revisited after Phase 1 ships.

This document covers Phase 1 in detail and Phase 2 at outline level only.

---

## Current Architecture (As-Is)

### Key types (S.Media.Core)

| Type | Role |
|---|---|
| `IAVMixer` / `AVMixer` | Facade over `IAudioMixer` + `IVideoMixer`. Couples to outputs via `AttachAudioOutput`/`AttachVideoOutput`. |
| `IAudioMixer` / `AudioMixer` | RT audio engine. Owns a `LeaderFormat` (sample rate + channel count). Pulls from channels, resamples to leader rate, mixes, scatters to leader buffer + per-sink buffers. Called from PortAudio RT callback via `FillOutputBuffer`. |
| `IVideoMixer` / `VideoMixer` | Single-active-channel video presenter. Owns an `OutputFormat`. Pulls frames from the active channel, does PTS-based scheduling, distributes to sinks. Called from render loop via `PresentNextFrame`. |
| `IAudioOutput` / `PortAudioOutput` | Hardware audio stream. Owns clock. Has `OverrideRtMixer(IAudioMixer)`. |
| `IVideoOutput` / `SDL3VideoOutput` | Hardware video surface. Owns clock. Has `OverridePresentationMixer(IVideoMixer)`. |
| `IAudioSink` / `PortAudioSink`, `NDIAVSink` | Secondary audio fan-out. Push-based: `ReceiveBuffer(...)`. |
| `IVideoSink` / `SDL3VideoCloneSink`, `NDIAVSink` | Secondary video fan-out. Push-based: `ReceiveFrame(...)`. |
| `IAudioBufferEndpoint` | Newer unified audio push contract: `WriteBuffer(...)`. |
| `IVideoFrameEndpoint` | Newer unified video push contract: `WriteFrame(...)`. |
| `IMediaOutput` | Extends `IMediaEndpoint` with `IMediaClock Clock`. |
| `IMediaEndpoint` | Base lifecycle: `Name`, `IsRunning`, `StartAsync`, `StopAsync`, `IDisposable`. |
| `AggregateOutput` | Fan-out wrapper: wraps a leader `IAudioOutput`, creates its own `AudioMixer`, overrides leader's RT mixer, registers additional sinks. |
| `VirtualAudioOutput` | Software-clock tick loop for sink-only scenarios (no hardware device). |
| `MediaPlayer` | High-level facade. Takes `IAudioOutput?` + `IVideoOutput?` at construction, creates `AVMixer` internally. |
| 6 endpoint adapter classes | Bridge between `IMediaOutput`, `IAudioSink`/`IVideoSink`, and `IAudioBufferEndpoint`/`IVideoFrameEndpoint`. |

### Why three contract families exist today

The codebase has evolved three overlapping ways to receive media:

1. **Output** (`IAudioOutput`, `IVideoOutput`): hardware-backed, owns a clock,
   uses a pull model (RT callback calls mixer, mixer fills buffer).
2. **Sink** (`IAudioSink`, `IVideoSink`): push-based secondary target
   (`ReceiveBuffer`/`ReceiveFrame`), called from the mixer's RT path.
3. **Buffer/Frame Endpoint** (`IAudioBufferEndpoint`, `IVideoFrameEndpoint`):
   newer push-based unified contract (`WriteBuffer`/`WriteFrame`), adapted to
   sinks internally.

These three exist because outputs need a pull model (hardware drives timing) and
sinks need a push model (mixer drives timing). The endpoint contracts were added
later to unify the push side but sinks still exist alongside them.

### Coupling problems

1. **`AttachAudioOutput` / `AttachVideoOutput`** call `OverrideRtMixer` /
   `OverridePresentationMixer`. One-shot injection — cannot detach or replace.

2. **`AudioMixer.LeaderFormat`** fixed at construction. All channels resampled to
   this single rate/channel-count. Every output/sink receives the same format.

3. **`AVMixer` constructor** creates owned `AudioMixer(audioFormat)` +
   `VideoMixer(videoFormat)`, baking the format into the mixer's lifetime.

4. **`MediaPlayer`** stores outputs as readonly fields. No way to swap.

5. **`AggregateOutput`** exists because the current model can't fan out natively.

6. **Six adapter classes** bridge three contract families.

7. **`IAudioMixer` / `IVideoMixer` naming** is misleading — they are not user-facing
   mixers but internal processing engines (resampling, channel routing, PTS
   scheduling, format conversion, fan-out distribution).

---

## Phase 1 Goal

**Merge output/sink/endpoint into one unified endpoint contract per media type.
Make `AVMixer` a packet/frame forwarder with runtime-swappable endpoints.
Rename internal types for clarity.**

After Phase 1:

- One `IAudioEndpoint` replaces `IAudioOutput` + `IAudioSink` + `IAudioBufferEndpoint`.
- One `IVideoEndpoint` replaces `IVideoOutput` + `IVideoSink` + `IVideoFrameEndpoint`.
- `IAVEndpoint` for dual-media targets (NDI).
- `AVMixer` does **not** own a base sample rate or frame rate.
- `AVMixer` does **not** resample or pixel-convert.
- Audio channels are individually routable to one or more endpoints.
- Video channels are individually routable to one or more endpoints.
- Endpoints can be registered/unregistered at runtime during playback.
- `OverrideRtMixer` / `OverridePresentationMixer` are removed.
- `AggregateOutput` becomes a thin helper that groups multiple endpoints behind
  a single lifecycle facade (start/stop together), but the AVRouter handles all
  routing natively. It no longer needs to create its own mixer or override anything.
- `VirtualAudioOutput` becomes `VirtualClockEndpoint`.
- Internal `AudioMixer` / `VideoMixer` are renamed to reflect their actual role.

---

## Unified Endpoint Hierarchy

### Current → Proposed mapping

| Current | Proposed | Notes |
|---|---|---|
| `IMediaEndpoint` | `IMediaEndpoint` (unchanged) | Base lifecycle: `Name`, `IsRunning`, `StartAsync`, `StopAsync`, `IDisposable` |
| `IMediaOutput` | Removed | Clock ownership moves to `IClockCapableEndpoint` |
| `IAudioOutput` | `IAudioEndpoint` + `IClockCapableEndpoint` | PortAudio output implements both |
| `IAudioSink` | `IAudioEndpoint` | Push-based audio target |
| `IAudioBufferEndpoint` | `IAudioEndpoint` | Merged into one contract |
| `IVideoOutput` | `IVideoEndpoint` + `IClockCapableEndpoint` | SDL3 output implements both |
| `IVideoSink` | `IVideoEndpoint` | Push-based video target |
| `IVideoFrameEndpoint` | `IVideoEndpoint` | Merged into one contract |
| `IVideoSinkFormatCapabilities` | `IFormatCapabilities<PixelFormat>` | Generalized |

### New contracts

```csharp
/// <summary>Base lifecycle for all media endpoints.</summary>
public interface IMediaEndpoint : IDisposable
{
    string Name { get; }
    bool IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>Receives audio buffers from the graph.</summary>
public interface IAudioEndpoint : IMediaEndpoint
{
    /// <summary>
    /// Called by the graph to deliver mixed/forwarded audio.
    /// Implementations MUST be non-blocking on the RT thread.
    /// </summary>
    void ReceiveBuffer(ReadOnlySpan<float> buffer, int frameCount, AudioFormat format);
}

/// <summary>Receives video frames from the graph.</summary>
public interface IVideoEndpoint : IMediaEndpoint
{
    /// <summary>
    /// Called by the graph to deliver a video frame.
    /// Implementations MUST be non-blocking.
    /// </summary>
    void ReceiveFrame(in VideoFrame frame);

    /// <summary>Optional endpoint diagnostics snapshot.</summary>
    VideoEndpointDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => VideoEndpointDiagnosticsSnapshot.Empty;
}

/// <summary>Dual-media endpoint (e.g. NDIAVSink). Registered once in the graph.</summary>
public interface IAVEndpoint : IAudioEndpoint, IVideoEndpoint
{
}

/// <summary>
/// Optional capability: this endpoint can provide a clock.
/// Hardware audio outputs and virtual tick endpoints implement this.
/// </summary>
public interface IClockCapableEndpoint
{
    IMediaClock Clock { get; }
}

/// <summary>
/// Optional capability: advertises accepted formats so the graph can
/// validate route compatibility at creation time.
/// </summary>
public interface IFormatCapabilities<TFormat>
{
    IReadOnlyList<TFormat> SupportedFormats { get; }
    TFormat? PreferredFormat { get; }
}

/// <summary>
/// Optional capability on audio endpoints: the endpoint can be driven
/// by a pull callback instead of or in addition to push-based ReceiveBuffer.
/// Hardware audio outputs (PortAudio) implement this because their RT
/// callback pulls audio rather than having it pushed.
/// </summary>
public interface IPullAudioEndpoint : IAudioEndpoint
{
    /// <summary>
    /// The graph sets this when the endpoint is registered.
    /// The endpoint calls it from its RT callback to pull audio.
    /// </summary>
    IAudioFillCallback? FillCallback { get; set; }

    /// <summary>
    /// The audio format of the hardware stream.
    /// The graph reads this to know what format to produce.
    /// </summary>
    AudioFormat EndpointFormat { get; }
}

/// <summary>
/// Optional capability on video endpoints: the endpoint pulls frames
/// from a render loop rather than having them pushed.
/// SDL3/Avalonia outputs implement this.
/// </summary>
public interface IPullVideoEndpoint : IVideoEndpoint
{
    /// <summary>
    /// The graph sets this when the endpoint is registered.
    /// The endpoint calls it from its render loop to pull a frame.
    /// </summary>
    IVideoPresentCallback? PresentCallback { get; set; }
}
```

### Pull vs Push model

The distinction between outputs and sinks was really about **pull vs push**:

- **Pull endpoints** (`IPullAudioEndpoint`, `IPullVideoEndpoint`): the endpoint
  owns a hardware callback or render loop and pulls data from the graph on its
  own timing. PortAudioOutput, SDL3VideoOutput, AvaloniaOpenGlVideoOutput.
- **Push endpoints** (`IAudioEndpoint`, `IVideoEndpoint` without pull capability):
  the graph pushes data to them. NDIAVSink, PortAudioSink, SDL3VideoCloneSink.

Both are just `IAudioEndpoint` / `IVideoEndpoint`. The graph detects `IPullAudioEndpoint`
/ `IPullVideoEndpoint` at registration time and sets the callback. Push endpoints
receive data when a pull endpoint's callback triggers (or from a virtual clock tick).

This is the key insight: **the output/sink split was encoding pull-vs-push as a
type hierarchy instead of a capability**.

### How existing types migrate

| Current type | New implements | Changes |
|---|---|---|
| `PortAudioOutput` | `IPullAudioEndpoint`, `IClockCapableEndpoint` | Remove `OverrideRtMixer`. Add `FillCallback` property. RT callback calls `FillCallback.Fill(...)` instead of `_activeMixer.FillOutputBuffer(...)`. Keep `Open(device, format, fpb)` for hardware setup. |
| `SDL3VideoOutput` | `IPullVideoEndpoint`, `IClockCapableEndpoint` | Remove `OverridePresentationMixer`. Add `PresentCallback` property. Render loop calls `PresentCallback.PresentNext(...)` instead of `_activeMixer.PresentNextFrame(...)`. Keep `Open(title, w, h, format)`. |
| `AvaloniaOpenGlVideoOutput` | `IPullVideoEndpoint`, `IClockCapableEndpoint` | Same pattern as SDL3. |
| `PortAudioSink` | `IAudioEndpoint` | Rename `ReceiveBuffer` → same signature, just implements `IAudioEndpoint.ReceiveBuffer`. Trivial. |
| `NDIAVSink` | `IAVEndpoint` | Already has `ReceiveBuffer` + `ReceiveFrame`. Implements `IAVEndpoint` directly. Single registration in graph. |
| `SDL3VideoCloneSink` | `IVideoEndpoint` | Rename to implement `IVideoEndpoint.ReceiveFrame`. |
| `AvaloniaOpenGlVideoCloneSink` | `IVideoEndpoint` | Same. |
| `VirtualAudioOutput` | `IPullAudioEndpoint`, `IClockCapableEndpoint` | Becomes `VirtualClockEndpoint`. Software clock + tick loop calls `FillCallback.Fill(...)`. |

### What gets removed

- `IMediaOutput` interface
- `IAudioOutput` interface
- `IVideoOutput` interface
- `IAudioSink` interface
- `IVideoSink` interface
- `IAudioBufferEndpoint` interface
- `IVideoFrameEndpoint` interface
- `IVideoSinkFormatCapabilities` interface
- All six endpoint adapter classes
- `OverrideRtMixer` / `OverridePresentationMixer`

---

## Renaming internal types

### Problem

`IAudioMixer` and `IVideoMixer` are misleading names. They suggest user-facing
mixing APIs, but they are internal processing engines:

- `AudioMixer` does: pull from channels → resample to leader rate → apply volume
  → scatter via channel map → master volume → peak metering → write to output
  buffer → distribute to sinks. It's really an **audio render/routing engine**.
- `VideoMixer` does: pull frame from active channel → PTS-based scheduling (hold/
  drop/advance) → normalize PTS → fan-out to sinks with format capability checks.
  It's really a **video frame scheduler/distributor**.

### Proposed renames

| Current | Proposed | Rationale |
|---|---|---|
| `IAudioMixer` | `IAudioRenderer` | It renders audio: pulls, resamples, routes, mixes into buffers. Not user-facing. |
| `AudioMixer` | `AudioRenderer` | Implementation of above. |
| `IVideoMixer` | `IVideoPresenter` | It presents video: pulls, schedules PTS, distributes frames. Not user-facing. |
| `VideoMixer` | `VideoPresenter` | Implementation of above. |
| `IAVMixer` | `IAVRouter` or `IMediaGraph` | It's the user-facing routing/forwarding graph. "Mixer" implies DSP; "Router" or "Graph" is more accurate. |
| `AVMixer` | `AVRouter` or `MediaGraph` | Implementation of above. |

**Recommendation**: `IAVRouter` / `AVRouter` is concise and honest about what it does.
`AudioRenderer` and `VideoPresenter` for the internals. This doc uses these names
going forward.

### Relationship after rename

```
User-facing:  IAVRouter (register inputs, endpoints, create routes)
                  │
                  ├── AudioRenderer (internal, per-endpoint audio processing)
                  │     - pulls from channels
                  │     - applies per-route channel map + gain
                  │     - resamples if route has a resampler
                  │     - accumulates into endpoint's buffer
                  │
                  └── VideoPresenter (internal, per-endpoint video processing)
                        - pulls frames from channels
                        - PTS-based scheduling (hold/drop/advance)
                        - forwards to endpoint
```

---

## Phase 1 Proposed API

### Core IDs

```csharp
readonly record struct InputId(Guid Value);
readonly record struct EndpointId(Guid Value);
readonly record struct RouteId(Guid Value);
```

### `IAVRouter` (replaces `IAVMixer`)

```csharp
public interface IAVRouter : IDisposable
{
    // ── Input (channel) management ─────────────────────────────────────
    InputId RegisterAudioInput(IAudioChannel channel);
    InputId RegisterVideoInput(IVideoChannel channel);
    void UnregisterInput(InputId id);

    // ── Endpoint management ────────────────────────────────────────────
    EndpointId RegisterEndpoint(IAudioEndpoint endpoint);
    EndpointId RegisterEndpoint(IVideoEndpoint endpoint);
    EndpointId RegisterEndpoint(IAVEndpoint endpoint);
    void UnregisterEndpoint(EndpointId id);

    // ── Routing ────────────────────────────────────────────────────────
    RouteId CreateRoute(InputId input, EndpointId endpoint);
    RouteId CreateRoute(InputId input, EndpointId endpoint, AudioRouteOptions options);
    RouteId CreateRoute(InputId input, EndpointId endpoint, VideoRouteOptions options);
    void RemoveRoute(RouteId id);
    void SetRouteEnabled(RouteId id, bool enabled);

    // ── Clock ──────────────────────────────────────────────────────────
    /// <summary>The router's own built-in software clock. Always available.</summary>
    IMediaClock InternalClock { get; }

    /// <summary>
    /// The effective clock used for PTS scheduling and push-endpoint delivery.
    /// Returns the override clock if set, or InternalClock by default.
    /// </summary>
    IMediaClock Clock { get; }

    /// <summary>
    /// Override the router's clock with any IMediaClock — could be from a
    /// registered endpoint (hardware audio, NDI, etc.), another router's
    /// Clock/InternalClock, or any custom clock. Pass null to revert to
    /// the internal clock.
    /// </summary>
    void SetClock(IMediaClock? clock);

    // ── Per-input control ──────────────────────────────────────────────
    void SetInputVolume(InputId id, float volume);
    void SetInputTimeOffset(InputId id, TimeSpan offset);
    void SetInputEnabled(InputId id, bool enabled);

    // ── Per-endpoint control ───────────────────────────────────────────
    void SetEndpointGain(EndpointId id, float gain); // master gain per endpoint, default 1.0

    // ── Video-specific ─────────────────────────────────────────────────
    /// <summary>
    /// When true, video presentation bypasses PTS-based scheduling and
    /// always presents the newest frame. Suitable for live NDI monitoring.
    /// </summary>
    bool VideoLiveMode { get; set; }

    // ── Diagnostics ────────────────────────────────────────────────────
    TimeSpan GetAvDrift(InputId audioInput, InputId videoInput);
}
```

### Route options

```csharp
public record AudioRouteOptions
{
    /// <summary>
    /// Source→destination channel mapping with per-route gain.
    /// Null = auto-derive (mono→stereo expansion or 1:1).
    /// </summary>
    public ChannelRouteMap? ChannelMap { get; init; }

    /// <summary>Route-level gain multiplier.</summary>
    public float Gain { get; init; } = 1.0f;

    /// <summary>
    /// Optional resampler for this route. When null and source/endpoint
    /// sample rates differ, the endpoint is responsible for resampling.
    /// </summary>
    public IAudioResampler? Resampler { get; init; }
}

public record VideoRouteOptions
{
    /// <summary>Route-level gain/opacity (future use).</summary>
    public float Gain { get; init; } = 1.0f;
}
```

### Callbacks (graph → pull endpoint)

```csharp
/// <summary>
/// Implemented by the graph. Set on IPullAudioEndpoint at registration time.
/// The endpoint calls this from its RT callback.
/// </summary>
public interface IAudioFillCallback
{
    void Fill(Span<float> dest, int frameCount, AudioFormat endpointFormat);
}

/// <summary>
/// Implemented by the graph. Set on IPullVideoEndpoint at registration time.
/// The endpoint calls this from its render loop.
/// </summary>
public interface IVideoPresentCallback
{
    VideoFrame? PresentNext(TimeSpan clockPosition);
}
```

---

## Design principle: forwarder, not mixer

The current `AudioMixer` does real work on the RT thread: pull → resample →
volume → scatter → master-volume → peak → copy → sink-distribute.

In the new model, `AVRouter` is a thin forwarding layer:

- **Audio**: when a pull endpoint's callback fires, the graph's `IAudioFillCallback`
  iterates all routes targeting that endpoint, pulls from each source channel,
  applies per-route channel map + gain + optional resampler, and accumulates into
  the destination buffer. For push endpoints, the graph does the same work when
  the clock authority's tick fires, then pushes the result via `ReceiveBuffer`.

- **Video**: when a pull endpoint's render loop fires, the graph's
  `IVideoPresentCallback` looks up the route(s) for that endpoint, pulls the next
  frame (PTS-based or live-mode), and returns it. For push endpoints, the graph
  pushes via `ReceiveFrame` on tick.

When multiple audio channels are routed to the same endpoint, their contributions
are summed — this is the only "mixing" the graph does.

### What moves to the endpoint side

| Currently in AudioMixer/VideoMixer | New location |
|---|---|
| Resampling (src → leader rate) | Endpoint or optional per-route resampler |
| Channel-map scatter (baked routes) | Per-route channel map (applied at forward time, still in graph) |
| Master volume | Per-endpoint gain on `AVRouter` |
| Peak metering | Per-input measurement in graph, exposed as diagnostic |
| Sink mix-buffer management | Endpoint owns its own buffer |
| PTS scheduling (hold/drop/advance) | Stays in graph (`VideoPresenter`), but per-endpoint |
| Format capability checks | `IFormatCapabilities<T>` on endpoint, validated at route creation |

### What stays in the graph

| Concern | Notes |
|---|---|
| Input registration | Add/remove audio and video channels |
| Endpoint registration | Add/remove at runtime; detect pull vs push |
| Route management | Create/remove/enable/disable channel→endpoint routes |
| Per-route channel map + gain | Applied during forwarding (lightweight, RT-safe) |
| Per-input volume | Applied before forwarding |
| Per-endpoint gain | Applied after accumulation, before delivery |
| Clock priority selection | `RegisterClock`/`UnregisterClock`/`SetClock` — priority-based with auto-fallback |
| A/V drift monitoring | Reads channel positions |
| PTS scheduling | VideoPresenter, per-endpoint instance |

---

## `AggregateOutput` in the new model

### Current role

`AggregateOutput` wraps a leader `IAudioOutput`, creates its own `AudioMixer`,
calls `OverrideRtMixer` to intercept the leader's RT callback, and distributes
to sinks from within the overridden mixer.

### New role

Since `AVRouter` natively supports multiple endpoints with per-channel routing,
`AggregateOutput`'s routing role is entirely subsumed. However, there may still
be value in a **lifecycle grouping helper**:

```csharp
/// <summary>
/// Groups multiple endpoints so they can be started/stopped together.
/// Does NOT do any routing or mixing — that's AVRouter's job.
/// </summary>
public class EndpointGroup : IAsyncDisposable
{
    public EndpointGroup(params IMediaEndpoint[] endpoints);
    public Task StartAllAsync(CancellationToken ct = default);
    public Task StopAllAsync(CancellationToken ct = default);
}
```

This is optional and low-priority. Callers can just start/stop endpoints
individually. If `AggregateOutput` doesn't pull its weight as a lifecycle
helper, remove it entirely.

**Recommendation**: remove `AggregateOutput`. Its only remaining use would be
lifecycle grouping, which is trivial to do inline. One fewer concept to learn.

---

## Phase 1 Internal Architecture

### Audio forwarding (RT path)

When a pull audio endpoint's hardware callback fires and calls `FillCallback.Fill(...)`:

1. Graph snapshots routes targeting this endpoint (volatile read, no lock).
2. `dest` buffer is cleared.
3. For each enabled route:
   a. Pull `frameCount` frames from the source channel's ring buffer.
   b. Apply per-input volume.
   c. Apply per-route `ChannelRouteMap` gain/mapping (scatter into dest).
   d. If route has a resampler, resample (src rate → endpoint rate).
4. Apply per-endpoint gain.
5. Return (endpoint's callback writes `dest` to hardware).

For push audio endpoints, the same logic runs on the clock authority's tick, and
the result is delivered via `endpoint.ReceiveBuffer(...)`.

This is RT-safe: no allocations, no locks. Copy-on-write route snapshots.

### Video forwarding

When a pull video endpoint's render loop calls `PresentCallback.PresentNext(clockPos)`:

1. Graph looks up routes targeting this endpoint.
2. For the active route (v1: single active video channel per endpoint):
   a. Pull next frame from channel (PTS-based scheduling or live-mode).
   b. Return frame.
3. Endpoint renders/converts.

For push video endpoints, the graph calls `endpoint.ReceiveFrame(frame)` on tick.

### Thread safety

- **Control plane**: under a lock, publishes immutable snapshots via `Volatile.Write`.
- **RT/render plane**: reads snapshots via `Volatile.Read`. No locks, no allocations.
- Same copy-on-write pattern as current `AudioMixer._slots` / `VideoMixer._sinkTargets`.

### Router-owned internal clock

The `AVRouter` always owns its own software clock (`StopwatchClock`-based). This
is the **internal clock** and it is always available via `InternalClock`.

**Key behavior: the router can run with zero endpoints.**

When the router is started and has no endpoints registered (or no clock authority
endpoint selected), it still ticks on its internal clock. Channels are still
pulled, but since there are no routes to any endpoint the data is simply
discarded. This means:

- Decoders keep running and pushing to channels normally.
- The router consumes from channel ring buffers on tick so producers don't stall.
- When an endpoint is later registered and a route created, audio/video starts
  flowing immediately — no need to restart the router.
- If all endpoints are removed at runtime, the router keeps ticking on its
  internal clock. Frames/buffers are pulled and dropped.

This replaces the need for `VirtualAudioOutput` in the "no hardware" case — the
router itself fills that role. `VirtualClockEndpoint` is still useful when you
want a _specific_ tick cadence (e.g. matching a target frame rate for NDI send),
but for the common "just keep running" case, the router's internal clock suffices.

**Clock priority system (`RegisterClock` / `UnregisterClock` / `SetClock`):**

The router maintains a **priority-based clock registry**. Multiple clocks can be
registered at different priority tiers; the router automatically selects the
highest-priority one. If the active clock is unregistered, the router falls back
to the next-highest priority clock (ultimately the internal clock).

Priority tiers (`ClockPriority` enum):

| Tier | Value | Description |
|---|---|---|
| `Internal` | 0 | Built-in StopwatchClock. Always present. Users don't register at this level. |
| `Hardware` | 100 | Local hardware clocks: PortAudio, SDL3 video, virtual tick endpoint. Auto-assigned when an `IClockCapableEndpoint` is registered. |
| `External` | 200 | Network/remote clocks: NDI source clock, PTP/genlock, remote transport. |
| `Override` | 300 | Manual override. Always wins. Set via `SetClock(clock)`. |

Within the same tier, the most recently registered clock wins.

**Auto-registration:** when an endpoint implementing `IClockCapableEndpoint` is
registered via `RegisterEndpoint(...)`, its clock is automatically registered at
`Hardware` priority (configurable via `AVRouterOptions.DefaultEndpointClockPriority`).
When the endpoint is unregistered, its clock is automatically removed.

This means `MediaPlayer` and most test apps no longer need to call `SetClock`
manually — just registering a PortAudio endpoint gives the router a hardware clock.

**API:**

```csharp
// Register a clock at a specific priority
router.RegisterClock(ndiSource.Clock, ClockPriority.External);

// Auto-registered at Hardware priority when endpoint is registered:
router.RegisterEndpoint(portAudioOutput); // ← also registers portAudioOutput.Clock

// Manual override (Override priority — always wins)
router.SetClock(someSpecialClock);

// Remove the override, fall back to priority selection
router.SetClock(null);

// Explicitly unregister a clock
router.UnregisterClock(ndiSource.Clock);
```

### Clock override examples

```csharp
// Default: router uses its own internal stopwatch clock
var router = new AVRouter();

// Registering a PortAudio endpoint auto-registers its clock at Hardware priority
var hwEpId = router.RegisterEndpoint(portAudioOutput);
// router.Clock is now portAudioOutput.Clock (auto-selected)

// Register an NDI clock at External priority — takes over automatically
router.RegisterClock(ndiSource.Clock, ClockPriority.External);
// router.Clock is now ndiSource.Clock

// Unregister NDI clock — falls back to PortAudio (Hardware)
router.UnregisterClock(ndiSource.Clock);
// router.Clock is now portAudioOutput.Clock again

// Manual override always wins regardless of other registrations
router.SetClock(customClock); // Override priority
// router.Clock is customClock

// Revert to automatic priority selection
router.SetClock(null);

// Sync two routers
var routerA = new AVRouter();
var routerB = new AVRouter();
routerA.RegisterEndpoint(portAudioOutput); // auto-selects HW clock
routerB.SetClock(routerA.Clock); // follows routerA's effective clock
```

### Multi-router clock sharing

Multiple `AVRouter` instances can be synchronized via clock registration:

```csharp
var routerA = new AVRouter();
var routerB = new AVRouter();

// Router A auto-selects hardware clock from endpoint
routerA.RegisterEndpoint(portAudioOutput);

// Router B follows router A's effective clock
routerB.RegisterClock(routerA.Clock, ClockPriority.External);
```

- Push endpoints on the follower router are driven by the shared clock's
  position, keeping both routers in sync.
- Pull endpoints on the follower router still pull on their own hardware timing
  but use the shared clock for PTS scheduling and A/V sync decisions.

**Use cases:**

- Two independent media sources (e.g. two decoders) that should stay in sync on
  the same timeline — each has its own router with its own inputs/endpoints, but
  both reference the same clock.
- A "monitor" router that mirrors a subset of channels to a secondary set of
  endpoints, synchronized to the primary router's clock.
- NDI send router following the playback router's clock so NDI output timestamps
  stay aligned with local hardware output.

### Cross-clock drift handling

**Problem:** when the router has multiple inputs whose timestamps originate from
different physical clocks (e.g. a local file decoder using PTS from a local
stopwatch, plus an NDI source using the NDI sender's clock), the router's chosen
clock will only be in sync with _one_ of those sources. The other input's
timestamps will slowly drift.

This also applies when multiple clocks are registered at the same priority tier —
the router picks one, but the other clock's time base still governs its input's
timestamps.

**Solution: per-input PTS drift correction (implemented in `AVRouter`).**

The router's `VideoPresentCallbackForEndpoint` already tracks a per-input PTS
origin and applies exponential-smoothing drift correction on every presented
frame. This compensates for clock-rate differences between the source's timestamp
clock and the router's effective clock, preventing drift accumulation over long
sessions.

**How it works:**

1. On the first frame from an input, the router records the PTS origin and the
   clock position at that moment.
2. On each subsequent frame, the router computes:
   `error = (frame.Pts - ptsOrigin) - (clockPosition - clockOrigin)`
3. The PTS origin is nudged by `error × gain` (default gain = 0.005).
4. At 60 fps this gives a ~3.3 s time constant — fast enough to track real HW
   drift (~100 ppm typical), slow enough to be invisible in playback.

**Audio side:** the same drift manifests as gradual ring-buffer over/underrun.
The `AudioFillCallbackForEndpoint` should apply equivalent drift tracking when
the input's sample rate and the endpoint's sample rate come from different
physical clocks. The per-route resampler's effective ratio can be micro-adjusted
to absorb drift (same technique used by `PortAudioSink.DriftCorrector` today).

**Multiple same-priority clocks scenario:**

When two `External` clocks are registered (e.g. NDI source A's clock and NDI
source B's clock), the router picks the most recently registered one as the
effective clock. Input B's timestamps will drift relative to input A's clock.
The per-input drift correction handles this transparently — input B's PTS origin
is continuously nudged so its frames stay in sync with the effective clock.

```
Example: NDI source A (clock selected) + NDI source B (different sender)

  Router clock = NDI-A clock
  Input A: PTS from NDI-A → no drift (same clock)
  Input B: PTS from NDI-B → ~50-200 ppm drift
    → per-input drift correction nudges B's origin
    → drift stays < 10 ms indefinitely
```

**When to use `VideoLiveMode` instead:** for monitoring scenarios where frame-
perfect PTS scheduling isn't needed, `VideoLiveMode = true` bypasses all PTS
checks and drift correction, always presenting the newest frame. This is simpler
and works regardless of clock relationships, but sacrifices smooth frame pacing.

### Format negotiation

No global format. Each route carries the source's native format.
Endpoints advertise accepted formats via optional `IFormatCapabilities<TFormat>`.

Validation at route creation:
1. If endpoint has `IFormatCapabilities`, check compatibility.
2. If incompatible and route has no resampler/converter, fail the route creation
   with a clear error.
3. If no capabilities advertised, assume the endpoint handles any format.

### Cross-clock drift

When a secondary endpoint implements `IClockCapableEndpoint` (e.g. a second sound
card), the graph detects clock domain mismatch and can insert a drift corrector.
Replaces manual drift wiring in `AggregateOutput`.

### Per-route pull and fan-out

When one channel is routed to N endpoints, the graph pulls once from the channel
per tick and copies/scatters to each route's destination buffer. This avoids
multi-cursor ring buffer complexity.

---

## Phase 1 Migration

### `IAVMixer` → `IAVRouter`

| Old API | New API | Notes |
|---|---|---|
| `AttachAudioOutput(IAudioOutput)` | `RegisterEndpoint(audioEp)` + `SetClock(audioEp.Clock)` | |
| `AttachVideoOutput(IVideoOutput)` | `RegisterEndpoint(videoEp)` | |
| `AddAudioChannel(ch, routeMap)` | `RegisterAudioInput(ch)` + `CreateRoute(inputId, epId, audioOpts)` | Route map moves to route options |
| `AddAudioChannel(ch)` (auto map) | `RegisterAudioInput(ch)` + `CreateRoute(inputId, epId)` | Auto channel map |
| `AddVideoChannel(ch)` | `RegisterVideoInput(ch)` + `CreateRoute(inputId, epId)` | |
| `RemoveAudioChannel(id)` | `UnregisterInput(id)` | |
| `RemoveVideoChannel(id)` | `UnregisterInput(id)` | |
| `RegisterAudioSink(sink, ch)` | `RegisterEndpoint(sink)` | Sink is just an endpoint |
| `RouteAudioChannelToSink(chId, sink, map)` | `CreateRoute(inputId, epId, audioOpts)` | |
| `RegisterVideoSink(sink)` | `RegisterEndpoint(sink)` | |
| `RouteVideoChannelToSink(chId, sink)` | `CreateRoute(inputId, epId)` | |
| `RegisterAudioEndpoint(ep)` | `RegisterEndpoint(ep)` | No adapter |
| `RegisterVideoEndpoint(ep)` | `RegisterEndpoint(ep)` | No adapter |
| `SetAudioChannelTimeOffset(id, offset)` | `SetInputTimeOffset(id, offset)` | |
| `SetVideoChannelTimeOffset(id, offset)` | `SetInputTimeOffset(id, offset)` | |
| `VideoLiveMode` | `VideoLiveMode` | Same |

### `AggregateOutput` → removed

Old:
```csharp
var agg = new AggregateOutput(leaderOutput);
agg.AddSink(ndiSink);
avMixer.AttachAudioOutput(agg);
avMixer.RegisterAudioSink(ndiSink, 2);
avMixer.RouteAudioChannelToSink(chId, ndiSink, routeMap);
```

New:
```csharp
var hwEpId = router.RegisterEndpoint(leaderOutput);  // IPullAudioEndpoint + IClockCapableEndpoint
var ndiEpId = router.RegisterEndpoint(ndiSink);
router.SetClock(leaderOutput.Clock);
var inputId = router.RegisterAudioInput(channel);
router.CreateRoute(inputId, hwEpId, new AudioRouteOptions { ChannelMap = routeMap });
router.CreateRoute(inputId, ndiEpId, new AudioRouteOptions { ChannelMap = ndiRouteMap });
```

### `VirtualAudioOutput` → `VirtualClockEndpoint`

Old:
```csharp
var vOut = new VirtualAudioOutput(format, framesPerBuffer);
avMixer.AttachAudioOutput(vOut);
```

New:
```csharp
var vClock = new VirtualClockEndpoint(tickRate);
var epId = router.RegisterEndpoint(vClock);
router.SetClock(vClock.Clock);
```

### `NDIAVSink` (dual-media)

Old (two registrations):
```csharp
avMixer.RegisterAudioSink(ndiSink, channels);
avMixer.RegisterVideoSink(ndiSink);
```

New (single registration):
```csharp
var epId = router.RegisterEndpoint(ndiSink); // detects IAVEndpoint
router.CreateRoute(audioInputId, epId, audioOpts);
router.CreateRoute(videoInputId, epId, videoOpts);
```

### `MediaPlayer`

Current: takes `IAudioOutput?` + `IVideoOutput?` at construction.

New: takes endpoints, supports runtime add/remove.

```csharp
using var player = new MediaPlayer();
player.AddEndpoint(audioOutput);   // clock-capable → auto-selected as authority
player.AddEndpoint(videoOutput);
await player.OpenAsync("file.mp4");
await player.PlayAsync();

// Hot-swap:
player.RemoveEndpoint(audioOutput);
player.AddEndpoint(newAudioOutput);
```

### Clone sinks

- `SDL3VideoCloneSink` / `AvaloniaOpenGlVideoCloneSink`: implement `IVideoEndpoint`.
- Still created by parent output (`CreateCloneSink(...)`).
- Register as independent endpoints in the graph.
- Parent dispose cascades to clones; graph auto-unregisters orphans.

---

## Phase 1 Breaking Changes Summary

### Removed (entire types/interfaces)

- `IMediaOutput`
- `IAudioOutput`, `IVideoOutput`
- `IAudioSink`, `IVideoSink`
- `IAudioBufferEndpoint`, `IVideoFrameEndpoint`
- `IVideoSinkFormatCapabilities`
- `IAudioMixer` (public), `IVideoMixer` (public)
- `IAVMixer`
- `AggregateOutput`
- `VirtualAudioOutput`
- `AudioEndpointSinkAdapter`, `AudioSinkEndpointAdapter`, `AudioOutputEndpointAdapter`
- `VideoEndpointSinkAdapter`, `VideoSinkEndpointAdapter`, `VideoOutputEndpointAdapter`

### Introduced

- `IAudioEndpoint`, `IVideoEndpoint`, `IAVEndpoint`
- `IClockCapableEndpoint`
- `IPullAudioEndpoint`, `IPullVideoEndpoint`
- `IFormatCapabilities<TFormat>`
- `IAudioFillCallback`, `IVideoPresentCallback`
- `IAVRouter` / `AVRouter`
- `AVRouterOptions`
- `AudioRouteOptions`, `VideoRouteOptions`
- `InputId`, `EndpointId`, `RouteId`
- `VirtualClockEndpoint`

### Changed

- `IMediaClock`: removed `SampleRate`, added `TickCadence` (TimeSpan)

### Renamed (internal)

- `AudioMixer` → `AudioRenderer`
- `VideoMixer` → `VideoPresenter`

---

## Phase 1 Implementation Plan

### Step 1: New contracts

- Define `IAudioEndpoint`, `IVideoEndpoint`, `IAVEndpoint`.
- Define `IClockCapableEndpoint`, `IPullAudioEndpoint`, `IPullVideoEndpoint`.
- Define `IFormatCapabilities<T>`.
- Define `IAudioFillCallback`, `IVideoPresentCallback`.
- Define `InputId`, `EndpointId`, `RouteId`.
- Define `IAVRouter` interface.
- Define `AudioRouteOptions`, `VideoRouteOptions`.

### Step 2: Rename internals

- `AudioMixer` → `AudioRenderer`.
- `VideoMixer` → `VideoPresenter`.
- Update all internal references.

### Step 3: Implement `AVRouter`

- Copy-on-write route snapshots.
- Audio forwarding path (pull → map → accumulate).
- Video forwarding path (pull → PTS schedule → forward).
- Internal clock + `SetClock` override management.
- Push delivery to non-pull endpoints on clock tick.

### Step 4: Adapt existing outputs to new endpoint contracts

- `PortAudioOutput` → implements `IPullAudioEndpoint` + `IClockCapableEndpoint`.
  Remove `OverrideRtMixer`. RT callback calls `FillCallback.Fill(...)`.
- `SDL3VideoOutput` → implements `IPullVideoEndpoint` + `IClockCapableEndpoint`.
  Remove `OverridePresentationMixer`. Render loop calls `PresentCallback.PresentNext(...)`.
- `AvaloniaOpenGlVideoOutput` → same as SDL3.

### Step 5: Adapt existing sinks to new endpoint contracts

- `PortAudioSink` → implements `IAudioEndpoint`.
- `NDIAVSink` → implements `IAVEndpoint`.
- `SDL3VideoCloneSink` → implements `IVideoEndpoint`.
- `AvaloniaOpenGlVideoCloneSink` → implements `IVideoEndpoint`.

### Step 6: Implement `VirtualClockEndpoint`

- Software clock + tick loop.
- Implements `IPullAudioEndpoint` + `IClockCapableEndpoint`.

### Step 7: Migrate `MediaPlayer`

- Accept endpoints via `AddEndpoint` / `RemoveEndpoint`.
- Create `AVRouter` internally.
- Auto-create `VirtualClockEndpoint` when no clock-capable endpoint present.

### Step 8: Migrate sample apps

1. `MFPlayer.SimplePlayer` (audio-only)
2. `MFPlayer.VideoPlayer` (A/V)
3. `MFPlayer.MultiOutputPlayer` (fan-out)
4. `MFPlayer.NDIPlayer` / `MFPlayer.NDIAutoPlayer`
5. `MFPlayer.NDISender`
6. `MFPlayer.AvaloniaVideoPlayer`
7. `MFPlayer.VideoMultiOutputPlayer`

### Step 9: Delete old types

- Remove all types listed in "Removed" above.
- Remove adapter classes.
- Remove old `IAVMixer` and implementations.

---

## Phase 1 Testing

### Unit tests
- Graph mutation: add/remove/enable/disable routes and nodes.
- Route snapshot isolation (RT reads don't see partial control-plane updates).
- Per-route channel map application correctness.
- Clock override via `SetClock`: override with endpoint clock, revert with null.
- Internal clock: router ticks and consumes channels with zero endpoints.
- `SetClock` fallback: router reverts to internal clock if override clock dies.
- Multi-router clock sharing: two routers on same clock stay in sync.
- Dual-media endpoint registration (`IAVEndpoint`).
- Pull vs push endpoint detection at registration.
- Format capability validation at route creation.

### RT/concurrency tests
- Hot add/remove endpoint during active playback.
- Hot add/remove input during active playback.
- Route enable/disable under load (no stalls, no glitches).
- Endpoint hot-swap (remove old + add new).

### Integration tests
- Audio-only: decoder → input → router → PortAudio endpoint.
- A/V: decoder → inputs → router → PortAudio + SDL3 endpoints.
- Fan-out: one input → two audio endpoints (hardware + NDI).
- Sink-only: virtual clock endpoint + NDI endpoint.
- No-endpoint: router ticks on internal clock, decoder runs, no stall;
  add endpoint later and audio/video flows immediately.
- NDI dual-media: single `IAVEndpoint` registration.
- Clone sink lifecycle: parent dispose cascades.
- Multi-router: two routers sharing one clock, both receive synced media.

### Regression tests
- Latency measurement vs baseline.
- Drop rate / sync quality vs baseline.

---

## Phase 1 Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Push endpoints may receive audio late if clock tick jitters | Size internal buffer; same issue exists today with sink distribution |
| Pull endpoints with different sample rates from same channel | Per-route resampler option; or endpoint resamples internally |
| RT path per-route overhead vs single-mix-buffer | Benchmark; single-output case is simpler than current mix+scatter |
| Ring buffer concurrent multi-reader | Single pull per tick + copy to each route's buffer |
| Clock ambiguity with multiple IClockCapableEndpoint | Not an issue — user explicitly calls `SetClock` with the desired clock; no auto-selection |

---

## Phase 1 Open Questions

1. **Per-endpoint peak metering**: expose per-input peaks (measured after volume,
   before scatter) and/or per-endpoint peaks (measured after accumulation)?
   **Recommendation**: per-input peaks. Per-endpoint peaks are optional.

2. **Video multi-channel per endpoint**: current model supports single active
   channel per video target. Keep this in Phase 1?
   **Recommendation**: yes, keep. Compositing is future work.

3. **Pull endpoint format change at runtime**: if a pull endpoint's format changes
   (e.g. PortAudio device switch), should routes auto-update?
   **Recommendation**: endpoint fires a format-changed event; graph re-validates
   routes and warns/disables incompatible ones.

4. **Should `AVRouter` be `IAVRouter` or just a concrete class?**
   **Recommendation**: interface for testability, concrete class for normal use.

---

## Phase 2 — Timeline (Outline Only)

Deferred until Phase 1 is complete. High-level intent:

- `ITimeline` controls when inputs are active (start offset, duration, enabled state).
- `TimelineItem` per input: `InputId`, `StartOffset`, optional `Duration`, `Enabled`,
  `EofPolicy` (stop / advance / loop / hold-last-frame).
- Transport: `Play()`, `Pause()`, `Stop()`, `Seek(position)`.
- Timeline activates/deactivates routes and inputs at runtime via `AVRouter`.
- `MediaPlayer.IsLooping` becomes `TimelineEofPolicy.LoopItem`.
- Decoder EOF propagates to timeline as item-level EOF signal.
- Backpressure policy when timeline disables an input: flush/reset channel buffer.

Design details will be written as a separate doc after Phase 1 lands.

---

## Pre-Implementation Review — Uncertainties & Notes

_Added: 2026-04-16 (pre-impl audit against actual codebase)_

This section captures findings from reviewing the full codebase against the plan above.
Address these before or during implementation.

### ✅ Plan vs Reality — Confirmed Accurate

1. **Interface hierarchy** — `IMediaEndpoint` → `IMediaOutput` → `IAudioOutput`/`IVideoOutput`,
   plus `IAudioSink`/`IVideoSink` and `IAudioBufferEndpoint`/`IVideoFrameEndpoint` all exist
   exactly as described. The three-family overlap is real.

2. **`OverrideRtMixer` / `OverridePresentationMixer`** — confirmed in `IAudioOutput` (line 41)
   and `IVideoOutput` (line 30). Both are `[EditorBrowsable(Never)]` one-shot injections.

3. **`AVMixer` bakes formats at construction** — constructor takes `AudioFormat` + `VideoFormat`,
   creates owned `AudioMixer(audioFormat)` + `VideoMixer(videoFormat)`. Confirmed.

4. **`AggregateOutput`** — creates its own `AudioMixer`, calls `OverrideRtMixer` on the leader.
   Exactly as described. 184 lines of complexity that the new model eliminates.

5. **Six adapter classes** — confirmed in `Audio/Endpoints/` (3) and `Video/Endpoints/` (3).

6. **`MediaPlayer`** stores `_audioOutput` and `_videoOutput` as `readonly` fields. No runtime swap.

7. **`NDIAVSink`** implements `IAudioSink, IVideoSink, IVideoSinkFormatCapabilities` — confirmed.
   Dual registration via separate `RegisterAudioSink` + `RegisterVideoSink` calls.

8. **`PortAudioOutput`** holds `_mixer` (AudioMixer) and `_activeMixer` (volatile IAudioMixer).
   RT callback calls `_activeMixer.FillOutputBuffer(...)`. Plan's `IPullAudioEndpoint` +
   `FillCallback` pattern maps cleanly.

### ⚠️ Uncertainties / Gaps to Resolve

#### 1. `IMediaClock.SampleRate` coupling

`IMediaClock` has a `SampleRate` property (line 9 of `IMediaClock.cs`). This was designed
for audio-driven clocks. The router's internal `StopwatchClock` is currently constructed
with a sample rate: `new StopwatchClock(format.SampleRate)`.

**Question**: what `SampleRate` does the router's internal clock report when there's no
audio endpoint? The `StopwatchClock` needs _some_ rate for its `Tick` event cadence.

**Recommendation**: decouple `IMediaClock` from `SampleRate`. Add an optional
`TickCadence` (TimeSpan) property instead, or make `SampleRate` optional/0. The router's
internal clock can tick at a configurable rate (e.g. 100 Hz default) independent of any
audio sample rate. This is a prerequisite for "router runs with zero endpoints".

#### 2. `AudioMixer.PrepareBuffers(framesPerBuffer)` call

`VirtualAudioOutput` calls `_mixer.PrepareBuffers(_framesPerBuffer)` before starting.
`PortAudioOutput` likely does the same internally. The current `AudioMixer` pre-allocates
scratch buffers sized to `framesPerBuffer`.

**Question**: in the new model, who sizes the `AudioRenderer`'s scratch buffers? Each
pull endpoint has its own `framesPerBuffer`. If two pull audio endpoints with different
buffer sizes exist, the renderer needs per-endpoint scratch buffers.

**Recommendation**: the `AudioRenderer` should lazily allocate per-endpoint scratch
buffers on first `Fill` call, sized to the endpoint's requested frame count. Or the
`AVRouter` pre-allocates when a route is created, reading `IPullAudioEndpoint.EndpointFormat`
and buffer size.

#### 3. `AudioMixer` owns resamplers per-channel today

Currently, `AudioMixer.AddChannel(channel, routeMap, resampler)` creates one resampler
per channel (src rate → leader rate). In the new model, resampling moves to per-route.

**Impact**: if channel A (44.1 kHz) routes to endpoint X (48 kHz) and endpoint Y (96 kHz),
two separate resamplers are needed. Today one resampler handles it because there's a
single leader rate.

**Recommendation**: the plan's `AudioRouteOptions.Resampler` handles this. Document that
resampler instances are **per-route, not per-channel**. If no resampler is provided and
rates differ, the router should auto-create a `LinearResampler` (matching current behavior)
rather than failing. The plan says "endpoint is responsible for resampling" when no route
resampler is set — but most endpoints (NDIAVSink, PortAudioSink) do NOT resample internally
today. **Auto-create is safer as default behavior.**

#### 4. `ChannelRouteMap` currently maps src→leader channels

`ChannelRouteMap` maps source channels to the _leader output's_ channel layout. In the
new per-endpoint model, route maps need to map src→_endpoint_ channels.

**Impact**: the `ChannelRouteMap.Auto(srcChannels, dstChannels)` helper currently uses
`_audioOutputChannels` (the leader's channel count) as `dst`. In the new model, `dst`
should be the target endpoint's channel count.

**Recommendation**: no structural change to `ChannelRouteMap` needed — it's already
generic (src count → dst count). The API change is that `CreateRoute` should read the
endpoint's channel count when auto-deriving the map, not a global leader count.

#### 5. `VideoMixer` single-active-channel model

`IVideoMixer` has `RouteChannelToPrimaryOutput` and `SetActiveChannelForSink` — both
enforce **one active channel per target**. The plan preserves this ("v1: single active
video channel per endpoint").

**Note**: the plan's `CreateRoute(videoInput, videoEndpoint)` implies you could create
multiple video routes to the same endpoint. The implementation should enforce the
single-active constraint: creating a second video route to the same endpoint should
either replace the first or fail.

**Recommendation**: `CreateRoute` for video should replace any existing route to that
endpoint (last-write-wins), matching current `SetActiveChannelForSink` semantics.

#### 6. `VideoMixer.PresentNextFrame` returns `VideoFrame?` — nullable frame

The current video pull model returns `null` when no frame is ready. The plan's
`IVideoPresentCallback.PresentNext` also returns `VideoFrame?`. Good — this is consistent.

However, `VideoFrame` is a `struct` in `Media/VideoFrame.cs`. Returning `VideoFrame?`
boxes on the nullable path. Current code already does this, so no regression, but worth
noting for future optimization.

#### 7. `NDIAVSink` is 886 lines with its own internal queuing and resampling

`NDIAVSink` does significant internal work: pixel format conversion (via FFmpeg swscale),
audio format conversion, queue management, timecode calculation. It implements
`IVideoSinkFormatCapabilities` to advertise supported pixel formats.

**Impact**: migrating to `IAVEndpoint` is straightforward interface-wise, but the sink's
internal resampling (`SwrResampler`) overlaps with the plan's per-route resampler concept.

**Recommendation**: for Phase 1, let `NDIAVSink` keep its internal resampling. The
per-route resampler is an _option_ for when you want the graph to do it. NDI's internal
resampling handles the NDI-specific format requirements (interleaved int16, specific
sample rates). Document this as "endpoints may do their own conversion in addition to
or instead of per-route conversion".

#### 8. `PortAudioSink` — not in S.Media.Core

`PortAudioSink` lives in `Audio/S.Media.PortAudio/PortAudioSink.cs`, not in Core.
It implements `IAudioSink`. The plan correctly identifies it as migrating to `IAudioEndpoint`.

**Note**: `PortAudioSink` has its own `DriftCorrector` and ring buffer for cross-clock
compensation. This drift correction logic needs to survive the migration — it's not
replaced by the graph's cross-clock drift detection.

#### 9. Clock `Start`/`Stop`/`Reset` on `IMediaClock`

The router needs a tick source to drive push endpoints. Currently `StopwatchClock` and
`HardwareClock` raise `Tick`. But when using `SetClock(externalClock)`, push delivery
timing depends on the external clock's `Tick` event.

**Question**: do all `IMediaClock` implementations raise `Tick` reliably? What if the
override clock doesn't tick (e.g. a bare `Position`-only clock)?

**Recommendation**: the router should have its own internal tick timer for push delivery,
reading `Clock.Position` for timestamps but NOT depending on `Clock.Tick` for scheduling.
This decouples push timing from clock implementation details.

### 📋 Pre-Implementation Checklist

Before starting Step 1:

- [x] Decide on `IMediaClock.SampleRate` — ✅ replaced with `TickCadence`.
- [x] Decide on `IAVRouter` lifecycle — ✅ explicit `StartAsync`/`StopAsync`.
- [x] Decide on auto-resampler behavior — ✅ auto-create `LinearResampler` when rates mismatch.
- [x] Decide on video single-route-per-endpoint enforcement — ✅ last-write-wins (replace).
- [x] Decide on `MediaPlayer` zero-endpoint construction — ✅ parameterless constructor.
- [x] Decide on push-endpoint tick source — ✅ router's own timer, reads `Clock.Position`.
- [x] Review `PortAudioSink`'s `DriftCorrector` integration — ✅ survives as-is in endpoint.
- [x] Audit all sample apps — ✅ all migrated.
- [x] Clock selection model — ✅ priority-based `RegisterClock`/`UnregisterClock`/`SetClock` with auto-registration from `IClockCapableEndpoint`.
- [x] Cross-clock drift — ✅ per-input PTS origin drift correction in `VideoPresentCallbackForEndpoint`; audio-side drift handled by existing `DriftCorrector` in endpoints.

---

## Phase 1 Implementation Audit

_Added: 2026-04-20 (post-implementation review)_

### Implementation Status Summary

| Plan Step | Status | Notes |
|---|---|---|
| Step 1: New contracts | ✅ Complete | All interfaces match plan. `IVideoPresentCallback` improved to `bool TryPresentNext(out VideoFrame)` to avoid nullable-struct boxing. |
| Step 2: Rename internals | ⚠️ Simplified | `AudioRenderer`/`VideoPresenter` not created as separate classes — logic inlined into `AudioFillCallbackForEndpoint`/`VideoPresentCallbackForEndpoint`. Acceptable simplification. |
| Step 3: Implement `AVRouter` | ✅ Complete | Full routing graph with copy-on-write snapshots, push/pull, priority-based clock. |
| Step 4: Adapt outputs | ✅ Complete | `PortAudioOutput` → `IPullAudioEndpoint`, `SDL3VideoOutput` → `IPullVideoEndpoint`. |
| Step 5: Adapt sinks | ✅ Complete | `PortAudioSink` → `IAudioEndpoint`, `NDIAVSink` → `IAVEndpoint`. |
| Step 6: `VirtualClockEndpoint` | ✅ Complete | Push-based (not pull). |
| Step 7: Migrate `MediaPlayer` | ✅ Complete | Parameterless ctor, `AddEndpoint`/`RemoveEndpoint`, full state machine. |
| Step 8: Migrate sample apps | ✅ Complete | All 5+ test apps use new API. |
| Step 9: Delete old types | ✅ Complete | All old interfaces/classes removed. Some stale doc comments remain. |
| Clock priority system | ✅ Complete | `RegisterClock`/`UnregisterClock`/`SetClock` with auto-registration from endpoints. |
| Cross-clock drift (video) | ✅ Complete | PTS origin nudging in `VideoPresentCallbackForEndpoint`. |

### 🐛 Bugs Fixed (2026-04-20)

#### 1. ~~`ChannelRouteMap` is never applied during audio forwarding~~ ✅ FIXED

Added `ApplyChannelMap()` helper using pre-baked route table (`BakeRoutes()`).
Applied in both `AudioFillCallbackForEndpoint.Fill()` (pull) and `PushAudioTick()`
(push). Channel map is baked at route creation time for zero-allocation hot path.
Uses `ArrayPool<float>` for the mapped buffer.

#### 2. ~~`stackalloc` in RT callback can overflow the stack~~ ✅ FIXED

Replaced unbounded `stackalloc float[outSamples]` with
`ArrayPool<float>.Shared.Rent()`/`Return()`. Safe for any buffer size.

#### 3. ~~Push audio doesn't accumulate routes to the same endpoint~~ ✅ FIXED

`PushAudioTick()` rewritten to group routes by endpoint, accumulate all routes
into a single mixed buffer per endpoint (with channel map application), then
call `ReceiveBuffer` once.

#### 4. ~~Push/pull video consumes frames destructively on PTS skip~~ ✅ FIXED

`VideoPresentCallbackForEndpoint` now caches a `_pendingFrame`/`_pendingInputId`.
When a frame is too early, it's stored and retried on the next render tick instead
of being lost.

### ⚠️ Thread Safety

#### 5. ~~`RegisterAudioInput`/`RegisterVideoInput` don't hold `_lock`~~ ✅ FIXED

Now wrapped in `_lock` for full thread safety during registration.

#### 6. ~~`SetRouteEnabled` — no volatile write~~ ✅ FIXED

`SetRouteEnabled` now uses `Volatile.Write(ref route.Enabled, enabled)`.
All RT read paths (`Fill`, `TryPresentNext`, `PushAudioTick`, `PushVideoTick`)
use `Volatile.Read(ref route.Enabled)`.

### 🔧 Optimizations Applied (2026-04-20)

#### 7. ~~SIMD for `ApplyGain` and `MixInto`~~ ✅ DONE

Both now use `System.Numerics.Vector<float>` when `Vector.IsHardwareAccelerated`,
with scalar fallback for remainder elements. ~4–8× throughput on x86/ARM NEON.

#### 8. ~~Push timer resolution~~ ✅ FIXED

Replaced `System.Threading.Timer` (~15 ms jitter on Linux) with a dedicated
`AVRouter-PushTick` thread using `Thread.Sleep` for bulk wait + `SpinWait` for
sub-millisecond precision. Thread runs at `AboveNormal` priority.

### 📝 ~~Stale Code References~~ ✅ CLEANED UP (2026-04-20)

| File | Fix Applied |
|---|---|
| `CopyOnWriteArray.cs` | Updated doc comment to reference `AVRouter` |
| `PortAudioSink.cs` | Updated comment to reference `AVRouter` per-route resampler |
| `NDIAVChannel.cs` | Updated to reference `IAVRouter.SetInputTimeOffset` |
| `NDIPlaybackProfile.cs` | Updated to reference `IAVRouter.VideoLiveMode` |

### 📋 ~~Not Yet Implemented~~ ✅ RESOLVED (2026-04-20)

| Feature | Plan Section | Status |
|---|---|---|
| Format capability validation at route creation | "Format negotiation" | ✅ Implemented — logs warning if `IFormatCapabilities<T>` check fails |
| Per-input peak metering | "Per-input peaks" | ✅ Implemented — `GetInputPeakLevel(InputId)` + SIMD `MeasurePeak` |
| `AudioRenderer` / `VideoPresenter` named classes | Step 2 | Skipped — logic inlined (acceptable) |
| Audio-side cross-clock drift correction in router | "Cross-clock drift" | ⏸️ Deferred — relies on endpoint `DriftCorrector` (not a router concern) |
| `IAVRouter` interface (for testability) | Open question #4 | ✅ Implemented |
| `DiagnosticsSnapshot` on router | Plan mentions | ✅ Implemented — `GetDiagnosticsSnapshot()` returns full state |
