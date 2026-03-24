# S.Media Error Codes

Source of truth for allocation policy and reserved code chunks across `S.Media.*`.

## Global Ranges

- `0-999`: generic/common
- `1000-1999`: playback/player lifecycle
- `2000-2999`: decoding (`S.Media.FFmpeg`)
- `3000-3999`: mixing/sync/conflict
- `4000-4999`: output/render (`S.Media.PortAudio`, `S.Media.OpenGL`, adapters)
- `5000-5199`: NDI integration (`S.Media.NDI`)
- Core symbol mirror: `Media/S.Media.Core/Errors/MediaErrorAllocations.cs`

## Reserved Chunks (Implementation Planning)

- `2000-2099`: FFmpeg active initial allocation block
- FFmpeg fixed pick: `2010` = `FFmpegInvalidConfig` (invalid/nonsensical FFmpeg open/config combination)
- FFmpeg fixed pick: `2011` = `FFmpegInvalidAudioChannelMap` (explicit route-map policy without valid explicit map)
- FFmpeg fixed pick: `2014` = `FFmpegConcurrentReadViolation` (single-source concurrent read contract violation)
- `2100-2199`: FFmpeg runtime/native loading and interop-lifetime reserve
- `2200-2299`: FFmpeg mapping/resampler/format-conversion reserve
- `3000-3099`: Core mixing active initial allocation block
- Core mixing fixed pick: `3000` = `MixerDetachStepFailed` (remove/clear detach-step failure when no more specific code applies)
- Core mixing fixed pick: `3001` = `MixerSourceIdCollision` (duplicate source registration by `SourceId`)
- Core mixing fixed pick: `3002` = `MixerClockTypeInvalid` (nonsensical clock type for mixer kind, for example `VideoLed` on `AudioMixer`)
- `4000-4099`: Core generic video-output/backpressure initial allocation block
- Core output fixed pick: `4000` = `VideoOutputBackpressureQueueFull` (push rejected due to configured queue/backpressure limit)
- Core output fixed pick: `4001` = `VideoOutputBackpressureTimeout` (push timed out waiting for configured backpressure policy)
- Core output fixed pick: `4002` = `VideoFrameDisposed` (push rejected because frame instance was already disposed)
- `4300-4399`: PortAudio active initial allocation block
- `4400-4499`: OpenGL clone and render-graph active initial allocation block
- `5000-5079`: NDI active + near-term reserve block
- `5080-5199`: NDI future reserve block
- `900-949`: MIDI initial reserve block inside generic/common range
- MIDI active initial picks currently span `900-920` in `Media/S.Media.MIDI/API-outline.md`.

## Symbol Mapping (Planned Core)

- `GenericCommon` -> `0-999`
- `Playback` -> `1000-1999`
- `Decoding` -> `2000-2999`
- `Mixing` -> `3000-3999`
- `OutputRender` -> `4000-4999`
- `NDI` -> `5000-5199`
- `FFmpegActive` -> `2000-2099`
- `FFmpegInvalidConfig` -> `2010`
- `FFmpegInvalidAudioChannelMap` -> `2011`
- `FFmpegConcurrentReadViolation` -> `2014`
- `FFmpegRuntimeReserve` -> `2100-2199`
- `FFmpegMappingReserve` -> `2200-2299`
- `MixingActive` -> `3000-3099`
- `MixerDetachStepFailed` -> `3000`
- `MixerSourceIdCollision` -> `3001`
- `MixerClockTypeInvalid` -> `3002`
- `OutputBackpressureActive` -> `4000-4099`
- `VideoOutputBackpressureQueueFull` -> `4000`
- `VideoOutputBackpressureTimeout` -> `4001`
- `VideoFrameDisposed` -> `4002`
- `PortAudioActive` -> `4300-4399`
- `OpenGLActive` -> `4400-4499`
- `NDIActiveNearTerm` -> `5000-5079`
- `NDIFutureReserve` -> `5080-5199`
- `MIDIReserve` -> `900-949`
- `MediaConcurrentOperationViolation` -> `950` (shared generic/common contract for same-instance concurrent operation misuse)
- `FFmpegConcurrentReadViolation` (`2014`) -> maps to shared semantic `MediaConcurrentOperationViolation` (`950`)
- `MIDIConcurrentOperationRejected` (`918`) -> maps to shared semantic `MediaConcurrentOperationViolation` (`950`)
- `MIDIDeviceDisconnected` (`919`) and `MIDIReconnectFailed` (`920`) are MIDI lifecycle/recovery picks inside `MIDIReserve`.
- MIDI reconnect policy semantics: `NoRecover` disconnect paths use `MIDIDeviceDisconnected` (`919`); auto-reconnect exhaustion/timeout paths use `MIDIReconnectFailed` (`920`).
- `MediaSourceReadTimeout` (`4209`) is reserved for timeout-bounded read paths where no frame/sample arrives before the configured deadline; partial data before deadline remains success.
- `MediaExternalClockUnavailable` (`4211`) is reserved for configured external-clock paths that are unavailable (including NDI transport/session loss) and must not fall back implicitly.

## Allocation Rules

- `0` is `MediaResult.Success`; all non-zero values are failures.
- Every new owned failure path gets a dedicated stable code before merge.
- Never reuse a retired code for a different semantic.
- Prefer adding new codes in the module's reserved chunk before opening a new chunk.
- Shared semantic classification is centralized in `Media/S.Media.Core/Errors/ErrorCodeRanges.cs` via `ResolveSharedSemantic(int code)`.
- `MediaConcurrentOperationViolation` (`950`) may be surfaced directly by Core/orchestration paths when no module-specific code is more precise.
- Detach return-code precedence: when an operation has a specific owned module/backend detach-step failure code, return that specific code; use `MixerDetachStepFailed` (`3000`) only as generic fallback.
- Source registration rule: duplicate `SourceId` attempts must return `MixerSourceIdCollision` (`3001`) and must not partially mutate registrations.
- Clock-type rule: invalid mixer/clock-type combinations must return `MixerClockTypeInvalid` (`3002`) with no state mutation.
- Do not use `MediaSourceReadTimeout` (`4209`) for invalid timeout inputs or non-timeout failures (for example `MediaInvalidArgument`, `MediaSourceNonSeekable`).
- Use `VideoOutputBackpressureQueueFull` (`4000`) for policy-based immediate queue rejection; use `VideoOutputBackpressureTimeout` (`4001`) only when a configured wait budget expires.
- Video-output default backpressure policy is `DropOldest`; if Wait mode derives timeout from effective frame duration and cadence is unresolved, configuration is invalid (`MediaInvalidArgument`) unless explicit timeout override is provided.
- Use `VideoFrameDisposed` (`4002`) when `PushFrame(...)` receives a disposed frame instance.
- If a value is clamped by policy (for example queue size minimums), do not use an error code for that path.
- Log payload should include: operation context, backend/native detail (when available), and correlation id.
- `DebugKeys.MixerDetachSecondaryFailure` is diagnostics-only and should include `operation`, `sourceId`, `step`, `errorCode`, `correlationId`, plus backend/native detail when available.

## Ownership

- Core range rules and helpers: `Media/S.Media.Core`.
- Module-level picks live in each module outline during planning and move to constants/enums during implementation.
- Any code change must update this file and the module outline in the same change.

