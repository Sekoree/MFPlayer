# AVMixer + Timeline Refactor Plan

Status: Draft (planning only, no implementation)
Date: 2026-04-15 (revised)
Scope: Breaking API changes are expected

## Why This Refactor

Today `AVMixer` still has output-coupled behavior (`AttachAudioOutput`, `AttachVideoOutput`) while also supporting sink/endpoint fan-out. This creates overlap and friction when you need:

- runtime add/remove of inputs and outputs
- scheduling what plays when (playlist/timeline behavior)
- transport-style control independent of output lifecycles
- straightforward routing semantics for both single-playback and complex graphs

The target model is:

- `AVMixer` is a routing/mixing graph core (IO orchestration only)
- `Timeline` is scheduling/playlist control (what is enabled and when)
- endpoints consume media and convert formats if needed
- outputs and sinks are unified into one endpoint concept

## Goals

- Decouple `AVMixer` from direct output attachment and output-owned mixer overrides.
- Support dynamic runtime graph mutation (add/remove inputs, endpoints, routes).
- Introduce `Timeline` to schedule channel activation by offsets/windows/enabled state.
- Unify Output/Sink semantics into a single endpoint abstraction.
- Keep simple direct path valid: decoder loop directly to endpoint without `AVMixer`.
- Push format conversion responsibility to endpoint side by default.
- Preserve low-latency real-time behavior and non-blocking callback constraints.
- Subsume `AggregateOutput` and `VirtualAudioOutput` patterns into the graph model so
  they no longer need special-case wiring.
- Merge or replace the existing endpoint adapter zoo (`AudioEndpointSinkAdapter`,
  `AudioSinkEndpointAdapter`, `VideoEndpointSinkAdapter`, `VideoSinkEndpointAdapter`,
  `AudioOutputEndpointAdapter`, `VideoOutputEndpointAdapter`) with the unified endpoint
  contract so adapters are only needed for legacy bridge code during migration.

## Non-Goals

- Rewriting decoder internals (`S.Media.FFmpeg`) in this phase.
- Designing a full nonlinear editor UI model.
- Solving every advanced transition/effect/multitrack composition feature now.
- Keeping old API source compatibility (breaking changes are allowed).
- Multi-channel video compositing/layering (VideoMixer is single-active-channel in v1;
  compositing is a future concern beyond this refactor).

## Current Pain Points (As-Is)

- `IAVMixer` includes both graph and attachment concerns (`AttachAudioOutput`, `AttachVideoOutput`).
- Distinction between `IAudioOutput`/`IVideoOutput` vs `IAudioSink`/`IVideoSink` is conceptually overlapping for users.
- Routing lifecycle is less explicit than desired for runtime graph edits.
- Playlist-like sequencing currently lives outside first-class scheduling abstractions.
- Some auto-routing assumptions (for example channel-count assumptions tied to output format) are harder to reason about once outputs are optional/dynamic.
- `AggregateOutput` re-implements fan-out by wrapping a leader output and injecting
  its own `AudioMixer` via `OverrideRtMixer`. This pattern should be expressible as
  a standard graph configuration instead of a special class.
- `VirtualAudioOutput` exists solely to provide a clock + tick loop for sink-only
  scenarios without hardware audio. The graph should offer a "virtual clock endpoint"
  as a first-class concept.
- Six different endpoint adapter classes exist to bridge between the three
  contract families (`IMediaOutput`, `IAudioSink`/`IVideoSink`,
  `IAudioBufferEndpoint`/`IVideoFrameEndpoint`). This explosion is a symptom of
  having too many endpoint roles.
- `NDIAVSink` implements both `IAudioSink` and `IVideoSink` on a single object.
  The refactored model must support **dual-media endpoints** (one object, two frame
  receive paths) without requiring separate registration.
- Clone sinks (`SDL3VideoCloneSink`, `AvaloniaOpenGlVideoCloneSink`) are created by
  parent outputs via `CreateCloneSink(...)`. The plan must decide how clone lifecycle
  ownership works when outputs are just endpoints.
- `VideoMixer` only supports a single active channel per sink target (no layering/
  compositing). Per-sink channel selection is implemented via
  `SetActiveChannelForSink`, which is a routing concept that should move to the graph.
