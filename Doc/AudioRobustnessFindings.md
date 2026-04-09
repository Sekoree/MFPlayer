# Audio Robustness Review Findings (Pre-Video Rework)

Date: 2026-04-09  
Scope: audio decode, mixing, output, sink fan-out, seek/lifecycle/threading behavior.  
Validation run: `dotnet test MFPlayer.sln --no-build` (160/160 passing).

## Severity rubric

- **Critical**: likely data corruption/loss or hard-to-debug failures in normal playback.
- **High**: correctness or RT-safety issue likely to cause audible glitches, drift, or race failures.
- **Medium**: reliability/perf/observability issue that should be addressed before scaling features.
- **Low**: mismatch or cleanup item with limited immediate runtime risk.

## Findings (ordered by severity)

### 1) Critical - `FFmpegAudioChannel` drops decoded audio frames from multi-frame packets

- **Impact**: audible data loss and timeline distortion with codecs/containers where one packet yields multiple frames.
- **Evidence**:
  - `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:185-191` keeps only `last = ConvertFrame()` in the receive loop and returns just one frame array.
  - `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:152-162` enqueues only that single returned array.
- **Why this matters**: FFmpeg commonly returns multiple frames per packet for compressed formats; discarding all but the last frame loses audio content.
- **Recommendation**:
  - Enqueue each decoded frame inside the `avcodec_receive_frame` loop.
  - Keep pooled packet return in a `finally` block (already done correctly here).
- **Missing test**:
  - Integration test with AAC/Opus where one packet produces multiple decoded frames; assert decoded sample count/position continuity.

### 2) High - Seek path violates channel single-reader assumptions and flushes codec from the wrong thread

- **Impact**: race conditions during seek, occasional stale data, undefined channel behavior, and potential FFmpeg codec-context thread safety issues.
- **Evidence**:
  - `Media/S.Media.FFmpeg/FFmpegDecoder.cs:209-210` drains per-stream packet queues by reading `q.Reader` from control thread.
  - Those queues are configured as `SingleReader = true` (`Media/S.Media.FFmpeg/FFmpegDecoder.cs:132-137`) and already consumed by decode threads.
  - `Media/S.Media.FFmpeg/FFmpegDecoder.cs:212-213` calls `FlushAfterSeek()` directly on channels.
  - `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:293-297` and `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:272-276` call `avcodec_flush_buffers` outside decode thread context.
  - `EncodedPacket.Flush()` exists but is never used (`Media/S.Media.FFmpeg/FFmpegDecoder.cs:54`; no call sites).
- **Recommendation**:
  - Use in-band seek control messages (flush sentinel + seek epoch) sent to decode threads.
  - Keep all `avcodec_*` operations for a channel on that channel's decode thread.
  - Avoid control-thread reads from channels configured with `SingleReader = true`.
- **Missing test**:
  - Concurrency seek stress test (rapid seeks while playing) asserting no deadlock/race and stable post-seek decode.

### 3) High - `FFmpegVideoChannel` leaks pooled packet buffers on `avcodec_send_packet` failure

- **Impact**: sustained memory pressure under decode errors/corrupt streams.
- **Evidence**:
  - `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:157` returns early on send failure.
  - Pooled buffer return happens only after successful send (`Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:159-161`).
  - Unlike audio channel, there is no `finally` around packet processing.
- **Recommendation**:
  - Mirror audio-channel pattern: wrap packet submission/receive in `try/finally` and always return pooled arrays when `IsPooled`.
- **Missing test**:
  - Fault-injection test with invalid packets to assert pool-return path still executes.

### 4) High - PortAudio callback exception path does not actually output silence

- **Impact**: undefined/stale output samples when callback throws; potential bursts/noise.
- **Evidence**:
  - `Audio/S.Media.PortAudio/PortAudioOutput.cs:159-163` comment says "Output silence" but code only returns `paContinue`.
  - No `dest.Clear()` in catch path.
- **Recommendation**:
  - In catch path, explicitly zero the output span before returning.
  - Consider lightweight exception telemetry counter for diagnostics.
- **Missing test**:
  - Unit test with a mixer that throws in callback; assert output buffer is zeroed.

### 5) High - `PortAudioSink` does not implement the documented auto-resampler fallback

- **Impact**: wrong-rate sink playback can drift or have incorrect timing unless caller manually injects resampler.
- **Evidence**:
  - Constructor docs claim auto `LinearResampler` when null + rate mismatch (`Audio/S.Media.PortAudio/PortAudioSink.cs:38-41`).
  - Implementation only resamples if `_resampler != null` (`Audio/S.Media.PortAudio/PortAudioSink.cs:138-141`); otherwise raw copy path is used (`Audio/S.Media.PortAudio/PortAudioSink.cs:142-146`).
