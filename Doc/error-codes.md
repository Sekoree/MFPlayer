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
- `2100-2199`: FFmpeg runtime/native loading and interop-lifetime reserve
- `2200-2299`: FFmpeg mapping/resampler/format-conversion reserve
- `4300-4399`: PortAudio active initial allocation block
- `4400-4499`: OpenGL clone and render-graph active initial allocation block
- `5000-5079`: NDI active + near-term reserve block
- `5080-5199`: NDI future reserve block
- `900-949`: MIDI initial reserve block inside generic/common range

## Symbol Mapping (Planned Core)

- `GenericCommon` -> `0-999`
- `Playback` -> `1000-1999`
- `Decoding` -> `2000-2999`
- `Mixing` -> `3000-3999`
- `OutputRender` -> `4000-4999`
- `NDI` -> `5000-5199`
- `FFmpegActive` -> `2000-2099`
- `FFmpegRuntimeReserve` -> `2100-2199`
- `FFmpegMappingReserve` -> `2200-2299`
- `PortAudioActive` -> `4300-4399`
- `OpenGLActive` -> `4400-4499`
- `NDIActiveNearTerm` -> `5000-5079`
- `NDIFutureReserve` -> `5080-5199`
- `MIDIReserve` -> `900-949`

## Allocation Rules

- `0` is `MediaResult.Success`; all non-zero values are failures.
- Every new owned failure path gets a dedicated stable code before merge.
- Never reuse a retired code for a different semantic.
- Prefer adding new codes in the module's reserved chunk before opening a new chunk.
- If a value is clamped by policy (for example queue size minimums), do not use an error code for that path.
- Log payload should include: operation context, backend/native detail (when available), and correlation id.

## Ownership

- Core range rules and helpers: `Media/S.Media.Core`.
- Module-level picks live in each module outline during planning and move to constants/enums during implementation.
- Any code change must update this file and the module outline in the same change.