- Audio time offsets are implemented as "insert silence / discard frames" at channel
  start. Video time offsets shift the PTS clock per target. These two mechanisms differ
  and the graph model should unify the offset semantic or at least document the
  asymmetry.
- `LocalVideoOutputRoutingPolicy` makes pixel-format routing decisions based on
  output capabilities. In the new model this logic should be expressible via the
  `IFormatCapabilities` interface on the endpoint.
- `DriftCorrector` is used by some sinks to compensate clock skew between a leader
  output and a secondary hardware sink. The graph model needs to account for
  cross-clock-domain drift correction.

## Target Architecture

### 1) Separation of Responsibilities

- `AVMixer` (or renamed `MediaGraph`):
  - owns channel registration and route graph
  - performs mixing/distribution
  - does not own windows/devices or attach to outputs directly
  - does not own playlist/scheduling semantics

- `Timeline`:
  - owns time-based activation/deactivation of channels/inputs
  - applies scheduled graph mutations to mixer control plane
  - supports seek/reset/loop semantics for playlist behavior

- `Endpoint`:
  - receives audio and/or video from mixer or decoder-direct path
  - may own clock (hardware output) or follow external clock
  - performs conversion to supported format when necessary

### 2) Unify Output and Sink

Replace output/sink split with one endpoint family:

- `IMediaEndpoint` remains base lifecycle.
- Add capability-based specializations rather than role names:
  - `IAudioBufferEndpoint` (push audio)
  - `IVideoFrameEndpoint` (push video)
  - `IAVEndpoint` (implements both audio and video receive — replaces the dual-interface
    pattern currently used by `NDIAVSink`)
  - `IClockOwner` or `IMediaClockProvider` (optional capability)
  - `IFormatCapabilities<TFormat>` (optional capability — subsumes
    `IVideoSinkFormatCapabilities` and adds audio equivalent)

Interpretation:

- A hardware output endpoint is just an endpoint that also owns/provides a clock.
- A clone/secondary destination is the same endpoint contract without clock ownership.

This removes user-facing ambiguity: "everything is an endpoint; some endpoints also define clock authority".

#### Clone Sink Lifecycle

Clone sinks created by parent outputs (`CreateCloneSink(...)`) should:

- still be created by the parent (backend-specific constraints remain).
- register as independent endpoints in the graph.
- be disposed by the parent output endpoint on parent dispose (ownership unchanged).
- graph unregisters them automatically when the parent endpoint is unregistered, or
  orphan detection logs a warning.

#### Dual-Media Endpoints (A/V Sinks)

Current `NDIAVSink` implements both `IAudioSink` and `IVideoSink`. The new model should:

- support a single endpoint object being registered once and receiving both audio and
  video through `IAVEndpoint` (which extends both `IAudioBufferEndpoint` and
  `IVideoFrameEndpoint`).
- allow routing audio and video independently to the same endpoint via separate routes.
- keep the graph registration call singular: `RegisterEndpoint(endpoint)` detects
  capabilities and creates internal audio + video target slots.

### 3) Graph Objects (Control Plane)

Introduce explicit graph records/contracts (names are proposals):

- `InputNodeId`, `EndpointId`, `RouteId`
- `IInputNode` (wraps `IAudioChannel` and/or `IVideoChannel` references)
- `IEndpointNode` (wraps endpoint instance + capabilities)
- `IRoute` (source -> destination with options)
- `IMediaGraph` (mutation/query API)

Key operations:

- add/remove input node
- add/remove endpoint node
- create/remove route
- enable/disable input
- enable/disable route
- apply route options (audio channel map, gain, video selection policy)

All control-plane mutations are explicit, transactional, and thread-safe.

### 4) Subsume AggregateOutput and VirtualAudioOutput

`AggregateOutput` is a fan-out wrapper that:

1. wraps a leader `IAudioOutput`
2. creates an internal `AudioMixer`
3. injects it into the leader via `OverrideRtMixer`
4. registers sinks with the internal mixer

In the new model this is just a graph with multiple endpoints, one of which is the
clock authority. No special wrapper class needed.

`VirtualAudioOutput` provides:

1. a software clock (`StopwatchClock`)
2. a background tick loop calling `FillOutputBuffer`
3. no actual hardware device

In the new model this becomes a **virtual clock endpoint**: an endpoint that owns a
software clock and drives the graph tick without producing audible output. The graph
scheduler uses it as clock authority for sink-only topologies.

