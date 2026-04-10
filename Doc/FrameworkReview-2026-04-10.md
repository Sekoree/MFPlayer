# MFPlayer Framework Review (2026-04-10)

Scope:
- Audio decode, mix, outputs, and sink fan-out architecture
- Local playback + NDI sink routing behavior
- `AudioMixer` / `AggregateOutput` API shape and necessity
- Future-readiness for video path expansion

Status update:
- 2026-04-10: Phase 1 completed.
- 2026-04-10: Phase 2 completed.

## Executive take

The current framework is in a strong place architecturally:
- Core interfaces are clean and composable (`IAudioChannel`, `IAudioOutput`, `IAudioMixer`, `IAudioSink`)
- Cross-project boundaries are sensible (`S.Media.Core` abstractions, backend implementations in `PortAudio`, `FFmpeg`, `NDI`)
- Multi-target routing exists and works (`AudioMixer.RegisterSink` + `RouteTo`)

Main opportunities now are not big rewrites; they are focused cleanup and consistency improvements:
1. tighten RT guarantees for sinks,
2. simplify `AggregateOutput` layering,
3. remove behavioral ambiguities in seek/UI policy,
4. formalize video sync model before broad video feature work.

---

## Findings (ordered by severity)

### 1) High - RT contract mismatch in `NDIAudioSink.ReceiveBuffer`

Status: Resolved (2026-04-10).

- File: `NDI/S.Media.NDI/NDIAudioSink.cs`
- Issue: On pool miss or insufficient capacity, `ReceiveBuffer` allocates (`new float[writeSamples]`).
- Why it matters: `IAudioSink.ReceiveBuffer` is called from the leader RT callback path (via `AudioMixer.FillOutputBuffer`) and should avoid allocation/blocking.
- Recommendation:
  - Match `PortAudioSink` strategy: drop on pool/capacity miss and expose counters.
  - Keep all allocations out of `ReceiveBuffer`; resize only on non-RT thread.

### 2) Medium - `AggregateOutput.AddSink(channels)` loses pre-open channel intent

Status: Resolved (2026-04-10).

- File: `Media/S.Media.Core/Audio/AggregateOutput.cs`
- Issue: `AddSink(IAudioSink sink, int channels)` only applies `channels` if mixer is already available. When added before `Open()`, `Open()` registers all sinks with `channels: 0`.
- Why it matters: Pre-open configuration is silently ignored, surprising users in multi-output scenarios.
- Recommendation:
  - Store sink registration metadata (`sink`, `channels`) and replay exact values on `Open()`.

### 3) Medium - `AggregateOutput` is now mostly a lifecycle wrapper; mixer pass-through is redundant

Status: Resolved (2026-04-10).

- Files: `Media/S.Media.Core/Audio/AggregateOutput.cs`, `Media/S.Media.Core/Mixing/AudioMixer.cs`
- Observation:
  - `AudioMixer` already supports sink registration/routing directly.
  - `AggregateAudioMixer` is a thin delegate and adds little behavior.
- Recommendation:
  - Keep `AggregateOutput` only as optional convenience for sink lifecycle (`AddSink`/`StartAsync`/`StopAsync`/`Dispose`).
  - Consider deprecating `AggregateAudioMixer` indirection and avoid callback mixer swapping when not needed.

### 4) Medium - Seek UX policy constants are fragile and currently very tight

Status: Open.

- File: `Test/MFPlayer.SimplePlayer/Program.cs`
- Observation: `TrySeekBy` uses hard-coded timing windows (`0.075`, `20ms`) that strongly affect repeated-seek behavior and can drift from actual key-repeat cadence.
- Recommendation:
  - Promote to named constants (e.g., `RapidSeekWindow`, `SameTargetDeadzone`).
  - Optionally tune from command-line/config for test apps.

### 5) Medium - Busy-wait write loops in sinks can waste CPU under low traffic

Status: Open.

- Files: `NDI/S.Media.NDI/NDIAudioSink.cs`, `Audio/S.Media.PortAudio/PortAudioSink.cs`
- Observation: write threads use `Thread.Yield()` when no pending buffers.
- Recommendation:
  - Use bounded channels or wait handles (`Channel<T>`, `AutoResetEvent`) for lower idle CPU and clearer back-pressure.

### 6) Low - Logging noise from expected seek control drops

Status: Partially resolved (2026-04-10).

- File: `Media/S.Media.FFmpeg/FFmpegDecoder.cs`
- Observation: `Seek control packet dropped` logs appear in normal test runs.
- Recommendation:
  - Demote to debug logging level or rate-limit.

### 7) Low - Docs and code have drift in a few areas

Status: Mostly resolved (2026-04-10).

- Files: `Doc/MediaPipelineArchitecture.md` plus recent implementation files
- Observation: architecture doc still reflects some pre-cleanup assumptions and does not highlight recent callback chunking and seek-epoch behaviors.
- Recommendation:
  - Update architecture/status docs with current behavior and known constraints.

---

## AudioMixer + local playback + NDI assessment

### Is multi-output routing possible today?

Yes.
- `AudioMixer` supports N targets via `RegisterSink` and per-(channel,sink) routing via `RouteTo`/`UnrouteTo`.
- A single source can feed:
  - leader hardware output (local playback), and
  - one or more sinks (`NDIAudioSink`, `PortAudioSink`),
  with different route maps.

### How good is the current model?

Good, with one caveat:
- Functionally: strong and flexible.
- RT robustness: depends on sink implementations truly honoring non-blocking/no-allocation behavior.

### Is `AggregateOutput` necessary?

Not strictly for mixing/routing.
- Routing capability lives in `AudioMixer` itself.
- `AggregateOutput` is primarily convenience for sink lifecycle coordination and pre-open sink setup.

Suggested direction:
1. Keep `AggregateOutput` as optional orchestration helper.
2. Document direct usage path (`output.Mixer.RegisterSink(...)`) as first-class.
3. Keep `AggregateOutput` lightweight (lifecycle + registration convenience only).

---

## Future video recommendations (before full expansion)

1. Define an explicit A/V clock master policy:
   - audio-master, video-master, or external clock.
2. Add queue/latency policy for video:
   - frame drop strategy under lag,
   - max queue age,
   - seek flush semantics across audio+video.
3. Formalize renderer thread contract:
   - ownership/disposal expectations for `VideoFrame.MemoryOwner` and back-pressure handling.
4. Add integration tests that pair `FFmpegAudioChannel` + `FFmpegVideoChannel` with clock assertions (not only unit-level channel tests).

---

## Suggested action plan

### Phase 1 (short, high-value)
- [x] Make `NDIAudioSink.ReceiveBuffer` fully RT-safe (no alloc on miss).
- [x] Preserve `AddSink(..., channels)` across pre-open flow in `AggregateOutput`.
- [x] Demote seek-drop log verbosity (rate-limited warnings).

### Phase 2 (API simplification)
- [x] Reduce/flatten `AggregateOutput` indirection (removed `AggregateAudioMixer` pass-through).
- [x] Promote direct `IAudioMixer` sink-routing pattern in docs/status.

### Phase 3 (video readiness)
- [ ] Implement explicit A/V sync policy and integration tests.
- [ ] Add video pipeline stress tests (seek + rate mismatch + mixed outputs).

---

## Positive notes

- The seek epoch filtering design in FFmpeg decode workers is a strong robustness improvement.
- Callback chunking in `PortAudioOutput` is an important cross-backend stability fix.
- The `AudioMixer` copy-on-write route snapshots are a solid concurrency choice for RT path safety.