- **Recommendation**:
  - Either implement auto-create-on-mismatch behavior (and own/dispose it), or fix docs to state resampler is mandatory for mismatch.
- **Missing test**:
  - Sink integration test with leader 48k -> sink 44.1k and no explicit resampler; assert timing and sample count behavior.

### 6) Medium - RT-path allocation guarantees are not consistently upheld in mixer/sink hot paths

- **Impact**: occasional GC pressure or callback jitter under dynamic workloads.
- **Evidence**:
  - `Media/S.Media.Core/Mixing/AudioMixer.cs:307-309`, `323-326`: allocates new arrays in `FillOutputBuffer` when buffers are undersized.
  - `Audio/S.Media.PortAudio/PortAudioSink.cs:135-136`: allocates when pool is empty/undersized in `ReceiveBuffer` (called from RT callback path via mixer).
- **Context**: design docs and comments repeatedly state no-alloc RT hot paths.
- **Recommendation**:
  - Convert to strict preallocation policy with hard caps and drop/clip policy instead of allocating on RT thread.
  - Expose counters for pool misses / fallback allocations.
- **Missing test**:
  - Allocation regression test around `FillOutputBuffer` and sink `ReceiveBuffer` with `GC.GetAllocatedBytesForCurrentThread` (or benchmarking harness).

### 7) Medium - Position/accounting inconsistencies in `FFmpegAudioChannel` around underrun/seek

- **Impact**: clock/position drift in edge cases, making sync diagnostics harder.
- **Evidence**:
  - On partial underrun, `_framesInRing` is decremented for consumed frames, but `_framesConsumed` is not incremented (`Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:241-249`).
  - `Seek()` clears ring data but does not reset `_framesInRing` to zero (`Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:284-291`).
- **Recommendation**:
  - Keep accounting symmetric with `AudioChannel`: update consumed frames on partial pulls and reset occupancy on seek.
- **Missing test**:
  - Mirror `AudioChannel` position/buffer-available tests for `FFmpegAudioChannel` (partial pull then underrun; seek after buffered data).

### 8) Medium - Generic exception swallowing reduces failure visibility in decode loops

- **Impact**: silent decode thread termination with no actionable diagnostics.
- **Evidence**:
  - `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs:143-144` and `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs:139-140` use broad `catch { break; }` around packet reads.
- **Recommendation**:
  - Catch expected cancellation/object-disposed exceptions explicitly; log unexpected exceptions with stream id and codec.
- **Missing test**:
  - Thread-failure observability test (forced exception) to ensure errors are surfaced.

## Open questions / assumptions

- I assumed seek may be called while playback/decode is active; if seek is serialized externally, some race likelihood drops but thread-safety concerns remain.
- I assumed `AudioMixer.FillOutputBuffer` runs on a hard RT callback thread (as documented) and should avoid any fallback allocation behavior.

## Audio readiness gate before deeper video work

Recommended **must-fix before video expansion**:

1. Fix decoded-frame drop in `FFmpegAudioChannel`.
2. Rework seek synchronization/control messaging for decoder threads.
3. Fix `FFmpegVideoChannel` packet-pool return leak (shared decoder robustness).
4. Fix PortAudio callback silence-on-exception behavior.
5. Resolve `PortAudioSink` resampler contract mismatch.

After those, run targeted stress tests (long playback + rapid seek + multi-output fan-out) before investing in broader video pipeline rework.

---

## Second-pass review (2026-04-09, post-fixes)

### New findings

#### 9) High - `FFmpegDecoder.Start()` is not idempotent and can spawn duplicate demux/decode threads

- **Impact**: repeated `Start()` calls can create multiple demux/decode loops over the same decoder state, leading to undefined behavior and hard-to-debug races.
- **Evidence**:
  - `Media/S.Media.FFmpeg/FFmpegDecoder.cs:195-207` always starts a new demux thread.
  - No guard for already-started state in `FFmpegDecoder.Start()`.
- **Recommendation**:
  - Add an `_started` flag guarded by `Interlocked.Exchange` or lock; make `Start()` idempotent (or throw on second call).
- **Missing test**:
  - Call `Start()` twice and assert deterministic behavior (no second thread creation / explicit exception).

#### 10) High - Seek control-packet write can block caller when per-stream packet queues are full

- **Impact**: UI/control thread can stall on seek under back-pressure (e.g., paused output / saturated decode pipeline).
- **Evidence**:
  - `Media/S.Media.FFmpeg/FFmpegDecoder.cs:233-244` uses `WriteAsync(...).GetResult()` for control packets.
  - With full bounded queues and blocked decode threads, this can block the seek caller.