### 5) RT Callback Bridging (OverrideRtMixer)

Currently `IAudioOutput.OverrideRtMixer(IAudioMixer)` and
`IVideoOutput.OverridePresentationMixer(IVideoMixer)` are the mechanism by which
`AVMixer` injects itself into hardware callbacks.

In the new model:

- Hardware audio endpoints (PortAudio wrapper) call a graph-provided fill callback
  directly, rather than owning their own mixer and having it overridden.
- Hardware video endpoints (SDL3, Avalonia) call a graph-provided present callback.
- The `OverrideRtMixer` pattern is removed; instead, the graph registers as the
  data provider to the hardware endpoint at `RegisterEndpoint` time.
- Specifics: the endpoint exposes a "set data source" method or the graph wraps
  the endpoint in a thin bridge that connects the RT callback to the graph's
  internal mixer. Either approach eliminates the override hack.

### 6) Per-Route Audio Resampling

Currently `AudioMixer` auto-creates a `LinearResampler` when a channel's sample rate
differs from the leader format. In the new model:

- resampling is still automatic by default.
- route options can specify a custom `IAudioResampler`.
- when multiple endpoints have different sample rates, each route can resample
  independently (currently all routes share one leader format).
- this is a stretch goal — initial implementation can keep single-leader-rate mixing
  and let endpoints resample at their boundary if rates differ.

## Canonical Data Flows

### Flow A: Decoder direct to endpoint

`Decoder -> loop until EOF -> Endpoint`

Used for minimal/simple playback or specialized one-off pipelines where mixing is unnecessary.

### Flow B: Decoder via channel and mixer

`Decoder -> InputChannel -> AVMixer -> Endpoint`

Playback begins when input channel is started/enabled and route is active.

### Flow C: Decoder via timeline scheduling

`Decoder -> InputChannel -> Timeline(schedule) -> AVMixer -> Endpoint`

`Timeline` controls when channel is active in the mixer graph.

## Timeline Model (Playlist + Scheduling)

Introduce a first-class `Timeline` object that controls channel activation and seek behavior.

### Core Concepts

- `TimelineItem`:
  - `InputNodeId`
  - `StartOffset`
  - optional `Duration`
  - `Enabled`
  - optional `LoopPolicy`
  - optional `Track`/`Layer`

- `TimelineTransportState`:
  - `Stopped`, `Running`, `Paused`, `Seeking`

- `TimelineClockSource`:
  - external clock injection or endpoint clock binding policy

### Baseline Behavior

- Items can be enabled/disabled without removing them.
- At playback position `t`, timeline computes active items and applies graph state.
- For sequential playlist behavior:
  - item1 enabled at `t=0`
  - on EOF or boundary, item1 disabled and rewound as configured
  - item2 enabled and optionally auto-started
- Seeking re-evaluates active set and applies deterministic state transitions.

### EOF Policy

Per item and/or per timeline:

- `StopTimeline`
- `AdvanceToNextEnabled`
- `LoopItem`
- `HoldLastFrame` (video-specific policy optional)

### Timeline and Decoder Interaction

The timeline does not own decoder instances. It manages input activation. When an item
is activated, the timeline:

1. enables the corresponding input node in the graph
2. optionally seeks the decoder channel to the item's media-in point
3. starts the decoder if not already running

When an item is deactivated:

1. disables the input node (route goes silent / no frames)
2. optionally seeks to 0 / resets for later reuse

The decoder's `EndOfStream` event fires and propagates to the timeline as an EOF
signal for that input, which triggers the item's `EofPolicy`.

### Push-Mode Channel Backpressure

`IAudioChannel.WriteAsync` is back-pressured by the ring buffer. When a timeline
disables an input, the pull side stops consuming, which will eventually block the
push side. The timeline must either:

- flush/reset the channel buffer on disable, or
- keep consuming (but discarding) to prevent decoder stalls.

This must be explicitly designed and documented.

## Lifecycle and State Model

### Input Channel State

Proposed channel runtime states:

- `Created`
- `Registered`
- `Ready`
- `Running`
- `Paused`
- `Eof`
- `Faulted`
- `Disposed`

Channels are registerable independent of endpoint presence.

### Endpoint State

Endpoints follow `IMediaEndpoint` lifecycle and can be attached/detached at runtime:

