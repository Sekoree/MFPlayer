# Full-repo review findings — 2026-07-27

Scope: framework timing/audio infrastructure, audio backends, NDI, session layer, plus repo
hygiene. HaViz-specific findings: `HaViz-Clocking-And-Headless-Audio.md`. Cue player designs:
`CuePlayer-Enhancements.md`, `CuePlayer-Timeline-Editor.md`. All references are against branch
`test-enhancements` (HEAD `c56f0619`).

## 0. Branch state — RESOLVED 2026-07-27

At review time `master` was 42 commits behind `test-enhancements` (HaViz existed only on the
branch). `test-enhancements` has since been merged to master (PR #9, `a83d1b74`); the merged
tree is byte-identical to the reviewed commit `c56f0619`, so every file:line reference in
these docs applies to current master as-is. Full solution builds clean;
S.Media.Session tests 155/155 pass, HaPlay tests 720 pass / 4 skipped.

## 1. Clock architecture: solid core, several dead or unwired pieces

The abstractions are good (`IPlaybackClock` → `MediaClock`/`SessionClock`/`TransportTimeline`,
separate `IRouterClock` pacing axis, wallclock default). A session *can* advance time with no
audio output (free-running `MediaClock`, `DiscardingAudioOutput`, wallclock `SessionClock`).
But a lot of the sophisticated machinery is written and never used:

| Piece | State |
|---|---|
| `CompositePlaybackClock` + `SetMasterChain` (priority/blend/handoff) | test-only, no production caller |
| `VideoPtsClock` | dead — `videoOnlyMaster` is null at every real call site; video-only playback runs on wall time, not PTS |
| `NDIIngestPlaybackClock` | created by every `NDISource` (`NDISource.cs:127-128`) but consumed only by a probe tool; `SlaveToNDI`/`SlaveToIngest` have zero production call sites |
| `OutputSyncGroup` / `VideoPresentSyncGroup` / `LiveTimelineDriver` | docs/tools/tests only — no genlock domain is ever constructed |
| `SoundboardGrid.TryCreateScheduledFire` | computes a quantized fire time that nothing dispatches |
| `TimecodeSyncKind { Mtc, Ltc, Smpte }` | enum declared, no implementation |

Either wire these (the NDI ingest clock and PTS clock both fix real sync classes) or prune
them — half-connected clock machinery is where subtle drift bugs hide.

**The single most consequential wiring gap for HaPlay:** `SharedAudioOutput.ClientInput`
implements `IClockedOutput` but deliberately **not** `IPlaybackClock`
(`SharedAudioOutput.cs:163-176`), and HaPlay routes every deck/cue line through shared
outputs. Result: `AudioRouter`'s device-master promotion never fires in the app — the visible
playhead and video schedule run on `Stopwatch`, not on played samples, for all shared-output
lines. Long-running clips will drift from the DAC by the accumulated device-rate error. Fix: a
`SharedAudioOutput`-level `IPlaybackClock` (terminal device clock minus per-client epoch).

**Proposed addition — `NullClockedAudioOutput`.** There is no silent output backend; headless
runs degrade to wallclock pacing implicitly. A `NullClockedAudioOutput : IAudioOutput,
IClockedOutput, IPlaybackClock` that consumes at exactly `chunk/rate` (absolute-deadline
`Task.Delay` pacing) makes headless/CI/HaViz playback first-class and deterministic. Measured
feasibility on this box (Release, 512 frames @ 48 kHz, 10 s): p50 jitter 0.43 ms, p99 0.92 ms,
max 2.0 ms, **zero cumulative drift**. (Anti-finding: `PeriodicTimer` quantizes fractional-ms
periods — it drifted −625 ms over the same 10 s. Don't use it for audio cadence.)

## 2. Bugs / fragile spots (ranked)

### High

1. **Native handle races in both backend clocks.** `PortAudioOutput` reads `_stream` outside
   `_streamLifecycleGate` in `StreamActive`, `StreamTime`, `ElapsedSinceStart`, `IsAdvancing`,
   `Flush` pre-check (`PortAudioOutput.cs:100-167,499`) while `Stop()` closes/nulls it under
   the gate; `ElapsedSinceStart` is polled ~30 Hz+ from `MediaClock`'s driver thread, so a
   stop can race a `Pa_GetStreamTime` on a freed handle — a real use-after-free window, not
   theoretical. `MiniAudioOutput` has the identical pattern on `_device` vs `DeviceDestroy` +
   `FreeHGlobal` (`MiniAudioOutput.cs:64-98,257,274`).
2. **No device-loss detection anywhere.** miniaudio's notification callback is never
   registered; nothing polls stream state. When a device disappears, `IsAdvancing` silently
   goes false and the mastered `MediaClock` freezes — playback appears hung with no event, no
   log. At minimum surface an `OutputLost` event; ideally re-master (the router's
   `PromoteNextPrimaryIfNeeded` only runs on *explicit* `RemoveOutput`).
3. **Callback faults are latched and never read.** Both backends correctly latch
   `_callbackFaulted` instead of throwing across the native boundary — but no router, player,
   or session code ever reads it. A faulted PortAudio callback returns `paAbort`, killing the
   stream; the app sees silence and a frozen clock.
4. **`NDIIngestPlaybackClock` violates monotonicity.** `ComputeElapsedUnlocked`
   (`NDIIngestPlaybackClock.cs:266-273`) includes the full last-frame duration *plus* wall
   time since arrival, so a late frame steps the clock backwards; `IPlaybackClock` documents
   monotonic, and `MediaClock` only clamps against its anchor — the playhead can regress.
   (Latent until the ingest clock is wired, but fix before wiring it.)

### Medium

5. **`SessionClock` torn reference/shift pair.** `SetReference` writes two plain fields
   non-atomically while `TransportTimeline.SafeMasterTime` reads from arbitrary threads
   (`SessionClock.cs:19-50`, `TransportTimeline.cs:248-252`) — a reader can see new reference
   + old shift → time jump. One `record` holding both fields, swapped by reference, fixes it.
6. **`MediaClock.Pause`/`Start` race** — Pause joins the driver thread *outside* the gate;
   a racing Start can spin up a second driver while the first still ticks
   (`MediaClock.cs:154-173,249-261`).
7. **`MiniAudioOutput.Flush` throws under `_deviceLifecycleGate`** before clearing the ring
   (`MiniAudioOutput.cs:261`) — a failing stop leaves half-flushed state; the PortAudio
   equivalent deliberately ignores the abort result. Same pattern at `:189,:282`.
8. **Backend clock quality asymmetry.** PortAudio interpolates with `Pa_GetStreamTime` and
   subtracts negotiated DAC latency; miniaudio does neither (`MiniAudioOutput.cs:84-92`) —
   switching backends changes lip-sync by the device buffer and the clock advances in
   period-sized stair steps.
9. **Release builds silently swallow sink failures.** Video `Submit` failures log under
   `#if DEBUG` only (`VideoPlayer.cs:878-897`); `NDISource.cs:163-170` even sets
   `_state = Disconnected` *inside* the `#if DEBUG` block — Release keeps a dead source
   "connected". `NDIOutput.cs:139-146` same.
10. **`MFP_PORTAUDIO_HOST_API` is honored only for the catalog's `IsDefault` flag**, not by
    `PortAudioOutput`'s own `deviceIndex ?? Pa_GetDefaultOutputDevice()` (`:219`) — opening
    with a null device id bypasses the operator's host-API override.
11. **`ShowSession` resolves the fallback device once at construction** and never refreshes
    (`ShowSession.cs:259-263`), unlike the 5 s TTL `AudioOutputDeviceCache` used elsewhere.

### Low / by-design-but-sharp

12. A wedged output pump deliberately leaks itself after 3 s and poisons the router
    non-restartably (`AudioRouter.OutputPump.cs:315-351`) — consider surfacing this as a
    health event so hosts can rebuild.
13. `ClipCompositionRuntime.CheckMasterDrift` uses `default` as the unprimed sentinel, so a
    master at exactly 0 re-primes every tick and drift is never measured
    (`ClipCompositionRuntime.cs:915-940`).
14. Seek anchor vs flush epoch: `AvPlaybackCoordinator.Seek` re-anchors implicitly; a
    `Flush` between source-seek and clock-seek leaves a stale anchor
    (`AvPlaybackCoordinator.cs:276-301`); `MediaPlayer.Position`'s clamp-on-EOF (`:414-424`)
    is a workaround for the same epoch-reset. Worth one deliberate "epoch generation"
    mechanism instead of three local patches.
15. PortAudio clock reports 0 for the first `outputLatency` (up to ~90 ms) then jumps
    (`PortAudioOutput.cs:150-151`) — visible as a stalled-then-jumping playhead at start.
16. `MiniAudioBackend` builds/destroys a fresh `ma_context` per enumeration call; fine behind
    the session cache, wasteful for direct callers.

## 3. Benchmarks

Existing (BenchmarkDotNet, in-solution, run with
`dotnet run -c Release --project MediaFramework/Benchmarks/<name>`):
`S.Media.Audio.Benchmarks` (ChannelMap/matrix-mix/float-ring/clip-voice),
`S.Media.Compositor.Benchmarks`, `OSCLib.Benchmarks`, `LibAssLib.Benchmarks`. Plus
allocation-guard tests for clock reads (`PlaybackClockAllocationTests`).

Missing, and worth adding alongside the fixes above:
- a **clock-read latency/allocation** benchmark for `PortAudioOutput`/`MiniAudioOutput`
  `ElapsedSinceStart` (they sit on `MediaClock`'s hot path);
- a **pacing-jitter harness** (the `Task.Delay` absolute-deadline scheme measured in this
  review: p50 0.43 ms / p99 0.92 ms / no drift) for the proposed `NullClockedAudioOutput`;
- envelope-evaluation numbers already measured for the timeline feature: 21.5 ns per
  piecewise-linear eval (20 keyframes) → ~2 µs per audio-second per cue at per-block rate —
  volume automation is computationally free (see `CuePlayer-Timeline-Editor.md`).

## 4. Suggested priorities

1. Backend handle races + device-loss/fault surfacing (§2.1–3) — these are the "show goes
   silent with no error" class.
2. HaPlay default-device leaks (`CuePlayer-Enhancements.md` §5) and HaViz clocking rework
   (`HaViz-Clocking-And-Headless-Audio.md`) — same theme, both wiring-level.
3. `NullClockedAudioOutput` + `SharedAudioOutput` playback clock (§1) — makes headless
   correct and gives the app a real sample clock.
4. Decide wire-or-prune for the dead clock machinery (§1 table).
5. Cue player feature work (`CuePlayer-Enhancements.md` build order).

## 5. Fix-round status (2026-07-28, verified by post-implementation review)

§2 findings: **1, 4, 5, 10, 11 fixed and verified**; **6, 7 fixed** with benign residuals
(documented in the review transcript). **3 now complete**: backends fail Submit/WaitForCapacity
fast on a latched callback fault (commit `cdc326a2`), and the follow-up round closed the two
gaps that made that dangerous — the router run loop raises `Faulted` (log-error, not a trace
`break`) when the pacing clock fails without cancellation, and `ShowSession` subscribes every
committed clip router's `OutputErrored`/`Faulted` into a new `ShowSession.PlaybackAlert` event
that HaPlay's coordinator logs + surfaces as an operator status message. **9 fixed** (all
`#if DEBUG`-only sink-failure logging in `VideoPlayer`/`NDISource`/`NDIOutput`/`OutputPump` is
now unconditional, streak-throttled on hot paths; `NDISource` keeps its state consistent in
Release). **12 fixed** (a wedged leaked pump raises `OutputErrored` → `PlaybackAlert`).
**13 fixed** (nullable prime sentinel — a 0-position master no longer re-primes every tick).

§1 additions: `NullClockedAudioOutput` implemented + tested (post-review: Start/Dispose race
closed, dispose observed within ~100 ms in `WaitForCapacity`) but still has no production
consumer. `SharedAudioOutput.ClientInput` now implements `IPlaybackClock` and the router
promotes it to MediaClock master — the HaPlay shared-output drift gap is closed (post-review:
a terminal-clock re-anchor is detected and the client clock resumes from its high-water mark
instead of freezing at zero). Known limitation: the client clock leads the DAC by the
client-bus + pump + device-ring latency (~100 ms constant bias), unlike `PortAudioOutput`'s
latency-compensated clock — acceptable for now, noted for a latency pass.

**Round 2 (2026-07-28, later):** **2 fixed** — miniaudio registers the layout-safe `ma_stop_proc`
stop callback (unexpected stop → `_deviceLost` latch → the same fail-fast triple as callback
faults → `OutputErrored`/`Faulted` → `PlaybackAlert`; deliberate Stop/Flush guarded by an
intentional-stop flag; WASAPI silent-reroute caveat documented), and PortAudio latches loss via
gated `Pa_IsStreamActive` health checks on the existing clock-poll path plus a 250 ms probe
inside a blocked `WaitForCapacity`. **8 fixed** — miniaudio clock now interpolates between data
callbacks (clamped to one period, monotonic via a CAS high-water) and subtracts a period of DAC
latency; residual ≤ ~(periods−1) internal periods, documented. **15 fixed** — audible position
eases in quadratically over the first `2×latency` window, C¹-continuous into `elapsed − latency`.
**16 fixed** — one cached `ma_context` per backend instance with a stale-context rebuild-once
path; backend is now `IDisposable`. Truth-table/state-machine tests in
`S.Media.Audio.Backends.Tests` (41 tests, hardware-dependent cases device-gated).

**Still open:** 14 (seek/flush epoch generations — deliberate deferral) and the §1
wire-or-prune decision for `CompositePlaybackClock`/`VideoPtsClock`/`NDIIngestPlaybackClock`
(now monotonic but still unwired)/`OutputSyncGroup`/`TimecodeSyncKind`.