- **Recommendation**:
  - Prefer non-blocking control-path enqueue (`TryWrite` + best-effort fallback/log), or separate high-priority control channel per stream.
- **Missing test**:
  - Back-pressure seek stress test where decode is intentionally stalled, asserting seek call completes promptly.

#### 11) Medium - Seek epoch rollback on `av_seek_frame` failure is race-prone under concurrent seeks

- **Impact**: concurrent seek callers can observe non-monotonic epoch state, allowing stale packet classification bugs.
- **Evidence**:
  - `Media/S.Media.FFmpeg/FFmpegDecoder.cs:212-218` increments epoch before seek, then decrements on failure.
  - Decrement assumes no concurrent epoch changes.
- **Recommendation**:
  - Use monotonic epoch only (never decrement); on seek failure, emit error and skip flush packet/channel seek reset.
- **Missing test**:
  - Multi-threaded seek test with injected seek failures asserting epoch monotonicity.

#### 12) Medium - `PortAudioSink` now allocates a `LinearResampler` even when sample rates already match

- **Impact**: avoidable allocation and lifecycle complexity for same-rate sinks.
- **Evidence**:
  - `Audio/S.Media.PortAudio/PortAudioSink.cs:47-49` assigns `_resampler = resampler ?? new LinearResampler();` unconditionally.
  - Resampler is only used on mismatch (`Audio/S.Media.PortAudio/PortAudioSink.cs:137-140`).
- **Recommendation**:
  - Restore lazy mismatch-only fallback creation without locks on RT path (e.g., decide at construction from known leader rate, or pre-create off-RT if mismatch is known).
- **Missing test**:
  - Construction/behavior test asserting no fallback resampler allocation for same-rate source/sink path.

### Notes on requested `MFPlayer.SimplePlayer` controls

- Added controls in `Test/MFPlayer.SimplePlayer/Program.cs`:
  - `Space` pause/play
  - `Left/Right` seek -/+5s
  - `Up/Down` volume -/+0.05
  - `Enter/Q/Esc` stop
- Added periodic debug stats (`clock`, source `Position`, `BufferAvailable`, `Volume`, `PeakLevels`) once per second.

---

## Implementation status update (2026-04-09, async orchestration pass)

### Completed since prior review

- `FFmpeg` decode orchestration moved to async workers while keeping unsafe/native FFmpeg operations in channel classes:
  - `Media/S.Media.FFmpeg/FFmpegDecodeWorkers.cs` (`RunAudioAsync`, `RunVideoAsync`)
  - `Media/S.Media.FFmpeg/FFmpegDemuxWorker.cs` (`RunAsync`, async queue writes)
- Decoder/worker lifecycle moved to task-based handles for demux/decode task ownership and bounded shutdown waits.
- Sync-over-async in orchestration control flow removed (channel read/write flow now async in worker helpers).

### Findings status (current)

- **Resolved**: #1, #2, #3, #4, #5, #6, #7, #8, #9, #10, #11, #12.
- **Still open**: none.

### Roadmap / closure table

| Finding | Severity | Status | Files | Tests | Next action |
|---|---|---|---|---|---|
| #6 RT allocation guarantees | Medium | Done (2026-04-09) | `Media/S.Media.Core/Mixing/AudioMixer.cs`, `Audio/S.Media.PortAudio/PortAudioSink.cs` | `Test/S.Media.Core.Tests/AudioMixerTests.cs` | Monitor miss counters in long fan-out runs; tune pool sizes for target devices. |
| #8 Decode-loop observability | Medium | Done (2026-04-09) | `Media/S.Media.FFmpeg/FFmpegDecodeWorkers.cs`, `Media/S.Media.FFmpeg/FFmpegDemuxWorker.cs`, `Media/S.Media.FFmpeg/FFmpegAudioChannel.cs`, `Media/S.Media.FFmpeg/FFmpegVideoChannel.cs`, `Media/S.Media.FFmpeg/FFmpegDecoder.cs` | `Test/S.Media.FFmpeg.Tests/FFmpegDecoderTests.cs` (existing lifecycle/start/seek coverage) | Optionally route diagnostics through structured logger/event hook. |

### Remaining must-fix items before deeper video expansion

- No must-fix blockers remain from this review set.

### Notes on `Task` vs `ValueTask` usage

- Current guidance in this codebase:
  - Keep long-lived worker handles as `Task` (`_demuxTask`, `_decodeTask`) for lifecycle management.
  - Use `ValueTask` for short hot-path async helpers that frequently complete synchronously (e.g. packet write helpers).
- This pass follows that split and avoids direct `ValueTask.GetAwaiter().GetResult()` in channel orchestration paths.

