# AVMixer + Timeline Migration Guide

Status: Draft migration guide (design-time, revised)
Date: 2026-04-15
Related: `Doc/AVMixer-Timeline-Refactor-Plan.md`, `Doc/AVMixer-Timeline-Proposed-Interfaces.md`

## Purpose

This guide maps current `AVMixer`/`MediaPlayer` usage to the proposed graph + timeline architecture.

- Scope is API migration only (no implementation details).
- Breaking changes are expected and intentional.
- Terminology follows the refactor plan: inputs, endpoints, routes, timeline.

## Migration Principles

- Treat `AVMixer` as graph orchestration only (no output attachment APIs).
- Treat all destinations as endpoints (no user-facing output vs sink split).
- Use explicit route creation instead of implicit attach + auto-route behavior.
- Move sequencing/playlist behavior into `Timeline`.

## Terminology Mapping

| Current term | Proposed term | Notes |
|---|---|---|
| Audio/Video Output | Endpoint with clock capability | Hardware-backed endpoint usually owns clock |
| Audio/Video Sink | Endpoint without clock capability | Secondary fan-out target |
| Channel add/remove on `IAVMixer` | Input node register/unregister | Input may contain audio, video, or both |
| Attach output to mixer | Register endpoint + choose clock authority | No direct mixer→output attach call |
| Route to sink/endpoint methods | `CreateRoute(...)` + `SetRouteEnabled(...)` | One routing model for all destinations |
| Ad-hoc playlist logic in player | `ITimeline` items + transport | Timeline owns activation windows |
| `AggregateOutput` | Graph with multiple audio endpoints | Fan-out is a graph topology, not a wrapper class |
| `VirtualAudioOutput` | Virtual clock endpoint | Drives graph tick without hardware device |
| `NDIAVSink` (dual IAudioSink + IVideoSink) | Single `IAVEndpoint` registration | One object, two frame paths, one graph entry |
| Clone sink (e.g. `SDL3VideoCloneSink`) | Clone endpoint (still parent-created) | Registers independently in graph, parent owns lifecycle |
| `OverrideRtMixer(...)` | Graph-provided fill/present callback | Hack replaced by graph-endpoint binding |

## Old -> New API Mapping

The table below is the practical migration center.

### `IAVMixer` mappings

| Old API | New API | Migration action |
|---|---|---|
| `AttachAudioOutput(IAudioOutput)` | `RegisterEndpoint(IAudioBufferEndpoint)` + `SelectClockAuthority(...)` | Replace attach with endpoint registration and explicit clock choice |
| `AttachVideoOutput(IVideoOutput)` | `RegisterEndpoint(IVideoFrameEndpoint)` + `SelectClockAuthority(...)` (if needed) | Same model as audio |
| `AddAudioChannel(...)` | `RegisterInput(inputNode)` | Input node wraps channel + metadata |
| `AddVideoChannel(...)` | `RegisterInput(inputNode)` | Use same input abstraction |
| `RemoveAudioChannel(Guid)` | `UnregisterInput(InputNodeId)` | ID becomes graph-level node ID |
| `RemoveVideoChannel(Guid)` | `UnregisterInput(InputNodeId)` | same |
| `RegisterAudioSink(...)` | `RegisterEndpoint(...)` | sink/output role removed |
| `RegisterVideoSink(...)` | `RegisterEndpoint(...)` | same |
| `RouteAudioChannelToSink(...)` | `CreateRoute(inputId, endpointId, options)` | include channel map in options |
| `RouteVideoChannelToSink(...)` | `CreateRoute(inputId, endpointId, options)` | include video route policy in options |
| `Unroute*` methods | `RemoveRoute(routeId)` or `SetRouteEnabled(routeId, false)` | disable vs remove is explicit choice |
| `Set*ChannelTimeOffset(...)` | timeline item `StartOffset` and/or per-route offset | scheduling belongs in timeline |
| `RegisterAudioEndpoint(...)` / `RegisterVideoEndpoint(...)` | `RegisterEndpoint(...)` | Unified call; no separate audio/video registration |
| `RouteAudioChannelToEndpoint(...)` / `RouteVideoChannelToEndpoint(...)` | `CreateRoute(...)` with `AudioRouteOptions` / `VideoRouteOptions` | Audio route map moves into route options |