- register endpoint -> optional start
- route creation can happen before or after start (define deterministic behavior)
- remove endpoint safely drains or drops per endpoint policy

### Route State

Routes can be created disabled, then enabled atomically by timeline or control API.

## Threading Model

Split into two planes:

- Control plane (app/API thread):
  - graph mutations, timeline edits, transport commands

- Real-time/render plane:
  - audio callback mixing (`FillOutputBuffer` style)
  - video frame presentation loop

Rules:

- No blocking allocations/locks in RT callbacks.
- Control-plane edits are committed via lock-free snapshot or copy-on-write graph update.
- RT side reads immutable snapshots for deterministic per-buffer/per-frame behavior.

### Audio RT Callback Ownership

Currently the audio RT callback is owned by the PortAudio output and calls
`IAudioMixer.FillOutputBuffer`. In the new model:

- The graph's internal audio mixer exposes the fill callback.
- The hardware endpoint calls this callback via a registered delegate/interface.
- No `OverrideRtMixer` needed.
- Multiple audio endpoints with independent hardware clocks would each need their own
  fill callback (currently not supported — revisit if multi-clock-domain is needed).

### Video Render Loop Ownership

Currently the video render loop is owned by `SDL3VideoOutput` / Avalonia and calls
`IVideoMixer.PresentNextFrame(clockPosition)`. Same bridging pattern as audio:

- Graph exposes a present callback.
- Video endpoint calls it from its render thread.

## Timing and Clock Authority

### Clock Decision

Because outputs are no longer special, clock ownership must be explicit:

- one active clock authority per playback domain (A/V group)
- candidates:
  - hardware audio endpoint clock (common default)
  - video render clock
  - external/system clock
  - virtual clock endpoint (replaces `VirtualAudioOutput` for sink-only scenarios)

### Clock Types to Preserve

- `HardwareClock` (backed by `Pa_GetStreamTime` or similar) — used by hardware audio
  endpoints. Includes fallback to `Stopwatch` when hardware time is unavailable.
- `StopwatchClock` — pure software clock. Used by `VirtualAudioOutput` and NDI clock.
- `VideoPtsClock` — if it exists, document its role.

The graph selects one clock as authority. Other clocks may still run for monitoring
but do not drive presentation timing.

### Synchronization

- `Timeline` uses the selected playback clock.
- Audio/video channel offsets remain supported at graph or item level.
- Drift monitoring API remains available but shifts to node IDs rather than channel-only assumptions.

### Cross-Clock-Domain Drift

When a secondary endpoint has its own hardware clock (e.g. a second sound card via
`AggregateOutput` today), drift correction is needed. Currently `DriftCorrector`
handles this for some sinks.

In the new model:

- Each endpoint can optionally declare its own clock via `IClockOwner`.
- The graph detects when a routed endpoint's clock differs from the authority clock.
- An automatic or configurable `DriftCorrector` is inserted per cross-clock route.
- This replaces the manual drift correction wiring in `AggregateOutput` and sink
  implementations.

## Format Negotiation and Conversion Policy

Primary rule: mixer routes generic frames/samples; endpoint adapts.

- Mixer does not require one global fixed format for all endpoints.
- Route carries source format metadata.
- Endpoint advertises accepted formats/capabilities (optional interface).
- Conversion location order:
  1. endpoint internal conversion (preferred)
  2. optional shared conversion helper inserted at route edge
  3. fail route activation if unsupported and no converter available

This aligns with your requirement that outputs/sinks should convert if they can.

### Audio Format Concerns

- Currently `AudioMixer` requires a single `LeaderFormat` (sample rate + channels).
  All sources are resampled to this format.
- In the new model, the mixer can keep a single internal mixing format but endpoints
  with different needs resample at their boundary.
- `ChannelRouteMap` (src→dst channel mapping with gain) should attach to route options,
  not to the channel's registration with the mixer.

### Video Format Concerns

- `VideoMixer` passes raw source frames through without conversion (documented as
  "mixer does not convert").
- Endpoints convert at their boundary using `IPixelFormatConverter`.
- `LocalVideoOutputRoutingPolicy` currently selects the optimal pixel format based on
  output capabilities. In the new model this maps to the `IFormatCapabilities<VideoFormat>`
  / `IFormatCapabilities<PixelFormat>` interface on the endpoint, and the graph (or
  the endpoint adapter) uses it to decide whether conversion is needed.
