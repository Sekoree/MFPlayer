# S.Media.NDI — Issue Checklist

> **Last updated:** 2026-03-31
> **Source document:** `S.Media.NDI.md`
> **Legend:** ✅ Fixed · ❌ Outstanding · 🔴 Critical · 🟡 Important · 🔵 Minor / Arch

All issues from both review passes in one place. Tick a box when a fix is merged.

---

## First-Pass Issues (§1–§4)

| # | Status | Priority | Issue | Section |
|---|---|---|---|---|
| 1.1 | ✅ Fixed | 🔴 | `NDIVideoOutput` not implementing `IAudioSink` — mixer cannot route audio to NDI | §1.1 |
| 1.2 | ✅ Fixed | 🟡 | `public PushAudio(in AudioFrame, TimeSpan)` removed; callers now use `((IAudioSink)output).PushFrame(...)` | §1.2 |
| 2.1 | ✅ Fixed | 🔴 | `_gate` held during blocking `NDISender.SendVideo()` native call — deadlock when `ClockVideo = true` | §2.1 |
| 2.2 | ✅ Fixed | 🟡 | Non-standard no-arg `Start()` overload not on `IVideoOutput` | §2.2 |
| 2.3 | ✅ Fixed | 🔵 | `VideoOutputConfig` validated in `Start()` then silently ignored — config explicitly discarded with doc comment | §2.3 |
| 2.4 | ✅ Fixed | 🟡 | `_stagingBuffer` / `_audioStagingBuffer` grow monotonically — now use `ArrayPool<byte/float>`, returned in `Dispose()` | §2.4 |
| 3.1 | ✅ Fixed | 🔴 | Public source constructors create independent `NDICaptureCoordinator` — now delegate to `mediaItem.CaptureCoordinator` | §3.1 |
| 4.1 | ✅ Fixed | 🟡 | `RequireAudioPathOnStart` duplicated — removed from `NDIIntegrationOptions`, kept only on `NDIOutputOptions` | §4.1 |
| 4.2 | ✅ Fixed | 🟡 | No `CreateMediaItem` factory on `NDIEngine` — added `CreateMediaItem(receiver, out item)` | §4.2 |

---

## Second-Pass Issues (§5)

### 5.1 — Bugs & Correctness

| # | Status | Priority | Issue | Section |
|---|---|---|---|---|
| 5.1 | ✅ Fixed | 🔴 | `DropNewest` and `RejectIncoming` were identical — `DropNewest` now evicts the tail of the queue (copy-evict-re-enqueue) | §5.1 |
| 5.2 | ✅ Fixed | 🟡 | `NDIVideoOutput.Start()` returned `NDIOutputPushVideoFailed` when disposed — now returns `MediaObjectDisposed` | §5.2 |
| 5.3 | ✅ Fixed | 🟡 | `IAudioSink.Start()` returned `Success` when already running even if `EnableAudio = false` — now rejects early | §5.3 |
| 5.4 | ✅ Fixed | 🟡 | `CreateAudioSource`/`CreateVideoSource` returned `NDIReceiverCreateFailed` on options validation failure — now propagates specific error | §5.4 |
| 5.5 | ✅ Fixed | 🔴 | Concurrent `CaptureOnce()` from audio and video threads — serialized with `SemaphoreSlim(1,1)` non-blocking tryacquire | §5.5 |
| 5.6 | ✅ Fixed | 🟡 | `CaptureOnce()` bare `catch {}` swallowed fatal exceptions — now `catch (Exception ex) when (ex is not OOM and not AV)` | §5.6 |
| 5.7 | ✅ Fixed | 🟡 | `TryGetFallbackFrame()` used `DateTime.UtcNow` for timeout — replaced with `Stopwatch.GetTimestamp()` | §5.7 |
| 5.8 | ✅ Fixed | 🟡 | Jitter-buffer priming returned `NDIVideoFallbackUnavailable` — now returns `NDIVideoBuffering` (new error code 5028) | §5.8 |

### 5.2 — Dead / Unenforced Configuration

| # | Status | Priority | Issue | Section |
|---|---|---|---|---|
| 5.9 | ✅ Fixed | 🔴 | `NDILimitsOptions` queue limits never enforced — `NDICaptureCoordinator` now accepts `maxVideoFrames`/`maxAudioBlocks`; engine passes limits at creation; coordinator disposed in `Terminate()` | §5.9 |
| 5.10 | ✅ Fixed | 🔵 | `NDIOutputOptions.ValidateCapabilitiesOnStart` dead flag — removed from the record | §5.10 |
| 5.11 | ✅ Fixed | 🔵 | `NDIEngineDiagnostics.ClockDriftMs` misleading name — renamed to `DiagnosticsIntervalBudgetMs` with doc clarification | §5.11 |
| 5.12 | ✅ Fixed | 🟡 | `NDIExternalTimelineClock` had no call-sites — added doc comment explaining integration status and usage guidance | §5.12 |

### 5.3 — API Gaps & Missing NDI Features

| # | Status | Priority | Issue | Section |
|---|---|---|---|---|
| 5.13 | ✅ Fixed | 🟡 | `PushFrame()` did not check `Options.EnableVideo` — guard added; returns `NDIInvalidOutputOptions` | §5.13 |
| 5.14 | ✅ Fixed | 🟡 | Tally support absent — `GetTally(out bool onProgram, out bool onPreview)` added to `NDIVideoOutput` | §5.14 |
| 5.15 | ✅ Fixed | 🟡 | Connection count not exposed — `GetConnectionCount(uint timeoutMs = 0)` added to `NDIVideoOutput` | §5.15 |
| 5.16 | ✅ Fixed | 🟡 | `NDIMediaItem` hardcoded `1920×1080@60` — `VideoStreams` now uses null dimensions; doc comment added | §5.16 |
| 5.17 | ✅ Fixed | 🟡 | `Timecode` set to PTS ticks — now uses `NdiConstants.TimecodeSynthesize`; `Timestamp` carries PTS | §5.17 |

### 5.4 — Performance & Minor

| # | Status | Priority | Issue | Section |
|---|---|---|---|---|
| 5.18 | ✅ Fixed | 🔵 | `PlaybackAudioSources`/`PlaybackVideoSources` called `.ToArray()` each access — replaced with `.AsReadOnly()` | §5.18 |
| 5.19 | ✅ Fixed | 🔵 | `_lastPushMs` written outside lock with plain assignment — write now done inside `_gate` lock with counter updates | §5.19 |

### 5.5 — Architectural

| # | Status | Priority | Issue | Section |
|---|---|---|---|---|
| 5.A | ✅ Fixed | 🔵 | `NDICaptureCoordinator` replaced with `INDICaptureCoordinator` interface + `NDIFrameSyncCoordinator` (SDK TBC). Pixel conversion extracted to `NDIVideoPixelConverter`. `NDIMediaItem` and `NDIEngine` now prefer `NDIFrameSyncCoordinator`, falling back to `NDICaptureCoordinator` if framesync creation fails. | §5.5 |

---

## Summary

| Pass | Total | ✅ Fixed | ❌ Outstanding |
|---|---|---|---|
| 1st (§1–§4) | 9 | 9 | 0 |
| 2nd (§5) | 19 (+1 arch) | 19 (+1 arch) | 0 |
| **All** | **28 (+1 arch)** | **29** | **0** |

### Outstanding by priority

| Priority | Count |
|---|---|
| 🔴 Critical | 0 |
| 🟡 Important | 0 |
| 🔵 Minor / Arch | 0 |