### `AggregateOutput` mappings

| Old pattern | New pattern | Migration action |
|---|---|---|
| `new AggregateOutput(leader)` | Register leader as clock-capable endpoint in graph | No wrapper class needed |
| `aggregate.AddSink(sink)` | `graph.RegisterEndpoint(sink)` + `graph.CreateRoute(...)` | Sinks are just endpoints |
| `aggregate.Open(device, format)` | Leader endpoint `Open(...)` (unchanged, endpoint-specific) | Hardware setup stays on endpoint |
| `aggregate.StartAsync()` starts leader + sinks | `graph.Play()` or start endpoints individually | Graph or timeline manages lifecycle |
| `mixer.AttachAudioOutput(aggregate)` | `graph.TrySelectClockAuthority(leaderEndpointId)` | Graph drives the mixer; leader clock drives the graph |

### `VirtualAudioOutput` mappings

| Old pattern | New pattern | Migration action |
|---|---|---|
| `new VirtualAudioOutput(format, fpb)` | `new VirtualClockEndpoint(format, fpb)` | Same concept, new type |
| `avMixer.AttachAudioOutput(virtualOut)` | `graph.RegisterEndpoint(virtualClock)` + `graph.TrySelectClockAuthority(...)` | Virtual clock drives graph |
| `virtualOut.StartAsync()` | `virtualClock.StartAsync()` or `graph.Play()` | Part of normal graph lifecycle |

### `NDIAVSink` mappings

| Old pattern | New pattern | Migration action |
|---|---|---|
| `avMixer.RegisterAudioSink(ndiSink, ch)` + `avMixer.RegisterVideoSink(ndiSink)` (two registrations) | `graph.RegisterEndpoint(ndiSink)` (single registration; graph detects `IAVEndpoint`) | One call registers both audio and video targets |
| `avMixer.RouteAudioChannelToSink(chId, ndiSink, routeMap)` | `graph.CreateRoute(inputId, ndiEndpointId, audioOptions)` | Audio route with channel map in options |
| `avMixer.RouteVideoChannelToSink(chId, ndiSink)` | `graph.CreateRoute(inputId, ndiEndpointId, videoOptions)` | Video route (same endpoint, different media kind) |

### Clone sink mappings

| Old pattern | New pattern | Migration action |
|---|---|---|
| `var clone = output.CreateCloneSink(...)` | Same (backend-specific creation stays) | No change to creation |
| `avMixer.RegisterVideoSink(clone)` | `graph.RegisterEndpoint(clone)` | Clone is just an endpoint |
| `avMixer.RouteVideoChannelToSink(chId, clone)` | `graph.CreateRoute(inputId, cloneEndpointId)` | Standard route |
| Disposing parent output disposes clones | Same + graph auto-unregisters orphans | Add orphan detection |

### `MediaPlayer` mappings

| Old API | New API direction | Migration action |
|---|---|---|
| `new MediaPlayer(audioOutput, videoOutput)` | `MediaPlayer` builds graph + default timeline internally | user passes endpoints or endpoint factory |
| `AddAudioSink(...)` / `AddVideoSink(...)` | `AddEndpoint(...)` + `CreateRoute(...)` | high-level wrapper can keep convenience overloads |
| `RemoveAudioSink(...)` / `RemoveVideoSink(...)` | `RemoveEndpoint(...)` (or unroute first) | explicit graph lifecycle |
| `OpenAsync(...)` + implicit channel wiring | `OpenAsync(...)` + explicit `RegisterInput(...)` (internal or advanced mode) | no hidden output attach |
| `PlayAsync()` starts outputs + decoder | `Timeline.Play()` + endpoint start policy | transport is timeline-first |
| `Seek(TimeSpan)` directly on decoder | `Timeline.Seek(position)` (+ decoder seek bridge) | timeline is authoritative for active set |
| `IsLooping` property | `TimelineEofPolicy.LoopItem` on the timeline item | First-class EOF policy |

### Endpoint adapter mappings (removal)