- `IVideoSinkFormatCapabilities` (current) merges into the generic
  `IFormatCapabilities<PixelFormat>` interface.

## Diagnostics

Both `AudioMixer` and `VideoMixer` track extensive diagnostics (present counts, drop
counts, held frames, sink format hits/misses, etc.). The graph model should:

- expose per-route and per-endpoint diagnostics snapshots.
- merge the current mixer-internal counters into a graph-level diagnostics API.
- preserve the current `VideoEndpointDiagnosticsSnapshot` and `SDL3VideoOutput.DiagnosticsSnapshot`
  patterns but route them through the graph query API.
- add timeline-level diagnostics (active item, transport state, item transitions).

## API Direction (Breaking by Design)

### Remove / Deprecate

- `IAVMixer.AttachAudioOutput(...)`
- `IAVMixer.AttachVideoOutput(...)`
- `IAudioOutput.OverrideRtMixer(IAudioMixer)`
- `IVideoOutput.OverridePresentationMixer(IVideoMixer)`
- `AggregateOutput` (replaced by graph with multiple endpoints)
- `VirtualAudioOutput` (replaced by virtual clock endpoint)
- output/sink split routing methods that encode role semantics
- All six endpoint adapter classes (replaced by unified endpoint contracts and
  optional legacy bridge adapters during migration only)
- `IVideoSinkFormatCapabilities` (merged into `IFormatCapabilities<PixelFormat>`)

### Introduce / Replace

- graph-centric methods:
  - `RegisterInput(...)`, `UnregisterInput(...)`
  - `RegisterEndpoint(...)`, `UnregisterEndpoint(...)`
  - `CreateRoute(...)`, `RemoveRoute(...)`
  - `SetInputEnabled(...)`, `SetRouteEnabled(...)`

- timeline-centric methods:
  - `CreateTimeline(...)`
  - `AddItem(...)`, `RemoveItem(...)`, `MoveItem(...)`
  - `Seek(...)`, `Play(...)`, `Pause(...)`, `Stop(...)`

- virtual clock endpoint (replaces `VirtualAudioOutput`)
- `IAVEndpoint` for dual-media sinks (replaces implementing `IAudioSink` + `IVideoSink`)
- optional compatibility adapter layer for one release cycle (if desired)

### MediaPlayer Positioning

`MediaPlayer` becomes a high-level convenience wrapper over:

- decoder channel creation
- single default timeline construction
- default endpoint routing

Simple playlist behavior (play item1 then item2) should be implemented via timeline APIs instead of hidden player internals.

`MediaPlayer` currently requires at least one output at construction time. In the new
model it should accept zero or more endpoints and create a graph internally. If no
clock-capable endpoint is provided, it creates a virtual clock endpoint automatically.

## Migration Plan

### Phase 0: Design Freeze

- Finalize endpoint unification contract and clock ownership API.
- Finalize timeline item schema and EOF policies.
- Decide on `IAVEndpoint` shape for dual-media sinks.
- Decide on RT callback bridging mechanism (delegate vs interface).
- Decide push-mode backpressure policy when timeline disables an input.

### Phase 1: Introduce New Abstractions (Side-by-Side)

- Add new graph + timeline interfaces without removing old ones yet.
- Provide thin adapters from old output/sink APIs to new endpoints.
- Implement virtual clock endpoint.

### Phase 2: Switch Core Implementations

- Rework mixer internals to use graph snapshot model.
- Move scheduling logic out of ad-hoc player flows into timeline engine.
- Migrate `AggregateOutput` use sites to graph-based fan-out.
- Migrate `VirtualAudioOutput` use sites to virtual clock endpoint.

### Phase 3: Break Old API

- Remove/deprecate old attach/register split methods.
- Remove `OverrideRtMixer` and `OverridePresentationMixer`.
- Remove `AggregateOutput` and `VirtualAudioOutput`.
- Remove endpoint adapter classes.
- Update samples and docs to endpoint + timeline-first model.

### Phase 4: Cleanup

- Remove compatibility shims.
- Simplify `MediaPlayer` to wrapper-only responsibilities.
- Validate all sample apps compile and run.

## Testing Strategy

### Unit Tests

- graph mutation correctness (add/remove/enable/disable routes and nodes)
- timeline activation with offsets, overlaps, disabled items
- EOF policy transitions
- clock selection and drift calculations
- dual-media endpoint registration and independent A/V routing
- push-mode backpressure behavior on disable/enable transitions

### Concurrency/RT Tests

- mutation under active playback without deadlocks/glitches
- snapshot swap correctness across audio callback ticks
- endpoint hot-add/hot-remove stress tests

### Integration Tests

- the three canonical flow topologies
- mixed endpoint capabilities (conversion success/failure)
- `MediaPlayer` wrapper parity for simple scenarios
- NDI A/V sink via graph (replaces current `VirtualAudioOutput` + `AggregateOutput` wiring)
- clone sink lifecycle (parent dispose cascades, graph unregisters)

### Regression Tests

- latency, drop rate, and sync behavior compared to baseline in representative sample apps
- per-second diagnostics output matches or improves current stats reporting

## Risks and Mitigations

- Clock ambiguity with multiple clock-capable endpoints:
  - mitigate by explicit clock authority selection API and validation

- Runtime graph mutation complexity:
  - mitigate via immutable snapshots and transactional mutation API

- Conversion cost moved to endpoints:
  - mitigate with optional shared converters and capability probing

- Breaking API impact on samples/users:
  - mitigate with migration guide and temporary adapter layer

- Removing `AggregateOutput` may regress fan-out latency:
  - benchmark graph-based fan-out vs current `AggregateOutput` RT distribution

- Push-mode backpressure stall when timeline disables an input:
  - mitigate by defining clear flush/drain policy on disable

- NDI dual-sink registration complexity:
  - mitigate by designing `IAVEndpoint` early and testing with `NDIAVSink` first

## Open Questions

- Should one timeline drive both audio and video always, or allow linked dual timelines?
  **Recommendation:** single timeline per A/V group; separate timelines only for
  independent media streams (e.g. background music separate from video playlist).
- Should route changes apply immediately on next RT tick or only at timeline boundaries?
  **Recommendation:** immediate by default, with `RouteActivationMode.TimelineBoundary`
  opt-in for deterministic scripted sequences.
- How much of route DSP (gain/pan/ducking) belongs in mixer core vs dedicated processing nodes?
- Should decoder-direct flow share endpoint contracts exactly, or use a lightweight direct writer API that adapters bridge?
  **Recommendation:** share contracts exactly; decoder-direct is just "no graph in between".
- Should clone sinks auto-unregister from the graph when their parent endpoint is
  unregistered, or should the graph reject unregistering a parent that has active clones?
- Should the virtual clock endpoint support configurable tick rates (currently
  `VirtualAudioOutput` derives from `framesPerBuffer / sampleRate`)?
- How should `ChannelRouteMap` interact with the new route options — embed directly
  in `AudioRouteOptions`, or keep as a separate per-route configuration step?
  **Recommendation:** embed in `AudioRouteOptions` for simplicity.
- Should the graph auto-detect `IClockOwner` on registered endpoints and offer to
  set clock authority, or always require explicit selection?
  **Recommendation:** auto-detect + log suggestion, but require explicit selection to
  avoid silent ambiguity.

## Acceptance Criteria

This refactor is complete when all are true:

- No mixer API requires attaching outputs directly.
- `OverrideRtMixer` and `OverridePresentationMixer` are removed.
- Outputs and sinks are represented by one endpoint concept with capability flags/interfaces.
- `AggregateOutput` and `VirtualAudioOutput` are no longer needed.
- Inputs and endpoints can be added/removed at runtime during playback.
- Timeline can schedule and enable/disable inputs by offsets and playlist-like sequence.
- All three requested data-flow patterns are documented and covered by integration tests.
- `NDIAVSink` works through a single endpoint registration with independent A/V routing.
- Clone sinks integrate with the graph lifecycle.
- Existing sample apps are migrated to the new API model (or equivalent updated samples exist).
- Diagnostics parity with current per-second stats reporting.

## Deliverables

- New architecture doc (this file)
- API migration guide (`Doc/AVMixer-Timeline-Migration-Guide.md`)
- Proposed interface signatures (`Doc/AVMixer-Timeline-Proposed-Interfaces.md`)
- Updated usage examples in `Doc/Usage-Guide.md` and test apps after implementation phase