| Adapter class | New approach |
|---|---|
| `AudioEndpointSinkAdapter` | Not needed — `IAudioBufferEndpoint` is the primary contract |
| `AudioSinkEndpointAdapter` | Not needed — old `IAudioSink` implementors migrate to `IAudioBufferEndpoint` |
| `AudioOutputEndpointAdapter` | Not needed — hardware outputs implement `IAudioBufferEndpoint` + `IClockOwner` directly |
| `VideoEndpointSinkAdapter` | Not needed — `IVideoFrameEndpoint` is the primary contract |
| `VideoSinkEndpointAdapter` | Not needed — old `IVideoSink` implementors migrate to `IVideoFrameEndpoint` |
| `VideoOutputEndpointAdapter` | Not needed — hardware outputs implement `IVideoFrameEndpoint` + `IClockOwner` directly |

During migration: keep adapters as legacy bridge code in Phase 1; remove in Phase 3.

## Migration Recipes

### 1) Decoder -> Endpoint (no mixer)

Use when you do not need mixing/routing/timeline.

- Keep decoder loop (`while !EOF`) behavior.
- Write directly to endpoint contract.
- Endpoint performs conversion if required.

Resulting design: minimal path remains valid and explicit.

### 2) Decoder -> Input -> AVMixer -> Endpoint

Use when you need graph routing but not scheduling.

- Register input node(s) from decoder channels.
- Register endpoint(s).
- Create and enable route(s).
- Start input channels/decoder and endpoints.

Resulting design: runtime add/remove inputs/endpoints is straightforward.

### 3) Decoder -> Input -> Timeline -> AVMixer -> Endpoint

Use for playlist/sequencing and timed activation.

- Register all candidate inputs once.
- Build timeline items with `StartOffset`, `Enabled`, optional `Duration`.
- Timeline activates/deactivates routes/inputs at runtime.
- EOF policy controls next-item behavior (`AdvanceToNextEnabled`, `LoopItem`, etc.).

Resulting design: sequencing logic leaves player glue code and becomes first-class.

### 4) Fan-Out to Multiple Endpoints (replaces AggregateOutput)

Use when routing one source to multiple destinations (e.g. hardware + NDI).

Old (current):
```
AggregateOutput(leader) → AddSink(ndi) → AVMixer.AttachAudioOutput(aggregate)
```

New:
```
graph.RegisterEndpoint(hardwareEndpoint)   // clock authority
graph.RegisterEndpoint(ndiEndpoint)        // secondary
graph.TrySelectClockAuthority(hwEpId)
graph.RegisterInput(inputNode)
graph.CreateRoute(inputId, hwEpId, audioOpts)
graph.CreateRoute(inputId, ndiEpId, audioOpts)
```

### 5) Sink-Only (no hardware audio, replaces VirtualAudioOutput)

Use when only sink endpoints exist (e.g. NDI-only sending).

Old (current):
```
VirtualAudioOutput(format) → AVMixer.AttachAudioOutput(virtual) → RegisterAudioSink(ndi)
```

New:
```
graph.RegisterEndpoint(virtualClockEndpoint)   // provides tick + clock
graph.RegisterEndpoint(ndiEndpoint)
graph.TrySelectClockAuthority(virtualClockId)
graph.RegisterInput(inputNode)
graph.CreateRoute(inputId, ndiEpId, audioOpts)
```

### 6) NDI A/V Sink (dual-media endpoint)

Old (current):
```
avMixer.RegisterAudioSink(ndiSink, channels)
avMixer.RegisterVideoSink(ndiSink)
avMixer.RouteAudioChannelToSink(audioChId, ndiSink, routeMap)
avMixer.RouteVideoChannelToSink(videoChId, ndiSink)
```

New:
```
var epId = graph.RegisterEndpoint(ndiSink)          // detects IAVEndpoint
graph.CreateRoute(audioInputId, epId, audioOpts)    // routes audio
graph.CreateRoute(videoInputId, epId, videoOpts)    // routes video
```

## Compatibility Strategy (Optional Bridge Release)

If you want one transition release:

- keep old methods as thin wrappers over new graph calls
- mark old methods `[Obsolete]` with migration hints
- emit runtime warnings when wrappers are used
- remove wrappers in next major

Example wrapper semantics:

- `AttachAudioOutput(output)` -> internally `RegisterEndpoint(outputAdapter)` + set clock authority
- `RouteVideoChannelToSink(channelId, sink)` -> internally resolve IDs + `CreateRoute(...)`
- `AggregateOutput` constructor -> internally creates graph with leader + sinks

## Breaking Changes Checklist

Use this checklist per app/sample:

- [ ] remove all calls to `AttachAudioOutput` / `AttachVideoOutput`
- [ ] remove all calls to `OverrideRtMixer` / `OverridePresentationMixer`
- [ ] replace sink/output registration with endpoint registration
- [ ] replace route-to-sink methods with `CreateRoute` and stored `RouteId`
- [ ] replace `AggregateOutput` wiring with graph multi-endpoint topology
- [ ] replace `VirtualAudioOutput` with virtual clock endpoint
- [ ] replace dual sink registration for `NDIAVSink` with single endpoint registration
- [ ] update clone sink creation to register clones as graph endpoints
- [ ] move playlist logic to timeline items and EOF policies
- [ ] replace `IsLooping` with `TimelineEofPolicy.LoopItem`
- [ ] remove assumptions that mixer has one fixed output format
- [ ] pick explicit clock authority for each playback domain
- [ ] remove direct endpoint adapter usage (adapters become internal-only)
- [ ] move `ChannelRouteMap` from channel registration to route options

## Testing Checklist for Migration

- [ ] hot add/remove endpoint while playback is running
- [ ] hot add/remove input while playback is running
- [ ] route enable/disable under load (no stalls)
- [ ] timeline seek with multiple overlapping/disabled items
- [ ] EOF policy transitions (next, loop, stop)
- [ ] endpoint conversion success/failure paths
- [ ] fan-out to two+ audio endpoints (replaces AggregateOutput test)
- [ ] sink-only topology with virtual clock endpoint (replaces VirtualAudioOutput test)
- [ ] NDI dual-media endpoint receives both audio and video via single registration
- [ ] clone endpoint lifecycle: parent dispose cascades, graph orphan detection
- [ ] push-mode backpressure: disable input doesn't stall decoder
- [ ] cross-clock-domain drift correction between endpoints

## Common Pitfalls

- Assuming route creation implies endpoint start (it should not unless policy says so).
- Assuming one endpoint format drives all routes.
- Mixing timeline activation and direct route toggles without precedence rules.
- Not selecting clock authority when multiple clock-capable endpoints are present.
- Forgetting to flush channel buffers when timeline disables an input (decoder stall).
- Registering a dual-media endpoint twice (once as audio, once as video) — use
  single `RegisterEndpoint` and let graph detect `IAVEndpoint`.
- Not handling clone sink orphan warnings after parent endpoint is disposed.
- Passing `ChannelRouteMap` at channel registration instead of route creation (old habit).

## Suggested Rollout

1. Migrate internal core APIs (`IAVMixer` replacement + graph contracts).
2. Implement virtual clock endpoint (replaces `VirtualAudioOutput`).
3. Update `MediaPlayer` to timeline-backed transport behavior.
4. Migrate sample apps in `Test/` one-by-one:
   - `MFPlayer.SimplePlayer` (audio-only, simplest)
   - `MFPlayer.VideoPlayer` (A/V + optional NDI sink)
   - `MFPlayer.MultiOutputPlayer` (fan-out, replaces AggregateOutput)
   - `MFPlayer.NDIPlayer` / `MFPlayer.NDIAutoPlayer` (NDI A/V receive)
   - `MFPlayer.NDISender` (NDI send)
   - `MFPlayer.AvaloniaVideoPlayer` (Avalonia clone sink)
   - `MFPlayer.VideoMultiOutputPlayer` (video multi-output)
5. Remove compatibility wrappers in next major version.

## Quick Reference

- Architecture intent: `Doc/AVMixer-Timeline-Refactor-Plan.md`
- Proposed signatures: `Doc/AVMixer-Timeline-Proposed-Interfaces.md`

